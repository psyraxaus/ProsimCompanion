using System.Globalization;
using System.Text;

namespace ProsimCompanion.Core.Airports.Parking;

/// <summary>
/// The MSFS scenery NAME enum of a parking spot (SDK <c>TAXI_PARKING.NAME</c>). The numeric
/// values are the sim's own and double as GSX's <c>FSDT_GSX_SetGate_Name</c> readback map
/// (10 = Gate, 12..37 = Gate A..Z — see <c>GsxGateResolver.FormatReadback</c>), so the same
/// enum reads the facility data, the GSX profile section <c>[gate w 40]</c> and the GSX
/// <c>.py</c> group key <c>GATE_W</c>.
/// </summary>
public enum ParkingName
{
    None = 0,
    Parking = 1,
    NParking = 2,
    NeParking = 3,
    EParking = 4,
    SeParking = 5,
    SParking = 6,
    SwParking = 7,
    WParking = 8,
    NwParking = 9,
    Gate = 10,
    Dock = 11,
    GateA = 12,
    GateB, GateC, GateD, GateE, GateF, GateG, GateH, GateI, GateJ, GateK, GateL, GateM,
    GateN, GateO, GateP, GateQ, GateR, GateS, GateT, GateU, GateV, GateW, GateX, GateY,
    GateZ = 37,
}

/// <summary>
/// What a parking spot IS in the scenery: its name group, number and optional suffix letter
/// ("W40" = <see cref="ParkingName.GateW"/> 40, "" ; "34B" under Gate W = GateW 34 "B").
/// The one identity every source agrees on — scenery, GSX profile and GSX's remote API
/// (<c>number</c> + <c>bglName</c>) — and therefore the key the resolver matches on.
/// </summary>
public sealed record ParkingIdentity(ParkingName Name, int Number, string Suffix)
{
    /// <summary>The GATE letter (A–Z) when the name is a lettered gate, else null.</summary>
    public char? GateLetter => Name is >= ParkingName.GateA and <= ParkingName.GateZ
        ? (char)('A' + (Name - ParkingName.GateA))
        : null;

    /// <summary>The GSX <c>.py</c> group key for this name: <c>GATE</c>, <c>GATE_W</c>,
    /// <c>PARKING</c>, <c>N_PARKING</c>, <c>DOCK</c>; <c>0</c> for the NONE group (manual
    /// p.127 — "0" because <c>None</c> is a reserved key).</summary>
    public string PyGroupKey => Name switch
    {
        ParkingName.None => "0",
        ParkingName.Parking => "PARKING",
        ParkingName.NParking => "N_PARKING",
        ParkingName.NeParking => "NE_PARKING",
        ParkingName.EParking => "E_PARKING",
        ParkingName.SeParking => "SE_PARKING",
        ParkingName.SParking => "S_PARKING",
        ParkingName.SwParking => "SW_PARKING",
        ParkingName.WParking => "W_PARKING",
        ParkingName.NwParking => "NW_PARKING",
        ParkingName.Gate => "GATE",
        ParkingName.Dock => "DOCK",
        _ => "GATE_" + GateLetter,
    };

    /// <summary>The name words GSX writes in its <c>.ini</c> section header, lower-case:
    /// <c>gate w</c>, <c>gate</c>, <c>parking</c>, <c>n parking</c>, <c>dock</c>, <c>none</c>.</summary>
    public string IniNameWords => Name switch
    {
        ParkingName.None => "none",
        ParkingName.Parking => "parking",
        ParkingName.NParking => "n parking",
        ParkingName.NeParking => "ne parking",
        ParkingName.EParking => "e parking",
        ParkingName.SeParking => "se parking",
        ParkingName.SParking => "s parking",
        ParkingName.SwParking => "sw parking",
        ParkingName.WParking => "w parking",
        ParkingName.NwParking => "nw parking",
        ParkingName.Gate => "gate",
        ParkingName.Dock => "dock",
        _ => "gate " + char.ToLowerInvariant(GateLetter!.Value),
    };

    /// <summary>
    /// GSX's display name for an UNCUSTOMISED parking (manual p.23/181 examples "Gate A1",
    /// "Parking 12"): the name words, a space, the letter (no space), number and suffix —
    /// "Gate W40", "Gate 17", "Parking 401", "N Parking 5", "Dock 3". Only a guess for the
    /// exact spacing; compare through <see cref="ParkingText.Normalize"/>, never literally.
    /// </summary>
    public string DefaultDisplayName
    {
        get
        {
            var words = IniNameWords;
            var builder = new StringBuilder(words.Length + 8);
            var capitalize = true;
            foreach (var c in words)
            {
                builder.Append(capitalize ? char.ToUpperInvariant(c) : c);
                capitalize = c == ' ';
            }

            if (GateLetter is { } letter)
            {
                // "Gate W" → "Gate W40": the letter already ends the words; glue the number on.
                builder.Append(Number.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(' ').Append(Number.ToString(CultureInfo.InvariantCulture));
            }

            return builder.Append(Suffix).ToString();
        }
    }

    /// <summary>The short designator a pilot says or types: "W40", "34B", "313", "A12".</summary>
    public string Designator
        => string.Create(CultureInfo.InvariantCulture, $"{GateLetter}{Number}{Suffix}");

    /// <summary>Parses the GSX <c>.py</c> group key (<c>GATE_W</c>, <c>PARKING</c>, <c>0</c>…)
    /// into a name; null for anything that is not a parking group (e.g. <c>FUEL</c>).</summary>
    public static ParkingName? ParsePyGroupKey(string key)
    {
        var k = key.Trim().ToUpperInvariant();
        switch (k)
        {
            case "0":
            case "NONE":
                return ParkingName.None;
            case "PARKING": return ParkingName.Parking;
            case "N_PARKING": return ParkingName.NParking;
            case "NE_PARKING": return ParkingName.NeParking;
            case "E_PARKING": return ParkingName.EParking;
            case "SE_PARKING": return ParkingName.SeParking;
            case "S_PARKING": return ParkingName.SParking;
            case "SW_PARKING": return ParkingName.SwParking;
            case "W_PARKING": return ParkingName.WParking;
            case "NW_PARKING": return ParkingName.NwParking;
            case "GATE": return ParkingName.Gate;
            case "DOCK": return ParkingName.Dock;
        }

        if (k.Length == 6 && k.StartsWith("GATE_", StringComparison.Ordinal) && k[5] is >= 'A' and <= 'Z')
        {
            return ParkingName.GateA + (k[5] - 'A');
        }

        return null;
    }

    /// <summary>Parses the name words of a GSX <c>.ini</c> section header ("gate w", "parking",
    /// "n parking", "none"); null when the words are not a parking name (de-ice areas and the
    /// like are free text and fall out here).</summary>
    public static ParkingName? ParseIniNameWords(string words)
    {
        var w = words.Trim().ToLowerInvariant();
        switch (w)
        {
            case "none": return ParkingName.None;
            case "parking": return ParkingName.Parking;
            case "n parking": return ParkingName.NParking;
            case "ne parking": return ParkingName.NeParking;
            case "e parking": return ParkingName.EParking;
            case "se parking": return ParkingName.SeParking;
            case "s parking": return ParkingName.SParking;
            case "sw parking": return ParkingName.SwParking;
            case "w parking": return ParkingName.WParking;
            case "nw parking": return ParkingName.NwParking;
            case "gate": return ParkingName.Gate;
            case "dock": return ParkingName.Dock;
        }

        if (w.Length == 6 && w.StartsWith("gate ", StringComparison.Ordinal) && w[5] is >= 'a' and <= 'z')
        {
            return ParkingName.GateA + (w[5] - 'a');
        }

        return null;
    }
}
