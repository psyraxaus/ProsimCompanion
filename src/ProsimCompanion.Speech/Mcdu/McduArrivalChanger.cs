using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Briefings;
using ProsimCompanion.Speech.Recognition;
using ProsimCompanion.Speech.Roles;

namespace ProsimCompanion.Speech.Mcdu;

/// <summary>
/// Pure phrase core for the arrival changer: the generated "change arrival runway …" /
/// "change to runway …" vocabulary and the utterance → runway resolution. Split out of the
/// changer so the 288-phrase grammar is testable without ProSim.
///
/// ARCHAEOLOGY (Prosim2FO, 2025): the phrases are explicit because the recognizer matches
/// whole phrases — a "{number}" placeholder can't compose with a trailing side word, so
/// runways 01–36 × (none/left/right/center) × two natural prefixes are all spelled out:
/// 36 × 4 × 2 = 288 phrases.
/// </summary>
public static class McduArrivalPhrases
{
    /// <summary>Plain recognition digit words ("nine", not "niner" — ASR transcribes what
    /// pilots actually say; the FO's own read-backs use the ICAO "niner" via Aviation).</summary>
    private static readonly string[] DigitWords =
        ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];

    private static readonly IReadOnlyDictionary<string, string> RunwayByPhrase = Build();

    /// <summary>All 288 command phrases (normalized form).</summary>
    public static IReadOnlyCollection<string> Phrases => RunwayByPhrase.Keys.ToArray();

    /// <summary>Resolves a normalized utterance to its runway ("04L"). Exact match only —
    /// a flight-plan mutation must never fire off a guess.</summary>
    public static bool TryResolve(string normalizedUtterance, out string runway)
    {
        ArgumentNullException.ThrowIfNull(normalizedUtterance);
        if (RunwayByPhrase.TryGetValue(normalizedUtterance, out var found))
        {
            runway = found;
            return true;
        }

        runway = "";
        return false;
    }

    private static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var n = 1; n <= 36; n++)
        {
            var number = n.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
            var spoken = $"{DigitWords[number[0] - '0']} {DigitWords[number[1] - '0']}";
            foreach (var (suffix, word) in new[] { ("", ""), ("L", " left"), ("R", " right"), ("C", " center") })
            {
                var runway = number + suffix;
                map[$"change arrival runway {spoken}{word}"] = runway;
                map[$"change to runway {spoken}{word}"] = runway;
            }
        }

        return map;
    }
}

/// <summary>
/// Arrival runway/approach change on the CDU2 (Prosim2FO semantics). This is the one flow
/// that MUTATES the active flight plan, so it is defensive to a fault: it validates the
/// runway against the DFD, drives FPLN → LAT REV → ARRIVAL matching the approach by on-screen
/// text (never blind-press), stages the TMPY, verifies the TMPY is actually up, announces
/// with a cancel-window pause, INSERTs, and cross-checks the result — and on ANY surprise or
/// abort it ERASEs the staged TMPY so the plan is never left half-changed. Gated on armed +
/// FO pilot flying; "my aircraft"/"negative" aborts (and backs the TMPY out). Voice sets the
/// runway with the best ILS; the API path can also pass a specific approach variant and STAR.
/// </summary>
public sealed class McduArrivalChanger : IVoiceFeature, IDisposable
{
    private static readonly string[] CancelWords = ["negative", "disregard", "cancel", "belay that"];

    /// <summary>How many ARROW_DOWN scrolls to try while hunting a row on the ARRIVAL page.</summary>
    private const int MaxScroll = 4;

    private readonly ProcedureSource _procedures;
    private readonly DfdNavDataProvider _navData;
    private readonly IMcduReader _reader;
    private readonly IMcduActuator _actuator;
    private readonly RoleManager _roles;
    private readonly ISpeechArbiter _arbiter;
    private readonly IOptionsMonitor<McduOptions> _options;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<McduArrivalChanger> _logger;

    private readonly Action _onRoleChanged;
    private readonly SemaphoreSlim _execGate = new(1, 1);
    private readonly object _gate = new();
    private CancellationTokenSource? _pending;

    public McduArrivalChanger(
        ProcedureSource procedures,
        DfdNavDataProvider navData,
        IMcduReader reader,
        IMcduActuator actuator,
        RoleManager roles,
        ISpeechArbiter arbiter,
        IOptionsMonitor<McduOptions> options,
        JsonlEventLog eventLog,
        ILogger<McduArrivalChanger> logger)
    {
        ArgumentNullException.ThrowIfNull(procedures);
        ArgumentNullException.ThrowIfNull(navData);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(actuator);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _procedures = procedures;
        _navData = navData;
        _reader = reader;
        _actuator = actuator;
        _roles = roles;
        _arbiter = arbiter;
        _options = options;
        _eventLog = eventLog;
        _logger = logger;

        // "my aircraft" (any handover) aborts — the finally backs the TMPY out.
        _onRoleChanged = () => CancelPending();
        _roles.Changed += _onRoleChanged;
    }

    public IEnumerable<string> Phrases => McduArrivalPhrases.Phrases;

    /// <summary>Raw transcription, before the snapper: the digit words in "change arrival
    /// runway zero four left" must survive verbatim — snapping onto the nearest of 288
    /// near-identical phrases could silently change the runway.</summary>
    public bool ValueParse => true;

    public bool TryHandle(string utterance)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return false;
        }

        var text = CommandMatcher.Normalize(utterance);
        if (CancelWords.Contains(text))
        {
            // The cancelled task's own catch speaks — no double acknowledgement here.
            return CancelPending(); // false: nothing pending, let others see the cancel word
        }

        if (!McduArrivalPhrases.TryResolve(text, out var runway))
        {
            return false;
        }

        _ = ChangeArrivalAsync(runway, approachVariant: null, star: null, requirePilotFlying: true);
        return true;
    }

    /// <summary>
    /// Changes the arrival to <paramref name="runway"/> (e.g. "04L"), selecting the best ILS
    /// approach (optionally the <paramref name="approachVariant"/> Y/Z) and optionally a
    /// <paramref name="star"/>. <paramref name="requirePilotFlying"/> enforces the FO-is-PF
    /// gate (true for voice).
    /// </summary>
    public async Task ChangeArrivalAsync(
        string runway, char? approachVariant, string? star, bool requirePilotFlying,
        CancellationToken cancellationToken = default)
    {
        var rwy = McduSpeech.NormalizeRunway(runway);
        if (rwy is null)
        {
            Speak("Unable — I didn't catch the runway.");
            return;
        }

        // Degraded-mode ladder: explain the most fundamental missing piece first.
        if (!_reader.IsDisplayAvailable)
        {
            Speak("Unable — I can't see the M C D U; ProSim isn't connected.");
            _eventLog.Record("mcdu.arrival.refused", new { reason = "no-display", runway = rwy });
            return;
        }

        if (!_actuator.IsArmed)
        {
            Speak("Unable — the M C D U isn't armed.");
            _eventLog.Record("mcdu.arrival.refused", new { reason = "not-armed", runway = rwy });
            return;
        }

        if (requirePilotFlying && !_roles.IsFoPilotFlying)
        {
            Speak("You're pilot flying — the arrival is yours.");
            return;
        }

        // Resolve the arrival airport and validate the runway/approach against the DFD.
        string? airport;
        ApproachOption? target;
        try
        {
            airport = _procedures.Resolve(departure: false).Airport;
            target = PickApproach(_navData.ApproachesForRunway(airport, rwy), approachVariant);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Arrival: resolve/validate failed");
            Speak("Unable — I couldn't validate the arrival.");
            return;
        }

        if (string.IsNullOrWhiteSpace(airport) || target is null)
        {
            Speak($"Unable — I don't have an approach for {McduSpeech.SpeakRunway(rwy)}.");
            _eventLog.Record("mcdu.arrival.unable", new { reason = "no-approach", airport, runway = rwy });
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? previous;
        lock (_gate)
        {
            previous = _pending;
            _pending = cts;
        }

        previous?.Cancel();
        var token = cts.Token;

        await _execGate.WaitAsync(token).ConfigureAwait(false);
        var committed = false;
        try
        {
            Speak($"Changing the arrival to {McduSpeech.SpeakRunway(rwy)}, {target.Spoken}"
                + $"{(star is null ? "" : $", {McduSpeech.SpeakLetters(star)} arrival")}.");
            _eventLog.Record("mcdu.arrival.announce",
                new { airport, runway = rwy, approach = target.Identifier, star });

            await Task.Delay(VerifyPause(), token).ConfigureAwait(false);
            if (requirePilotFlying && !_roles.IsFoPilotFlying)
            {
                Speak("You have control — the arrival is yours.");
                _eventLog.Record("mcdu.arrival.aborted", new { reason = "role-changed", runway = rwy });
                return;
            }

            committed = await RunStateMachineAsync(rwy, target, star, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Speak("Disregard — backing that out.");
            _eventLog.Record("mcdu.arrival.cancelled", new { runway = rwy });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Arrival change failed");
            Speak("Unable — arrival change failed, backing it out.");
        }
        finally
        {
            // SAFETY: never leave a staged TMPY behind. Uncancellable token so an abort
            // still erases.
            if (!committed)
            {
                await EnsureNoTmpyAsync(CancellationToken.None).ConfigureAwait(false);
            }

            _execGate.Release();
            lock (_gate)
            {
                if (ReferenceEquals(_pending, cts))
                {
                    _pending = null;
                }
            }

            cts.Dispose();
        }
    }

    /// <summary>Picks the approach for a change: the requested Y/Z variant when given
    /// (preferring ILS at that variant), else the best ILS, else the provider's first
    /// (already ILS-first ranked). Null when there are no candidates. Pure and testable.</summary>
    public static ApproachOption? PickApproach(IReadOnlyList<ApproachOption> options, char? variant)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Count == 0)
        {
            return null;
        }

        if (variant is { } v)
        {
            var wanted = char.ToUpperInvariant(v);
            var byVariant =
                options.FirstOrDefault(o => o.Variant is { } ov && char.ToUpperInvariant(ov) == wanted
                    && o.Kind.Equals("ILS", StringComparison.OrdinalIgnoreCase))
                ?? options.FirstOrDefault(o => o.Variant is { } ov && char.ToUpperInvariant(ov) == wanted);
            if (byVariant is not null)
            {
                return byVariant;
            }
        }

        return options.FirstOrDefault(o => o.Kind.Equals("ILS", StringComparison.OrdinalIgnoreCase))
            ?? options[0];
    }

    /// <summary>True when a row's data is the target approach. ARCHAEOLOGY (Prosim2FO, 2025):
    /// the DFD identifier "I04LY" shows on the ARRIVAL page as "ILSY04L" — kind spelled out,
    /// variant letter adjacent to the kind or the runway — so matching is by parts, never the
    /// raw identifier. Pure and testable.</summary>
    public static bool MatchesApproach(McduRow row, ApproachOption target, string runway)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(runway);

        var data = row.Data.Replace(" ", "").ToUpperInvariant();
        var runwayKey = runway.Replace(" ", "").ToUpperInvariant();
        if (!data.Contains(runwayKey, StringComparison.Ordinal))
        {
            return false;
        }

        var kind = target.Kind.ToUpperInvariant();
        var kindKey = kind switch
        {
            "RNP" => "RNAV", // the box labels RNP approaches RNAV
            _ => kind,
        };
        if (!data.Contains(kindKey, StringComparison.Ordinal))
        {
            return false;
        }

        // When a specific variant is targeted, require it adjacent to the kind (ILS Y…) or
        // the runway (…Y04L).
        if (target.Variant is { } v)
        {
            var variant = char.ToUpperInvariant(v);
            return data.Contains(kindKey + variant, StringComparison.Ordinal)
                || data.Contains(variant + runwayKey, StringComparison.Ordinal);
        }

        return true;
    }

    public void Dispose()
    {
        _roles.Changed -= _onRoleChanged;
        CancelPending();
        _execGate.Dispose();
    }

    /// <summary>Drives the page flow. Returns true only on a verified INSERT. Any surprise →
    /// false, and the caller erases the TMPY.</summary>
    private async Task<bool> RunStateMachineAsync(
        string rwy, ApproachOption target, string? star, CancellationToken ct)
    {
        // FPLN → LAT REV. ARCHAEOLOGY (Prosim2FO, 2025): the destination is pinned at LSK6L
        // on the F-PLN page, LSK1R on LAT REV opens ARRIVAL, LSK6R inserts the TMPY and
        // LSK6L erases it.
        await _actuator.PressKeyAsync("FPLN", ct).ConfigureAwait(false);
        var page = await _actuator.WaitSettledAsync(ct).ConfigureAwait(false);
        if (page.IsTemporary)
        {
            Fail("a temporary plan is already staged", target, rwy);
            return false;
        }

        await _actuator.PressLskAsync(6, right: false, ct).ConfigureAwait(false); // LSK6L → LAT REV
        page = await _actuator.WaitSettledAsync(ct).ConfigureAwait(false);
        if (!page.Title.Contains("LAT REV", StringComparison.OrdinalIgnoreCase))
        {
            Fail("I couldn't open the lateral revision page", target, rwy);
            return false;
        }

        await _actuator.PressLskAsync(1, right: true, ct).ConfigureAwait(false); // LSK1R → ARRIVAL
        page = await _actuator.WaitSettledAsync(ct).ConfigureAwait(false);
        if (!page.Title.Contains("ARRIVAL", StringComparison.OrdinalIgnoreCase))
        {
            Fail("I couldn't open the arrival page", target, rwy);
            return false;
        }

        // Find and select the approach by on-screen text (scrolling if needed) — stages the TMPY.
        var row = await FindRowAsync(page, r => MatchesApproach(r, target, rwy), ct).ConfigureAwait(false);
        if (row is null)
        {
            Fail("I couldn't find that approach on the arrival page", target, rwy);
            return false;
        }

        await _actuator.PressLskAsync(row.Row, right: false, ct).ConfigureAwait(false);
        page = await _actuator.WaitSettledAsync(ct).ConfigureAwait(false);
        if (!page.IsTemporary)
        {
            Fail("selecting the approach didn't stage a temporary plan", target, rwy);
            return false;
        }

        // Optional STAR on the auto-shown STARS list. Best-effort: not found keeps the
        // runway change rather than failing the whole flow.
        if (!string.IsNullOrWhiteSpace(star))
        {
            var starKey = star.Replace(" ", "");
            var starRow = await FindRowAsync(page,
                r => r.Data.Replace(" ", "").Contains(starKey, StringComparison.OrdinalIgnoreCase), ct)
                .ConfigureAwait(false);
            if (starRow is not null)
            {
                await _actuator.PressLskAsync(starRow.Row, right: false, ct).ConfigureAwait(false);
                page = await _actuator.WaitSettledAsync(ct).ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation("Arrival: STAR {Star} not found on screen — leaving the STAR unchanged", star);
                _eventLog.Record("mcdu.arrival.starNotFound", new { star });
            }
        }

        if (!page.IsTemporary)
        {
            Fail("the temporary plan cleared before I could insert it", target, rwy);
            return false;
        }

        // Announce + cancel window, then INSERT (LSK6R on the TMPY arrival page).
        Speak("Temporary plan staged — inserting.");
        await Task.Delay(VerifyPause(), ct).ConfigureAwait(false);

        await _actuator.PressLskAsync(6, right: true, ct).ConfigureAwait(false); // LSK6R → INSERT*
        page = await _actuator.WaitSettledAsync(ct).ConfigureAwait(false);
        if (page.IsTemporary)
        {
            Fail("the plan didn't commit", target, rwy);
            return false;
        }

        // Cross-check: the F-PLN destination line should now show the new runway.
        await _actuator.PressKeyAsync("FPLN", ct).ConfigureAwait(false);
        var fpln = await _actuator.WaitSettledAsync(ct).ConfigureAwait(false);
        var shows = fpln.Rows.Any(r => r.Data.Replace(" ", "").Contains(rwy, StringComparison.OrdinalIgnoreCase));
        if (!shows)
        {
            Speak($"Inserted, but I can't confirm {McduSpeech.SpeakRunway(rwy)} on the flight plan — please verify.");
            _eventLog.Record("mcdu.arrival.unconfirmed", new { runway = rwy, approach = target.Identifier });
            return true; // committed (the TMPY is gone) — we just couldn't visually re-confirm
        }

        Speak($"Arrival set, {McduSpeech.SpeakRunway(rwy)}, {target.Spoken}.");
        _eventLog.Record("mcdu.arrival.set", new { runway = rwy, approach = target.Identifier, star });
        return true;
    }

    /// <summary>Finds a row matching <paramref name="predicate"/> on the current page,
    /// scrolling down when the page allows it.</summary>
    private async Task<McduRow?> FindRowAsync(
        McduPage page, Func<McduRow, bool> predicate, CancellationToken ct)
    {
        for (var i = 0; i <= MaxScroll; i++)
        {
            var hit = page.Rows.FirstOrDefault(r => !r.IsEmpty && predicate(r));
            if (hit is not null)
            {
                return hit;
            }

            if (!page.CanScrollDown)
            {
                return null;
            }

            await _actuator.PressKeyAsync("ARROW_DOWN", ct).ConfigureAwait(false);
            page = await _actuator.WaitSettledAsync(ct).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>Backs out any staged temporary plan (ERASE at LSK6L on the TMPY F-PLN page).
    /// Uncancellable cleanup — it must run even mid-abort.</summary>
    private async Task EnsureNoTmpyAsync(CancellationToken ct)
    {
        try
        {
            if (!_reader.Read().IsTemporary)
            {
                return;
            }

            await _actuator.PressKeyAsync("FPLN", ct).ConfigureAwait(false);
            var page = await _actuator.WaitSettledAsync(ct).ConfigureAwait(false);
            if (!page.IsTemporary)
            {
                return; // already gone
            }

            await _actuator.PressLskAsync(6, right: false, ct).ConfigureAwait(false); // ERASE
            var after = await _actuator.WaitSettledAsync(ct).ConfigureAwait(false);
            _eventLog.Record("mcdu.arrival.erased", new { stillTemporary = after.IsTemporary });
            if (after.IsTemporary)
            {
                _logger.LogWarning("Arrival back-out: TMPY still present after ERASE");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Arrival TMPY back-out failed");
        }
    }

    private void Fail(string why, ApproachOption target, string rwy)
    {
        Speak($"Unable — {why}. Backing it out.");
        _eventLog.Record("mcdu.arrival.unable", new { reason = why, runway = rwy, approach = target.Identifier });
    }

    private TimeSpan VerifyPause()
        => TimeSpan.FromSeconds(Math.Max(0, _options.CurrentValue.VerifyPauseSeconds));

    private bool CancelPending()
    {
        CancellationTokenSource? pending;
        lock (_gate)
        {
            pending = _pending;
            _pending = null;
        }

        pending?.Cancel();
        return pending is not null;
    }

    private void Speak(string text)
        => _ = _arbiter.EnqueueAsync(new SpeechRequest(text, SpeechPriority.Normal, Tag: "mcdu"));
}
