using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Commands;
using ProsimCompanion.Core.Commands.Handlers;
using ProsimCompanion.Core.Day;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Menu;
using Xunit;

namespace ProsimCompanion.Core.Tests.Flight;

/// <summary>Issue #166, the restart fault (EGLL 2026-11-14, sessions 035435 and 043241): the
/// captain said "no de-icing" at 04:20:45Z, the app was restarted at the stand at 04:32:42Z on
/// the same plan (request 193487122), and at 04:40:51Z GSX's own offer was answered "Yes" on
/// "policy verdict Pending". The answer is now a part of the flight-state snapshot.</summary>
public sealed class DeiceVerdictSnapshotPartTests : IAsyncDisposable
{
    private const string FlightPlanRequest = "193487122";
    private const string AskReason = "OAT -1 °C (ProSim OAT) is at or below the 20 °C threshold; precipitation not required by policy — asking the captain";
    private static readonly DateTimeOffset OfferShownUtc = new(2026, 11, 14, 4, 40, 51, TimeSpan.Zero);

    private readonly JsonlEventLog _eventLog = new(Directory.CreateTempSubdirectory("pc-tests-").FullName, NullLogger<JsonlEventLog>.Instance);
    private readonly List<IDisposable> _disposables = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        await _eventLog.DisposeAsync();
    }

    private static OfpData Ofp(string requestId = FlightPlanRequest) => new()
    {
        RequestId = requestId,
        OriginIcao = "EGLL",
        DestinationIcao = "LOWW",
        FlightNumber = "BAW722",
    };

    /// <summary>One app session: its own stores and its own part.</summary>
    private sealed record Session(GroundOpsSignals Signals, DeiceRequestStore Store, OfpStore Ofp, DutyDaySignals DutyDay, DeiceVerdictSnapshotPart Part);

    private Session NewSession(OfpData? ofp)
    {
        var signals = new GroundOpsSignals();
        var store = new DeiceRequestStore(signals);
        var ofpStore = new OfpStore();
        if (ofp is not null)
        {
            ofpStore.Set(ofp);
        }

        var dutyDay = new DutyDaySignals();
        var part = new DeiceVerdictSnapshotPart(store, ofpStore, _eventLog, NullLogger<DeiceVerdictSnapshotPart>.Instance, dutyDay);
        _disposables.Add(part);
        _disposables.Add(store);
        return new Session(signals, store, ofpStore, dutyDay, part);
    }

    /// <summary>The first session of the flight up to 04:20:45Z: the step's turn, the Ask
    /// verdict, "no de-icing" by voice.</summary>
    private Session SessionWithTheCaptainsNo()
    {
        var session = NewSession(Ofp());
        session.Store.MarkStepNext();
        session.Store.Publish(DeicePolicyVerdict.Ask, AskReason, -0.98, "none", "EGLL", "Captain, conditions call for de-icing — OAT -1, cold OAT. Request it?");
        Assert.Equal(CommandOutcome.Success, DeiceCommandHandlers.Decline(session.Store, "voice").Outcome);
        return session;
    }

    /// <summary>The node as the file holds it: through text, as a restart reads it.</summary>
    private static JsonNode ThroughTheFile(JsonNode? captured)
    {
        Assert.NotNull(captured);
        return JsonNode.Parse(captured.ToJsonString(FlightStateSnapshotStore.JsonOptions))!;
    }

    [Fact]
    public void ThePart_IsNamed_AndRestoresAfterTheOfpAndTheDepartureCycle()
    {
        IFlightStatePart part = NewSession(null).Part;

        Assert.Equal("deiceVerdict", part.Key);
        Assert.Equal(55, part.RestoreOrder);
        Assert.False(part.PersistsAcrossLegs);
    }

    [Fact]
    public void WithoutTheRestore_TheOfferIsAnsweredYes_TheFlightsFault()
    {
        // 04:40:51Z: "answered: picked 'Yes' — policy verdict Pending — GSX's own icing judgement stands".
        var restarted = NewSession(Ofp());

        Assert.Equal(DeicePolicyVerdict.Pending, restarted.Store.Snapshot().Verdict);
        Assert.True(GsxQuestionCatalog.IceWarningAnswer(restarted.Store.Snapshot(), OfferShownUtc));
    }

    [Fact]
    public void TheCaptainsNo_SurvivesTheRestart_AndAnswersNoToGsxsOffer()
    {
        var saved = ThroughTheFile(SessionWithTheCaptainsNo().Part.Capture());

        // 04:32:42Z: a new process. The OFP part (order 10) put the same plan back first.
        var restarted = NewSession(Ofp());
        var answeredEdges = 0;
        var raised = 0;
        restarted.Store.Answered += (_, _) => answeredEdges++;
        restarted.Store.QuestionRaised += (_, _) => raised++;
        restarted.Part.Restore(saved);

        var snapshot = restarted.Store.Snapshot();
        Assert.Equal("declined by the captain (voice)", snapshot.Declined);
        Assert.False(snapshot.RequestThisCycle);
        Assert.Equal(DeicePolicyVerdict.Ask, snapshot.Verdict);
        Assert.Equal(AskReason, snapshot.Reason);
        Assert.Equal("EGLL", snapshot.Icao);
        Assert.True(snapshot.StepNext);
        Assert.Null(snapshot.Question);
        Assert.False(snapshot.Undecided);
        Assert.Equal("voice", snapshot.Asking.Answer!.Source);

        // 04:40:51Z: GSX's "Ice warning" offer.
        Assert.False(GsxQuestionCatalog.IceWarningAnswer(snapshot, OfferShownUtc));

        // Nothing is announced or logged a second time: the answer was given in the first session.
        Assert.Equal(0, answeredEdges);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void TheRestoredNo_StandsWhenThePolicyRunsAgain_AndIsWrittenAgain()
    {
        var saved = ThroughTheFile(SessionWithTheCaptainsNo().Part.Capture());
        var restarted = NewSession(Ofp());
        restarted.Part.Restore(saved);

        // The policy service evaluates again after the start: the same Ask verdict.
        var raised = 0;
        restarted.Store.QuestionRaised += (_, _) => raised++;
        restarted.Store.MarkStepNext();
        restarted.Store.Publish(DeicePolicyVerdict.Ask, AskReason, -1.2, "none", "EGLL", "Request it?");

        Assert.Equal(0, raised);
        Assert.Equal("declined by the captain (voice)", restarted.Store.Snapshot().Declined);

        // A second restart finds it again, for the same plan.
        var again = restarted.Part.Capture().Deserialize<DeiceVerdictMemory>(FlightStateSnapshotStore.JsonOptions);
        Assert.NotNull(again);
        Assert.False(again.Accepted);
        Assert.Equal("req:" + FlightPlanRequest, again.PlanId);
    }

    [Fact]
    public void ACapturedAnswer_CarriesThePlanItWasGivenFor()
    {
        var memory = SessionWithTheCaptainsNo().Part.Capture().Deserialize<DeiceVerdictMemory>(FlightStateSnapshotStore.JsonOptions);

        Assert.NotNull(memory);
        Assert.Equal("req:" + FlightPlanRequest, memory.PlanId);
        Assert.False(memory.Accepted);
        Assert.Equal("voice", memory.Source);
        Assert.Equal("declined by the captain (voice)", memory.Reason);
        Assert.Equal(DeicePolicyVerdict.Ask, memory.Verdict);
        Assert.True(memory.StepNext);
    }

    [Fact]
    public void NothingIsKept_WithoutAnAnswer()
    {
        var session = NewSession(Ofp());
        Assert.Null(session.Part.Capture());

        // An open question is not an answer: the policy asks again after a restart.
        session.Store.MarkStepNext();
        session.Store.Publish(DeicePolicyVerdict.Ask, AskReason, -0.98, "none", "EGLL", "Request it?");
        Assert.Null(session.Part.Capture());

        session.Store.Publish(DeicePolicyVerdict.NotRequired, "OAT 12 °C is above the threshold", 12, "none", "EGLL", "Request it?");
        Assert.Null(session.Part.Capture());
    }

    [Fact]
    public void AYes_AndThePolicysOwnRequest_AreRestoredToo()
    {
        var first = NewSession(Ofp());
        first.Store.Publish(DeicePolicyVerdict.Ask, AskReason, -0.98, "none", "EGLL", "Request it?");
        first.Store.Accept("web");
        var restarted = NewSession(Ofp());
        restarted.Part.Restore(ThroughTheFile(first.Part.Capture()));

        Assert.True(restarted.Store.Snapshot().RequestThisCycle);
        Assert.Null(restarted.Store.Snapshot().Declined);
        Assert.True(GsxQuestionCatalog.IceWarningAnswer(restarted.Store.Snapshot(), OfferShownUtc));

        var auto = NewSession(Ofp());
        auto.Store.Publish(DeicePolicyVerdict.Request, "snow at -2 °C", -2, "snow", "EGLL", "Request it?");
        var restartedAuto = NewSession(Ofp());
        restartedAuto.Part.Restore(ThroughTheFile(auto.Part.Capture()));

        Assert.True(restartedAuto.Store.Snapshot().RequestThisCycle);
        Assert.Equal("policy", restartedAuto.Store.Snapshot().Asking.Answer!.Source);
        Assert.Equal(DeicePolicyVerdict.Request, restartedAuto.Store.Snapshot().Verdict);
    }

    [Fact]
    public void AnAnswerForAnotherPlan_IsNotRestored()
    {
        var saved = ThroughTheFile(SessionWithTheCaptainsNo().Part.Capture());

        var nextLeg = NewSession(Ofp("193499001"));
        nextLeg.Part.Restore(saved);

        Assert.Equal(DeiceRequestSnapshot.Empty, nextLeg.Store.Snapshot());
        Assert.Null(nextLeg.Part.Capture());
    }

    [Fact]
    public void AnAnswerThisSessionAlreadyHas_IsKept()
    {
        var saved = ThroughTheFile(SessionWithTheCaptainsNo().Part.Capture());

        var restarted = NewSession(Ofp());
        restarted.Store.Accept("called"); // the pilot called de-icing on GSX's menu before the restore ran
        restarted.Part.Restore(saved);

        Assert.True(restarted.Store.Snapshot().RequestThisCycle);
        Assert.Null(restarted.Store.Snapshot().Declined);
    }

    [Theory]
    [InlineData(FlightCycleResetKind.Arrival)]
    [InlineData(FlightCycleResetKind.NewPlan)]
    public void TheCycleReset_DropsTheAnswer_AndTheNextWriteLeavesItOut(FlightCycleResetKind kind)
    {
        var session = SessionWithTheCaptainsNo();
        var changed = 0;
        session.Part.Changed += () => changed++;

        session.Signals.RaiseFlightCycleReset(kind);

        Assert.Equal(DeiceRequestSnapshot.Empty, session.Store.Snapshot());
        Assert.Null(session.Part.Capture());
        Assert.True(changed > 0); // the snapshot service is told, so the file is rewritten without it
    }

    [Fact]
    public void TheEndOfDuty_DropsTheAnswer()
    {
        var session = SessionWithTheCaptainsNo();

        session.DutyDay.RaiseDayEnded(new DutyDayEnded("2026-11-14", "voice", new DateTimeOffset(2026, 11, 14, 4, 50, 0, TimeSpan.Zero)));

        Assert.Equal(DeiceRequestSnapshot.Empty, session.Store.Snapshot());
        Assert.Null(session.Part.Capture());
    }

    [Fact]
    public void ADifferentPlanLoaded_BeforeTheResetHasRun_IsNotWrittenWithTheOldAnswer()
    {
        var session = SessionWithTheCaptainsNo();

        // The new OFP lands: the OFP part's change triggers a write before the new-plan cycle
        // reset has emptied the store.
        session.Ofp.Set(Ofp("193499001"));

        Assert.Null(session.Part.Capture());
        Assert.NotNull(session.Store.Snapshot().Declined); // the store is the reset's to empty, not the part's
    }

    [Fact]
    public void Memory_IsForPlan_ComparesThroughTheOnePlanIdentity()
    {
        var memory = new DeiceVerdictMemory("req:193487122", false, "voice", "declined by the captain (voice)",
            new DateTimeOffset(2026, 11, 14, 4, 20, 45, TimeSpan.Zero), DeicePolicyVerdict.Ask, AskReason, -0.98, "none", "EGLL", true);

        Assert.True(memory.IsForPlan("req:193487122"));
        Assert.True(memory.IsForPlan("193487122"));   // a bare request id is the same plan
        Assert.True(memory.IsForPlan(null));          // no plan loaded: nothing contradicts it
        Assert.False(memory.IsForPlan("req:193499001"));
        Assert.True((memory with { PlanId = null }).IsForPlan("req:193499001"));
    }
}
