using System.Runtime.InteropServices;

namespace ProsimCompanion.Sim.Facilities;

/// <summary>
/// The facility-data rows this app asks MSFS for (SimConnect <c>AddToFacilityDefinition</c>;
/// SDK "Facility Definitions"). Field ORDER and TYPES must match the definition strings in
/// <see cref="FacilityDefinition"/> exactly — SimConnect marshals the bytes straight into
/// these structs. Plain data, no SimConnect types: the records cross into the facility
/// service and Core without the wrapper assembly ever being needed there.
/// </summary>
public static class FacilityDefinition
{
    /// <summary>The definition, in the order the structs below declare their fields.</summary>
    public static readonly string[] Fields =
    [
        "OPEN AIRPORT",
        "LATITUDE",
        "LONGITUDE",
        "ALTITUDE",
        "N_TAXI_PARKINGS",
        "N_JETWAYS",
        "OPEN TAXI_PARKING",
        "TYPE",
        "TAXI_POINT_TYPE",
        "NAME",
        "SUFFIX",
        "NUMBER",
        "ORIENTATION",
        "HEADING",
        "RADIUS",
        "BIAS_X",
        "BIAS_Z",
        "N_AIRLINES",
        "CLOSE TAXI_PARKING",
        "OPEN JETWAY",
        "PARKING_GATE",
        "PARKING_SUFFIX",
        "PARKING_SPOT",
        "CLOSE JETWAY",
        "CLOSE AIRPORT",
    ];
}

/// <summary>AIRPORT row: reference point (the parkings' BIAS_X/BIAS_Z are metres from it).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FacilityAirportRow
{
    public double Latitude;
    public double Longitude;
    public double AltitudeM;
    public int TaxiParkingCount;
    public int JetwayCount;
}

/// <summary>TAXI_PARKING row. NAME is the <c>ParkingName</c> enum value, SUFFIX 0 = none,
/// 1..26 = A..Z, TYPE the SDK parking type (9 GATE_MEDIUM, 10 GATE_HEAVY, 5 RAMP_CARGO…),
/// HEADING degrees true, RADIUS metres, BIAS_X/BIAS_Z metres east/north of the airport
/// reference point (sign convention verified against GSX ini positions — see
/// <c>AirportParkingCatalog</c>'s pose-spread log line).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FacilityParkingRow
{
    public int Type;
    public int TaxiPointType;
    public int Name;
    public int Suffix;
    public uint Number;
    public int Orientation;
    public float HeadingDeg;
    public float RadiusM;
    public float BiasX;
    public float BiasZ;
    public int AirlineCount;
}

/// <summary>JETWAY row: which parking (name enum, suffix, number) it serves.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FacilityJetwayRow
{
    public int ParkingGate;
    public int ParkingSuffix;
    public int ParkingSpot;
}
