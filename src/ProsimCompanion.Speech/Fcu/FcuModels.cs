namespace ProsimCompanion.Speech.Fcu;

/// <summary>FCU dataref allow-list (Prosim2FO's fixed 15-entry set): value analogs are
/// latched writes, switches are momentary pulses (heading/altitude/speed/VS knobs encode
/// 1=push=managed, 2=pull=selected). Read-only indicators verify outcomes.</summary>
public static class FcuControls
{
    // Write targets (knobs/pushbuttons), aliased to the catalog's wire vocabulary.
    public const string HeadingKnob = Core.Aircraft.ProsimDataRefNames.FcuHeading;
    public const string AltitudeKnob = Core.Aircraft.ProsimDataRefNames.FcuAltitude;
    public const string SpeedKnob = Core.Aircraft.ProsimDataRefNames.FcuSpeed;
    public const string VsKnob = Core.Aircraft.ProsimDataRefNames.FcuVerticalSpeed;

    public const string Ap1 = Core.Aircraft.ProsimDataRefNames.FcuAp1;
    public const string Ap2 = Core.Aircraft.ProsimDataRefNames.FcuAp2;
    public const string Athr = Core.Aircraft.ProsimDataRefNames.FcuAthr;
    public const string Appr = Core.Aircraft.ProsimDataRefNames.FcuAppr;
    public const string Loc = Core.Aircraft.ProsimDataRefNames.FcuLoc;
    public const string Exped = Core.Aircraft.ProsimDataRefNames.FcuExped;
    public const string SpdMach = Core.Aircraft.ProsimDataRefNames.FcuSpdMach;

    // Read-only value/indicator refs are the typed catalog descriptors (#83):
    // ProsimDataRefNames.FcuSpeedValue/FcuHeadingValue/FcuAltitudeValue/FcuVsValue and
    // FcuAp1Indicator/FcuAp2Indicator/FcuAthrIndicator/FcuApprIndicator/FcuLocIndicator,
    // FcuSpeedManaged/FcuHeadingManaged/FcuAltitudeManaged.
}

public enum FcuField
{
    Heading,
    Altitude,
    Speed,
    VerticalSpeed,
}

public enum FcuInstructionType
{
    Unknown,

    /// <summary>Parsed and verbally relayed, deliberately never actioned.</summary>
    Conditional,

    /// <summary>A number was heard but no field — the FO asks which.</summary>
    Query,

    SetValue,
    Managed,
    Selected,
    Autopilot,
    AutoThrust,
    Approach,
    Localizer,
    Expedite,
    SpeedMachToggle,
}

/// <summary>One parsed ATC-style instruction.</summary>
public sealed record FcuInstruction(
    FcuInstructionType Type,
    FcuField? Field = null,
    double? Value = null,
    string? Readback = null,
    string? Reason = null,
    string RawText = "");
