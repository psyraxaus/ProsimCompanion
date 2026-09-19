using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.State;

public sealed class FuelConfirmationStoreTests
{
    private readonly OfpStore _ofp = new();
    private readonly GroundOpsSignals _signals = new();

    [Fact]
    public void Confirm_RecordsFigureSourceAndTime()
    {
        using var store = new FuelConfirmationStore(_ofp, _signals);

        store.Confirm(7500, "confirm");

        var snapshot = store.Snapshot();
        Assert.True(snapshot.Confirmed);
        Assert.Equal(7500, snapshot.ConfirmedKg);
        Assert.Equal("confirm", snapshot.Source);
        Assert.NotNull(snapshot.ConfirmedAtUtc);
    }

    [Fact]
    public void FlightCycleReset_ClearsTheConfirmation()
    {
        using var store = new FuelConfirmationStore(_ofp, _signals);
        store.Confirm(7500, "confirm");

        _signals.RaiseFlightCycleReset();

        Assert.False(store.Confirmed);
    }

    [Fact]
    public void NewOfp_ClearsTheConfirmation_ButAReimportOfTheSameOfpKeepsIt()
    {
        _ofp.Set(new OfpData { RequestId = "A", FuelPlanRampKg = 7000 });
        using var store = new FuelConfirmationStore(_ofp, _signals);
        store.Confirm(7000, "confirm");

        _ofp.Set(new OfpData { RequestId = "A", FuelPlanRampKg = 7000 }); // same plan re-imported
        Assert.True(store.Confirmed);

        _ofp.Set(new OfpData { RequestId = "B", FuelPlanRampKg = 8000 }); // a new proposal
        Assert.False(store.Confirmed);
    }

    [Fact]
    public void Observers_AreNotifiedOnConfirm()
    {
        using var store = new FuelConfirmationStore(_ofp, _signals);
        var notified = 0;
        using var observer = store.Observe(_ => notified++);

        store.Confirm(7000, "confirm");

        Assert.Equal(1, notified);
    }
}
