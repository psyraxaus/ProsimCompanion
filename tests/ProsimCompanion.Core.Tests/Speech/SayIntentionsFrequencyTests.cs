using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.SayIntentions;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>The wrong-frequency report (owner request 2026-10-05). The message texts are the
/// live getCommsHistory capture of that day's EFHK→LKPR flight.</summary>
public sealed class SayIntentionsFrequencyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 4, 19, 53, 0, TimeSpan.Zero);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);
    private static readonly AssignedFrequency Tallinn = new(134_325, "Tallinn Control", AssignedFrequencySource.Handoff);

    // ---- hand-off text ----

    [Theory]
    [InlineData("Contact Tower on 119.7. Have a Good morning", "Tower", 119_700)]
    [InlineData("Contact Helsinki radar on 119.1", "Helsinki radar", 119_100)]
    [InlineData("Contact Helsinki Control on 127.425.", "Helsinki Control", 127_425)]
    [InlineData("Finnair-one-two-two-one, Contact Tallinn Control on 134.325.", "Tallinn Control", 134_325)]
    [InlineData("cross runway 22L, contact Ground on 121.9", "Ground", 121_900)]
    [InlineData("Contact departure 124.5, good day", "departure", 124_500)]
    [InlineData("Monitor Tower on 118.12", "Tower", 118_125)]
    public void Handoff_IsReadWithStationAndFrequency(string message, string station, int khz)
    {
        var handoff = HandoffParser.Parse(message);

        Assert.NotNull(handoff);
        Assert.Equal(station, handoff.Station);
        Assert.Equal(khz, handoff.Khz);
        Assert.Equal(AssignedFrequencySource.Handoff, handoff.Source);
    }

    [Theory]
    [InlineData("Helsinki Radar, Identified. climb FL080")]
    [InlineData("Push and start approved. Face North-East.")]
    [InlineData("Cleared to Prague via the VALO4Q departure. Initial climb to 4,000. Departure on 119.1. Squawk 4321.")]
    [InlineData("Departure, radar contact, climb and maintain 5,000")]
    [InlineData("Contact departure.")]
    [InlineData("Contact Tower on 219.7")]
    [InlineData("")]
    [InlineData(null)]
    public void Handoff_NothingReadable_IsNull(string? message)
        => Assert.Null(HandoffParser.Parse(message));

    [Theory]
    [InlineData("Contact departure.", true)]
    [InlineData("Runway 22R vacated, contact ground point niner", true)]
    [InlineData("Departure, radar contact, climb and maintain 5,000", false)]
    [InlineData("Helsinki Radar, Identified. climb FL080", false)]
    public void Instruction_IsAClauseThatOpensWithTheVerb(string message, bool expected)
        => Assert.Equal(expected, HandoffParser.IsInstruction(message));

    [Theory]
    [InlineData("119.7", 119_700)]
    [InlineData("134.325", 134_325)]
    [InlineData("118.12", 118_125)]
    [InlineData("118.97", 118_975)]
    [InlineData("121.65", 121_650)]
    [InlineData("EFIN", null)]
    [InlineData("99.5", null)]
    [InlineData(null, null)]
    public void ToKhz_ReadsTheBandOnly(string? text, int? expected)
        => Assert.Equal(expected, HandoffParser.ToKhz(text));

    // ---- reply bodies ----

    [Fact]
    public void CommHistory_ParsesTheLiveShape()
    {
        const string body = """
            {"flight_id":883498018,"comm_history":[
              {"channel":"INTERCOM3","position":"GUIDE","id":81213949,"outgoing_message":"Ground to flight deck, jetway is operating.","frequency":"124.85"},
              {"channel":"COM1","position":"GROUND","id":81242944,"copilot":0,"frequency":"118.125",
               "outgoing_message":"Contact Tower on 119.7. Have a Good morning",
               "outgoing_message_english":"Finnair-one-two-two-one, Contact Tower on 119.7. Have a Good morning"},
              {"channel":"COM1","position":"GROUND","id":81242969,"copilot":1,"outgoing_message":null,"outgoing_message_english":null,
               "incoming_message":"Contacting Tower on 119.7."}
            ]}
            """;

        var (flightId, entries) = CommHistory.Parse(body);

        Assert.Equal(883498018, flightId);
        Assert.Equal(3, entries.Count);
        Assert.Equal(new CommEntry(81242944, "COM1", "Contact Tower on 119.7. Have a Good morning",
            "Finnair-one-two-two-one, Contact Tower on 119.7. Have a Good morning"), entries[1]);
        Assert.Null(entries[2].Outgoing);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("""{"other":true}""")]
    public void CommHistory_Malformed_IsEmpty(string body)
        => Assert.Empty(CommHistory.Parse(body).Entries);

    [Fact]
    public void CorrectFrequency_IsReadAtTheGate_AndNullInCruise()
    {
        var gate = CommHistory.ParseCorrectFrequency(
            """{"airport":"EFHK","correct_frequency":"118.125","correct_position":"GROUND","frequencies":[]}""");
        var cruise = CommHistory.ParseCorrectFrequency(
            """{"correct_frequency":null,"correct_position":null,"frequencies":[],"airport":"EEEI"}""");

        Assert.Equal(new AssignedFrequency(118_125, "GROUND", AssignedFrequencySource.CorrectFrequency), gate);
        Assert.Null(cruise);
        Assert.Null(CommHistory.ParseCorrectFrequency("not json"));
    }

    // ---- the assignment ----

    private static CommEntry Atc(long id, string? outgoing, string channel = "COM1") => new(id, channel, outgoing, null);

    [Fact]
    public void Tracker_FollowsTheHandoffChain()
    {
        var tracker = new AssignedFrequencyTracker();

        tracker.ApplyHistory(1,
        [
            Atc(10, "Push and start approved. Face North-East."),
            Atc(20, "Contact Tower on 119.7. Have a Good morning"),
            Atc(30, "Contact Helsinki radar on 119.1"),
            Atc(40, "Contact Helsinki Control on 127.425."),
            Atc(50, "Contact Tallinn Control on 134.325."),
        ]);

        Assert.Equal(Tallinn, tracker.Current);
        Assert.Equal(50, tracker.LastId);
        Assert.False(tracker.NeedsCorrectFrequency);
    }

    // The 2026-10-05 trap: assigned Tallinn Control, the owner tuned Helsinki Radar and Radar
    // answered "Identified". The exchange must not become the assignment.
    [Fact]
    public void Tracker_AnExchangeOnAnotherStation_DoesNotMoveTheAssignment()
    {
        var tracker = new AssignedFrequencyTracker();
        tracker.ApplyHistory(1, [Atc(50, "Contact Tallinn Control on 134.325.")]);

        tracker.ApplyHistory(1, [Atc(60, "Helsinki Radar, Identified. Good morning")]);

        Assert.Equal(Tallinn, tracker.Current);
    }

    [Fact]
    public void Tracker_AnUnreadableHandoff_ClearsTheAssignment_UntilTheNextReadableOne()
    {
        var tracker = new AssignedFrequencyTracker();
        tracker.ApplyHistory(1, [Atc(50, "Contact Tower on 119.7.")]);

        tracker.ApplyHistory(1, [Atc(60, "Contact departure.")]);
        Assert.Null(tracker.Current);

        tracker.ApplyHistory(1, [Atc(70, "Contact Center on 127.425")]);
        Assert.Equal(127_425, tracker.Current!.Khz);
    }

    [Fact]
    public void Tracker_UsesCorrectFrequencyOnlyBeforeTheFirstHandoff()
    {
        var ground = new AssignedFrequency(118_125, "GROUND", AssignedFrequencySource.CorrectFrequency);
        var tracker = new AssignedFrequencyTracker();

        tracker.ApplyCorrectFrequency(ground);
        Assert.True(tracker.NeedsCorrectFrequency);
        Assert.Equal(ground, tracker.Current);

        tracker.ApplyHistory(1, [Atc(20, "Contact Tower on 119.7.")]);
        tracker.ApplyCorrectFrequency(ground);
        Assert.Equal(119_700, tracker.Current!.Khz);
    }

    [Fact]
    public void Tracker_IgnoresTheRampCrewAndAcars_AndRepeatedEntries()
    {
        var tracker = new AssignedFrequencyTracker();
        tracker.ApplyHistory(1,
        [
            Atc(10, "Contact Tower on 119.7.", channel: "INTERCOM3"),
            Atc(20, "Contact Tower on 119.7.", channel: "ACARS"),
        ]);
        Assert.Null(tracker.Current);
        Assert.True(tracker.NeedsCorrectFrequency);

        tracker.ApplyHistory(1, [Atc(30, "Contact Tower on 119.7.")]);
        tracker.ApplyHistory(1, [Atc(30, "Contact Ground on 121.9"), Atc(25, "Contact Ground on 121.9")]);
        Assert.Equal(119_700, tracker.Current!.Khz);
    }

    [Fact]
    public void Tracker_ANewFlight_StartsClean()
    {
        var tracker = new AssignedFrequencyTracker();
        tracker.ApplyHistory(1, [Atc(50, "Contact Tallinn Control on 134.325.")]);

        tracker.ApplyHistory(2, [Atc(5, "Push and start approved.")]);

        Assert.Null(tracker.Current);
        Assert.True(tracker.NeedsCorrectFrequency);
        Assert.Equal(5, tracker.LastId);
    }

    // ---- the timing ----

    [Fact]
    public void Watch_SpeaksOnce_AfterTheWait()
    {
        var watch = new FrequencyWatchCore();

        Assert.Null(watch.Step(T0, Tallinn, 119_100, Wait));
        Assert.Null(watch.Step(T0.AddSeconds(55), Tallinn, 119_100, Wait));
        var advisory = watch.Step(T0.AddSeconds(60), Tallinn, 119_100, Wait);
        Assert.Null(watch.Step(T0.AddSeconds(600), Tallinn, 119_100, Wait));

        Assert.NotNull(advisory);
        Assert.Equal(Tallinn, advisory.Assigned);
        Assert.Equal(119_100, advisory.Com1Khz);
        Assert.Equal(Wait, advisory.Waited);
    }

    [Fact]
    public void Watch_OnTheAssignedFrequency_IsSilent_AndReArms()
    {
        var watch = new FrequencyWatchCore();
        Assert.Null(watch.Step(T0, Tallinn, 119_100, Wait));
        Assert.NotNull(watch.Step(T0.AddSeconds(60), Tallinn, 119_100, Wait));

        Assert.Null(watch.Step(T0.AddSeconds(70), Tallinn, 134_325, Wait));
        Assert.Null(watch.Step(T0.AddSeconds(80), Tallinn, 119_100, Wait));
        Assert.NotNull(watch.Step(T0.AddSeconds(140), Tallinn, 119_100, Wait));
    }

    [Fact]
    public void Watch_DiallingThroughChannels_RestartsTheWait()
    {
        var watch = new FrequencyWatchCore();

        Assert.Null(watch.Step(T0, Tallinn, 119_100, Wait));
        Assert.Null(watch.Step(T0.AddSeconds(50), Tallinn, 121_500, Wait));
        Assert.Null(watch.Step(T0.AddSeconds(100), Tallinn, 121_500, Wait));
        Assert.NotNull(watch.Step(T0.AddSeconds(110), Tallinn, 121_500, Wait));
    }

    [Fact]
    public void Watch_ANewHandoff_RestartsTheWait()
    {
        var tower = new AssignedFrequency(119_700, "Tower", AssignedFrequencySource.Handoff);
        var watch = new FrequencyWatchCore();

        Assert.Null(watch.Step(T0, Tallinn, 127_425, Wait));
        Assert.Null(watch.Step(T0.AddSeconds(59), tower, 127_425, Wait));
        Assert.Null(watch.Step(T0.AddSeconds(100), tower, 127_425, Wait));
        Assert.NotNull(watch.Step(T0.AddSeconds(119), tower, 127_425, Wait));
    }

    [Theory]
    [InlineData(134_325)]   // on it
    [InlineData(134_330)]   // the 8.33 kHz name of the same carrier
    [InlineData(0)]         // ProSim absent
    [InlineData(108_500)]   // not a COM frequency
    public void Watch_IsSilent(int com1Khz)
    {
        var watch = new FrequencyWatchCore();

        Assert.Null(watch.Step(T0, Tallinn, com1Khz, Wait));
        Assert.Null(watch.Step(T0.AddSeconds(600), Tallinn, com1Khz, Wait));
    }

    [Fact]
    public void Watch_UnknownAssignment_IsSilent_AndForgetsTheRunningWait()
    {
        var watch = new FrequencyWatchCore();
        Assert.Null(watch.Step(T0, Tallinn, 119_100, Wait));

        Assert.Null(watch.Step(T0.AddSeconds(59), null, 119_100, Wait));
        Assert.Null(watch.Step(T0.AddSeconds(61), Tallinn, 119_100, Wait));
    }

    // ---- the spoken line and the phase window ----

    [Fact]
    public void Advisory_NamesTheStationAndReadsTheFrequencyDigitByDigit()
    {
        Assert.Equal(
            "Captain, we should be on Tallinn Control, one three four decimal three two five.",
            SayIntentionsFrequencyMonitor.ComposeAdvisory(Tallinn));
        Assert.Equal(
            "Captain, we should be on Ground, one one eight decimal one two five.",
            SayIntentionsFrequencyMonitor.ComposeAdvisory(
                new AssignedFrequency(118_125, "GROUND", AssignedFrequencySource.CorrectFrequency)));
        Assert.Equal(
            "Captain, we should be on one one niner decimal seven.",
            SayIntentionsFrequencyMonitor.ComposeAdvisory(
                new AssignedFrequency(119_700, " ", AssignedFrequencySource.Handoff)));
    }

    [Theory]
    [InlineData(FlightPhase.Unknown, false)]
    [InlineData(FlightPhase.ColdAndDark, false)]
    [InlineData(FlightPhase.Preflight, false)]
    [InlineData(FlightPhase.Departure, false)]
    [InlineData(FlightPhase.PushbackAndStart, true)]
    [InlineData(FlightPhase.TaxiOut, true)]
    [InlineData(FlightPhase.Cruise, true)]
    [InlineData(FlightPhase.TaxiIn, true)]
    [InlineData(FlightPhase.Shutdown, false)]
    public void TheWatch_RunsFromPushbackToTaxiIn(FlightPhase phase, bool watched)
        => Assert.Equal(watched, SayIntentionsFrequencyMonitor.IsWatchedPhase(phase));
}
