using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Commands;
using ProsimCompanion.Core.Commands.Handlers;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Deice;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Services;
using ProsimCompanion.Gsx.Sync;
using ProsimCompanion.Speech.Gsx;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Issue #166 parts 4 and 5: the FO's packs-off / ditching-on procedure (read-only),
/// the holdover card's fluid source, the de-icing activity seam, and the captain's voice
/// decline command.</summary>
public sealed class DeicePacksProcedureTests : IDisposable
{
    private readonly FakeArbiter _arbiter = new();
    private readonly FakePhaseSource _phase = new();
    private readonly FakeDataRefs _dataRefs = new();
    private readonly GroundOpsSignals _signals = new();
    private readonly DeiceActivityStore _activity;
    private readonly GsxOptions _options = new() { Deice = new GsxDeiceOptions { PacksProcedure = true } };

    public DeicePacksProcedureTests()
    {
        _activity = new DeiceActivityStore(_signals);
        _phase.SetLive(true);
    }

    public void Dispose() => _activity.Dispose();

    private DeicePacksProcedure Create()
    {
        var options = new Mock<IOptionsMonitor<GsxOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(() => _options);
        var procedure = new DeicePacksProcedure(
            _activity, _dataRefs, _phase, _arbiter, options.Object, SpeechTestSupport.TempEventLog(), NullLogger<DeicePacksProcedure>.Instance);
        procedure.Start();
        return procedure;
    }

    private void Lights(double pack1Off, double pack2Off, double ditchingOn)
    {
        _dataRefs.Values[ProsimDataRefNames.PneumaticPack1Indicator.Name] = pack1Off;
        _dataRefs.Values[ProsimDataRefNames.PneumaticPack2Indicator.Name] = pack2Off;
        _dataRefs.Values[ProsimDataRefNames.PneumaticDitchingIndicator.Name] = ditchingOn;
    }

    // ---- Pure core ----

    [Fact]
    public void Core_CallsAndConfiguration_PerStage()
    {
        Assert.Equal(DeicePacksProcedureCore.StartingCall, DeicePacksProcedureCore.CallFor(DeiceStage.Requested));
        Assert.Equal(DeicePacksProcedureCore.StartingCall, DeicePacksProcedureCore.CallFor(DeiceStage.Active));
        Assert.Equal(DeicePacksProcedureCore.CompleteCall, DeicePacksProcedureCore.CallFor(DeiceStage.Completed));
        Assert.Null(DeicePacksProcedureCore.CallFor(DeiceStage.None));
        Assert.True(DeicePacksProcedureCore.WantsTreatmentConfiguration(DeiceStage.Active));
        Assert.False(DeicePacksProcedureCore.WantsTreatmentConfiguration(DeiceStage.Completed));
    }

    [Fact]
    public void Core_Discrepancies_NameWhatIsWrong()
    {
        var treated = new DeicePacksProcedureCore.Lights(true, true, true);
        Assert.Empty(DeicePacksProcedureCore.Discrepancies(treated, treatment: true));
        Assert.Equal(["pack 1 still off", "pack 2 still off", "ditching still on"], DeicePacksProcedureCore.Discrepancies(treated, treatment: false));

        var normal = new DeicePacksProcedureCore.Lights(false, false, false);
        Assert.Equal(["pack 1 still on", "pack 2 still on", "ditching not on"], DeicePacksProcedureCore.Discrepancies(normal, treatment: true));
        Assert.Empty(DeicePacksProcedureCore.Discrepancies(normal, treatment: false));

        var halfway = new DeicePacksProcedureCore.Lights(true, false, true);
        Assert.Equal(["pack 2 still on"], DeicePacksProcedureCore.Discrepancies(halfway, treatment: true));
    }

    [Fact]
    public void Core_VerifyPhrase_ConfirmsOrRemindsOnce()
    {
        Assert.Equal("Packs off, ditching on — checked.", DeicePacksProcedureCore.VerifyPhrase([], treatment: true));
        Assert.Equal("Packs on, ditching off — checked.", DeicePacksProcedureCore.VerifyPhrase([], treatment: false));
        Assert.Equal(
            "Reminder — pack 2 still on. For de-icing: packs off, ditching on.",
            DeicePacksProcedureCore.VerifyPhrase(["pack 2 still on"], treatment: true));
    }

    // ---- Shell ----

    [Fact]
    public async Task RequestedEdge_SpeaksTheCall_ThenChecksTheLights()
    {
        using var procedure = Create();
        Lights(1, 1, 1);

        await procedure.RunAsync(DeiceStage.Requested, treatment: true, CancellationToken.None, verifyDelay: TimeSpan.Zero);

        Assert.Equal(DeicePacksProcedureCore.StartingCall, _arbiter.Requests[0].Text);
        Assert.Equal("Packs off, ditching on — checked.", _arbiter.Requests[1].Text);
        Assert.Empty(_dataRefs.Writes); // read-only, always
    }

    [Fact]
    public async Task CompletedEdge_RemindsOnce_WhenTheSwitchesWereNotReversed()
    {
        using var procedure = Create();
        Lights(1, 1, 1); // still in the treatment configuration

        await procedure.RunAsync(DeiceStage.Completed, treatment: false, CancellationToken.None, verifyDelay: TimeSpan.Zero);

        Assert.Equal(DeicePacksProcedureCore.CompleteCall, _arbiter.Requests[0].Text);
        Assert.Equal("Reminder — pack 1 still off, pack 2 still off, ditching still on. For de-icing: packs on, ditching off.", _arbiter.Requests[1].Text);
        Assert.Equal(2, _arbiter.Requests.Count);
    }

    [Fact]
    public async Task LightsNotReported_SkipsTheCheck_Silently()
    {
        using var procedure = Create();

        await procedure.RunAsync(DeiceStage.Active, treatment: true, CancellationToken.None, verifyDelay: TimeSpan.Zero);

        Assert.Single(_arbiter.Requests); // the call only
    }

    [Fact]
    public async Task Edges_FireOncePerCycle_AndOnlyWhenEnabled()
    {
        using var procedure = Create();
        _options.Deice.PacksProcedure = false;
        _activity.SetStage(DeiceStage.Requested);
        await Task.Delay(50);
        Assert.Empty(_arbiter.Requests);

        _signals.RaiseFlightCycleReset();
        _options.Deice.PacksProcedure = true;
        _activity.SetStage(DeiceStage.Requested);
        _activity.SetStage(DeiceStage.Active); // the same "starting" edge — not spoken twice
        await Task.Delay(100);
        Assert.Single(_arbiter.Requests);
        Assert.Equal(DeicePacksProcedureCore.StartingCall, _arbiter.Requests[0].Text);
    }

    // ---- Activity seam ----

    [Fact]
    public void ActivityStore_StagesNeverGoBackwards_AndResetWithTheCycle()
    {
        _activity.SetStage(DeiceStage.Active);
        _activity.SetStage(DeiceStage.Requested); // a late mirror edge
        Assert.Equal(DeiceStage.Active, _activity.Snapshot().Stage);
        Assert.True(_activity.Snapshot().InProgress);

        _activity.SetStage(DeiceStage.Completed);
        Assert.False(_activity.Snapshot().InProgress);

        _activity.RecordMenuAnswer(4, null);
        _activity.RecordMenuAnswer(null, 75);
        Assert.Equal((4, 75), (_activity.Snapshot().MenuFluidType, _activity.Snapshot().MenuConcentration));

        _signals.RaiseFlightCycleReset();
        Assert.Equal(DeiceActivitySnapshot.Empty, _activity.Snapshot());
    }

    [Fact]
    public void Relay_MapsTheLifecycleEdges_AndToleratesTheSpellings()
    {
        Assert.Equal(DeiceStage.Requested, GsxDeiceActivityRelay.StageFor(GsxServiceLifecycleEvent.Requested));
        Assert.Equal(DeiceStage.Active, GsxDeiceActivityRelay.StageFor(GsxServiceLifecycleEvent.Active));
        Assert.Equal(DeiceStage.Completed, GsxDeiceActivityRelay.StageFor(GsxServiceLifecycleEvent.Completed));
        Assert.True(GsxDeiceActivityRelay.IsDeiceService("DeIce"));
        Assert.True(GsxDeiceActivityRelay.IsDeiceService("De-Icing"));
        Assert.False(GsxDeiceActivityRelay.IsDeiceService("Boarding"));
    }

    // ---- Holdover fluid source (part 4) ----

    [Fact]
    public void ResolveFluid_PrefersTheLvar_ThenTheMenuAnswer_ThenTheSettingsWithAWarning()
    {
        Assert.Equal((1, 75, "GSX", false), DeiceHoldoverService.ResolveFluid(1, 4, 75, "Type IV", "75"));
        Assert.Equal((2, 100, "menu answer", false), DeiceHoldoverService.ResolveFluid(0, 2, 100, "Type IV", "75"));
        Assert.Equal((2, 75, "menu answer", false), DeiceHoldoverService.ResolveFluid(0, 2, null, "Type IV", "75"));
        Assert.Equal((4, 75, "settings (unconfirmed)", true), DeiceHoldoverService.ResolveFluid(0, null, null, "Type IV", "75"));
        Assert.Equal((1, 100, "settings (unconfirmed)", true), DeiceHoldoverService.ResolveFluid(9, null, null, "Type I", "garbage"));
    }

    // ---- The captain's later answer by voice (part 2) ----

    [Fact]
    public void DeclineCommand_AnswersAnOpenQuestion_AndRefusesAfterARequest()
    {
        using var store = new DeiceRequestStore(_signals);
        store.Publish(DeicePolicyVerdict.Ask, "snow", -2, "snow", "EFHK", "Request it?");

        var declined = DeiceCommandHandlers.Decline(store, "voice");
        Assert.Equal(CommandOutcome.Success, declined.Outcome);
        Assert.Equal("declined by the captain (voice)", store.Snapshot().Declined);
        Assert.Equal(CommandOutcome.AlreadySatisfied, DeiceCommandHandlers.Decline(store, "voice").Outcome);

        _signals.RaiseFlightCycleReset();
        store.Accept("web");
        Assert.Equal(CommandOutcome.PreconditionFailed, DeiceCommandHandlers.Decline(store, "voice").Outcome);
        Assert.Equal(CommandOutcome.AlreadySatisfied, DeiceCommandHandlers.Accept(store, "voice").Outcome);
        Assert.Equal(CommandOutcome.Unavailable, DeiceCommandHandlers.Decline(null, "voice").Outcome);
    }

    [Fact]
    public void DeclinePhrases_MapToTheCommand()
    {
        Assert.Equal("gsx.declineDeice", GsxVoicePhrases.Bindings["no de-icing"].Command);
        Assert.Equal("gsx.declineDeice", GsxVoicePhrases.Bindings["negative de-icing"].Command);
        Assert.Equal("gsx.requestDeice", GsxVoicePhrases.Bindings["request de-icing"].Command);
    }
}
