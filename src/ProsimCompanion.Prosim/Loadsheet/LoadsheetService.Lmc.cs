using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft.Acars;
using ProsimCompanion.Core.Aircraft.WeightAndBalance;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Prosim.Loadsheet;

/// <summary>
/// Last-minute changes on the final loadsheet (issue #181). Once per departure cycle, at the
/// boarding-complete edge, when a final already exists: the load actually aboard (the live
/// W&amp;B, bit-exact) against the cached final. Within tolerance → the LMC block stands under
/// the final, the FO reads it (Speech side, off the store), an ACARS "LMC" message goes to the
/// cockpit in its own slot — never over the loadsheets in 01/02 — and the FMS is NOT re-synced
/// (SYNC TO FMS picks the figures up). Beyond tolerance → the captain's "issue a revised
/// final?" question, or the revision straight away with <c>lmcAutoRevise</c>. The on-demand
/// path (<c>loadsheet.lmc</c>, the page button, "last minute change") ignores the latch.
/// </summary>
public sealed partial class LoadsheetService
{
    /// <summary>The ACARS slot the LMC text is uplinked to — beside the loadsheets, so a
    /// revised final in 02 never overwrites it and it never overwrites a loadsheet.</summary>
    public const string LmcAcarsSlot = "03";

    /// <inheritdoc />
    public Task<bool> EvaluateLastMinuteChangeAsync(CancellationToken cancellationToken = default)
        => EvaluateLmcAsync(onDemand: true, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> GenerateRevisedFinalAsync(CancellationToken cancellationToken = default)
    {
        LoadsheetLmc? lmc;
        lock (_stateLock)
        {
            if (_cachedFinal is null || _cachedPrelimContext is null)
            {
                RecordDecision("cannot issue a revised final: no final this cycle");
                return false;
            }

            lmc = _store.Snapshot().Lmc;
            // EDNO +1: the final inherits the prelim context's edition, so bump it there.
            _cachedPrelimContext.EditionNumber++;
            _pendingRevision = lmc is { } change ? $"LMC PAX {Signed(change.Result.DeltaPax)}" : "LMC";
        }

        RecordDecision($"revised final loadsheet after last-minute change — EDNO {_cachedPrelimContext!.EditionNumber}");
        var ok = await GenerateFinalAsync(cancellationToken).ConfigureAwait(false);
        if (ok)
        {
            _store.SetLmcDisposition(LmcDisposition.RevisionIssued);
            _eventLog.Record("loadsheet.lmc", new { outcome = "revised", edno = _cachedPrelimContext.EditionNumber });
        }

        return ok;
    }

    private async Task EvaluateLmcAtBoardingCompleteAsync()
    {
        try
        {
            lock (_stateLock)
            {
                if (_lmcEvaluated)
                {
                    return;
                }

                if (_cachedFinal is null)
                {
                    RecordDecision("boarding complete — no final loadsheet yet, last-minute change not evaluated");
                    return;
                }

                _lmcEvaluated = true;
            }

            await EvaluateLmcAsync(onDemand: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Last-minute change evaluation failed");
        }
    }

    private async Task<bool> EvaluateLmcAsync(bool onDemand, CancellationToken cancellationToken)
    {
        LoadsheetData final;
        LoadsheetContext finalContext;
        lock (_stateLock)
        {
            if (_cachedFinal is null || _cachedFinalContext is null)
            {
                RecordDecision("last-minute change: no final loadsheet this cycle");
                return false;
            }

            final = _cachedFinal;
            finalContext = _cachedFinalContext;
        }

        LoadsheetData actual;
        try
        {
            actual = A320WeightAndBalance.CalculateFinal(ReadLiveState());
        }
        catch (InvalidOperationException ex)
        {
            RecordDecision($"last-minute change aborted: {ex.Message}");
            return false;
        }

        var options = _options.CurrentValue;
        var tolerances = new LmcTolerances(Math.Max(0, options.LmcMaxPax), Math.Max(0, options.LmcMaxWeightKg), Math.Max(0, options.LmcMaxMacShift));
        var result = LmcEvaluator.Evaluate(final, actual, tolerances);
        var disposition = result.Verdict switch
        {
            LmcVerdict.NoChange => LmcDisposition.NoChange,
            LmcVerdict.WithinTolerance => LmcDisposition.Applied,
            _ => LmcDisposition.AwaitingDecision,
        };

        _eventLog.Record("loadsheet.lmc", new
        {
            verdict = result.Verdict.ToString(),
            onDemand,
            againstEdno = finalContext.EditionNumber,
            deltaPax = result.DeltaPax,
            deltaZfwKg = Math.Round(result.DeltaZfwKg),
            deltaCargoKg = Math.Round(result.DeltaCargoKg),
            deltaFuelKg = Math.Round(result.DeltaFuelKg),
            deltaTowKg = Math.Round(result.DeltaTowKg),
            macTowShift = Math.Round(result.MacTowShift, 2),
            newZfwKg = Math.Round(result.NewZfwKg),
            newTowKg = Math.Round(result.NewTowKg),
            newLawKg = Math.Round(result.NewLawKg),
            newMacZfw = Math.Round(result.NewMacZfw, 1),
            newMacTow = Math.Round(result.NewMacTow, 1),
            reasons = result.Reasons,
            tolerances = new { tolerances.MaxPax, tolerances.MaxWeightKg, tolerances.MaxMacShift },
        });
        RecordDecision($"last-minute change {result.Verdict}: pax {Signed(result.DeltaPax)}, ZFW {result.DeltaZfwKg:+0;-0} kg, TOW {result.DeltaTowKg:+0;-0} kg, MACTOW shift {result.MacTowShift:0.00}"
            + (result.Reasons.Count > 0 ? $" — {string.Join("; ", result.Reasons)}" : ""));

        // NoChange is silent on the automatic path; an on-demand check shows "no change" on the page.
        if (result.Verdict == LmcVerdict.NoChange && !onDemand)
        {
            return true;
        }

        _store.SetLmc(new LoadsheetLmc(result, disposition, finalContext.EditionNumber, DateTimeOffset.UtcNow));

        switch (result.Verdict)
        {
            case LmcVerdict.WithinTolerance:
                await SendLmcAcarsAsync(result, caption: null, cancellationToken).ConfigureAwait(false);
                break;

            case LmcVerdict.RevisedLoadsheetRequired when options.LmcAutoRevise:
                await GenerateRevisedFinalAsync(cancellationToken).ConfigureAwait(false);
                break;

            case LmcVerdict.RevisedLoadsheetRequired:
                if (!_lmcQuestion.Ask(LmcEvaluator.QuestionPrompt(result)))
                {
                    RecordDecision("revised-final question already stands");
                }

                break;
        }

        return true;
    }

    /// <summary>The captain answered "issue a revised final?" — by voice, on the card or by
    /// the TTL (no). Yes re-runs the final with EDNO +1; no leaves the LMC block with the
    /// "flown on captain's authority" caption and sends the LMC text to the cockpit.</summary>
    private void OnLmcRevisionAnswered(object? sender, (bool Accepted, string Source) answer)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (answer.Accepted)
                {
                    await GenerateRevisedFinalAsync().ConfigureAwait(false);
                    return;
                }

                _store.SetLmcDisposition(LmcDisposition.FlownOnCaptainsAuthority);
                RecordDecision($"revised final declined ({answer.Source}) — flown on captain's authority");
                _eventLog.Record("loadsheet.lmc", new { outcome = "captains-authority", source = answer.Source });
                if (_store.Snapshot().Lmc is { } lmc)
                {
                    await SendLmcAcarsAsync(lmc.Result, LoadsheetFormatter.FlownOnCaptainsAuthorityCaption, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Revised-final answer handling failed");
            }
        });
    }

    private async Task SendLmcAcarsAsync(LmcResult result, string? caption, CancellationToken cancellationToken)
    {
        var sent = await _acars.SendAsync(new AcarsMessage
        {
            Type = "LMC",
            Header = "LAST MINUTE CHANGE",
            Id = LmcAcarsSlot,
            Accept = false,
            Content = LoadsheetFormatter.FormatLmc(result, caption),
        }, cancellationToken).ConfigureAwait(false);
        if (!sent)
        {
            RecordDecision("LMC ACARS uplink failed (the block is still on the Loadsheet page)");
        }
    }

    private static string Signed(int value) => value >= 0 ? $"+{value}" : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
