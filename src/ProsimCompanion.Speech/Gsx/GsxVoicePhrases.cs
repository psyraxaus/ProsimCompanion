namespace ProsimCompanion.Speech.Gsx;

/// <summary>One phrase → command mapping. <see cref="SuccessPhrase"/> is the FO's confirmation
/// on a single-shot phrase; <see cref="CrewAckPhrase"/> is what the hailed crew says instead
/// when the request came through a hail dialogue (issue #51). <see cref="CabinAck"/> marks the
/// cabin-flavored boarding phrases that add the purser's "Boarding underway."</summary>
public sealed record GsxVoiceBinding(
    string Command,
    string SuccessPhrase,
    string CrewAckPhrase,
    bool CabinAck = false);

/// <summary>
/// THE phrase→command catalog for GSX voice control — one table shared by the single-shot
/// <see cref="GsxVoiceService"/> and the hail dialogues (<see cref="Crew.CrewHailService"/>),
/// so adding a service phrase is one line and the two paths can never drift apart.
/// "Cockpit to ground" is deliberately absent: it is the hail, not a command (ADR-0006).
/// </summary>
public static class GsxVoicePhrases
{
    /// <summary>Phrases that start the departure sequence (and release the voice-mode ground
    /// prep gate) — mapped to gsx.startDepartureServices at dispatch time.</summary>
    public static readonly IReadOnlyList<string> StartGroundServicesPhrases =
        // "begin" added 2026-09-06 (issue #127): the owner's natural phrasing needed a
        // did-you-mean round-trip every departure.
        ["commence ground services", "start ground services", "begin ground services"];

    /// <summary>Cancel words accepted inside a hail's listening window.</summary>
    public static readonly IReadOnlyList<string> CancelPhrases =
        ["disregard", "cancel", "cancel that", "nothing", "never mind"];

    public static IReadOnlyDictionary<string, GsxVoiceBinding> Bindings { get; } =
        new Dictionary<string, GsxVoiceBinding>(StringComparer.OrdinalIgnoreCase)
        {
            ["call the next service"] = new(
                "gsx.forceNextService", "Calling the next service.", "Copied — calling the next service."),
            ["next service"] = new(
                "gsx.forceNextService", "Calling the next service.", "Copied — calling the next service."),
            ["request boarding"] = new(
                "gsx.requestBoarding", "Boarding requested.", "Copied — we'll start boarding."),
            ["start boarding"] = new(
                "gsx.requestBoarding", "Boarding requested.", "Copied — we'll start boarding.", CabinAck: true),
            ["cabin crew start boarding"] = new(
                "gsx.requestBoarding", "Boarding requested.", "Copied — we'll start boarding.", CabinAck: true),
            ["request refueling"] = new(
                "gsx.requestRefuel", "Refueling requested.", "Copied — fuel truck on the way."),
            ["call the fuel truck"] = new(
                "gsx.requestRefuel", "Refueling requested.", "Copied — fuel truck on the way."),
            // Real-world SOP (2026-09-19): the captain confirms the block fuel after their own
            // considerations, and only then is the truck ordered (gsx.refuelCall =
            // onFuelConfirmed). After a completed refuel the same phrase orders a top-up.
            ["fuel confirmed"] = new(
                "gsx.confirmFuel", "Fuel figure confirmed — refueling requested.", "Copied — fuel truck on the way with the confirmed figure."),
            ["fuel figure confirmed"] = new(
                "gsx.confirmFuel", "Fuel figure confirmed — refueling requested.", "Copied — fuel truck on the way with the confirmed figure."),
            ["confirm fuel figure"] = new(
                "gsx.confirmFuel", "Fuel figure confirmed — refueling requested.", "Copied — fuel truck on the way with the confirmed figure."),
            ["request catering"] = new(
                "gsx.requestCatering", "Catering requested.", "Copied — catering is on the way."),
            ["request pushback"] = new(
                "gsx.requestPushback", "Pushback requested.", "Copied — standing by for pushback."),
            ["request de-icing"] = new(
                "gsx.requestDeice", "De-icing requested.", "Copied — de-icing crew on the way."),
        };

    /// <summary>The ground-side hail grammar: every service phrase, the start phrases, and the
    /// cancel words. (Boarding phrases stay listed — a captain who asks ground for boarding
    /// still gets it; GSX-wise it is the same trigger.)</summary>
    public static IReadOnlyList<string> GroundHailGrammar { get; } =
        [.. Bindings.Keys, .. StartGroundServicesPhrases, .. CancelPhrases];

    /// <summary>The cabin-side hail grammar: boarding only, plus cancel words.</summary>
    public static IReadOnlyList<string> CabinHailGrammar { get; } =
        [.. Bindings.Where(b => b.Value.Command == "gsx.requestBoarding").Select(b => b.Key), .. CancelPhrases];
}
