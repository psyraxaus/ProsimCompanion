using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Geo;
using ProsimCompanion.Core.Weather;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Persona;
using Xunit;

namespace ProsimCompanion.Core.Tests.Persona;

/// <summary>Cruise small talk (issue #195): every gate alone blocks, the chattiness is the
/// count, no race with the region facts, the sequence never repeats, quiet zeroes it, one
/// follow-up only, and the arbiter still drops a line that slips below the ceiling.</summary>
public sealed class SmallTalkCoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
    private const double Ceiling = 10_000;

    private static SmallTalkOptions Options(int max = 3, int quiet = 8, bool enabled = true)
        => new() { Enabled = enabled, MaxPerFlight = max, MinQuietMinutes = quiet };

    private static SmallTalkTick Tick(
        FlightPhase phase = FlightPhase.Cruise, bool live = true, DateTimeOffset? now = null, double altitude = 35_000,
        bool valid = true, bool abnormal = false, bool question = false, TurbulenceLevel turbulence = TurbulenceLevel.None,
        DateTimeOffset? lastSpeech = null, DateTimeOffset? regionFact = null, int chattiness = 2, bool sterile = false)
        => new(phase, live, now ?? T0.AddMinutes(10), altitude, valid, abnormal, question, turbulence, lastSpeech, regionFact, chattiness, sterile);

    private static readonly IReadOnlyList<SmallTalkKind> AllKinds = [SmallTalkKind.Offer, SmallTalkKind.View, SmallTalkKind.Layover, SmallTalkKind.Crew];

    /// <summary>A core that entered the cruise at T0 (the first gap runs from there).</summary>
    private static SmallTalkCore InCruise(SmallTalkOptions? options = null)
    {
        var core = new SmallTalkCore();
        core.Due(Tick(now: T0), options ?? Options(), Ceiling, AllKinds, () => 0);
        return core;
    }

    [Fact]
    public void Baseline_InTheCruise_AfterTheQuietGap_IsDue()
    {
        var core = InCruise();
        Assert.Equal(SmallTalkKind.Offer, core.Due(Tick(), Options(), Ceiling, AllKinds, () => 0));
    }

    public static IEnumerable<object[]> Blockers()
    {
        yield return ["disabled", Tick(), Options(enabled: false)];
        yield return ["not live", Tick(live: false), Options()];
        yield return ["not cruise", Tick(phase: FlightPhase.Climb), Options()];
        yield return ["below the sterile ceiling", Tick(altitude: 9_000), Options()];
        yield return ["altitude not valid", Tick(valid: false), Options()];
        yield return ["sterile verdict", Tick(sterile: true), Options()];
        yield return ["abnormal open", Tick(abnormal: true), Options()];
        yield return ["question open", Tick(question: true), Options()];
        yield return ["moderate turbulence", Tick(turbulence: TurbulenceLevel.Moderate), Options()];
        yield return ["spoken 3 min ago", Tick(lastSpeech: T0.AddMinutes(7)), Options()];
        yield return ["region fact due in 2 min", Tick(regionFact: T0.AddMinutes(12)), Options()];
        yield return ["chattiness 0", Tick(chattiness: 0), Options()];
        yield return ["too soon after the cruise entry", Tick(now: T0.AddMinutes(5)), Options()];
        yield return ["max per flight 0", Tick(), Options(max: 0)];
    }

    [Theory]
    [MemberData(nameof(Blockers))]
    public void EachGateAlone_Blocks(string gate, SmallTalkTick tick, SmallTalkOptions options)
    {
        var core = InCruise(options);
        Assert.True(core.Due(tick, options, Ceiling, AllKinds, () => 0) is null, gate);
    }

    [Fact]
    public void LightTurbulence_AndAnOldRegionFact_DoNotBlock()
    {
        var core = InCruise();
        Assert.NotNull(core.Due(Tick(turbulence: TurbulenceLevel.Light), Options(), Ceiling, AllKinds, () => 0));
        Assert.NotNull(core.Due(Tick(regionFact: T0.AddMinutes(14)), Options(), Ceiling, AllKinds, () => 0)); // due in 4 min: clear
        Assert.NotNull(core.Due(Tick(lastSpeech: T0.AddMinutes(1)), Options(), Ceiling, AllKinds, () => 0)); // 9 min of silence
    }

    [Theory]
    [InlineData(0, 3, 0)]
    [InlineData(1, 3, 1)]
    [InlineData(2, 3, 2)]
    [InlineData(3, 3, 3)]
    [InlineData(3, 2, 2)]
    [InlineData(9, 5, 3)]
    public void Ceiling_IsTheChattiness_CappedByTheOption(int chattiness, int max, int expected)
        => Assert.Equal(expected, SmallTalkCore.Ceiling(Options(max: max), chattiness));

    [Fact]
    public void Count_StopsAtTheChattiness_NotTheOption()
    {
        var core = InCruise();
        var options = Options(max: 3);
        var now = T0.AddMinutes(10);
        for (var i = 0; i < 2; i++)
        {
            Assert.NotNull(core.Due(Tick(now: now, chattiness: 2), options, Ceiling, AllKinds, () => 0));
            core.Claim(options, now);
            now = now.AddMinutes(10);
        }

        Assert.Null(core.Due(Tick(now: now, chattiness: 2), options, Ceiling, AllKinds, () => 0));
        Assert.NotNull(core.Due(Tick(now: now, chattiness: 3), options, Ceiling, AllKinds, () => 0)); // a chattier persona gets the third
    }

    [Fact]
    public void QuietPlease_MidFlight_ZeroesIt()
    {
        var core = InCruise();
        Assert.NotNull(core.Due(Tick(chattiness: 2), Options(), Ceiling, AllKinds, () => 0));
        Assert.Null(core.Due(Tick(chattiness: 0), Options(), Ceiling, AllKinds, () => 0)); // QuietState → PersonaService.Chattiness = 0
    }

    [Fact]
    public void Sequence_CoversEveryEnabledKindOnce_WithoutRepeats()
    {
        var core = new SmallTalkCore();
        var dice = new Random(7);
        core.DrawSequence(AllKinds, () => dice.NextDouble());
        Assert.Equal(4, core.Remaining.Count);
        Assert.Equal(4, core.Remaining.Distinct().Count());

        var claimed = new List<SmallTalkKind>();
        var options = Options(max: 10);
        for (var i = 0; i < 4; i++)
        {
            claimed.Add(core.Claim(options, T0)!.Value);
        }

        Assert.Equal(4, claimed.Distinct().Count());
        Assert.Null(core.Claim(options, T0));
    }

    [Fact]
    public void Claim_HoldsTheNextLineForTheQuietGap_AndReleaseGivesItBack()
    {
        var core = InCruise();
        var options = Options(quiet: 8);
        var kind = core.Claim(options, T0.AddMinutes(10))!.Value;
        Assert.Equal(1, core.CountThisFlight);
        Assert.Null(core.Due(Tick(now: T0.AddMinutes(15), lastSpeech: null), options, Ceiling, AllKinds, () => 0));

        core.Release(kind, T0.AddMinutes(10));
        Assert.Equal(0, core.CountThisFlight);
        Assert.Equal(kind, core.Remaining[0]);
        Assert.Equal(kind, core.Due(Tick(now: T0.AddMinutes(13)), options, Ceiling, AllKinds, () => 0)); // the short retry
    }

    [Fact]
    public void FollowUp_OncePerExchange()
    {
        var core = InCruise();
        Assert.False(core.TryBeginFollowUp()); // nothing claimed yet
        core.Claim(Options(), T0.AddMinutes(10));
        Assert.True(core.TryBeginFollowUp());
        Assert.False(core.TryBeginFollowUp());
    }

    [Fact]
    public void FlightReset_ClearsTheCountAndTheSequence()
    {
        var core = InCruise();
        core.Claim(Options(), T0.AddMinutes(10));
        core.OnPhaseChanged(FlightPhase.Shutdown, FlightPhase.Preflight);
        Assert.Equal(0, core.CountThisFlight);
        Assert.Empty(core.Remaining);
    }

    [Fact]
    public void EnabledKinds_FollowTheTicks_AndCrewNeedsTheRoster()
    {
        var options = new SmallTalkOptions { Kinds = { Offer = false, View = true, Layover = true, Crew = true } };
        Assert.Equal([SmallTalkKind.View, SmallTalkKind.Layover, SmallTalkKind.Crew], SmallTalkCore.EnabledKinds(options, rosterHasOfficer: true));
        Assert.Equal([SmallTalkKind.View, SmallTalkKind.Layover], SmallTalkCore.EnabledKinds(options, rosterHasOfficer: false));
    }

    [Fact]
    public void Arbiter_StillDropsASmallTalkLine_BelowTheSterileCeiling()
    {
        var options = new SpeechOptions { SterileEnabled = true, SterileCockpitCeilingFt = 10_000, SterileSuppressLow = true };
        var rule = new SterileCockpitRule(() => options);
        var line = new SpeechRequest("Nice view off the left today.", SpeechPriority.Low, Tag: "smalltalk.view");

        Assert.Equal(SpeechSuppressionVerdict.Suppress, rule.Evaluate(line, new SpeechContext(FlightPhase.Climb, 6_000, true)));
        Assert.Equal(SpeechSuppressionVerdict.Allow, rule.Evaluate(line, new SpeechContext(FlightPhase.Cruise, 35_000, true)));
        // The purser's coffee line is cabin.* and goes through regardless.
        var coffee = new SpeechRequest(SmallTalkBank.PurserCoffeeLine, SpeechPriority.Normal, Tag: "cabin.smalltalk.coffee", Role: SpeechRole.Purser);
        Assert.Equal(SpeechSuppressionVerdict.Allow, rule.Evaluate(coffee, new SpeechContext(FlightPhase.Descent, 6_000, true)));
    }

    // ---- the bank ----

    [Fact]
    public void Bank_ViewLine_PrefersTheSun_ThenTheDeck_ThenATown()
    {
        Assert.Equal("Sun's getting low off the left — nice light on the clouds.", SmallTalkBank.ViewLine(new SmallTalkContext(SunSide: "left", SunElevationDeg: 4)));
        Assert.Equal("Nice sunrise coming up off the right, captain.", SmallTalkBank.ViewLine(new SmallTalkContext(SunSide: "right", SunElevationDeg: -1, Morning: true)));
        Assert.StartsWith("Solid deck below", SmallTalkBank.ViewLine(new SmallTalkContext(SunElevationDeg: 40, Sky: SkyCondition.Overcast)), StringComparison.Ordinal);
        Assert.Equal("That'll be Lyon off the right, captain — nice view today.", SmallTalkBank.ViewLine(new SmallTalkContext(TownName: "Lyon", TownSide: "right", Sky: SkyCondition.Clear)));
        Assert.Equal("Nice view off the left today, captain.", SmallTalkBank.ViewLine(SmallTalkContext.Empty));
    }

    [Fact]
    public void Bank_CrewLine_UsesTheRecordOnly_AndItsNumbersAreTheAllowedSet()
    {
        var context = new SmallTalkContext(FoName: "Tom", FoBase: "Frankfurt", FoHours: 300, Confidence: "nervous");
        Assert.Equal("I'm based at Frankfurt, captain — about 300 hours on the type now, still learning something every sector.", SmallTalkBank.CrewLine(context));
        Assert.Contains(300, SmallTalkBank.Numbers(SmallTalkKind.Crew, context));
        Assert.Empty(SmallTalkBank.Numbers(SmallTalkKind.View, context));
        Assert.Equal("Not much to tell about me, captain — happy to be here.", SmallTalkBank.CrewLine(SmallTalkContext.Empty));
    }

    [Fact]
    public void Bank_LayoverLine_NamesTheCity()
    {
        var line = SmallTalkBank.Line(SmallTalkKind.Layover, new SmallTalkContext(DestinationCity: "Prague"), () => 0);
        Assert.Equal("Do you know Prague at all, captain?", line);
        Assert.Contains("RECORD", SmallTalkBank.UserPrompt(SmallTalkKind.Crew, SmallTalkContext.Empty, "x", ""), StringComparison.Ordinal);
    }

    [Fact]
    public void FirstSentence_TrimsTheModelReply()
    {
        Assert.Equal("Lovely spot, captain.", CruiseSmallTalkService.FirstSentence("\"Lovely spot, captain. You'll like the old town.\""));
        Assert.Equal("No question here", CruiseSmallTalkService.FirstSentence("No question here"));
        Assert.Equal("", CruiseSmallTalkService.FirstSentence(null));
    }

    [Theory]
    [InlineData("yes please", true)]
    [InlineData("please", true)]
    [InlineData("go on then", true)]
    [InlineData("affirm", true)]
    [InlineData("no thanks", false)]
    [InlineData("not now", false)]
    [InlineData("negative", false)]
    [InlineData("say again", null)]
    public void OfferPhrases_Interpret(string heard, bool? expected)
        => Assert.Equal(expected, SmallTalkOfferPhrases.Interpret(heard));

    // ---- the sun ----

    [Fact]
    public void SolarPosition_NoonAtGreenwichOnTheEquinox_IsSouthAndAbout38Degrees()
    {
        var sun = SolarPosition.At(51.5, 0, new DateOnly(2026, 3, 20), new TimeSpan(12, 8, 0));
        Assert.InRange(sun.ElevationDeg, 36, 40);
        Assert.InRange(sun.AzimuthDeg, 170, 190);

        var midnight = SolarPosition.At(51.5, 0, new DateOnly(2026, 3, 20), new TimeSpan(0, 8, 0));
        Assert.True(midnight.ElevationDeg < -30);
    }

    [Theory]
    [InlineData(90, 0, "right")]
    [InlineData(270, 0, "left")]
    [InlineData(10, 0, "ahead")]
    [InlineData(180, 0, "behind")]
    [InlineData(0, 270, "right")]
    public void SolarPosition_SideOf(double bearing, double track, string side)
        => Assert.Equal(side, SolarPosition.SideOf(bearing, track));
}
