using ProsimCompanion.Gsx.Automation;
using ProsimCompanion.Gsx.Mirror;
using ProsimCompanion.Gsx.Protocol;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

public sealed class DepartureSequencerTests
{
    private static readonly List<string> Order = ["Refueling", "Catering", "Boarding"];

    private static Dictionary<string, GsxServiceInfo> Services(
        params (string Id, GsxServiceState State, bool CanTrigger)[] entries)
        => entries.ToDictionary(
            e => e.Id,
            e => new GsxServiceInfo(e.Id, e.Id, null, e.State, e.CanTrigger, false, null, null),
            StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void FirstCallableTriggerable_IsTriggered()
    {
        var decision = DepartureSequencer.Next(
            Order,
            Services(("Refueling", GsxServiceState.Callable, true)),
            _ => false,
            ofpImported: true,
            requireOfp: true);

        Assert.Equal(DepartureDecisionKind.Trigger, decision.Kind);
        Assert.Equal("Refueling", decision.ServiceId);
    }

    [Fact]
    public void OfpGatedService_HeldUntilOfpImported()
    {
        var decision = DepartureSequencer.Next(
            Order,
            Services(("Refueling", GsxServiceState.Callable, true)),
            _ => false,
            ofpImported: false,
            requireOfp: true);

        Assert.Equal(DepartureDecisionKind.Hold, decision.Kind);
        Assert.Contains("OFP", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void OfpGate_Disabled_TriggersWithoutOfp()
    {
        var decision = DepartureSequencer.Next(
            Order,
            Services(("Refueling", GsxServiceState.Callable, true)),
            _ => false,
            ofpImported: false,
            requireOfp: false);

        Assert.Equal(DepartureDecisionKind.Trigger, decision.Kind);
    }

    [Fact]
    public void NonOfpGatedService_NotHeldByOfp()
    {
        var decision = DepartureSequencer.Next(
            ["Catering"],
            Services(("Catering", GsxServiceState.Callable, true)),
            _ => false,
            ofpImported: false,
            requireOfp: true);

        Assert.Equal(DepartureDecisionKind.Trigger, decision.Kind);
        Assert.Equal("Catering", decision.ServiceId);
    }

    [Fact]
    public void InProgressService_HoldsTheSequence()
    {
        var decision = DepartureSequencer.Next(
            Order,
            Services(
                ("Refueling", GsxServiceState.Active, false),
                ("Catering", GsxServiceState.Callable, true)),
            _ => false,
            ofpImported: true,
            requireOfp: true);

        Assert.Equal(DepartureDecisionKind.Hold, decision.Kind);
        Assert.Equal("Refueling", decision.ServiceId);
    }

    [Fact]
    public void CompletedService_AdvancesToNext()
    {
        var decision = DepartureSequencer.Next(
            Order,
            Services(
                ("Refueling", GsxServiceState.Callable, true),
                ("Catering", GsxServiceState.Callable, true)),
            id => id == "Refueling",
            ofpImported: true,
            requireOfp: true);

        Assert.Equal(DepartureDecisionKind.Trigger, decision.Kind);
        Assert.Equal("Catering", decision.ServiceId);
    }

    [Fact]
    public void UnavailableAndMissingServices_AreSkippedWithReasons()
    {
        var decision = DepartureSequencer.Next(
            Order,
            Services(
                ("Refueling", GsxServiceState.NotAvailable, false),
                ("Boarding", GsxServiceState.Callable, true)),
            _ => false,
            ofpImported: true,
            requireOfp: true);

        Assert.Equal(DepartureDecisionKind.Trigger, decision.Kind);
        Assert.Equal("Boarding", decision.ServiceId);
        Assert.Equal(2, decision.Skipped.Count);
        Assert.Contains(decision.Skipped, s => s.ServiceId == "Refueling" && s.Reason == "unavailable");
        Assert.Contains(decision.Skipped, s => s.ServiceId == "Catering");
    }

    [Fact]
    public void CallableButNotTriggerable_Holds()
    {
        var decision = DepartureSequencer.Next(
            ["Catering"],
            Services(("Catering", GsxServiceState.Callable, false)),
            _ => false,
            ofpImported: true,
            requireOfp: true);

        Assert.Equal(DepartureDecisionKind.Hold, decision.Kind);
    }

    [Fact]
    public void EverythingCompletedOrSkipped_IsAllDone()
    {
        var decision = DepartureSequencer.Next(
            Order,
            Services(("Catering", GsxServiceState.Bypassed, false)),
            id => id is "Refueling" or "Boarding",
            ofpImported: true,
            requireOfp: true);

        Assert.Equal(DepartureDecisionKind.AllDone, decision.Kind);
    }
}
