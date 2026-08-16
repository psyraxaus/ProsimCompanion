using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Prosim.Loadsheet;

/// <summary>
/// Uplinks takeoff performance to <c>aircraft.fms.perf.takeOff.*</c>. The runway-shift value is
/// unit-sensitive: ProSim interprets the number in whatever
/// <c>system.config.Units.TakeoffShiftUnit</c> says ("Meters" | "Feet"), so the metres figure is
/// converted before writing (predecessor-verified trap).
/// </summary>
public sealed class FmsPerfUplinkService : IFmsPerfUplink, IDisposable
{
    private const double FeetPerMeter = 3.28084;

    private readonly IProsimDataRefs _prosim;
    private readonly ILogger<FmsPerfUplinkService> _logger;
    private readonly IDataRefSubscription<string> _shiftUnit;

    public FmsPerfUplinkService(IProsimDataRefs prosim, ILogger<FmsPerfUplinkService> logger)
    {
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(logger);
        _prosim = prosim;
        _logger = logger;
        _shiftUnit = prosim.Subscribe(ProsimDataRefNames.ConfigTakeoffShiftUnit);
    }

    public void Dispose() => _shiftUnit.Dispose();

    public async Task<bool> UplinkTakeoffAsync(TakeoffPerfUplink values, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        // Predecessor parity: shift is rounded to the nearest 100 in the DISPLAY unit
        // (default "Feet" — ProSim's own default for this config value).
        var shiftUnit = _shiftUnit.Value;
        var shiftInUnit = string.Equals(shiftUnit, "Meters", StringComparison.OrdinalIgnoreCase)
            ? values.ShiftMeters
            : values.ShiftMeters * FeetPerMeter;
        var shift = (int)(100 * Math.Round(shiftInUnit / 100.0));

        try
        {
            await _prosim.WriteAsync(ProsimDataRefNames.FmsPerfTakeoffFlaps, values.Flaps, cancellationToken).ConfigureAwait(false);
            // FLEX rides the dataref as a double; 0 is the FMS's TOGA sentinel. String-name
            // overload on purpose: the descriptor is DataRef<int> for reads, but the write
            // must stay a double (predecessor-verified wire behaviour).
            await _prosim.WriteAsync(ProsimDataRefNames.FmsPerfTakeoffFlexTemp.Name, (double)values.FlexTemp, cancellationToken).ConfigureAwait(false);
            await _prosim.WriteAsync(ProsimDataRefNames.FmsPerfTakeoffV1, values.V1, cancellationToken).ConfigureAwait(false);
            await _prosim.WriteAsync(ProsimDataRefNames.FmsPerfTakeoffVr, values.Vr, cancellationToken).ConfigureAwait(false);
            await _prosim.WriteAsync(ProsimDataRefNames.FmsPerfTakeoffV2, values.V2, cancellationToken).ConfigureAwait(false);
            await _prosim.WriteAsync(ProsimDataRefNames.FmsPerfTakeoffThs, values.Ths, cancellationToken).ConfigureAwait(false);
            await _prosim.WriteAsync(ProsimDataRefNames.FmsPerfTakeoffShift, shift, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("FMS perf uplink failed: {Message}", ex.Message);
            return false;
        }

        _logger.LogInformation(
            "FMS PERF TO uplinked: CONF {Flaps}, FLEX {Flex}, V1/{V1} VR/{Vr} V2/{V2}, THS {Ths:F1}, shift {Shift} {Unit}",
            values.Flaps, values.FlexTemp, values.V1, values.Vr, values.V2, values.Ths, shift, shiftUnit);
        return true;
    }
}
