using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;

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
///
/// Freshness cross-check (issue #60, 2026-08-16 flight evidence): ProSim's
/// <c>efb.simbriefPlanImported</c> can carry a STALE true from a previous session, which let
/// GSX-side service requests run 55 s before any plan existed. The dataref alone is therefore
/// no longer trusted: it must be corroborated by this-session evidence — either the app's own
/// <see cref="OfpStore"/> holds a current OFP, or the pilot's MCDU plan is present.
/// </summary>
public sealed class GsxFlightPlanMonitor : IGsxFlightPlanStatus, IDisposable
{
    private readonly IDataRefSubscription<bool> _ofpImported;
    private readonly IDataRefSubscription<string?> _fmsOrigin;
    private readonly IDataRefSubscription<string?> _fmsDestination;
    private readonly OfpStore _ofpStore;
    private readonly ILogger<GsxFlightPlanMonitor> _logger;
    private bool _staleWarned;

    public GsxFlightPlanMonitor(IProsimDataRefs prosim, OfpStore ofpStore, ILogger<GsxFlightPlanMonitor> logger)
    {
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(ofpStore);
        ArgumentNullException.ThrowIfNull(logger);

        _ofpStore = ofpStore;
        _logger = logger;
        _ofpImported = prosim.Subscribe(ProsimDataRefNames.EfbSimbriefPlanImported);
        _fmsOrigin = prosim.Subscribe(ProsimDataRefNames.FmsOrigin);
        _fmsDestination = prosim.Subscribe(ProsimDataRefNames.FmsDestination);
    }

    public bool OfpImported => _ofpImported.Value;

    public string? FmsOrigin => _fmsOrigin.Value;

    public string? FmsDestination => _fmsDestination.Value;

    public bool FmsPlanPresent => IsValidIcao(FmsOrigin) && IsValidIcao(FmsDestination);

    public bool FlightPlanAvailable
    {
        get
        {
            var ofpImported = OfpImported;
            var fmsPlanPresent = FmsPlanPresent;
            var ofpHeldThisSession = _ofpStore.Current is not null;
            var available = IsFlightPlanAvailable(ofpImported, fmsPlanPresent, ofpHeldThisSession);

            // Stale-dataref episode: say why ONCE per episode, not on every 3 s pump read.
            if (!available && ofpImported)
            {
                if (!_staleWarned)
                {
                    _staleWarned = true;
                    _logger.LogWarning(
                        "ProSim reports a SimBrief plan imported, but no OFP was imported this session "
                        + "and the MCDU has no plan — treating the dataref as stale from a previous "
                        + "session; the flight-plan gate stays closed (issue #60)");
                }
            }
            else
            {
                _staleWarned = false;
            }

            return available;
        }
    }

    /// <summary>
    /// The pure availability rule (issue #60): the ProSim "imported" dataref is only believed
    /// when corroborated by this-session evidence — the app's own OFP store, or a pilot-loaded
    /// MCDU plan. A dataref-only "imported" (no app import this session, FMS empty) is treated
    /// as stale, because ProSim persists it across its own sessions while the aircraft has no
    /// actual plan (2026-08-15 flight: refuel latched targets 55 s before the plan arrived).
    /// </summary>
    public static bool IsFlightPlanAvailable(bool ofpImportedDataref, bool fmsPlanPresent, bool ofpHeldThisSession)
        => fmsPlanPresent || (ofpImportedDataref && ofpHeldThisSession);

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
