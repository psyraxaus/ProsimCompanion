using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Callouts;

/// <summary>
/// Acknowledges the pilot-flying's SOP announcements (issue #127, verbal only — the remainder
/// of #67's FMA gap): the takeoff FMA readout ("man flex, SRS, runway, nav blue"), approach
/// capture calls ("localizer star", "glideslope star"), the "manual flight" handling
/// announcement, and the "continue" decision at minimums. FMA reads get the real PM's one-word
/// "Checked."; the decision calls are echoed. Before this feature every one of these earned a
/// reject chirp or a silent absorb (2026-09-06 leg: four rejects). Phase-gated so the words
/// keep their ordinary meanings elsewhere — an unmatched phase returns false and the utterance
/// routes on. Never writes to the aircraft.
/// </summary>
public sealed class PilotAnnouncementFeature : IVoiceFeature
{
    /// <summary>Grammar-bias forms; matching in <see cref="Classify"/> is token-based, so ASR
    /// junctions ("Manflex", "Lockstar") still land.</summary>
    private static readonly string[] BiasPhrases =
    [
        "man flex srs runway nav blue", "man toga srs runway nav blue", "man flex", "man toga",
        "localizer star", "loc star", "glideslope star", "glide slope star",
        "manual flight", "continue",
    ];

    internal enum AnnouncementKind
    {
        TakeoffFma,
        CaptureFma,
        ManualFlight,
        Continue,
    }

    private readonly ISpeechArbiter _arbiter;
    private readonly IFlightPhaseSource _flight;
    private readonly JsonlEventLog _eventLog;

    public PilotAnnouncementFeature(
        ISpeechArbiter arbiter,
        IFlightPhaseSource flight,
        JsonlEventLog eventLog)
    {
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(eventLog);

        _arbiter = arbiter;
        _flight = flight;
        _eventLog = eventLog;
    }

    public bool Enabled => true;

    public IEnumerable<string> Phrases => BiasPhrases;

    public bool ValueParse => false;

    /// <summary>Pure classification — exposed for tests. The ASR runs words together
    /// ("Manflex SRS Runway Navblue", "Lockstar" on 2026-09-06), so FMA matching is
    /// substring-based over the normalized text, while the decision calls stay exact.</summary>
    internal static AnnouncementKind? Classify(string normalized, FlightPhase phase)
    {
        if (phase is FlightPhase.TakeoffRoll or FlightPhase.InitialClimb
            && (normalized.Contains("man flex", StringComparison.Ordinal)
                || normalized.Contains("manflex", StringComparison.Ordinal)
                || normalized.Contains("man toga", StringComparison.Ordinal)
                || normalized.Contains("srs", StringComparison.Ordinal)
                || normalized.Contains("nav blue", StringComparison.Ordinal)
                || normalized.Contains("navblue", StringComparison.Ordinal)))
        {
            return AnnouncementKind.TakeoffFma;
        }

        if (phase is FlightPhase.Descent or FlightPhase.Approach
            && (normalized.Contains("localizer star", StringComparison.Ordinal)
                || normalized.Contains("loc star", StringComparison.Ordinal)
                || normalized.Contains("lockstar", StringComparison.Ordinal)
                || normalized.Contains("locstar", StringComparison.Ordinal)
                || normalized.Contains("glideslope star", StringComparison.Ordinal)
                || normalized.Contains("glide slope star", StringComparison.Ordinal)
                || normalized.Contains("glidestar", StringComparison.Ordinal)))
        {
            return AnnouncementKind.CaptureFma;
        }

        if (normalized.Equals("manual flight", StringComparison.Ordinal))
        {
            return AnnouncementKind.ManualFlight;
        }

        // Only the approach decision window — a bare "continue" during a held checklist never
        // reaches this feature (the router's resume path owns it there).
        if (phase is FlightPhase.Approach && normalized.Equals("continue", StringComparison.Ordinal))
        {
            return AnnouncementKind.Continue;
        }

        return null;
    }

    public bool TryHandle(string utterance)
    {
        var kind = Classify(CommandMatcher.Normalize(utterance), _flight.CurrentPhase);
        if (kind is null)
        {
            return false;
        }

        var response = kind switch
        {
            AnnouncementKind.ManualFlight => "Manual flight.",
            AnnouncementKind.Continue => "Continue.",
            _ => "Checked.",
        };
        _eventLog.Record("pilot.announcement", new { kind = kind.ToString(), response });
        _ = _arbiter.EnqueueAsync(new SpeechRequest(
            response, SpeechPriority.Normal, Ttl: TimeSpan.FromSeconds(10), Tag: "fo.announce"));
        return true;
    }
}
