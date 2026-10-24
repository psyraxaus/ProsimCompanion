using ProsimCompanion.Core.Aircraft.WeightAndBalance;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Company;
using ProsimCompanion.Speech.Gsx;
using Xunit;

namespace ProsimCompanion.Core.Tests.Aircraft.WeightAndBalance;

/// <summary>Issue #181: last-minute changes — the evaluator's verdict tables, the LMC block,
/// the spoken line, the store, the revised-final question and its phrase table.</summary>
public sealed class LmcTests
{
    private static LoadsheetData Final() => new()
    {
        ZeroFuelWeight = 59000, ZeroFuelWeightMac = 26.9, TakeoffWeight = 66000, TakeoffWeightMac = 25.4,
        FuelWeight = 7000, LandingWeight = 61600, TotalPassengers = 150, PassengersByZone = [30, 40, 40, 40],
        ForwardCargoWeight = 800, AftCargoWeight = 1200,
    };

    /// <summary>The live W&amp;B after boarding: pax and weights moved by the given deltas.</summary>
    private static LoadsheetData Actual(int paxDelta = 0, double zfwDelta = 0, double cargoDelta = 0, double fuelDelta = 0, double macTowDelta = 0, double macZfwDelta = 0)
    {
        var f = Final();
        return new LoadsheetData
        {
            ZeroFuelWeight = f.ZeroFuelWeight + zfwDelta + cargoDelta,
            ZeroFuelWeightMac = f.ZeroFuelWeightMac + macZfwDelta,
            TakeoffWeight = f.TakeoffWeight + zfwDelta + cargoDelta + fuelDelta,
            TakeoffWeightMac = f.TakeoffWeightMac + macTowDelta,
            FuelWeight = f.FuelWeight + fuelDelta,
            LandingWeight = f.TakeoffWeight + zfwDelta + cargoDelta + fuelDelta,
            TotalPassengers = f.TotalPassengers + paxDelta,
            ForwardCargoWeight = f.ForwardCargoWeight,
            AftCargoWeight = f.AftCargoWeight + cargoDelta,
        };
    }

    [Fact]
    public void NoChange_WhenEverythingIsWithinJitter()
    {
        var result = LmcEvaluator.Evaluate(Final(), Actual(zfwDelta: 5, fuelDelta: 30, macTowDelta: 0.02), LmcTolerances.Default);

        Assert.Equal(LmcVerdict.NoChange, result.Verdict);
        Assert.Empty(result.Reasons);
        Assert.Equal(0, result.DeltaPax);
    }

    [Theory]
    [InlineData(-2, -176.0, 0.0, LmcVerdict.WithinTolerance)]      // two no-shows
    [InlineData(4, 352.0, 0.0, LmcVerdict.RevisedLoadsheetRequired)] // 4 pax ok, but 352 kg > 300
    [InlineData(-5, -300.0, 0.0, LmcVerdict.RevisedLoadsheetRequired)] // 5 pax > 4
    [InlineData(-4, -300.0, 0.0, LmcVerdict.WithinTolerance)]      // exactly the limits: not beyond
    [InlineData(0, 0.0, 0.6, LmcVerdict.RevisedLoadsheetRequired)]  // MAC shift alone
    [InlineData(0, 0.0, 0.5, LmcVerdict.WithinTolerance)]          // MAC exactly at the limit
    public void Verdict_FollowsTheToleranceEdges(int paxDelta, double zfwDelta, double macDelta, LmcVerdict expected)
    {
        var result = LmcEvaluator.Evaluate(Final(), Actual(paxDelta, zfwDelta, macTowDelta: macDelta), LmcTolerances.Default);

        Assert.Equal(expected, result.Verdict);
        Assert.Equal(paxDelta, result.DeltaPax);
        Assert.Equal(zfwDelta, result.DeltaZfwKg, 1);
        Assert.Equal(expected == LmcVerdict.RevisedLoadsheetRequired, result.Reasons.Count > 0);
    }

    [Fact]
    public void CargoOnlyChange_IsAnLmc_AndTheNewFiguresAreTheActualOnes_WithLawFromTheFinalsTripFuel()
    {
        var result = LmcEvaluator.Evaluate(Final(), Actual(cargoDelta: 120), LmcTolerances.Default);

        Assert.Equal(LmcVerdict.WithinTolerance, result.Verdict);
        Assert.Equal(120, result.DeltaCargoKg, 1);
        Assert.Equal(59120, result.NewZfwKg, 1);
        Assert.Equal(66120, result.NewTowKg, 1);
        Assert.Equal(66120 - 4400, result.NewLawKg, 1); // the final's trip fuel 66000 − 61600
        Assert.Equal(25.4, result.NewMacTow, 2);
    }

    [Fact]
    public void FuelTopUpBeyondTheWeightLimit_RequiresARevision_WithTheTowReason()
    {
        var result = LmcEvaluator.Evaluate(Final(), Actual(fuelDelta: 800), LmcTolerances.Default);

        Assert.Equal(LmcVerdict.RevisedLoadsheetRequired, result.Verdict);
        Assert.Contains(result.Reasons, r => r.StartsWith("take-off weight differs by 800 kg", StringComparison.Ordinal));
    }

    [Fact]
    public void FormatLmc_IsTheAirlineBlock_WithOptionalCargoFuelAndCaption()
    {
        var plain = LmcEvaluator.Evaluate(Final(), Actual(-2, -176), LmcTolerances.Default);
        Assert.Equal("LMC  PAX -2  ZFW 58.8  TOW 65.8  LAW 61.4  MACZFW 26.9  MACTOW 25.4", LoadsheetFormatter.FormatLmc(plain));

        var moved = LmcEvaluator.Evaluate(Final(), Actual(3, 264, cargoDelta: 120, fuelDelta: 200), LmcTolerances.Default);
        Assert.StartsWith("LMC  PAX +3  CGO +120  FUEL +200  ZFW 59.4  TOW 66.6", LoadsheetFormatter.FormatLmc(moved), StringComparison.Ordinal);

        var captioned = LoadsheetFormatter.FormatLmc(plain, LoadsheetFormatter.FlownOnCaptainsAuthorityCaption);
        Assert.EndsWith(Environment.NewLine + "REVISED LOADSHEET REQUIRED - FLOWN ON CAPTAINS AUTHORITY", captioned, StringComparison.Ordinal);
    }

    [Fact]
    public void SpokenLine_ReadsTheDeltasAndTheNewFigures_InvariantCulture()
    {
        var result = LmcEvaluator.Evaluate(Final(), Actual(-2, -176), LmcTolerances.Default);

        Assert.Equal(
            "Last minute change: minus 2 passengers, zero fuel weight 58.8 tonnes, take-off weight 65.8 tonnes, MAC 25.4 percent. "
            + "Within limits, no new loadsheet. Update the FMS zero fuel weight when ready, captain.",
            LmcEvaluator.SpokenWithinLimits(result));
        Assert.Equal("plus 1 passenger, cargo plus 120 kilos", LmcEvaluator.SpokenDeltas(LmcEvaluator.Evaluate(Final(), Actual(1, 88, cargoDelta: 120), LmcTolerances.Default)));
        Assert.Equal("Boarding differs from the final loadsheet by 7 passengers — beyond the last minute change limit. Issue a revised final?",
            LmcEvaluator.QuestionPrompt(LmcEvaluator.Evaluate(Final(), Actual(-7, -616), LmcTolerances.Default)));
        Assert.StartsWith("Boarding differs from the final loadsheet by 800 kilos", LmcEvaluator.QuestionPrompt(LmcEvaluator.Evaluate(Final(), Actual(fuelDelta: 800), LmcTolerances.Default)), StringComparison.Ordinal);
    }

    [Fact]
    public void Announcer_SpeaksWithinLimitsAndNoChangeOnly()
    {
        var within = LmcEvaluator.Evaluate(Final(), Actual(-2, -176), LmcTolerances.Default);
        var at = DateTimeOffset.UtcNow;

        Assert.StartsWith("Last minute change:", LmcAnnouncer.LineFor(new LoadsheetLmc(within, LmcDisposition.Applied, 1, at)), StringComparison.Ordinal);
        Assert.Equal("Boarding matches the final loadsheet. No last minute change.", LmcAnnouncer.LineFor(new LoadsheetLmc(within, LmcDisposition.NoChange, 1, at)));
        Assert.Null(LmcAnnouncer.LineFor(new LoadsheetLmc(within, LmcDisposition.AwaitingDecision, 1, at)));
        Assert.Null(LmcAnnouncer.LineFor(new LoadsheetLmc(within, LmcDisposition.RevisionIssued, 1, at)));
        Assert.Null(LmcAnnouncer.LineFor(new LoadsheetLmc(within, LmcDisposition.FlownOnCaptainsAuthority, 1, at)));
    }

    [Fact]
    public void FinalWithRevision_PrintsTheRevisionLine()
    {
        var ctx = new LoadsheetContext { EditionNumber = 2, Revision = "LMC PAX -7" };
        var text = LoadsheetFormatter.FormatFinal(ctx, Final(), Actual(-7, -616));

        Assert.Contains("REVISION  LMC PAX -7", text, StringComparison.Ordinal);
        Assert.DoesNotContain("REVISION", LoadsheetFormatter.FormatFinal(new LoadsheetContext(), Final(), Final()), StringComparison.Ordinal);
    }

    [Fact]
    public void Store_HoldsTheLmc_ChangesItsDisposition_AndResetClearsIt()
    {
        var store = new LoadsheetStore();
        var result = LmcEvaluator.Evaluate(Final(), Actual(-7, -616), LmcTolerances.Default);

        store.SetLmcDisposition(LmcDisposition.RevisionIssued); // no-op without an LMC
        Assert.Null(store.Snapshot().Lmc);

        store.SetLmc(new LoadsheetLmc(result, LmcDisposition.AwaitingDecision, 3, DateTimeOffset.UtcNow));
        store.SetLmcDisposition(LmcDisposition.FlownOnCaptainsAuthority);
        Assert.Equal(LmcDisposition.FlownOnCaptainsAuthority, store.Snapshot().Lmc!.Disposition);
        Assert.Equal(3, store.Snapshot().Lmc!.AgainstEdition);

        store.SetStdOverride(new TimeOnly(10, 30));
        store.Reset();
        Assert.Null(store.Snapshot().Lmc);
        Assert.Equal(new TimeOnly(10, 30), store.Snapshot().StdOverrideUtc); // the reset keeps the STD, as before
    }

    [Fact]
    public void RevisionQuestion_Definition_AndAsk()
    {
        var d = LmcRevisionStore.QuestionDefinition;
        Assert.Equal("loadsheetRevision", d.Topic);
        Assert.Equal(CaptainsQuestionShape.YesNo, d.Shape);
        Assert.Equal(CaptainsQuestionExpiry.CountsAsNo, d.Expiry);
        Assert.Equal(CaptainsQuestionDefinition.StandardTtl, d.DefaultTtl);
        Assert.False(d.AnswerIsFinalForCycle);

        using var store = new LmcRevisionStore(new GroundOpsSignals());
        Assert.True(store.Ask("Issue a revised final?"));
        Assert.False(store.Ask("Issue a revised final?"));
        Assert.True(store.Decline("timeout", "no answer"));
        Assert.True(store.Ask("Issue a revised final?")); // not final for the cycle
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("issue it", true)]
    [InlineData("revised final please", true)]
    [InlineData("send it", true)]
    [InlineData("no", false)]
    [InlineData("negative, we fly", false)]
    [InlineData("on my authority", false)]
    [InlineData("no new loadsheet", false)]
    [InlineData("stand by", null)]
    public void RevisionPhrases_Interpret(string heard, bool? expected)
    {
        Assert.Equal(expected, LmcRevisionPhrases.Interpret(heard));
        Assert.Equal("loadsheetRevision", LmcRevisionPhrases.Table.Topic);
        Assert.Equal("loadsheet-revision-question", LmcRevisionPhrases.Table.EventName);
        Assert.Equal(expected switch { true => QuestionReplyKind.Yes, false => QuestionReplyKind.No, null => (QuestionReplyKind?)null },
            LmcRevisionPhrases.Table.Interpret(heard, new CaptainsQuestion("loadsheetRevision", DateTimeOffset.UtcNow, "x", null, 1, [], null))?.Kind);
    }
}
