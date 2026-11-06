namespace ProsimCompanion.Speech.Gsx;

/// <summary>One phrase → command mapping. <see cref="SuccessPhrase"/> is the FO's confirmation
/// on a single-shot phrase; <see cref="CrewAckPhrase"/> is what the hailed crew says instead
/// when the request came through a hail dialogue (issue #51). <see cref="CabinAck"/> marks the
/// boarding phrases: the FO relays them to the cabin and the purser answers "Boarding underway."</summary>
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
/// <para>
/// Since 2026-11-07 (owner request: "commence boarding", "start boarding", not just one
/// wording) the table is built from a GRID — every request verb × every name a service goes
/// by — plus the hand-written specials (fuel confirmed, ground power on/off, the de-icing
/// answers). The first entries of each service are the canonical ones the VOICE drawer shows.
/// Politeness and a leading address ("ground, please …") are stripped by the interpreter
/// before matching, so they need no entries here.
/// </para>
/// </summary>
public static class GsxVoicePhrases
{
    /// <summary>Phrases that start the departure sequence (and release the voice-mode ground
    /// prep gate) — mapped to gsx.startDepartureServices at dispatch time.</summary>
    public static readonly IReadOnlyList<string> StartGroundServicesPhrases =
        // "begin" added 2026-09-06 (issue #127): the owner's natural phrasing needed a
        // did-you-mean round-trip every departure.
        ["commence ground services", "start ground services", "begin ground services", "start the ground services", "commence the ground services"];

    /// <summary>Cancel words accepted inside a hail's listening window.</summary>
    public static readonly IReadOnlyList<string> CancelPhrases =
        ["disregard", "cancel", "cancel that", "nothing", "never mind"];

    /// <summary>The ground crew's answer to a hail request nothing maps to (issue #170:
    /// "please connect the ground power unit" earned a "Standing by."). Names what the hail
    /// can do so the captain rephrases instead of guessing; the window re-opens once.</summary>
    public const string UnmappedRequestText =
        "Say again — I can do boarding, fuel, catering, ground power, pushback or de-icing.";

    /// <summary>The verbs a captain uses to ask for a service, canonical first.</summary>
    public static readonly IReadOnlyList<string> RequestVerbs =
        ["request", "start", "commence", "begin", "call", "send", "order"];

    /// <summary>What the FO says when the captain asks for boarding: a relay to the cabin, not
    /// a report to ground — boarding is the cabin's and the gate's business in real life.</summary>
    public const string BoardingRelayText = "Cabin crew, commence boarding.";

    private const string GpuAck = "Copied — ground power coming up.";
    private const string GpuOffAck = "Copied — ground power coming off.";

    /// <summary>One service of the grid: the command, its spoken names (canonical first) and
    /// the two answers.</summary>
    private sealed record Service(string Command, string[] Names, string Success, string CrewAck, bool CabinAck = false);

    private static readonly Service[] Services =
    [
        new("gsx.requestBoarding", ["boarding", "the boarding", "passenger boarding", "the passengers"],
            BoardingRelayText, "Copied — we'll start boarding.", CabinAck: true),
        new("gsx.requestRefuel", ["refueling", "refuelling", "the refueling", "the refuelling", "fueling", "fuelling", "the fuel truck", "fuel", "the fuel"],
            "Refueling requested.", "Copied — fuel truck on the way."),
        new("gsx.requestCatering", ["catering", "the catering"],
            "Catering requested.", "Copied — catering is on the way."),
        new("gsx.requestPushback", ["pushback", "push back", "the pushback", "the push back"],
            "Pushback requested.", "Copied — standing by for pushback."),
        new("gsx.requestDeice", ["de-icing", "deicing", "de-ice", "the de-icing", "the deicing"],
            "De-icing requested.", "Copied — de-icing crew on the way."),
        new("gsx.requestGpu", ["ground power", "the ground power", "the gpu", "the ground power unit", "external power"],
            "Ground power requested.", GpuAck),
    ];

    public static IReadOnlyDictionary<string, GsxVoiceBinding> Bindings { get; } = Build();

    private static Dictionary<string, GsxVoiceBinding> Build()
    {
        var table = new Dictionary<string, GsxVoiceBinding>(StringComparer.OrdinalIgnoreCase)
        {
            ["call the next service"] = new(
                "gsx.forceNextService", "Calling the next service.", "Copied — calling the next service."),
            ["next service"] = new(
                "gsx.forceNextService", "Calling the next service.", "Copied — calling the next service."),
        };

        // The grid: canonical verb × canonical name first for every service, so the drawer's
        // first phrases read "request boarding", "start boarding", "commence boarding".
        foreach (var service in Services)
        {
            var binding = new GsxVoiceBinding(service.Command, service.Success, service.CrewAck, service.CabinAck);
            foreach (var verb in RequestVerbs)
            {
                foreach (var name in service.Names)
                {
                    table.TryAdd($"{verb} {name}", binding);
                }
            }
        }

        // Specials that no grid produces.
        var boarding = Services[0];
        table["cabin crew start boarding"] = new(boarding.Command, boarding.Success, boarding.CrewAck, CabinAck: true);
        table["cabin crew commence boarding"] = new(boarding.Command, boarding.Success, boarding.CrewAck, CabinAck: true);
        table["board the passengers"] = new(boarding.Command, boarding.Success, boarding.CrewAck, CabinAck: true);
        // Real-world SOP (2026-09-19): the captain confirms the block fuel after their own
        // considerations, and only then is the truck ordered (gsx.refuelCall =
        // onFuelConfirmed). After a completed refuel the same phrase orders a top-up.
        var fuelConfirmed = new GsxVoiceBinding("gsx.confirmFuel", "Fuel figure confirmed — refueling requested.", "Copied — fuel truck on the way with the confirmed figure.");
        table["fuel confirmed"] = fuelConfirmed;
        table["fuel figure confirmed"] = fuelConfirmed;
        table["confirm fuel figure"] = fuelConfirmed;
        table["fuel is confirmed"] = fuelConfirmed;
        // Ground power (issue #170, 2026-10-11 EGCC turnaround: "please connect the ground
        // power unit" through a hail earned "Standing by."). gsx.requestGpu calls GSX's
        // GPU, or the ProSim unit when GSX offers none at the stand; the existing
        // ground-crew upcall reports it connected. gsx.disconnectGpu takes it away.
        var gpu = new GsxVoiceBinding("gsx.requestGpu", "Ground power requested.", GpuAck);
        foreach (var phrase in new[] { "connect ground power", "connect the gpu", "connect the ground power unit", "connect external power", "connect the ground power", "plug in the gpu", "ground power on" })
        {
            table[phrase] = gpu;
        }

        var gpuOff = new GsxVoiceBinding("gsx.disconnectGpu", "Ground power disconnected.", GpuOffAck);
        foreach (var phrase in new[] { "disconnect ground power", "remove the gpu", "disconnect the gpu", "remove ground power", "disconnect the ground power unit", "ground power off" })
        {
            table[phrase] = gpuOff;
        }

        // The captain's "no" to the FO's de-icing question at any time while it stands
        // (issue #166): the listen window no longer decides it, so a later standalone
        // answer needs its own phrase. Store write, not a GSX call.
        var noDeice = new GsxVoiceBinding("gsx.declineDeice", "Copied — no de-icing.", "Copied — no de-icing today.");
        foreach (var phrase in new[] { "no de-icing", "negative de-icing", "decline de-icing", "de-icing not required", "no deicing", "we don't need de-icing" })
        {
            table[phrase] = noDeice;
        }

        return table;
    }

    /// <summary>The ground-side hail grammar: every service phrase, the start phrases, and the
    /// cancel words. (Boarding phrases stay listed — a captain who asks ground for boarding
    /// still gets it; GSX-wise it is the same trigger.)</summary>
    public static IReadOnlyList<string> GroundHailGrammar { get; } =
        [.. Bindings.Keys, .. StartGroundServicesPhrases, .. CancelPhrases];

    /// <summary>The cabin-side hail grammar: boarding only, plus cancel words.</summary>
    public static IReadOnlyList<string> CabinHailGrammar { get; } =
        [.. Bindings.Where(b => b.Value.Command == "gsx.requestBoarding").Select(b => b.Key), .. CancelPhrases];
}
