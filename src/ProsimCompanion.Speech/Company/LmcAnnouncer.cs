using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft.WeightAndBalance;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Hosting;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Persona;

namespace ProsimCompanion.Speech.Company;

/// <summary>
/// The FO reads a within-tolerance last-minute change (issue #181) when one lands on the
/// loadsheet store: "Last minute change: minus two passengers, zero fuel weight 58.9 tonnes …
/// Within limits, no new loadsheet. Update the FMS zero fuel weight when ready, captain." The
/// line is deterministic (<see cref="LmcEvaluator.SpokenWithinLimits"/>) and, with the persona
/// on, restyled under the number verifier with that line's figures as the allowed set. A change
/// beyond tolerance is not read here — its figures ride in the captain's question prompt, so
/// the question never talks over a separate announcement. Each LMC is spoken once (by its
/// evaluation stamp); an on-demand re-check with "no change" says so briefly.
/// </summary>
public sealed class LmcAnnouncer : IStartupModule, IDisposable
{
    private readonly LoadsheetStore _store;
    private readonly ISpeechArbiter _arbiter;
    private readonly StyledSpeechService? _styled;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<LmcAnnouncer> _logger;
    private IDisposable? _watch;
    private DateTimeOffset? _spokenFor;

    public LmcAnnouncer(
        LoadsheetStore store,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<LmcAnnouncer> logger,
        StyledSpeechService? styled = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
        _styled = styled;
    }

    public void Start() => _watch = _store.Observe(snapshot => _ = OnSnapshotAsync(snapshot));

    public void Dispose() => _watch?.Dispose();

    /// <summary>The line for one LMC snapshot, or null when nothing is to be said — pure, for
    /// the tests: within tolerance speaks the figures, an on-demand "no change" says so,
    /// everything else (awaiting the captain, revised, flown on authority) is somebody else's
    /// line.</summary>
    public static string? LineFor(LoadsheetLmc lmc)
    {
        ArgumentNullException.ThrowIfNull(lmc);
        return lmc.Disposition switch
        {
            LmcDisposition.Applied => LmcEvaluator.SpokenWithinLimits(lmc.Result),
            LmcDisposition.NoChange => "Boarding matches the final loadsheet. No last minute change.",
            _ => null,
        };
    }

    private async Task OnSnapshotAsync(LoadsheetSnapshot snapshot)
    {
        try
        {
            if (snapshot.Lmc is not { } lmc || lmc.EvaluatedAtUtc == _spokenFor)
            {
                return;
            }

            if (LineFor(lmc) is not { } line)
            {
                return;
            }

            _spokenFor = lmc.EvaluatedAtUtc;
            var text = _styled is null
                ? line
                : await _styled.StyleAsync(line, PersonaStyleCategory.Advisory, $"lmc:{lmc.EvaluatedAtUtc:O}").ConfigureAwait(false);
            _eventLog.Record("loadsheet.lmc.spoken", new { text, disposition = lmc.Disposition.ToString() });
            await _arbiter.EnqueueAsync(new SpeechRequest(text, SpeechPriority.Normal, Tag: "loadsheet.lmc")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Last-minute change announcement failed");
        }
    }
}
