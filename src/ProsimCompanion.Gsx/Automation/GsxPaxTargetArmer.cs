using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Gsx.Automation;

/// <summary>
/// Arms GSX's pax counter (L:FSDT_GSX_NUMPASSENGERS) with the booked count once per departure
/// — GSX then boards OUR manifest (incl. any no-show randomization) instead of its own
/// SimBrief figure (predecessor: SetPaxTarget before services). Optionally also writes the
/// crew/pilot skip flags so GSX never asks the crew question. One module because two paths
/// need the identical rule: the sequencer's pump and the hail/command start
/// (<see cref="IGsxDepartureStarter"/>, issue #169 — never board on a stale LVAR count).
/// </summary>
public sealed class GsxPaxTargetArmer : IDisposable
{
    private readonly IDataRefSubscription<string?> _bookedSeatString;
    private readonly ISimVars _simVars;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly IGsxDecisionLog _decisions;
    private readonly ILogger<GsxAutomationService> _logger;
    private bool _armed;

    public GsxPaxTargetArmer(
        IProsimDataRefs prosim,
        ISimVars simVars,
        IOptionsMonitor<GsxOptions> options,
        IGsxDecisionLog decisions,
        ILogger<GsxAutomationService> logger)
    {
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(logger);
        _simVars = simVars;
        _options = options;
        _decisions = decisions;
        _logger = logger;
        _bookedSeatString = prosim.Subscribe(ProsimDataRefNames.PaxBookedString);
    }

    public void Dispose() => _bookedSeatString.Dispose();

    /// <summary>The booked passenger count from ProSim's seat map — 0 while the OFP has not
    /// written the manifest yet.</summary>
    public int BookedPax() => SeatMap.Parse(_bookedSeatString.Value).Count(seat => seat);

    /// <summary>A new departure cycle: the target must be armed again.</summary>
    public void Reset() => _armed = false;

    /// <summary>Arms the pax target once per cycle and reports whether it is armed now (already
    /// armed counts). False = the booked map is not written yet, or MSFS is not connected —
    /// the sequencer retries on a later pump; an on-demand Boarding call must not proceed.</summary>
    public async Task<bool> TryArmAsync()
    {
        if (_armed)
        {
            return true;
        }

        var booked = BookedPax();
        if (booked <= 0)
        {
            return false; // booked map not written yet — retry on a later pump
        }

        _armed = true;
        try
        {
            await _simVars.WriteAsync(GsxLvarNames.NumPassengers.Name, booked).ConfigureAwait(false);
            _decisions.Record("pax target", $"armed GSX with {booked} passengers (booked manifest)");

            if (_options.CurrentValue.SkipCrewBoardingQuestion)
            {
                await _simVars.WriteAsync(GsxLvarNames.CrewNotBoarding, 1).ConfigureAwait(false);
                await _simVars.WriteAsync(GsxLvarNames.PilotsNotBoarding, 1).ConfigureAwait(false);
                await _simVars.WriteAsync(GsxLvarNames.CrewNotDeboarding, 1).ConfigureAwait(false);
                await _simVars.WriteAsync(GsxLvarNames.PilotsNotDeboarding, 1).ConfigureAwait(false);
                _decisions.Record("pax target", "crew/pilot boarding questions suppressed via LVARs");
            }

            return true;
        }
        catch (InvalidOperationException ex)
        {
            _armed = false; // MSFS not connected yet — retry on a later pump
            _logger.LogDebug("Pax target arming deferred: {Reason}", ex.Message);
            return false;
        }
    }
}
