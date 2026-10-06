namespace ProsimCompanion.Core.Aircraft.Setup;

/// <summary>How one ProSim IOS option compares with what this application wants.</summary>
public enum ProsimSetupStatus
{
    /// <summary>Not read yet (ProSim not connected, or the check has not run).</summary>
    Unknown,

    /// <summary>The option already has the recommended value.</summary>
    Ok,

    /// <summary>The option differs from the recommended value.</summary>
    Mismatch,

    /// <summary>ProSim answered "no such dataref" — an older or newer build without this option.
    /// Nothing to do; the row says so instead of nagging.</summary>
    NotPresent,

    /// <summary>The pilot switched this row off in the settings; it is shown greyed and never
    /// written by "Apply recommended".</summary>
    Ignored,
}

/// <summary>
/// One ProSim IOS option this application has an opinion about (the ProSim setup check,
/// owner request 2026-10-06). The IOS pages are plain datarefs under <c>system.config.*</c>
/// — readable and writable through the port-5000 gateway without a ProSim restart (verified
/// live on ProSim 1.75.1: Door logic flipped true and back, read back each time).
/// </summary>
/// <param name="DataRef">The <c>system.config.*</c> dataref name (also the key for the ignore list).</param>
/// <param name="Label">The option's name as the ProSim IOS shows it.</param>
/// <param name="Recommended">The value this application wants, in the dataref's own form
/// (<c>true</c>/<c>false</c> for a tick box, the exact choice text for a drop-down).</param>
/// <param name="Actual">What ProSim reported; null until read or when the ref is unknown.</param>
/// <param name="Status">The comparison outcome.</param>
/// <param name="Why">One sentence for the card: what breaks when the option is wrong.</param>
/// <param name="IosPath">Where to find the option by hand in ProSim System (Config → …).</param>
public sealed record ProsimSetupItem(
    string DataRef,
    string Label,
    string Recommended,
    string? Actual,
    ProsimSetupStatus Status,
    string Why,
    string IosPath)
{
    /// <summary>True when "Apply recommended" would write this row.</summary>
    public bool NeedsWrite => Status == ProsimSetupStatus.Mismatch;

    /// <summary>The recommended value as a bool when the dataref is a tick box.</summary>
    public bool IsBoolean => Recommended is "true" or "false";
}

/// <summary>The outcome of one "Apply recommended" click, for the card's note and the log.</summary>
/// <param name="Written">Rows that now read back with the recommended value.</param>
/// <param name="Failed">Rows ProSim rejected or that read back unchanged.</param>
public sealed record ProsimSetupApplyResult(IReadOnlyList<string> Written, IReadOnlyList<string> Failed)
{
    /// <summary>A short sentence for the card.</summary>
    public string Summary => (Written.Count, Failed.Count) switch
    {
        (0, 0) => "Nothing to change.",
        (_, 0) => $"Set {Written.Count} option{(Written.Count == 1 ? "" : "s")} in ProSim.",
        (0, _) => $"ProSim did not accept: {string.Join(", ", Failed)}.",
        _ => $"Set {Written.Count}; ProSim did not accept: {string.Join(", ", Failed)}.",
    };
}
