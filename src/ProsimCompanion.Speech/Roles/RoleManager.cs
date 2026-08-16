using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Roles;

/// <summary>
/// PF/PM role state. The FO acts on the FCU/MCDU/radios only while it is pilot flying.
/// Handover by voice: "you have control"/"your aircraft" gives the FO control — instantly on
/// the ground, but confirm-gated while airborne ("Confirm — I have control?" answered within
/// <see cref="ConfirmWindow"/> by "confirm control" et al.) so a mis-recognition can never
/// take the aircraft mid-flight (predecessor rule). Taking it back ("my aircraft"/
/// "i have control"/…) is always instant and ungated — the human can always reclaim the
/// aircraft. While the FO is pilot flying it also answers the user's PM calls verbally
/// ("positive climb" → "Gear up.") — verbal only, it never actuates anything from a call.
/// Defaults to the USER flying.
/// </summary>
public sealed class RoleManager : IVoiceFeature
{
    private static readonly string[] GiveControl =
        ["you have control", "your aircraft", "your controls", "you have the aircraft"];
    private static readonly string[] TakeControl =
        ["my aircraft", "i have control", "i have the aircraft", "i have the controls"];
    private static readonly string[] ConfirmControl =
        ["confirm control", "control confirmed", "confirm i have control"];

    // FO-as-PF verbal replies to the user's PM calls — spoken acknowledgements only, applied
    // solely while the FO is pilot flying (predecessor semantics; the gear itself stays with
    // the crew's normal flows — a reply must never actuate).
    private static readonly Dictionary<string, string> PmCallReplies = new(StringComparer.Ordinal)
    {
        ["one hundred knots"] = "Checked.",
        ["hundred knots"] = "Checked.",
        ["positive climb"] = "Gear up.",
        ["positive rate"] = "Gear up.",
    };

    /// <summary>How long an airborne handover waits for its "confirm control" (predecessor: 12 s).</summary>
    internal static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(12);

    private readonly ISpeechArbiter _arbiter;
    private readonly IFlightPhaseSource _flight;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<RoleManager> _logger;
    private readonly object _gate = new();

    private long _pendingConfirmUntilTick; // >0 while an airborne handover awaits confirmation

    public RoleManager(
        ISpeechArbiter arbiter,
        IFlightPhaseSource flight,
        JsonlEventLog eventLog,
        ILogger<RoleManager> logger)
    {
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _arbiter = arbiter;
        _flight = flight;
        _eventLog = eventLog;
        _logger = logger;
    }

    /// <summary>Raised on every handover (any thread).</summary>
    public event Action? Changed;

    public bool IsFoPilotFlying { get; private set; }

    public bool Enabled => true;

    public IEnumerable<string> Phrases =>
        [.. GiveControl, .. TakeControl, .. ConfirmControl, .. PmCallReplies.Keys];

    public bool ValueParse => false;

    public bool TryHandle(string utterance)
    {
        var text = CommandMatcher.Normalize(utterance);

        // Taking control back is ALWAYS instant and ungated — the human is in command, and a
        // pending airborne handover is abandoned by reclaiming the aircraft.
        if (TakeControl.Contains(text))
        {
            lock (_gate)
            {
                _pendingConfirmUntilTick = 0;
            }

            SetFoFlying(false);
            _ = _arbiter.SpeakAsync("You have control.", SpeechPriority.High);
            return true;
        }

        if (GiveControl.Contains(text))
        {
            if (IsAirborne())
            {
                lock (_gate)
                {
                    _pendingConfirmUntilTick =
                        Environment.TickCount64 + (long)ConfirmWindow.TotalMilliseconds;
                }

                _logger.LogInformation("Airborne handover to FO — awaiting confirmation");
                _eventLog.Record("roles.confirm-requested", new { });
                _ = _arbiter.SpeakAsync("Confirm — I have control?", SpeechPriority.High);
                return true;
            }

            SetFoFlying(true);
            _ = _arbiter.SpeakAsync("I have control.", SpeechPriority.High);
            return true;
        }

        if (ConfirmControl.Contains(text))
        {
            bool pending;
            lock (_gate)
            {
                pending = _pendingConfirmUntilTick > 0
                    && Environment.TickCount64 < _pendingConfirmUntilTick;
                _pendingConfirmUntilTick = 0;
            }

            if (!pending)
            {
                return false; // no handover awaiting confirmation — not ours
            }

            SetFoFlying(true);
            _ = _arbiter.SpeakAsync("I have control.", SpeechPriority.High);
            return true;
        }

        // PF verbal replies to the user's PM calls — only meaningful while the FO is flying;
        // otherwise the phrase falls through (the user may be answering their own callout).
        if (IsFoPilotFlying && PmCallReplies.TryGetValue(text, out var reply))
        {
            _eventLog.Record("roles.pf-reply", new { call = text, reply });
            _ = _arbiter.SpeakAsync(reply, SpeechPriority.High);
            return true;
        }

        return false;
    }

    /// <summary>Airborne per the predecessor's definition — every in-flight phase; ground
    /// phases (and Unknown) hand over without ceremony.</summary>
    private bool IsAirborne()
        => _flight.CurrentPhase is FlightPhase.InitialClimb or FlightPhase.Climb
            or FlightPhase.Cruise or FlightPhase.Descent or FlightPhase.Approach;

    private void SetFoFlying(bool foFlying)
    {
        if (IsFoPilotFlying == foFlying)
        {
            return;
        }

        IsFoPilotFlying = foFlying;
        _logger.LogInformation("Pilot flying: {Who}", foFlying ? "FO" : "user");
        _eventLog.Record("roles.handover", new { foFlying });
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Role change handler threw");
        }
    }
}
