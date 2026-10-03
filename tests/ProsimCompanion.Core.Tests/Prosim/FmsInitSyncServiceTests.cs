using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.State;
using ProsimCompanion.Prosim.Loadsheet;
using Xunit;

namespace ProsimCompanion.Core.Tests.Prosim;

/// <summary>Owner report 2026-10-04 at the gate: "Sync to FMS (FIN)" wrote the ordered block
/// (8.4 t) while the final loadsheet carried the fuel actually aboard (9576 kg). The block
/// that goes to INIT B must be the block of the loadsheet the ZFW came from.</summary>
public sealed class FmsInitSyncServiceTests
{
    private readonly Speech.FakeDataRefs _refs = new();
    private readonly OfpStore _ofp = new();
    private readonly LoadsheetStore _loadsheets = new();
    private readonly Mock<IEfbInitOverrides> _overrides = new();

    private FmsInitSyncService Service()
    {
        _overrides.Setup(o => o.Snapshot()).Returns(new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase));
        return new FmsInitSyncService(_refs, _ofp, _loadsheets, _overrides.Object, NullLogger<FmsInitSyncService>.Instance);
    }

    private static LoadsheetSlotView Slot(double zfw, double fuel, double macZfw = 27.58)
        => new(LoadsheetSlotStatus.Sent, 1, DateTimeOffset.UtcNow, zfw, zfw + fuel, macZfw, 25.2, fuel, 123, null);

    private double Written(string name) => Convert.ToDouble(_refs.Writes.Last(w => w.Name == name).Value, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Final_UsesTheFinalLoadsheetsOwnFuel_NotTheOrderedFigure()
    {
        _ofp.Set(new OfpData { FuelPlanRampKg = 8400 });                 // the ordered figure
        _loadsheets.SetFinal(Slot(56396, 9576));                          // the fuel actually aboard
        var service = Service();

        var result = await service.SyncAsync();

        Assert.NotNull(result);
        Assert.Equal("final", result.Source);
        Assert.Equal(56.396, Written(ProsimDataRefNames.FmsInitZfw), 3);
        Assert.Equal(27.58, Written(ProsimDataRefNames.FmsInitZfwcg), 2);
        Assert.Equal(9.6, Written(ProsimDataRefNames.FmsInitBlock), 3);   // 9576 rounded up to the next 100 kg
    }

    [Fact]
    public async Task Prelim_UsesThePrelimsFuel()
    {
        _ofp.Set(new OfpData { FuelPlanRampKg = 8400 });
        _loadsheets.SetPrelim(Slot(55809, 8200));
        var service = Service();

        var result = await service.SyncAsync();

        Assert.Equal("prelim", result!.Source);
        Assert.Equal(8.2, Written(ProsimDataRefNames.FmsInitBlock), 3);
    }

    [Fact]
    public async Task Live_FallsBackToTheOrderedFigure()
    {
        _ofp.Set(new OfpData { FuelPlanRampKg = 8400 });
        _refs.Values["aircraft.weight.zfw"] = 56000.0;
        _refs.Values["aircraft.zfwcg"] = 27.0;
        var service = Service();

        var result = await service.SyncAsync();

        Assert.NotNull(result);
        Assert.Equal("live", result.Source);
        Assert.Equal(8.4, Written(ProsimDataRefNames.FmsInitBlock), 3);
    }
}
