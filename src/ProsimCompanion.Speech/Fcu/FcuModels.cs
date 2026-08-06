namespace ProsimCompanion.Speech.Fcu;

/// <summary>FCU dataref allow-list (Prosim2FO's fixed 15-entry set): value analogs are
/// latched writes, switches are momentary pulses (heading/altitude/speed/VS knobs encode
/// 1=push=managed, 2=pull=selected). Read-only indicators verify outcomes.</summary>
public static class FcuControls
{
    public const string HeadingValue = "system.analog.A_FCU_HEADING";
    public const string AltitudeValue = "system.analog.A_FCU_ALTITUDE";
    public const string SpeedValue = "system.analog.A_FCU_SPEED";
    public const string VsValue = "system.analog.A_FCU_VS";

    public const string HeadingKnob = "system.switches.S_FCU_HEADING";
    public const string AltitudeKnob = "system.switches.S_FCU_ALTITUDE";
    public const string SpeedKnob = "system.switches.S_FCU_SPEED";
    public const string VsKnob = "system.switches.S_FCU_VERTICAL_SPEED";

    public const string Ap1 = "system.switches.S_FCU_AP1";
    public const string Ap2 = "system.switches.S_FCU_AP2";
    public const string Athr = "system.switches.S_FCU_ATHR";
    public const string Appr = "system.switches.S_FCU_APPR";
    public const string Loc = "system.switches.S_FCU_LOC";
    public const string Exped = "system.switches.S_FCU_EXPED";
    public const string SpdMach = "system.switches.S_FCU_SPD_MACH";

    // Read-only verification indicators (never written).
    public const string HeadingManaged = "system.indicators.I_FCU_HEADING_MANAGED";
    public const string AltitudeManaged = "system.indicators.I_FCU_ALTITUDE_MANAGED";
    public const string SpeedManaged = "system.indicators.I_FCU_SPEED_MANAGED";
    public const string IndAp1 = "system.indicators.I_FCU_AP1";
    public const string IndAp2 = "system.indicators.I_FCU_AP2";
    public const string IndAthr = "system.indicators.I_FCU_ATHR";
    public const string IndAppr = "system.indicators.I_FCU_APPR";
    public const string IndLoc = "system.indicators.I_FCU_LOC";
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
