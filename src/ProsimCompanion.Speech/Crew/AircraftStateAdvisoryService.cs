using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;

namespace ProsimCompanion.Speech.Crew;

/// <summary>
/// Speaks the FO's cold-and-dark advisory (issue #63): when the session-start aircraft state
/// check finds mismatches on a fresh departure, one spoken line lists the first few
/// discrepancies ("Captain, the aircraft is not in the expected cold and dark state — battery
/// one is on, and the parking brake is off."). Cross-pillar seam is the Core
/// <see cref="GsxDiagnosticsStore"/> the check publishes into — this service only subscribes
/// and reads; the announce policy (fresh departure + option) is already decided in the
/// verdict's <see cref="AircraftStateCheckView.Announce"/> flag, so nothing is re-derived
/// here. One advisory per verdict, keyed by its timestamp — the store fires Changed for many
/// unrelated updates and the FO must not repeat herself on each of them.
/// </summary>
public sealed class AircraftStateAdvisoryService : Core.Hosting.IStartupModule, IDisposable
{
    /// <summary>Spoken discrepancies before the remainder is summarized — matches the pure
    /// assessor's constant; a full recital of a 15-item definition would be noise.</summary>
    private const int SpokenLimit = 3;

    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<AircraftStateAdvisoryService> _logger;
    private readonly object _gate = new();
    private DateTimeOffset? _lastSpokenVerdict;

    public AircraftStateAdvisoryService(
        GsxDiagnosticsStore diagnostics,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<AircraftStateAdvisoryService> logger)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _diagnostics = diagnostics;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
    }

    public void Start() => _diagnostics.Changed += OnDiagnosticsChanged;

    public void Dispose() => _diagnostics.Changed -= OnDiagnosticsChanged;

    private void OnDiagnosticsChanged(object? sender, EventArgs e)
    {
        try
        {
            var check = _diagnostics.Snapshot().AircraftStateCheck;
            if (check is null || check.Status != AircraftStateCheckStatus.Mismatch || !check.Announce
                || check.Mismatches.Count == 0)
            {
                return;
            }

            lock (_gate)
            {
                if (_lastSpokenVerdict == check.Timestamp)
                {
                    return;
                }

                _lastSpokenVerdict = check.Timestamp;
            }

            var text = ComposeAdvisory([.. check.Mismatches.Select(mismatch => mismatch.Phrase)]);
            _eventLog.Record("fo.aircraft-state-advisory", new { text });
            _ = SpeakAsync(text);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Aircraft state advisory handling failed");
        }
    }

    /// <summary>Builds the single spoken line: up to <see cref="SpokenLimit"/> phrases joined
    /// naturally, with any remainder summarized as a count. Exposed for tests.</summary>
    public static string ComposeAdvisory(IReadOnlyList<string> phrases)
    {
        ArgumentNullException.ThrowIfNull(phrases);

        var spoken = phrases.Take(SpokenLimit).ToList();
        var remainder = phrases.Count - spoken.Count;
        var joined = spoken.Count switch
        {
            0 => "several switches are out of position",
            1 => spoken[0],
            2 => $"{spoken[0]}, and {spoken[1]}",
            _ => string.Join(", ", spoken[..^1]) + $", and {spoken[^1]}",
        };
        var tail = remainder > 0
            ? $", and {remainder} more item{(remainder == 1 ? "" : "s")}"
            : "";
        return $"Captain, the aircraft is not in the expected cold and dark state — {joined}{tail}.";
    }

    private async Task SpeakAsync(string text)
    {
        try
        {
            // Advisory band with a shelf life: pointless to hear it after the pilot has been
            // reconfiguring the aircraft for minutes anyway.
            await _arbiter.EnqueueAsync(new SpeechRequest(
                text, SpeechPriority.Normal, Ttl: TimeSpan.FromMinutes(2),
                Tag: "fo.aircraft-state")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Aircraft state advisory could not be spoken");
        }
    }
}
