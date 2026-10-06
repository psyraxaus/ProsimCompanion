using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Core.Aircraft.Setup;

/// <summary>
/// The ProSim IOS options this application wants set a certain way, and why. Pure: the list
/// depends only on the options in force, so the set of rows is unit-testable without ProSim.
/// The recommended values are the owner's working configuration, confirmed 2026-10-06 against
/// the live sim (ProSim 1.75.1): every row read exactly these values while ProsimCompanion
/// drove the ground services, the loadsheet and the fuel.
/// </summary>
public static class ProsimSetupRecommendations
{
    /// <summary>Door logic — ProSim's own door model (doors open/close on its own rules).</summary>
    public const string DoorLogic = "system.config.Config.DOORS";

    /// <summary>Automatic ground power — ProSim connects/disconnects the GPU itself.</summary>
    public const string AutomaticGroundPower = "system.config.Config.GROUNDPOWER";

    /// <summary>Datalink "Load Cargo/PAX" — ProSim loads the cabin from its own datalink.</summary>
    public const string DatalinkLoadCargo = "system.config.Datalink.loadCargo";

    /// <summary>Datalink "Load Fuel" — ProSim loads the fuel from its own datalink.</summary>
    public const string DatalinkLoadFuel = "system.config.Datalink.loadFuel";

    /// <summary>Refuelling rate (Quick / Realistic).</summary>
    public const string RefuelRate = "system.config.Config.refuelRate";

    /// <summary>ProSim's own version string, shown on the card so a report names the build.</summary>
    public const string ProsimVersion = "system.version";

    /// <summary>Every dataref the check may write — the write gate lists exactly these.</summary>
    public static readonly string[] WritableDataRefs =
    [
        DoorLogic,
        AutomaticGroundPower,
        DatalinkLoadCargo,
        DatalinkLoadFuel,
        RefuelRate,
    ];

    /// <summary>
    /// The rows that apply with <paramref name="gsx"/> in force, all at
    /// <see cref="ProsimSetupStatus.Unknown"/>. Rows whose owner feature is switched off are
    /// left out rather than shown green: a pilot running only the voice FO must not be told to
    /// turn off ProSim's door logic, because then nothing would drive the doors.
    /// </summary>
    public static IReadOnlyList<ProsimSetupItem> For(GsxOptions gsx)
    {
        ArgumentNullException.ThrowIfNull(gsx);

        var items = new List<ProsimSetupItem>();
        var groundServices = gsx.Enabled && gsx.AutomationEnabled;

        if (groundServices && gsx.DoorAutomationEnabled)
        {
            items.Add(Row(DoorLogic, "Door logic", "false",
                "This app opens and closes the doors with the GSX services. With ProSim's own door logic on, both move the doors.",
                "Config → Options → Door logic (untick)"));
        }

        if (groundServices)
        {
            items.Add(Row(AutomaticGroundPower, "Automatic ground power", "false",
                "This app connects and removes the GPU with the GSX equipment. ProSim's automatic ground power would connect it at the wrong moments.",
                "Config → Options → Automatic ground power (untick)"));
        }

        items.Add(Row(DatalinkLoadCargo, "Load Cargo/PAX (Datalink)", "false",
            "This app boards the passengers and cargo from the loadsheet. ProSim's datalink loading would overwrite the figures.",
            "Config → Datalink → Load Cargo/PAX (untick)"));

        items.Add(Row(DatalinkLoadFuel, "Load Fuel (Datalink)", "false",
            "This app sets the block fuel and runs the refuel. ProSim's datalink loading would set the tanks a second time.",
            "Config → Datalink → Load Fuel (untick)"));

        if (groundServices)
        {
            items.Add(Row(RefuelRate, "Refuelling rate", "Realistic",
                "This app runs the refuel at the GSX truck's pace. Quick would fill the tanks at once, before the truck is done.",
                "Config → Options → Refuelling rate → Realistic"));
        }

        return items;
    }

    private static ProsimSetupItem Row(string dataRef, string label, string recommended, string why, string iosPath)
        => new(dataRef, label, recommended, Actual: null, ProsimSetupStatus.Unknown, why, iosPath);

    /// <summary>
    /// Folds one gateway reading into a row. <paramref name="actual"/> null is the gateway's
    /// "no such dataref" answer (a real off reads <c>false</c>, never null — verified 2026-10-06),
    /// so it becomes <see cref="ProsimSetupStatus.NotPresent"/>. Bool comparison is
    /// case-insensitive (the gateway prints <c>True</c>/<c>true</c> depending on the path);
    /// drop-down text compares exactly, as the catalogue spells it.
    /// </summary>
    public static ProsimSetupItem Evaluate(ProsimSetupItem row, string? actual, bool ignored)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (ignored)
        {
            return row with { Actual = actual, Status = ProsimSetupStatus.Ignored };
        }

        if (actual is null)
        {
            return row with { Actual = null, Status = ProsimSetupStatus.NotPresent };
        }

        var matches = row.IsBoolean
            ? string.Equals(actual, row.Recommended, StringComparison.OrdinalIgnoreCase)
            : string.Equals(actual, row.Recommended, StringComparison.Ordinal);

        return row with { Actual = actual, Status = matches ? ProsimSetupStatus.Ok : ProsimSetupStatus.Mismatch };
    }
}
