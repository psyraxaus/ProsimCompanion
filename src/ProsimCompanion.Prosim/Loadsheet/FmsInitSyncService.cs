using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Aircraft.WeightAndBalance;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Prosim.Loadsheet;

/// <summary>
/// Pushes ZFW / ZFWCG / block fuel into the MCDU INIT B fields. Unit conversion is the trap:
/// <c>aircraft.fms.init.zfw</c> and <c>.block</c> expect TONNES (xx.x) while <c>.zfwcg</c> is
/// %MAC written as-is (predecessor-verified). ZFW and ZFWCG always resolve together from the
/// same source — final loadsheet, then prelim, then live datarefs — so the pair can never mix
/// dispatch and live figures. Block fuel rounds UP to the next 100 kg before converting.
/// </summary>
public sealed class FmsInitSyncService : IFmsInitSync, IDisposable
{
    private readonly IProsimDataRefs _prosim;
    private readonly OfpStore _ofpStore;
    private readonly LoadsheetStore _loadsheets;
    private readonly IEfbInitOverrides _initOverrides;
    private readonly ILogger<FmsInitSyncService> _logger;
    private readonly IDataRefSubscription<double> _zfw;
    private readonly IDataRefSubscription<double> _zfwcg;
    private readonly IDataRefSubscription<double> _plannedFuel;

    public FmsInitSyncService(
        IProsimDataRefs prosim,
        OfpStore ofpStore,
        LoadsheetStore loadsheets,
        IEfbInitOverrides initOverrides,
        ILogger<FmsInitSyncService> logger)
    {
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(ofpStore);
        ArgumentNullException.ThrowIfNull(loadsheets);
        ArgumentNullException.ThrowIfNull(initOverrides);
        ArgumentNullException.ThrowIfNull(logger);

        _prosim = prosim;
        _ofpStore = ofpStore;
        _loadsheets = loadsheets;
        _initOverrides = initOverrides;
        _logger = logger;

        _zfw = prosim.Subscribe(ProsimDataRefNames.WeightZfw);
        _zfwcg = prosim.Subscribe(ProsimDataRefNames.Zfwcg);
        _plannedFuel = prosim.Subscribe(ProsimDataRefNames.EfbPlannedFuel);
    }

    public void Dispose()
    {
        _zfw.Dispose();
        _zfwcg.Dispose();
        _plannedFuel.Dispose();
    }

    public async Task<FmsSyncResult?> SyncAsync(CancellationToken cancellationToken = default)
    {
        var trio = ResolveZfwSource();
        if (trio is null)
        {
            return null;
        }
        var (source, zfwKg, zfwCgMac) = trio.Value;

        // Block: the crew's INIT override first, then the OFP's ordered figure, else whatever
        // the EFB fuel page holds (one rule for every consumer — EffectiveBlockFuel).
        var figure = EffectiveBlockFuel.Resolve(_initOverrides.Snapshot(), _ofpStore.Current, _plannedFuel.Value);
        var blockKg = figure.Kg;
        if (blockKg <= 0)
        {
            _logger.LogWarning("FMS sync: no block fuel figure available (no OFP, empty efb.plannedfuel) — not writing INIT B");
            return null;
        }

        var zfwTonnes = zfwKg / 1000.0;
        var blockTonnes = blockKg / 1000.0;

        try
        {
            await _prosim.WriteAsync(ProsimDataRefNames.FmsInitZfw, zfwTonnes, cancellationToken).ConfigureAwait(false);
            await _prosim.WriteAsync(ProsimDataRefNames.FmsInitZfwcg, zfwCgMac, cancellationToken).ConfigureAwait(false);
            await _prosim.WriteAsync(ProsimDataRefNames.FmsInitBlock, blockTonnes, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("FMS sync write failed: {Message}", ex.Message);
            return null;
        }

        _logger.LogInformation(
            "FMS INIT B synced from {Source}: ZFW {Zfw:F1} t, ZFWCG {Cg:F1}%, block {Block:F1} t",
            source, zfwTonnes, zfwCgMac, blockTonnes);
        return new FmsSyncResult(source, zfwTonnes, zfwCgMac, blockTonnes);
    }

    /// <summary>ZFW and ZFWCG resolved together, never mixed across sources.</summary>
    private (string Source, double ZfwKg, double ZfwCgMac)? ResolveZfwSource()
    {
        var snapshot = _loadsheets.Snapshot();
        if (snapshot.Final is { Status: LoadsheetSlotStatus.Sent, ZfwKg: > 0, MacZfw: > 0 } final)
        {
            return ("final", final.ZfwKg, final.MacZfw);
        }
        if (snapshot.Prelim is { Status: LoadsheetSlotStatus.Sent, ZfwKg: > 0, MacZfw: > 0 } prelim)
        {
            return ("prelim", prelim.ZfwKg, prelim.MacZfw);
        }

        var liveZfw = _zfw.Value;
        var liveCg = _zfwcg.Value;
        try
        {
            A320WeightAndBalance.EnsurePlausibleCg(liveCg, "FMS sync ZFW");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("FMS sync: {Message}", ex.Message);
            return null;
        }
        if (liveZfw <= 0)
        {
            _logger.LogWarning("FMS sync: live ZFW is 0 — dataref not populated; not writing INIT B");
            return null;
        }
        return ("live", liveZfw, liveCg);
    }
}
