using System.Text.Json.Nodes;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Fuel;
using ProsimCompanion.Core.Speech;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Weather;
using ProsimCompanion.Prosim.Simbrief;
using ProsimCompanion.Speech.Monitoring;
using Xunit;

namespace ProsimCompanion.Core.Tests.Fuel;

/// <summary>Issue #183: the TAF reader, the fuel-decision advisor, the holding time, the
/// SimBrief fuel breakdown, the voice parsing and the stores.</summary>
public sealed class FuelDecisionTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 12, 16, 30, 0, TimeSpan.Zero);

    private const string Taf =
        "TAF EGLL 121100Z 1212/1318 24012KT 9999 SCT030 BECMG 1214/1216 25015G25KT TEMPO 1215/1219 4000 SHRA BKN012 "
        + "PROB30 TEMPO 1216/1220 2000 TSRA BKN008CB FM130300 20008KT 8000 BKN020 BECMG 1308/1310 0400 FG VV002";

    [Fact]
    public void Taf_BaseIsReadAndChangeGroupsApply()
    {
        var early = TafParser.ForecastAt(Taf, new DateTimeOffset(2026, 10, 12, 12, 30, 0, TimeSpan.Zero))!;
        Assert.Equal(9999, early.VisibilityM);
        Assert.Null(early.CeilingFt);           // SCT is not a ceiling
        Assert.Equal(240, early.WindDirDeg);
        Assert.Equal(12, early.WindSpeedKt);
        Assert.False(early.Thunderstorm);
        Assert.Equal("base", early.GroupsUsed);

        // 16:30 — BECMG wind applied, TEMPO showers (4000 m, BKN012) and PROB30 TEMPO TS (2000 m, BKN008CB) both cover it: worst case.
        var busy = TafParser.ForecastAt(Taf, At)!;
        Assert.Equal(2000, busy.VisibilityM);
        Assert.Equal(800, busy.CeilingFt);
        Assert.True(busy.Thunderstorm);
        Assert.Equal(25, busy.WindGustKt);
        Assert.Contains("PROB30 TEMPO 1216/1220", busy.GroupsUsed, StringComparison.Ordinal);

        // Next morning: the FM group replaced everything; the later BECMG (fog) has not begun.
        var morning = TafParser.ForecastAt(Taf, new DateTimeOffset(2026, 10, 13, 5, 0, 0, TimeSpan.Zero))!;
        Assert.Equal(8000, morning.VisibilityM);
        Assert.Equal(2000, morning.CeilingFt);
        Assert.False(morning.Thunderstorm);
        Assert.Equal(200, morning.WindDirDeg);

        // 09:00 — the BECMG fog is in progress: taken as in effect (conservative).
        var fog = TafParser.ForecastAt(Taf, new DateTimeOffset(2026, 10, 13, 9, 0, 0, TimeSpan.Zero))!;
        Assert.Equal(400, fog.VisibilityM);
        Assert.Equal(200, fog.CeilingFt);
    }

    [Fact]
    public void Taf_HandlesStatuteMilesCavokAndGarbage()
    {
        var us = TafParser.ForecastAt("KJFK 121130Z 1212/1318 18010KT P6SM FEW250 FM121800 20012KT 3SM -RA OVC015", At)!;
        Assert.Equal(9999, us.VisibilityM);
        Assert.Null(us.CeilingFt);
        var later = TafParser.ForecastAt("KJFK 121130Z 1212/1318 18010KT P6SM FEW250 FM121800 20012KT 3SM -RA OVC015", new DateTimeOffset(2026, 10, 12, 19, 0, 0, TimeSpan.Zero))!;
        Assert.Equal(4828, later.VisibilityM);
        Assert.Equal(1500, later.CeilingFt);
        Assert.Equal(PrecipKind.Rain, later.Precip);

        Assert.Equal(9999, TafParser.ForecastAt("TAF LOWW 121100Z 1212/1312 27008KT CAVOK", At)!.VisibilityM);
        Assert.Null(TafParser.ForecastAt("", At));
        Assert.Null(TafParser.ForecastAt(null, At));
    }

    private static ArrivalOutlook Outlook(int? vis, int? ceiling, bool ts = false, bool taf = true) => new("EGLL", vis, ceiling, ts, taf);

    [Fact]
    public void Advisor_VfrDay_RecommendsNothing()
    {
        var decision = FuelDecisionAdvisor.Advise(Outlook(9999, 3000), Outlook(9999, 2500), null, new FuelOptions());

        Assert.Equal(FuelRecommendation.None, decision.Recommendation);
        Assert.Equal(0, decision.ExtraKg);
        Assert.Empty(decision.Reasons);
        Assert.Null(decision.LegalityLine);
        Assert.Equal("Destination forecast fine at the ETA — no extra fuel recommended.", FuelDecisionAdvisor.Spoken(decision));
    }

    [Fact]
    public void Advisor_ThunderstormsAtEta_RecommendTwentyMinutes_SevenHundredKilos()
    {
        var decision = FuelDecisionAdvisor.Advise(Outlook(6000, 1500, ts: true), null, null, new FuelOptions());

        Assert.Equal(FuelRecommendation.Holding, decision.Recommendation);
        Assert.Equal(20, decision.HoldingMinutes);
        Assert.Equal(800, decision.ExtraKg); // 20/60 × 2200 = 733 → rounded up to 800
        Assert.Equal("Destination TAF has thunderstorms around our arrival — recommend 20 minutes holding, 800 kilos. Take it?", FuelDecisionAdvisor.QuestionPrompt(decision));
        Assert.Contains(800, FuelDecisionAdvisor.Numbers(decision));
    }

    [Fact]
    public void Advisor_BelowMinima_SaysAnAlternateIsRequired_AndNamesTheAlternateWhenAlsoBelow()
    {
        var options = new FuelOptions();
        var below = FuelDecisionAdvisor.Advise(Outlook(400, 100), Outlook(9999, 2000), null, options);
        Assert.True(below.DestinationBelowMinima);
        Assert.Equal(30, below.HoldingMinutes);
        Assert.Equal("Destination below minima at ETA — an alternate is required.", below.LegalityLine);
        Assert.False(below.AlternateBelowMinima);

        var both = FuelDecisionAdvisor.Advise(Outlook(400, 100), Outlook(300, 100), null, options);
        Assert.True(both.AlternateBelowMinima);
        Assert.EndsWith("and the alternate is also below minima.", both.LegalityLine, StringComparison.Ordinal);
        Assert.Contains("the alternate is also below minima", both.Reasons);
        Assert.Contains("an alternate is required", FuelDecisionAdvisor.Spoken(both), StringComparison.Ordinal);
    }

    [Fact]
    public void Advisor_Marginal_UsesTheMarginalMinutes_AndTheBriefedDecisionHeight()
    {
        var options = new FuelOptions();
        var marginal = FuelDecisionAdvisor.Advise(Outlook(1200, 600), Outlook(9999, 400), null, options);
        Assert.Equal(FuelRecommendation.Holding, marginal.Recommendation);
        Assert.Equal(15, marginal.HoldingMinutes);
        Assert.True(marginal.DestinationMarginal);
        Assert.True(marginal.AlternateMarginal);
        Assert.Contains("alternate weather also marginal", marginal.Reasons);

        // A briefed DH of 100 ft makes a 150 ft ceiling marginal, not below.
        var dh = FuelDecisionAdvisor.Advise(Outlook(800, 150), null, new ArrivalMinima(ArrivalMinimumKind.DecisionHeight, 100), options);
        Assert.False(dh.DestinationBelowMinima);
        Assert.True(dh.DestinationMarginal);
        Assert.Equal("briefed decision height", dh.Minima.Source);
        // A DA is MSL and cannot be compared with a TAF ceiling: the defaults stand.
        Assert.Equal("category 1 defaults", FuelDecisionAdvisor.Advise(Outlook(800, 150), null, new ArrivalMinima(ArrivalMinimumKind.DecisionAltitude, 320), options).Minima.Source);
    }

    [Fact]
    public void Outlook_PrefersTheTaf_ElseTheMetar_ElseNothing()
    {
        var withTaf = new WxFacts("EGLL 121620Z 24012KT 9999 FEW030 15/08 Q1012", 240, 12, 9999, 1012, 15, null, null, RawTaf: Taf);
        var outlook = ArrivalOutlook.From("EGLL", withTaf, At)!;
        Assert.True(outlook.FromTaf);
        Assert.True(outlook.Thunderstorm);

        var metarOnly = new WxFacts("EGLL 121620Z 24012KT 3000 TSRA BKN008 15/08 Q1012", 240, 12, 3000, 1012, 15, null, null, CeilingFt: 800, Precip: PrecipKind.Thunderstorm);
        var fromMetar = ArrivalOutlook.From("EGLL", metarOnly, At)!;
        Assert.False(fromMetar.FromTaf);
        Assert.True(fromMetar.Thunderstorm);
        Assert.Equal(800, fromMetar.CeilingFt);

        Assert.Null(ArrivalOutlook.From("EGLL", WxFacts.None, At));
        Assert.Null(ArrivalOutlook.From("", withTaf, At));
    }

    [Theory]
    [InlineData(6000, 1800, 1500, 1000, 25)]   // 6000 − 1800 = 4200 over destination − 2500 = 1700 → 46 min? no: 1700/2200×60 = 46
    [InlineData(4000, 1800, 1500, 1000, -8)]   // into the reserves
    [InlineData(4300, 1800, 1500, 1000, 0)]    // exactly nothing left: divert now
    public void HoldingTime_IsOverDestinationMinusAlternateAndReserve(double fob, double toDest, double alt, double reserve, int expectedMinutesOrZeroish)
    {
        var estimate = HoldingTimeCalculator.Compute(fob, toDest, alt, reserve, 2200, At)!;
        var expected = (int)Math.Floor((fob - toDest - alt - reserve) / 2200 * 60);
        Assert.Equal(expected, estimate.AvailableMinutes);
        Assert.Equal(expectedMinutesOrZeroish <= 0, estimate.DivertNow);
        Assert.Equal(estimate.DivertNow ? "No holding fuel — over the destination we would be into the alternate fuel. Divert now." : $"We can hold for {expected} minutes before we have to divert.", estimate.Spoken);
        Assert.Equal(estimate.DivertNow ? "DIVERT" : $"{expected} MIN", HoldingTimeCalculator.Figure(estimate));
        Assert.Null(HoldingTimeCalculator.Compute(fob, toDest, alt, reserve, 0, At));
    }

    [Fact]
    public void Importer_ReadsTheFuelBreakdown_AndOldPlansLoadWithZeros()
    {
        var full = JsonNode.Parse("""
            {"params":{"units":"kgs"},"fuel":{"plan_ramp":"8120","plan_landing":"3050","taxi":"250","min_takeoff":"7000","extra":"500",
             "enroute_burn":"4820","contingency":"241","alternate_burn":"1130","reserve":"1050","holding":"0","extra_optional":"300"},
             "origin":{"icao_code":"EGLL"},"destination":{"icao_code":"LIRF"},"weights":{"pax_count":"150"}}
            """)!.AsObject();
        var ofp = SimbriefImportService.ParseOfp(full);
        Assert.Equal(4820, ofp.FuelTripKg);
        Assert.Equal(241, ofp.FuelContingencyKg);
        Assert.Equal(1130, ofp.FuelAlternateKg);
        Assert.Equal(1050, ofp.FuelFinalReserveKg);
        Assert.Equal(0, ofp.FuelHoldingKg);
        Assert.Equal(300, ofp.FuelDiscretionaryKg);
        Assert.Equal(500, ofp.FuelExtraKg);

        var old = JsonNode.Parse("""{"params":{"units":"kgs"},"fuel":{"plan_ramp":"8120","plan_landing":"3050"},"origin":{"icao_code":"EGLL"},"destination":{"icao_code":"LIRF"},"weights":{"pax_count":"150"}}""")!.AsObject();
        var legacy = SimbriefImportService.ParseOfp(old);
        Assert.Equal(0, legacy.FuelAlternateKg);
        Assert.Equal(0, legacy.FuelFinalReserveKg);
        Assert.Equal(8200, legacy.FuelPlanRampKg); // rounded up to 100, as before
    }

    [Theory]
    [InlineData("extra fuel 20 minutes", 800.0)]
    [InlineData("extra fuel 7 hundred", 700.0)]
    [InlineData("extra fuel 700", 700.0)]
    [InlineData("extra fuel 650", 700.0)]
    [InlineData("extra fuel 1.2 tonnes", 1200.0)]
    [InlineData("extra fuel", null)]
    public void ParseExtra_MinutesHundredsTonnesAndPlainKilos(string utterance, double? expected)
        => Assert.Equal(expected, FuelDecisionService.ParseExtra(utterance, 2200));

    [Theory]
    [InlineData(8120, 700, 8900)]
    [InlineData(8100, 700, 8800)]
    [InlineData(8150, 650, 8800)]
    public void RaisedBlock_RoundsUpToTheHundred(double block, double extra, double expected)
        => Assert.Equal(expected, FuelDecisionService.RaisedBlockKg(block, extra));

    [Theory]
    [InlineData("yes", true)]
    [InlineData("take it", true)]
    [InlineData("affirm, load it", true)]
    [InlineData("no", false)]
    [InlineData("negative, plan fuel", false)]
    [InlineData("leave it", false)]
    [InlineData("stand by", null)]
    public void DecisionPhrases_Interpret(string heard, bool? expected)
    {
        Assert.Equal(expected, FuelDecisionPhrases.Interpret(heard));
        Assert.Equal("fuelDecision", FuelDecisionPhrases.Table.Topic);
        Assert.Equal("fuel-decision-question", FuelDecisionPhrases.Table.EventName);
    }

    [Fact]
    public void Stores_AskOncePerCycle_MarkApplied_AndHoldingResetsWithTheCycle()
    {
        var signals = new GroundOpsSignals();
        using var store = new FuelDecisionStore(signals);
        var decision = FuelDecisionAdvisor.Advise(Outlook(6000, 1500, ts: true), null, null, new FuelOptions());
        var answered = new List<bool>();
        store.Answered += (_, a) => answered.Add(a.Accepted);

        Assert.True(store.Ask("Take it?", decision));
        Assert.False(store.Ask("Take it?", decision));
        Assert.Equal(decision, store.Advice);
        store.MarkApplied(800, "voice");                 // "extra fuel …" while the question stood: applied + settled quietly
        Assert.Equal(800, store.Snapshot().AppliedExtraKg);
        Assert.True(store.Asking.Accepted);
        Assert.Empty(answered);                           // no Answered edge: the service already applied it
        Assert.False(store.Ask("Take it?", decision));   // final for the cycle

        using var holding = new HoldingTimeStore(signals);
        holding.Set(HoldingTimeCalculator.Compute(6000, 1800, 1500, 1000, 2200, At)!);
        Assert.NotNull(holding.Current);
        signals.RaiseFlightCycleReset();
        Assert.Null(holding.Current);
        Assert.Null(store.Advice);
    }

    [Fact]
    public void FactSheet_CarriesTheHoldingLine_AndTheAlternateAndReserve()
    {
        var ofp = new OfpData { FuelAlternateKg = 1130, FuelFinalReserveKg = 1050 };
        var sheet = FoFactSheetBuilder.Build(new FoFactInputs(null, null, null, ofp, null, null, null, null, null, At,
            HoldingTimeCalculator.Compute(6000, 1800, 1130, 1050, 2200, At)));

        Assert.Contains(sheet.Facts, f => f.Label == "Alternate fuel");
        Assert.Contains(sheet.Facts, f => f.Label == "Final reserve fuel");
        var holding = Assert.Single(sheet.Facts, f => f.Label == "Holding fuel available");
        Assert.StartsWith("55 minutes", holding.Value, StringComparison.Ordinal);
        Assert.Contains(55, sheet.AllowedNumbers);
    }
}
