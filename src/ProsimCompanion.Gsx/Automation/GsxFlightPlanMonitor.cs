using ProsimCompanion.Core.Aircraft;

namespace ProsimCompanion.Gsx.Automation;

/// <summary>Narrow read seam for "is a flight plan loaded" so consumers (the departure
/// sequencer, the on-demand service control) test against a mock instead of live datarefs.</summary>
public interface IGsxFlightPlanStatus
{
    /// <summary>SimBrief OFP imported into the EFB.</summary>
    bool OfpImported { get; }

    /// <summary>The pilot loaded a plan in the MCDU (valid origin + destination ICAOs).</summary>
    bool FmsPlanPresent { get; }

    /// <summary>Either plan source present — the flight-plan gate's input.</summary>
    bool FlightPlanAvailable { get; }

    /// <summary>Raw MCDU origin (diagnostics only — may be "----"/"Null" pre-plan).</summary>
    string? FmsOrigin { get; }

    /// <summary>Raw MCDU destination (diagnostics only).</summary>
    string? FmsDestination { get; }
}

/// <summary>
/// The single implementation of the flight-plan rule (ADR-0006 / issue #50): a plan exists
/// when the SimBrief OFP was imported into the EFB, OR the pilot loaded a plan in the MCDU
/// (valid FMS origin + destination). Extracted from the departure sequencer's pump so the
/// on-demand service path applies the exact same rule — never a re-derived copy.
/// </summary>
public sealed class GsxFlightPlanMonitor : IGsxFlightPlanStatus, IDisposable
{
    private readonly IDataRefSubscription _ofpImported;
    private readonly IDataRefSubscription _fmsOrigin;
    private readonly IDataRefSubscription _fmsDestination;

    public GsxFlightPlanMonitor(IProsimDataRefs prosim)
    {
        ArgumentNullException.ThrowIfNull(prosim);

        _ofpImported = prosim.Subscribe(ProsimDataRefNames.EfbSimbriefPlanImported, DataRefTier.Infrequent);
        _fmsOrigin = prosim.Subscribe(ProsimDataRefNames.FmsOrigin, DataRefTier.Infrequent);
        _fmsDestination = prosim.Subscribe(ProsimDataRefNames.FmsDestination, DataRefTier.Infrequent);
    }

    public bool OfpImported => _ofpImported.GetValue(false);

    public string? FmsOrigin => _fmsOrigin.GetValue<string?>(null);

    public string? FmsDestination => _fmsDestination.GetValue<string?>(null);

    public bool FmsPlanPresent => IsValidIcao(FmsOrigin) && IsValidIcao(FmsDestination);

    public bool FlightPlanAvailable => OfpImported || FmsPlanPresent;

    /// <summary>The MCDU FMS origin/destination datarefs carry a valid 4-char ICAO once the
    /// pilot loads a plan — but read "----" before that, and have been observed returning the
    /// literal string "Null" (predecessor archaeology, TakeoffPerfService).</summary>
    public static bool IsValidIcao(string? value)
        => value is { Length: 4 }
            && value != "----"
            && !value.Equals("Null", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        _ofpImported.Dispose();
        _fmsOrigin.Dispose();
        _fmsDestination.Dispose();
    }
}
