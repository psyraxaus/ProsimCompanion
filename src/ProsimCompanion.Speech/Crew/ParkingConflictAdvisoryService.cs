using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;

namespace ProsimCompanion.Speech.Crew;

/// <summary>
/// Speaks the FO's parking-conflict guidance (issue #44): when GSX does not recognize the
/// aircraft's position (parking-change menu up, session gate unknown — EGLL Stand 547,
/// 2026-08-22), the pilot's instinct is to restart the app, which cannot help. One spoken
/// line names the facility GSX itself offers and the two actions that actually resolve the
/// state. Same shape as <see cref="AircraftStateAdvisoryService"/>: observes the Core
/// diagnostics store, speaks each conflict timestamp once, zero policy of its own.
/// </summary>
public sealed class ParkingConflictAdvisoryService : Core.Hosting.IStartupModule, IDisposable
{
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<ParkingConflictAdvisoryService> _logger;
    private readonly object _gate = new();
    private DateTimeOffset? _lastSpokenConflict;
    private IDisposable? _subscription;

    public ParkingConflictAdvisoryService(
        GsxDiagnosticsStore diagnostics,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<ParkingConflictAdvisoryService> logger)
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

    public void Start() => _subscription = _diagnostics.Observe(OnDiagnosticsChanged);

    public void Dispose() => _subscription?.Dispose();

    private void OnDiagnosticsChanged(GsxDiagnosticsSnapshot snapshot)
    {
        try
        {
            var conflict = snapshot.ParkingConflict;
            if (conflict is null)
            {
                return;
            }

            lock (_gate)
            {
                if (_lastSpokenConflict == conflict.Timestamp)
                {
                    return;
                }

                _lastSpokenConflict = conflict.Timestamp;
            }

            var text = ComposeAdvisory(conflict.GsxFacility);
            _eventLog.Record("fo.parking-conflict-advisory", new { text });
            _ = SpeakAsync(text);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Parking conflict advisory handling failed");
        }
    }

    /// <summary>The single spoken line. The facility string is cleaned for speech — the
    /// stand-range parentheses and the Safedock trademark sign are display furniture, not
    /// something the FO should read out. Exposed for tests.</summary>
    public static string ComposeAdvisory(string gsxFacility)
    {
        ArgumentNullException.ThrowIfNull(gsxFacility);

        var spoken = SpeakableFacility(gsxFacility);
        var offer = spoken.Length > 0 ? $" It offers {spoken}." : "";
        return "Captain, GSX doesn't recognise our parking position — restarting won't help."
            + offer
            + " Select the stand in the GSX menu, or reposition the aircraft.";
    }

    /// <summary>Strips parenthesized ranges and non-name symbols:
    /// "Terminal 5B (531-548) Stand 547 with Safedock©" → "Terminal 5B Stand 547 with Safedock".</summary>
    internal static string SpeakableFacility(string facility)
    {
        var result = new System.Text.StringBuilder(facility.Length);
        var depth = 0;
        foreach (var ch in facility)
        {
            if (ch == '(')
            {
                depth++;
            }
            else if (ch == ')')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth == 0 && (char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '|'))
            {
                result.Append(ch == '|' ? ' ' : ch);
            }
        }

        return string.Join(' ', result.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private async Task SpeakAsync(string text)
    {
        try
        {
            // Advisory band with a shelf life: after minutes of the pilot working the GSX
            // menus this line is stale context, not information.
            await _arbiter.EnqueueAsync(new SpeechRequest(
                text, SpeechPriority.Normal, Ttl: TimeSpan.FromMinutes(2),
                Tag: "fo.parking-conflict")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Parking conflict advisory could not be spoken");
        }
    }
}
