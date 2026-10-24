using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Tests.Localization;
using ProsimCompanion.Core.Weather;
using ProsimCompanion.Speech.Cabin;
using ProsimCompanion.Web.Components;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Issue #186: the cabin's turbulence reactions, the PA prompt timing, the purser PA
/// edges, the question words, the prompt store and the script fill-in.</summary>
public sealed class TurbulenceCabinTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static CabinOptions On() => new()
    {
        Enabled = true, TurbulenceDetection = true, TurbulenceSeatbeltAdvice = true, TurbulencePurserCall = true,
        PaPrompts = true, PaAfterTakeoffMinutes = 10, PurserPaAudible = true,
    };

    private static TurbulenceCabinSample Ride(TurbulenceLevel level, bool seatbeltOn = false, bool ended = false, DateTimeOffset? at = null)
        => new(level, level != TurbulenceLevel.None || ended, ended, seatbeltOn, true, at ?? T0);

    // ---- reactions ----

    [Fact]
    public void Light_WithSignOff_AdvisesSeatbeltsOnce_NotWhenTheSignIsOn()
    {
        var core = new TurbulenceCabinCore();
        Assert.Equal(TurbulenceCabinAction.SeatbeltAdvice, core.Evaluate(Ride(TurbulenceLevel.Light), On()));
        Assert.Equal(TurbulenceCabinAction.None, core.Evaluate(Ride(TurbulenceLevel.Light), On()));

        var fresh = new TurbulenceCabinCore();
        Assert.Equal(TurbulenceCabinAction.None, fresh.Evaluate(Ride(TurbulenceLevel.Light, seatbeltOn: true), On()));
    }

    [Fact]
    public void Moderate_CallsThePurserOnce_ThenAdvisesSeatbelts_ThenNothing()
    {
        var core = new TurbulenceCabinCore();
        Assert.Equal(TurbulenceCabinAction.PurserCall, core.Evaluate(Ride(TurbulenceLevel.Moderate), On()));
        Assert.Equal(TurbulenceCabinAction.SeatbeltAdvice, core.Evaluate(Ride(TurbulenceLevel.Moderate), On()));
        Assert.Equal(TurbulenceCabinAction.None, core.Evaluate(Ride(TurbulenceLevel.Moderate), On()));
    }

    [Fact]
    public void Severe_ReportsCrewSeated_AndSkipsTheQuestion()
    {
        var core = new TurbulenceCabinCore();
        Assert.Equal(TurbulenceCabinAction.SevereReport, core.Evaluate(Ride(TurbulenceLevel.Severe, seatbeltOn: true), On()));
        Assert.Equal(TurbulenceCabinAction.None, core.Evaluate(Ride(TurbulenceLevel.Severe, seatbeltOn: true), On()));
        Assert.Equal(TurbulenceCabinAction.None, core.Evaluate(Ride(TurbulenceLevel.Moderate, seatbeltOn: true), On()));
    }

    [Fact]
    public void Yes_SchedulesTheSecuredReport_WhichFiresAtItsTime_Once()
    {
        var core = new TurbulenceCabinCore();
        core.Evaluate(Ride(TurbulenceLevel.Moderate, seatbeltOn: true), On());
        core.OnSecureAccepted(T0, TimeSpan.FromSeconds(45));
        Assert.Equal(T0.AddSeconds(45), core.SecuredDueAtUtc);
        Assert.Equal(TurbulenceCabinAction.None, core.Evaluate(Ride(TurbulenceLevel.Moderate, seatbeltOn: true, at: T0.AddSeconds(44)), On()));
        Assert.Equal(TurbulenceCabinAction.SecuredReport, core.Evaluate(Ride(TurbulenceLevel.Moderate, seatbeltOn: true, at: T0.AddSeconds(45)), On()));
        Assert.Equal(TurbulenceCabinAction.None, core.Evaluate(Ride(TurbulenceLevel.Moderate, seatbeltOn: true, at: T0.AddSeconds(46)), On()));
        Assert.Null(core.SecuredDueAtUtc);
    }

    [Fact]
    public void SecuredDelay_IsThirtyToNinetySeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), TurbulenceCabinCore.SecuredDelay(() => 0));
        Assert.Equal(TimeSpan.FromSeconds(90), TurbulenceCabinCore.SecuredDelay(() => 1));
        Assert.Equal(TimeSpan.FromSeconds(60), TurbulenceCabinCore.SecuredDelay(() => 0.5));
    }

    [Fact]
    public void EpisodeEnd_ReportsBackToNormal_OnlyAfterAPurserReaction_AndReArmsEverything()
    {
        var core = new TurbulenceCabinCore();
        core.Evaluate(Ride(TurbulenceLevel.Moderate, seatbeltOn: true), On()); // purser call
        Assert.Equal(TurbulenceCabinAction.BackToNormal, core.Evaluate(Ride(TurbulenceLevel.None, ended: true), On()));
        // Next episode asks again.
        Assert.Equal(TurbulenceCabinAction.PurserCall, core.Evaluate(Ride(TurbulenceLevel.Moderate, seatbeltOn: true), On()));

        var adviceOnly = new TurbulenceCabinCore();
        adviceOnly.Evaluate(Ride(TurbulenceLevel.Light), On()); // seat-belt advice is the FO's, not the cabin's
        Assert.Equal(TurbulenceCabinAction.None, adviceOnly.Evaluate(Ride(TurbulenceLevel.None, ended: true), On()));
    }

    [Fact]
    public void SwitchesOff_DoNothing()
    {
        var options = On();
        options.TurbulenceSeatbeltAdvice = false;
        options.TurbulencePurserCall = false;
        var core = new TurbulenceCabinCore();
        Assert.Equal(TurbulenceCabinAction.None, core.Evaluate(Ride(TurbulenceLevel.Severe), options));
    }

    // ---- the question words ----

    [Theory]
    [InlineData("yes", true)]
    [InlineData("secure the cabin", true)]
    [InlineData("crew be seated please", true)]
    [InlineData("no, carry on", false)]
    [InlineData("continue the service", false)]
    [InlineData("negative", false)]
    [InlineData("say again", null)]
    [InlineData("what's the weather", null)]
    public void SecureQuestion_InterpretsYesNoAndNotAnAnswer(string heard, bool? expected)
    {
        Assert.Equal(expected, CabinSecureQuestionPhrases.Interpret(heard));
    }

    // ---- PA prompts ----

    [Fact]
    public void AfterTakeoff_PromptsAtTheMinute_InClimbOrCruise_OncePerCycle()
    {
        var core = new PaPromptCore();
        var off = T0;
        PaPromptSample At(DateTimeOffset now, FlightPhase phase = FlightPhase.Climb) => new(off, phase, false, TurbulenceLevel.None, false, now);

        Assert.Null(core.Evaluate(At(off.AddMinutes(9.9)), On(), []));
        Assert.Equal(PaPromptKind.AfterTakeoff, core.Evaluate(At(off.AddMinutes(10)), On(), []));
        Assert.Null(core.Evaluate(At(off.AddMinutes(11)), On(), [PaPromptKind.AfterTakeoff]));
        Assert.Null(core.Evaluate(At(off.AddMinutes(11), FlightPhase.Descent), On(), []));
    }

    [Fact]
    public void TopOfDescent_PromptsOnTheSignal_Once()
    {
        var core = new PaPromptCore();
        var sample = new PaPromptSample(T0, FlightPhase.Cruise, true, TurbulenceLevel.None, false, T0.AddMinutes(1));
        Assert.Equal(PaPromptKind.TopOfDescent, core.Evaluate(sample, On(), []));
        Assert.Null(core.Evaluate(sample, On(), [PaPromptKind.TopOfDescent]));
        Assert.Null(core.Evaluate(sample, On(), [PaPromptKind.TopOfDescent, PaPromptKind.AfterTakeoff]));
    }

    [Fact]
    public void SeatbeltsOn_PromptsOnlyOnTheEdge_InCruiseTurbulence()
    {
        var core = new PaPromptCore();
        PaPromptSample S(bool on, TurbulenceLevel level, FlightPhase phase = FlightPhase.Cruise)
            => new(T0.AddHours(-1), phase, false, level, on, T0);
        var done = new[] { PaPromptKind.AfterTakeoff };

        Assert.Null(core.Evaluate(S(false, TurbulenceLevel.Light), On(), done));
        Assert.Equal(PaPromptKind.SeatbeltsOn, core.Evaluate(S(true, TurbulenceLevel.Light), On(), done));
        Assert.Null(core.Evaluate(S(true, TurbulenceLevel.Light), On(), [.. done, PaPromptKind.SeatbeltsOn]));

        var calm = new PaPromptCore();
        calm.Evaluate(S(false, TurbulenceLevel.None), On(), done);
        Assert.Null(calm.Evaluate(S(true, TurbulenceLevel.None), On(), done)); // smooth air: no prompt

        var climb = new PaPromptCore();
        climb.Evaluate(S(false, TurbulenceLevel.Light, FlightPhase.Climb), On(), done);
        Assert.Null(climb.Evaluate(S(true, TurbulenceLevel.Light, FlightPhase.Climb), On(), done));
    }

    [Fact]
    public void PaPromptsOff_PromptsNothing()
    {
        var options = On();
        options.PaPrompts = false;
        Assert.Null(new PaPromptCore().Evaluate(new PaPromptSample(T0.AddHours(-1), FlightPhase.Cruise, true, TurbulenceLevel.None, false, T0), options, []));
    }

    [Fact]
    public void PromptStore_OncePerKind_DismissAndReopen_ClearOnTheCycle()
    {
        var signals = new GroundOpsSignals();
        var store = new PaPromptStore(signals);
        Assert.True(store.Prompt(PaPromptKind.AfterTakeoff, PaScriptFacts.Empty, T0));
        Assert.False(store.Prompt(PaPromptKind.AfterTakeoff, PaScriptFacts.Empty, T0.AddMinutes(1)));
        Assert.True(store.Snapshot().Visible);
        store.Dismiss();
        Assert.False(store.Snapshot().Visible);
        Assert.Equal(PaPromptKind.AfterTakeoff, store.Snapshot().Kind);
        store.Reopen();
        Assert.True(store.Snapshot().Visible);
        Assert.True(store.Prompt(PaPromptKind.TopOfDescent, PaScriptFacts.Empty, T0.AddHours(1)));
        Assert.Equal(2, store.Snapshot().Done.Count);
        signals.RaiseFlightCycleReset();
        Assert.Equal(PaPromptSnapshot.Empty, store.Snapshot());
    }

    // ---- purser PAs ----

    [Fact]
    public void PurserPa_FiresOnEachEdgeOnce_AndNeverOnTheFirstSample()
    {
        var core = new PurserPaCore();
        PurserPaSample S(FlightPhase phase, bool beacon, bool enginesOff, bool parked, bool flown, double alt)
            => new(phase, beacon, enginesOff, parked, flown, alt);

        Assert.Null(core.Evaluate(S(FlightPhase.Preflight, true, true, true, false, 300))); // priming: beacon already on
        Assert.Null(core.Evaluate(S(FlightPhase.Preflight, false, true, true, false, 300)));
        Assert.Equal(PurserPaKind.ArmDoors, core.Evaluate(S(FlightPhase.Preflight, true, true, true, false, 300)));
        Assert.Null(core.Evaluate(S(FlightPhase.Preflight, false, true, true, false, 300)));
        Assert.Null(core.Evaluate(S(FlightPhase.Preflight, true, true, true, false, 300))); // once

        // Cruise → descent through 10,000 ft.
        core.Evaluate(S(FlightPhase.Cruise, true, false, false, true, 35000));
        core.Evaluate(S(FlightPhase.Descent, true, false, false, true, 10050));
        Assert.Equal(PurserPaKind.PrepareLanding, core.Evaluate(S(FlightPhase.Descent, true, false, false, true, 9950)));
        core.Evaluate(S(FlightPhase.Approach, true, false, false, true, 10100)); // a climb back up
        Assert.Null(core.Evaluate(S(FlightPhase.Approach, true, false, false, true, 9900)));

        // Engines off on stand after the flight.
        core.Evaluate(S(FlightPhase.TaxiIn, true, false, false, true, 300));
        Assert.Equal(PurserPaKind.DoorsManual, core.Evaluate(S(FlightPhase.Shutdown, false, true, true, true, 300)));
        Assert.Equal(3, core.Done.Count);

        core.Reset();
        Assert.Empty(core.Done);
    }

    [Fact]
    public void PurserPa_Climbing_Through10k_IsNotADescent()
    {
        var core = new PurserPaCore();
        core.Evaluate(new PurserPaSample(FlightPhase.Climb, true, false, false, true, 9900));
        Assert.Null(core.Evaluate(new PurserPaSample(FlightPhase.Climb, true, false, false, true, 10100)));
        core.Evaluate(new PurserPaSample(FlightPhase.Climb, true, false, false, true, 10100));
        Assert.Null(core.Evaluate(new PurserPaSample(FlightPhase.Climb, true, false, false, true, 9900))); // not in descent
    }

    // ---- the script ----

    private static readonly IStringLocalizer T = new ServiceCollection().AddTestLocalizer()
        .BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IStringLocalizer>();

    [Fact]
    public void ScriptFacts_FromThePlanProgressAndWeather()
    {
        var ofp = new OfpData
        {
            FlightNumber = "BAW123", DestinationName = "Rome Fiumicino", DestinationIcao = "LIRF",
            EstimatedEnroute = TimeSpan.FromMinutes(112), CruiseFlightLevel = 370,
        };
        var progress = new FlightProgressSnapshot { EtaUtc = T0.AddHours(2) };
        var wx = WeatherCard.From(WeatherCardRole.Destination, "LIRF", "Rome Fiumicino",
            WxProbe.Found(new WxFacts("LIRF 241150Z 24012KT 9999 FEW030 21/12 Q1016", 240, 12, 9999, 1016, 21, null, null)));

        var facts = PaScriptFacts.From(ofp, progress, wx);

        Assert.Equal("BAW123", facts.FlightNumber);
        Assert.Equal("Rome Fiumicino", facts.DestinationName);
        Assert.Equal(112, facts.FlightMinutes);
        Assert.Equal(370, facts.CruiseFlightLevel);
        Assert.Equal(T0.AddHours(2), facts.EtaUtc);
        Assert.Equal(21, facts.WeatherTemperatureC);
        Assert.Equal(240, facts.WeatherWindDirDeg);
        Assert.NotNull(facts.WeatherSkyLabel);

        var lines = PaScriptPresentation.Lines(PaPromptKind.AfterTakeoff, facts, T);
        Assert.Equal("Good day, ladies and gentlemen, this is your captain. Welcome aboard flight BAW123 to Rome Fiumicino.", lines[0]);
        Assert.Contains("1 h 52 min", lines[1], StringComparison.Ordinal);
        Assert.Contains("37,000 feet", lines[2], StringComparison.Ordinal);
        Assert.Contains("Rome Fiumicino", lines[3], StringComparison.Ordinal);
        Assert.Contains("21 degrees", lines[3], StringComparison.Ordinal);
        Assert.Contains("wind 240 at 12 knots", lines[3], StringComparison.Ordinal);
        Assert.Contains("14:00Z", lines[4], StringComparison.Ordinal);
        Assert.Equal(6, lines.Count);
    }

    [Fact]
    public void ScriptWithNoFacts_KeepsOnlyTheSentencesThatNeedNone()
    {
        Assert.Equal(PaScriptFacts.Empty, PaScriptFacts.From(null, null, null));

        var lines = PaScriptPresentation.Lines(PaPromptKind.AfterTakeoff, PaScriptFacts.Empty, T);
        Assert.Equal(2, lines.Count);
        Assert.StartsWith("Good day, ladies and gentlemen", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("—", string.Join(" ", lines), StringComparison.Ordinal);

        var descent = PaScriptPresentation.Lines(PaPromptKind.TopOfDescent, PaScriptFacts.Empty, T);
        Assert.Equal(3, descent.Count);
        Assert.Equal(3, PaScriptPresentation.Lines(PaPromptKind.SeatbeltsOn, PaScriptFacts.Empty, T).Count);
    }

    [Theory]
    [InlineData(48, "48 min")]
    [InlineData(60, "1 h 00 min")]
    [InlineData(112, "1 h 52 min")]
    public void Duration_Reads(int minutes, string expected)
    {
        Assert.Equal(expected, PaScriptPresentation.Duration(minutes));
    }
}
