using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Weather;
using ProsimCompanion.Gsx.Automation;
using ProsimCompanion.Gsx.Mirror;
using ProsimCompanion.Gsx.Protocol;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>The de-icing auto-request policy (2026-10-09): verdicts from OAT + METAR +
/// options, the step-list shaping, and the hold/skip reasons the sequencer consumes.</summary>
public sealed class DeiceRequestPolicyTests
{
    private static DeiceRequestPolicy.Inputs Inputs(
        string mode = "auto",
        double threshold = 3,
        bool requirePrecip = true,
        double? oat = null,
        string? metar = null,
        string? icao = "EGLL")
        => new(mode, threshold, requirePrecip, oat, metar is null ? null : MetarParser.ToFacts(metar), icao);

    // ---- Verdicts ----

    [Fact]
    public void ModeOff_NeverDecides_EvenInSnow()
    {
        var result = DeiceRequestPolicy.Evaluate(Inputs(mode: "off", oat: -5, metar: "EGLL 090850Z 27010KT 2000 -SN OVC008 M05/M06 Q1002"));
        Assert.Equal(DeicePolicyVerdict.Off, result.Verdict);
    }

    [Fact]
    public void UnknownMode_ReadsAsOff()
    {
        var result = DeiceRequestPolicy.Evaluate(Inputs(mode: "sometimes", oat: -5, metar: "EGLL 090850Z 27010KT 2000 -SN OVC008 M05/M06 Q1002"));
        Assert.Equal(DeicePolicyVerdict.Off, result.Verdict);
    }

    [Fact]
    public void NoOatAnywhere_IsNoData()
    {
        var result = DeiceRequestPolicy.Evaluate(Inputs(oat: null, metar: null));
        Assert.Equal(DeicePolicyVerdict.NoData, result.Verdict);
        Assert.Contains("no outside air temperature", result.Reason);
    }

    [Fact]
    public void WarmOat_IsNotRequired_WhateverTheMetar()
    {
        var result = DeiceRequestPolicy.Evaluate(Inputs(oat: 8, metar: "EGLL 090850Z 27010KT 2000 -SN OVC008 M05/M06 Q1002"));
        Assert.Equal(DeicePolicyVerdict.NotRequired, result.Verdict);
        Assert.Contains("above the 3 °C threshold", result.Reason);
    }

    [Fact]
    public void ColdOat_WithSnow_Requests_InAutoMode()
    {
        var result = DeiceRequestPolicy.Evaluate(Inputs(oat: -2, metar: "EGLL 090850Z 27010KT 2000 -SN OVC008 M02/M03 Q1002"));
        Assert.Equal(DeicePolicyVerdict.Request, result.Verdict);
        Assert.Equal("snow", result.Evidence);
        Assert.Equal(-2, result.OatC);
        Assert.Equal("snow", result.Precip);
        Assert.Contains("ProSim OAT", result.Reason);
    }

    [Fact]
    public void ColdOat_WithSnow_Asks_InAskMode()
    {
        var result = DeiceRequestPolicy.Evaluate(Inputs(mode: "ask", oat: -2, metar: "EGLL 090850Z 27010KT 2000 -SN OVC008 M02/M03 Q1002"));
        Assert.Equal(DeicePolicyVerdict.Ask, result.Verdict);
        Assert.Contains("asking the captain", result.Reason);
        Assert.Equal("Captain, conditions call for de-icing — OAT -2, snow. Request it?", DeiceRequestPolicy.Prompt(result));
    }

    [Fact]
    public void ColdOat_DryMetar_WideSpread_IsNotRequired()
    {
        var result = DeiceRequestPolicy.Evaluate(Inputs(oat: 1, metar: "EGLL 090850Z 27010KT 9999 FEW030 01/M06 Q1030"));
        Assert.Equal(DeicePolicyVerdict.NotRequired, result.Verdict);
        Assert.Contains("no precipitation", result.Reason);
    }

    [Fact]
    public void ColdOat_DryMetar_NarrowSpread_IsFrost()
    {
        var result = DeiceRequestPolicy.Evaluate(Inputs(oat: 1, metar: "EGLL 090850Z 27010KT 9999 FEW030 01/M01 Q1030"));
        Assert.Equal(DeicePolicyVerdict.Request, result.Verdict);
        Assert.Contains("frost likely", result.Evidence);
    }

    [Fact]
    public void ColdOat_FreezingFog_IsContamination_ThoughPrecipReadsNone()
    {
        var facts = MetarParser.ToFacts("EGLL 090850Z 00000KT 0200 FZFG VV001 M01/M01 Q1030");
        Assert.Equal(PrecipKind.None, facts.Precip);

        var result = DeiceRequestPolicy.Evaluate(Inputs(oat: -1, metar: "EGLL 090850Z 00000KT 0200 FZFG VV001 M01/M06 Q1030"));
        Assert.Equal(DeicePolicyVerdict.Request, result.Verdict);
        Assert.Equal("freezing fog", result.Evidence);
        Assert.Equal("freezing fog", result.Precip);
    }

    [Fact]
    public void FreezingFog_InRemarksOnly_DoesNotCount()
    {
        var result = DeiceRequestPolicy.Evaluate(Inputs(oat: -1, metar: "EGLL 090850Z 00000KT 9999 FEW030 M01/M06 Q1030 RMK FZFG EARLIER"));
        Assert.Equal(DeicePolicyVerdict.NotRequired, result.Verdict);
    }

    [Fact]
    public void ColdRain_IsContamination()
    {
        var result = DeiceRequestPolicy.Evaluate(Inputs(oat: 2, metar: "EGLL 090850Z 27010KT 4000 -RA OVC008 02/00 Q1002"));
        Assert.Equal(DeicePolicyVerdict.Request, result.Verdict);
        Assert.StartsWith("rain at 2 °C", result.Evidence);
    }

    [Fact]
    public void FreezingRain_AndIcePellets_AreContamination()
    {
        Assert.Equal("freezing precipitation", DeiceRequestPolicy.Evaluate(Inputs(oat: -1, metar: "EGLL 090850Z 27010KT 4000 FZRA OVC008 M01/M02 Q1002")).Evidence);
        Assert.Equal("ice pellets or hail", DeiceRequestPolicy.Evaluate(Inputs(oat: -1, metar: "EGLL 090850Z 27010KT 4000 PL OVC008 M01/M02 Q1002")).Evidence);
    }

    [Fact]
    public void ColdOat_NoMetar_WithPrecipRequired_IsNoData()
    {
        var result = DeiceRequestPolicy.Evaluate(Inputs(oat: -4, metar: null));
        Assert.Equal(DeicePolicyVerdict.NoData, result.Verdict);
        Assert.Contains("no METAR for EGLL", result.Reason);
    }

    [Fact]
    public void ColdOat_NoMetar_WithoutPrecipRequired_Requests()
    {
        var result = DeiceRequestPolicy.Evaluate(Inputs(requirePrecip: false, oat: -4, metar: null));
        Assert.Equal(DeicePolicyVerdict.Request, result.Verdict);
        Assert.Equal("cold OAT", result.Evidence);
        Assert.Equal("unknown", result.Precip);
    }

    [Fact]
    public void MetarTemperature_IsTheFallback_WhenProsimReportsNoOat()
    {
        var result = DeiceRequestPolicy.Evaluate(Inputs(oat: null, metar: "EGLL 090850Z 27010KT 2000 -SN OVC008 M03/M04 Q1002"));
        Assert.Equal(DeicePolicyVerdict.Request, result.Verdict);
        Assert.Equal(-3, result.OatC);
        Assert.Contains("EGLL METAR", result.Reason);
    }

    [Fact]
    public void ThresholdIsInclusive_AndConfigurable()
    {
        var at = DeiceRequestPolicy.Evaluate(Inputs(threshold: 5, oat: 5, metar: "EGLL 090850Z 27010KT 2000 -SN OVC008 05/04 Q1002"));
        var above = DeiceRequestPolicy.Evaluate(Inputs(threshold: 5, oat: 5.5, metar: "EGLL 090850Z 27010KT 2000 -SN OVC008 05/04 Q1002"));
        Assert.Equal(DeicePolicyVerdict.Request, at.Verdict);
        Assert.Equal(DeicePolicyVerdict.NotRequired, above.Verdict);
    }

    [Fact]
    public void ReasonUsesWholeDegrees_SoALiveOatDoesNotChurnTheLog()
    {
        var a = DeiceRequestPolicy.Evaluate(Inputs(oat: -2.04, metar: "EGLL 090850Z 27010KT 2000 -SN OVC008 M02/M03 Q1002"));
        var b = DeiceRequestPolicy.Evaluate(Inputs(oat: -2.44, metar: "EGLL 090850Z 27010KT 2000 -SN OVC008 M02/M03 Q1002"));
        Assert.Equal(a.Reason, b.Reason);
    }

    // ---- Step-list shaping ----

    private static readonly DateTimeOffset Now = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);

    private static DeiceRequestSnapshot Requested()
        => DeiceRequestSnapshot.Empty with { Verdict = DeicePolicyVerdict.Request, RequestThisCycle = true };

    private static DeiceRequestSnapshot Asked(DateTimeOffset askedAt)
        => DeiceRequestSnapshot.Empty with { Verdict = DeicePolicyVerdict.Ask, Question = new DeiceQuestion(askedAt, "Request it?") };

    private static List<DepartureServiceStep> Classic()
        => [.. GsxOptions.DefaultDepartureServices];

    [Fact]
    public void NotRequested_ReturnsTheConfiguredListUntouched()
    {
        var configured = Classic();
        var steps = DeiceRequestPolicy.EffectiveSteps(configured, DeiceRequestSnapshot.Empty, Now);
        Assert.Same(configured, steps);
    }

    [Fact]
    public void Requested_WithNoDeiceStep_AppendsOneLast_AfterAllCompleted()
    {
        var configured = Classic();
        var steps = DeiceRequestPolicy.EffectiveSteps(configured, Requested(), Now);

        Assert.Equal(configured.Count + 1, steps.Count);
        var last = steps[^1];
        Assert.Equal(GsxServiceIds.DeIce, last.Service);
        Assert.Equal(GsxServiceActivation.AfterAllCompleted, last.Activation);
        Assert.DoesNotContain(configured, s => s.Service == GsxServiceIds.DeIce); // never mutated
    }

    [Fact]
    public void Requested_WithASkippedDeiceStep_MovesItLast_AndWakesIt()
    {
        var configured = Classic();
        configured.Insert(2, new DepartureServiceStep(GsxServiceIds.DeIce, GsxServiceActivation.Skip));

        var steps = DeiceRequestPolicy.EffectiveSteps(configured, Requested(), Now);

        Assert.Equal(configured.Count, steps.Count);
        Assert.Equal(GsxServiceIds.DeIce, steps[^1].Service);
        Assert.Equal(GsxServiceActivation.AfterAllCompleted, steps[^1].Activation);
        Assert.Single(steps, s => s.Service == GsxServiceIds.DeIce);
        Assert.Equal(GsxServiceActivation.Skip, configured[2].Activation); // the setting is untouched
    }

    [Fact]
    public void Requested_WithAManualDeiceStep_LeavesThePilotsRowAlone()
    {
        var configured = Classic();
        configured.Insert(2, new DepartureServiceStep(GsxServiceIds.DeIce, GsxServiceActivation.Manual));

        var steps = DeiceRequestPolicy.EffectiveSteps(configured, Requested(), Now);

        Assert.Equal(configured.Count, steps.Count);
        Assert.Equal(GsxServiceIds.DeIce, steps[2].Service);
        Assert.Equal(GsxServiceActivation.Manual, steps[2].Activation);
    }

    [Fact]
    public void OpenQuestion_PlacesTheStep_AndHoldsIt_UntilTheTtlPasses()
    {
        var asked = Asked(Now);
        var steps = DeiceRequestPolicy.EffectiveSteps(Classic(), asked, Now + TimeSpan.FromMinutes(1));
        Assert.Equal(GsxServiceIds.DeIce, steps[^1].Service);
        Assert.NotNull(DeiceRequestPolicy.HoldReason(asked, Now + TimeSpan.FromMinutes(1)));

        var late = Now + DeiceRequestSnapshot.QuestionTtl + TimeSpan.FromSeconds(1);
        Assert.Equal(Classic().Count, DeiceRequestPolicy.EffectiveSteps(Classic(), asked, late).Count);
        Assert.Null(DeiceRequestPolicy.HoldReason(asked, late));
    }

    [Fact]
    public void Declined_SkipsAConfiguredStep_AndPlacesNone()
    {
        var declined = DeiceRequestSnapshot.Empty with { Verdict = DeicePolicyVerdict.Ask, Declined = "captain said 'negative'" };
        Assert.Equal("de-icing declined: captain said 'negative'", DeiceRequestPolicy.SkipReason(declined));
        Assert.Null(DeiceRequestPolicy.SkipReason(Requested()));
        Assert.Equal(Classic().Count, DeiceRequestPolicy.EffectiveSteps(Classic(), declined, Now).Count);
    }

    // ---- Through the sequencer: the appended step is really last and really held ----

    [Fact]
    public void Sequencer_HoldsTheQuestionStep_WithoutBlockingBoarding_ThenCallsItLast()
    {
        var asked = Asked(Now);
        var steps = DeiceRequestPolicy.EffectiveSteps(
            [new("Refueling", GsxServiceActivation.AfterCalled), new("Boarding", GsxServiceActivation.AfterAllCompleted)],
            asked,
            Now);
        var services = new Dictionary<string, GsxServiceInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["Refueling"] = new("Refueling", "Refueling", null, GsxServiceState.Completed, false, false, null, null),
            ["Boarding"] = new("Boarding", "Boarding", null, GsxServiceState.Callable, true, false, null, null),
            ["DeIce"] = new("DeIce", "De-icing", null, GsxServiceState.Callable, true, false, null, null),
        };
        var done = new DepartureCycleView(true, true, true, true);

        var plan = DepartureSequencer.Next(
            steps, services, id => id == "Refueling" ? done : default,
            awaitingConfirmation: null, flightPlanAvailable: true, requireOfp: true, isTurnaround: false, forceNext: false,
            preHold: id => id == GsxServiceIds.DeIce ? DeiceRequestPolicy.HoldReason(asked, Now) : null);

        Assert.Equal("Boarding", plan.Trigger); // the open question never holds boarding
        Assert.Contains(plan.Holds, h => h.ServiceId == GsxServiceIds.DeIce && h.Reason.Contains("captain's answer"));
        Assert.False(plan.AllDone);

        // Answered yes, boarding done: de-icing is the last call.
        var accepted = asked with { RequestThisCycle = true, Question = null };
        var plan2 = DepartureSequencer.Next(
            DeiceRequestPolicy.EffectiveSteps([new("Refueling", GsxServiceActivation.AfterCalled), new("Boarding", GsxServiceActivation.AfterAllCompleted)], accepted, Now),
            services, id => id is "Refueling" or "Boarding" ? done : default,
            awaitingConfirmation: null, flightPlanAvailable: true, requireOfp: true, isTurnaround: false, forceNext: false,
            preHold: id => id == GsxServiceIds.DeIce ? DeiceRequestPolicy.HoldReason(accepted, Now) : null);

        Assert.Equal(GsxServiceIds.DeIce, plan2.Trigger);
    }
}
