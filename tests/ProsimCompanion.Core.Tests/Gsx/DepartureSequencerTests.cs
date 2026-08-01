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

    private static DeparturePlan Next(
        Dictionary<string, GsxServiceInfo> services,
        Func<string, bool>? completed = null,
        Func<string, bool>? pending = null,
        bool plan = true,
        bool requireOfp = true,
        bool concurrent = true,
        List<string>? boardingAfter = null,
        List<string>? order = null)
        => DepartureSequencer.Next(
            order ?? Order,
            services,
            completed ?? (_ => false),
            pending ?? (_ => false),
            plan,
            requireOfp,
            concurrent,
            boardingAfter ?? []);

    [Fact]
    public void Concurrent_TriggersEveryCallableNonBoardingService()
    {
        var plan = Next(Services(
            ("Refueling", GsxServiceState.Callable, true),
            ("Catering", GsxServiceState.Callable, true),
            ("Boarding", GsxServiceState.Callable, true)));

        Assert.Equal(["Refueling", "Catering"], plan.Trigger);
        Assert.Contains(plan.Holds, h => h.ServiceId == "Boarding");
    }

    [Fact]
    public void Sequential_TriggersOnlyTheFirst()
    {
        var plan = Next(Services(
            ("Refueling", GsxServiceState.Callable, true),
            ("Catering", GsxServiceState.Callable, true)),
            concurrent: false);

        Assert.Equal(["Refueling"], plan.Trigger);
    }

    [Fact]
    public void Sequential_InProgressServiceBlocksTheRest()
    {
        var plan = Next(Services(
            ("Refueling", GsxServiceState.Active, false),
            ("Catering", GsxServiceState.Callable, true)),
            concurrent: false);

        Assert.Empty(plan.Trigger);
        Assert.Contains(plan.Holds, h => h.ServiceId == "Refueling" && h.Reason == "in progress");
    }

    [Fact]
    public void Concurrent_InProgressServiceDoesNotBlockOthers()
    {
        var plan = Next(Services(
            ("Refueling", GsxServiceState.Active, false),
            ("Catering", GsxServiceState.Callable, true)));

        Assert.Equal(["Catering"], plan.Trigger);
    }

    [Fact]
    public void PlanGate_HoldsEveryServiceUntilFlightPlanExists()
    {
        // Owner requirement (round 4): NO service is called before the OFP/FMS plan exists.
        var plan = Next(Services(
            ("Refueling", GsxServiceState.Callable, true),
            ("Catering", GsxServiceState.Callable, true),
            ("Boarding", GsxServiceState.Callable, true)),
            plan: false);

        Assert.Empty(plan.Trigger);
        Assert.Contains(plan.Holds, h => h.ServiceId == "Refueling" && h.Reason.Contains("flight plan", StringComparison.Ordinal));
        Assert.Contains(plan.Holds, h => h.ServiceId == "Catering" && h.Reason.Contains("flight plan", StringComparison.Ordinal));
    }

    [Fact]
    public void PlanGate_NotAppliedWhenOfpNotRequired()
    {
        var plan = Next(
            Services(("Catering", GsxServiceState.Callable, true)),
            plan: false,
            requireOfp: false,
            order: ["Catering"]);

        Assert.Equal(["Catering"], plan.Trigger);
    }

    [Fact]
    public void PendingService_HoldsInsteadOfRetriggering()
    {
        // GSX keeps quick services (Water) "callable" while they run — a called service must
        // never be triggered again until its cycle completes.
        var plan = Next(
            Services(("Water", GsxServiceState.Callable, true), ("Catering", GsxServiceState.Callable, true)),
            pending: id => id == "Water",
            order: ["Water", "Catering"]);

        Assert.Equal(["Catering"], plan.Trigger);
        Assert.Contains(plan.Holds, h => h.ServiceId == "Water" && h.Reason.Contains("already called", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicatedOrder_TriggersEachServiceOnce()
    {
        // A mis-merged settings file once doubled the order list; the sequencer dedupes.
        var plan = Next(
            Services(("Catering", GsxServiceState.Callable, true)),
            order: ["Catering", "Catering"]);

        Assert.Equal(["Catering"], plan.Trigger);
    }

    [Fact]
    public void Boarding_WaitsForAllOtherServicesByDefault()
    {
        var plan = Next(
            Services(("Boarding", GsxServiceState.Callable, true), ("Catering", GsxServiceState.Callable, true)),
            completed: id => id == "Refueling");

        Assert.DoesNotContain("Boarding", plan.Trigger);
        Assert.Contains(plan.Holds, h => h.ServiceId == "Boarding" && h.Reason.Contains("Catering", StringComparison.Ordinal));
    }

    [Fact]
    public void Boarding_TriggersWhenAllPrerequisitesComplete()
    {
        var plan = Next(
            Services(("Boarding", GsxServiceState.Callable, true)),
            completed: id => id is "Refueling" or "Catering");

        Assert.Equal(["Boarding"], plan.Trigger);
    }

    [Fact]
    public void Boarding_SelectedPrerequisites_OnlyThoseGate()
    {
        // Catering still callable, but boarding only waits for Refueling (Prosim2GSX-style).
        var plan = Next(
            Services(("Boarding", GsxServiceState.Callable, true), ("Catering", GsxServiceState.Callable, true)),
            completed: id => id == "Refueling",
            boardingAfter: ["Refueling"]);

        Assert.Contains("Boarding", plan.Trigger);
    }

    [Fact]
    public void Boarding_UnavailablePrerequisiteCountsAsSatisfied()
    {
        var plan = Next(
            Services(
                ("Catering", GsxServiceState.NotAvailable, false),
                ("Boarding", GsxServiceState.Callable, true)),
            completed: id => id == "Refueling");

        Assert.Contains("Boarding", plan.Trigger);
    }

    [Fact]
    public void MissingAndUnavailableServices_AreSkippedWithReasons()
    {
        var plan = Next(Services(
            ("Refueling", GsxServiceState.NotAvailable, false),
            ("Boarding", GsxServiceState.Callable, true)));

        Assert.Contains(plan.Skipped, s => s.ServiceId == "Refueling" && s.Reason == "unavailable");
        Assert.Contains(plan.Skipped, s => s.ServiceId == "Catering");
        Assert.Contains("Boarding", plan.Trigger); // both prereqs skippable ⇒ satisfied
    }

    [Fact]
    public void CallableButNotTriggerable_Holds()
    {
        var plan = Next(Services(("Catering", GsxServiceState.Callable, false)), order: ["Catering"]);

        Assert.Empty(plan.Trigger);
        Assert.Contains(plan.Holds, h => h.ServiceId == "Catering");
    }

    [Fact]
    public void EverythingCompletedOrSkipped_IsAllDone()
    {
        var plan = Next(
            Services(("Catering", GsxServiceState.Bypassed, false)),
            completed: id => id is "Refueling" or "Boarding");

        Assert.True(plan.AllDone);
    }

    [Fact]
    public void InProgressService_IsNotAllDone()
    {
        var plan = Next(
            Services(("Boarding", GsxServiceState.Active, false)),
            completed: id => id is "Refueling" or "Catering");

        Assert.False(plan.AllDone);
    }
}
