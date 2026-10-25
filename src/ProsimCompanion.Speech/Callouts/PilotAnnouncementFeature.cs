using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.VoiceReference;
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
        // 2026-09-13 EGLL→LIRF: the router fuzzy-matched the ASR junctions to the two-word
        // forms at 0.74–0.77 and asked "did you mean loc star?" instead of dispatching, so
        // the junctions and the "capture" phrasing are catalogue phrases in their own right
        // (exact match = 1.0). "alt star" is the ALT* capture the same leg rejected outright.
        "lockstar", "locstar", "glidestar",
        "localizer capture", "loc capture", "glideslope capture", "glide slope capture",
        "alt star", "altstar", "altitude star", "alt capture",
        // 2026-10-11 EFHK→LKPR (issue #177): "AntFlex SRS Runway NavBlue." scored 0.80
        // against "man flex srs runway nav blue" and earned a clarifier DURING THE ROLL;
        // "Glideslap capture lock" scored 0.72. The junctions are catalogue phrases now.
        "antflex", "ant flex", "antflex srs runway navblue", "ant flex srs runway nav blue",
        "glideslap", "glide slop", "glideslap capture", "glide slop capture", "glideslap star", "glide slop star",
        "manual flight", "manual control", "continue",
        // Owner request 2026-10-25: the visual calls on the approach ("Runway Insight" was the
        // ASR junction that earned a did-you-mean on the EKCH→EFHK leg).
        "runway in sight", "runway insight", "airfield in sight", "field in sight",
        "approach lights in sight", "lights in sight", "runway",
    ];

    internal enum AnnouncementKind
    {
        TakeoffFma,
        CaptureFma,
        ManualFlight,
        Continue,

        /// <summary>"Runway in sight" / "airfield in sight" / a bare "runway" on the approach.</summary>
        RunwayInSight,
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

    /// <summary>Roster declaration (see <see cref="VoiceFeatureDeclaration"/>): PF announcement
    /// acknowledgements (issue #127) — verbal only, phase-gated, never writes; last of the PM
    /// calls, before the MCDU trio.</summary>
    public static VoiceFeatureDeclaration Declared { get; } = new()
    {
        Precedence = 110,
        Gate = VoiceFeatureGate.AlwaysOn,
        Description = new DescribedVoiceReference("announcements", "voiceReference.group.announcements.title", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer,
        [
            new(["man flex srs runway nav blue", "man toga srs runway nav blue", "man flex", "man toga"],
                "voiceReference.group.announcements.fma", "Checked.", [VoiceBadges.Phase]),
            new(["localizer star", "loc star", "glideslope star", "glide slope star", "lockstar", "locstar", "glidestar",
                 "localizer capture", "loc capture", "glideslope capture", "glide slope capture"],
                "voiceReference.group.announcements.capture", "Checked.", [VoiceBadges.Phase]),
            new(["alt star", "altstar", "altitude star", "alt capture"], "voiceReference.group.announcements.altCapture", "Checked.", [VoiceBadges.Phase]),
            new(["manual flight", "manual control"], "voiceReference.group.announcements.manualFlight", "Manual flight."),
            new(["runway in sight", "airfield in sight", "runway"], "voiceReference.group.announcements.runwayInSight", "Checked.", [VoiceBadges.Phase]),
            new(["continue"], "voiceReference.group.announcements.continue", "Continue.", [VoiceBadges.Phase]),
        ]),
    };

    VoiceFeatureDeclaration IVoiceFeature.Declaration => Declared;

    public bool Enabled => true;

    public IEnumerable<string> Phrases => BiasPhrases;

    public bool ValueParse => false;

    /// <summary>Words only — "Checked." / an echo; nothing here touches the aircraft, so the
    /// router may fire a gray-band match without the clarifier in a sterile moment (#177).</summary>
    public bool AcknowledgeOnly => true;

    /// <summary>Pure classification — exposed for tests. The ASR runs words together
    /// ("Manflex SRS Runway Navblue", "Lockstar" on 2026-09-06; "AntFlex", "Glideslap" on
    /// 2026-10-11), so FMA matching is substring-based over the normalized text, while the
    /// decision calls stay exact.</summary>
    internal static AnnouncementKind? Classify(string normalized, FlightPhase phase)
    {
        if (phase is FlightPhase.TakeoffRoll or FlightPhase.InitialClimb
            && (normalized.Contains("man flex", StringComparison.Ordinal)
                || normalized.Contains("manflex", StringComparison.Ordinal)
                || normalized.Contains("ant flex", StringComparison.Ordinal)
                || normalized.Contains("antflex", StringComparison.Ordinal)
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
                || normalized.Contains("glidestar", StringComparison.Ordinal)
                || normalized.Contains("localizer capture", StringComparison.Ordinal)
                || normalized.Contains("loc capture", StringComparison.Ordinal)
                || normalized.Contains("glideslope capture", StringComparison.Ordinal)
                || normalized.Contains("glide slope capture", StringComparison.Ordinal)
                || normalized.Contains("glideslap", StringComparison.Ordinal)
                || normalized.Contains("glide slop", StringComparison.Ordinal)
                || normalized.Contains("alt star", StringComparison.Ordinal)
                || normalized.Contains("altstar", StringComparison.Ordinal)
                || normalized.Contains("altitude star", StringComparison.Ordinal)
                || normalized.Contains("alt capture", StringComparison.Ordinal)))
        {
            return AnnouncementKind.CaptureFma;
        }

        if (normalized.Equals("manual flight", StringComparison.Ordinal)
            || normalized.Equals("manual control", StringComparison.Ordinal))
        {
            return AnnouncementKind.ManualFlight;
        }

        // Visual acquisition calls (owner request 2026-10-25) — descent and approach only, so
        // "runway" keeps its ordinary meaning in a taxi instruction read-back elsewhere.
        if (phase is FlightPhase.Descent or FlightPhase.Approach
            && (normalized.Contains("in sight", StringComparison.Ordinal)
                || normalized.Contains("insight", StringComparison.Ordinal)
                || normalized.Equals("runway", StringComparison.Ordinal)))
        {
            return AnnouncementKind.RunwayInSight;
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
