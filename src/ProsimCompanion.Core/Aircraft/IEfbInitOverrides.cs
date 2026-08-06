namespace ProsimCompanion.Core.Aircraft;

/// <summary>
/// Pilot overrides for OFP-derived INIT figures (the EFB INIT page). Only four fields are
/// writable — each maps to a real dataref write; everything else on an OFP is display-only
/// (predecessor-verified: no dataref exists for trip/reserve/contingency fuel).
/// Setting an override always writes (explicit pilot intent); clearing one re-writes the OFP
/// value; overrides reset on a new OFP or flight cycle.
/// </summary>
public interface IEfbInitOverrides
{
    /// <summary>Writable override field names.</summary>
    public const string ZfwKg = "zfwKg";
    public const string FuelRampKg = "fuelRampKg";
    public const string CargoKg = "cargoKg";
    public const string PassengerCount = "passengerCount";

    /// <summary>Current overrides (field → value). Empty when the pilot has not overridden
    /// anything.</summary>
    IReadOnlyDictionary<string, double> Snapshot();

    event EventHandler? Changed;

    /// <summary>Sets an override and pushes it to the aircraft immediately.</summary>
    Task<bool> SetAsync(string field, double value, CancellationToken cancellationToken = default);

    /// <summary>Clears an override, re-writing the OFP value (no-op if the field was not
    /// overridden or no OFP is loaded).</summary>
    Task<bool> ClearAsync(string field, CancellationToken cancellationToken = default);

    /// <summary>Reverts every overridden field to its OFP value.</summary>
    Task<bool> ClearAllAsync(CancellationToken cancellationToken = default);
}
