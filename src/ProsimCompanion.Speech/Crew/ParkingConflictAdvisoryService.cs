using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;

namespace ProsimCompanion.Speech.Crew;

/// <summary>
/// Speaks the FO's parking-conflict guidance (issue #44): when GSX does not recognize the
/// aircraft's position (parking-change menu up, session gate unknown — EGLL Stand 547,
/// 2026-08-22), the pilot's instinct is to restart the app, which cannot help. One spoken
/// line names the facility GSX itself offers and the two actions that actually resolve the
/// state. Same shape as <see cref="AircraftStateAdvisoryService"/>: observes the Core
/// diagnostics store, speaks each conflict timestamp once, zero policy of its own — except
/// the one physical precondition (2026-09-20 EGLL, spoken on the landing roll): the guidance
/// only makes sense for a parked aircraft with engines off, so a conflict published while
/// moving is held and spoken once parked, if it still stands then.
/// </summary>
public sealed class ParkingConflictAdvisoryService : Core.Hosting.IStartupModule, IDisposable
{
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly IFlightPhaseSource _flightState;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<ParkingConflictAdvisoryService> _logger;
    private readonly object _gate = new();
    private DateTimeOffset? _lastSpokenConflict;
    private string? _lastSpokenText;
    private DateTimeOffset? _lastSpokenAtUtc;
    private GsxParkingConflictView? _heldWhileMoving;
    private IDisposable? _subscription;

    public ParkingConflictAdvisoryService(
        GsxDiagnosticsStore diagnostics,
        IFlightPhaseSource flightState,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<ParkingConflictAdvisoryService> logger)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(flightState);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _diagnostics = diagnostics;
        _flightState = flightState;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
    }

    public void Start()
    {
        _subscription = _diagnostics.Observe(OnDiagnosticsChanged);
        _flightState.PhaseChanged += OnPhaseChanged;
    }

    public void Dispose()
    {
        _flightState.PhaseChanged -= OnPhaseChanged;
        _subscription?.Dispose();
    }

    private void OnDiagnosticsChanged(GsxDiagnosticsSnapshot snapshot)
    {
        try
        {
            var conflict = snapshot.ParkingConflict;
            if (conflict is null)
            {
                lock (_gate)
                {
                    _heldWhileMoving = null;
                }
                return;
            }

            if (!ParkingConflictGate.IsParkedEnginesOff(_flightState.Snapshot().Data))
            {
                bool newlyHeld;
                lock (_gate)
                {
                    newlyHeld = _heldWhileMoving?.Timestamp != conflict.Timestamp;
                    _heldWhileMoving = conflict;
                }

                if (newlyHeld)
                {
                    _logger.LogInformation("Parking conflict published while moving or with engines running — advisory held until parked");
                    _eventLog.Record("fo.parking-conflict-held", new { reason = "aircraft moving or engines running" });
                }
                return;
            }

            Speak(conflict);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Parking conflict advisory handling failed");
        }
    }

    /// <summary>A held conflict is re-checked when the phase settles at the gate: spoken only
    /// if the SAME conflict still stands — GSX identifying the parking on shutdown clears it
    /// first, and then there is nothing to say.</summary>
    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        try
        {
            GsxParkingConflictView? held;
            lock (_gate)
            {
                held = _heldWhileMoving;
            }

            if (held is null || !e.Current.IsAtGate())
            {
                return;
            }

            if (!ParkingConflictGate.IsParkedEnginesOff(_flightState.Snapshot().Data))
            {
                return;
            }

            var standing = _diagnostics.Snapshot().ParkingConflict;
            lock (_gate)
            {
                _heldWhileMoving = null;
            }

            if (standing is not null && standing.Timestamp == held.Timestamp)
            {
                Speak(standing);
            }
            else
            {
                _logger.LogInformation("Held parking conflict no longer stands after parking — advisory dropped");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Held parking conflict follow-up failed");
        }
    }

    private void Speak(GsxParkingConflictView conflict)
    {
        var text = ComposeAdvisory(conflict.GsxFacility);
        lock (_gate)
        {
            if (_lastSpokenConflict == conflict.Timestamp)
            {
                return;
            }

            // One episode, one line (#121, 2026-09-05 EGLL): the prep hold published the
            // conflict, released it sixteen seconds later when the reposition remedy
            // started, and the position-select menu republished it — two identical
            // advisories for one standing problem. A clear-then-republish inside the
            // cooldown is the same episode unless the guidance itself changed (a facility
            // GSX now names is new information and still speaks).
            if (IsRepeatWithinCooldown(_lastSpokenText, _lastSpokenAtUtc, text, DateTimeOffset.UtcNow))
            {
                _lastSpokenConflict = conflict.Timestamp;
                _logger.LogDebug("Parking conflict republished within the cooldown — advisory not repeated");
                return;
            }

            _lastSpokenConflict = conflict.Timestamp;
            _lastSpokenText = text;
            _lastSpokenAtUtc = DateTimeOffset.UtcNow;
        }

        _eventLog.Record("fo.parking-conflict-advisory", new { text });
        _ = SpeakAsync(text);
    }

    /// <summary>Episode cooldown for re-published conflicts (#121). Pure — exposed for tests.
    /// A different spoken text (GSX now names a facility) is never a repeat.</summary>
    internal static bool IsRepeatWithinCooldown(
        string? lastText, DateTimeOffset? lastAtUtc, string text, DateTimeOffset nowUtc)
        => lastText is not null
            && lastAtUtc is not null
            && string.Equals(lastText, text, StringComparison.Ordinal)
            && nowUtc - lastAtUtc < RepeatCooldown;

    /// <summary>Long enough to bridge a hold-release/menu republish churn at one stand; short
    /// enough that a genuinely new conflict at the arrival airport hours later still speaks.</summary>
    internal static readonly TimeSpan RepeatCooldown = TimeSpan.FromMinutes(10);

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
