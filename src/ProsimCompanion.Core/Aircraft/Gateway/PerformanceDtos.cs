namespace ProsimCompanion.Core.Aircraft.Gateway;

// Wire DTOs for the ProSim gateway's /efb/calculate/* endpoints. Property names match the
// gateway's PascalCase request / camelCase response convention — serialize with no naming policy
// (PascalCase writes) and PropertyNameCaseInsensitive = true (camelCase reads). Field semantics
// were reverse-engineered in the predecessor projects; scale conventions are load-bearing.

/// <summary>Request for <c>POST /efb/calculate/vspeeds</c>.</summary>
public sealed class CalcVSpeedRequest
{
    public string AircraftType { get; set; } = "A320";

    /// <summary>"CFM" | "IAE".</summary>
    public string EngineVariant { get; set; } = "CFM";

    /// <summary>Takeoff weight in tens of kg (70.5 t → 705).</summary>
    public int Tow { get; set; }

    /// <summary>"KG" | "LBS".</summary>
    public string TowUnit { get; set; } = "KG";

    /// <summary>%MAC × 10 (25.3 → 253).</summary>
    public int Mactow { get; set; }

    /// <summary>TORA in metres (capped server-side at 3600).</summary>
    public int RwyLen { get; set; }

    /// <summary>Numeric runway heading (270 for "27L").</summary>
    public int RwyQDM { get; set; }

    /// <summary>"opt" | "1+F" | "2" | "3" — lowercase "opt".</summary>
    public string FlapVal { get; set; } = "opt";

    /// <summary>"DRY" | "WET".</summary>
    public string Surf { get; set; } = "DRY";

    /// <summary>"OFF" | "ENG" | "ENG+WING".</summary>
    public string IceVal { get; set; } = "OFF";

    /// <summary>"OFF" | "ON".</summary>
    public string PacksVal { get; set; } = "ON";

    /// <summary>"NO" | "YES".</summary>
    public string TogaVal { get; set; } = "NO";

    /// <summary>Airport elevation in feet.</summary>
    public int Elev { get; set; }

    /// <summary>OAT in °C.</summary>
    public int Temp { get; set; }

    /// <summary>QNH in hPa, as a string.</summary>
    public string QnhVal { get; set; } = "1013";

    /// <summary>"VRB" or a numeric heading ("270"), as a string.</summary>
    public string WindDir { get; set; } = "0";

    /// <summary>Wind speed in kt.</summary>
    public int WindMag { get; set; }

    public List<FailuresResponse> SelectedFailures { get; set; } = [];
}

/// <summary>Response of <c>POST /efb/calculate/vspeeds</c>.</summary>
public sealed class CalcVSpeedResult
{
    /// <summary>Set when the lookup tables cannot satisfy the inputs — delivered in a 200 body,
    /// not via an HTTP error.</summary>
    public string? CalculationError { get; set; }

    public int? InputTow { get; set; }
    public int? InputElevation { get; set; }

    public VSpeed? VSpeed { get; set; }

    /// <summary>1 → CONF 1+F, 2 → CONF 2, 3 → CONF 3. Maps directly to
    /// <c>aircraft.fms.perf.takeOff.flaps</c>.</summary>
    public int FlapSettings { get; set; }

    /// <summary><see cref="FlexOutput"/> is the clamped value the EFB displays — authoritative
    /// for display and FMS uplink; <see cref="Flex"/> is the unbounded internal value.
    /// 0 ⇒ TOGA forced (see <see cref="ForceToga"/>).</summary>
    public int? Flex { get; set; }
    public int? FlexOutput { get; set; }

    /// <summary>THS magnitude (0.0–2.5); <see cref="TrimDir"/> carries the sign as "UP"/"DN"/"".
    /// THS dataref value = (TrimDir=="DN" ? −1 : +1) × TrimOutput.</summary>
    public double? TrimOutput { get; set; }
    public string TrimDir { get; set; } = string.Empty;

    /// <summary>TOPL in kg regardless of TowUnit.</summary>
    public double Topl { get; set; }
    public bool ToplLimited { get; set; }

    /// <summary>Signed: positive = headwind, negative = tailwind.</summary>
    public int HwComp { get; set; }

    /// <summary>Always null on the A320 path; kept for forward compatibility.</summary>
    public int? FSpeed { get; set; }
    public int? SSpeed { get; set; }

    public int? GreenDot { get; set; }
    public int? VmcMinV2 { get; set; }
    public int MinFlex { get; set; }
    public int MaxFlex { get; set; }
    public bool ForceToga { get; set; }
    public bool FwdCG { get; set; }
    public int LowerTempBound { get; set; }
    public int HwBracket { get; set; }
    public double TowRound { get; set; }
    public double ElevRound { get; set; }

    public InfluenceFactor? InfluenceCG { get; set; }
    public InfluenceFactor? InfluenceEAI { get; set; }
    public InfluenceFactor? InfluencePacks { get; set; }
    public VSpeed? InfluenceTotal { get; set; }
}

/// <summary>Doubles on the wire; round to int for display and dataref writes.</summary>
public sealed class VSpeed
{
    public double V1 { get; set; }
    public double VR { get; set; }
    public double V2 { get; set; }
}

public sealed class InfluenceFactor
{
    public int? InflTopl { get; set; }
    public int? InflFlex { get; set; }
    public int? InflV1 { get; set; }
    public int? InflVR { get; set; }
    public int? InflV2 { get; set; }
    public int? Vmc { get; set; }
    public string? PosRelVmc { get; set; }
}

/// <summary>Request for <c>POST /efb/calculate/ldr</c>. The misspelled <c>Break*</c> property
/// names are what the gateway's binder reads — never "correct" them.</summary>
public sealed class CalcLdrRequest
{
    public int Qdm { get; set; }
    public float Elev { get; set; }

    /// <summary>"VRB" or a numeric string.</summary>
    public string? WindDir { get; set; }
    public float WindSpeed { get; set; }
    public float Oat { get; set; }

    /// <summary>QNH in hPa.</summary>
    public float Slp { get; set; }

    /// <summary>Runway condition 1–6 (6 = Dry … 1 = Poor). sic — gateway spelling.</summary>
    public int BreakAction { get; set; }

    public int? AircraftSpeed { get; set; }

    /// <summary>Landing weight in tonnes (66 t reference scale).</summary>
    public float LdgW { get; set; }

    /// <summary>"LOW" | "MED" | "MAX". sic — gateway spelling.</summary>
    public string? BreakMode { get; set; }

    /// <summary>"idle" or other.</summary>
    public string? Rev { get; set; }

    /// <summary>"0" | "1".</summary>
    public string? Autoland { get; set; }

    /// <summary>"FULL" | "3" (uppercased server-side).</summary>
    public string? FlapConfig { get; set; }

    /// <summary>"0" | "1".</summary>
    public string? Athr { get; set; }

    public List<FailuresResponse> SelectedFailures { get; set; } = [];
}

/// <summary>Response of <c>POST /efb/calculate/ldr</c>.</summary>
public sealed class CalcLdrResponse
{
    /// <summary>0 ⇒ no data (dash everything out); −2 ⇒ retreat-flap defensive value (not emitted
    /// on the A320 path); &gt; 0 ⇒ landing distance required in metres.</summary>
    public int Ldr { get; set; }
}
