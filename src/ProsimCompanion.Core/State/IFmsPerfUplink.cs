namespace ProsimCompanion.Core.State;

/// <summary>Takeoff performance values as uplinked to the FMS PERF TO page. All values are in
/// the dataref's native convention: speeds kt, flex °C, flaps the CONF int (1=1+F, 2, 3), THS
/// signed (negative = nose-down trim), shift in METRES (the implementation converts to the
/// cockpit's configured display unit before writing).</summary>
public sealed record TakeoffPerfUplink(
    int Flaps,
    int FlexTemp,
    int V1,
    int Vr,
    int V2,
    double Ths,
    double ShiftMeters);

/// <summary>
/// Writes takeoff performance figures into <c>aircraft.fms.perf.takeOff.*</c> (implemented by
/// the Prosim project). Degrade-not-fail: false on error, logged.
/// </summary>
public interface IFmsPerfUplink
{
    Task<bool> UplinkTakeoffAsync(TakeoffPerfUplink values, CancellationToken cancellationToken = default);
}
