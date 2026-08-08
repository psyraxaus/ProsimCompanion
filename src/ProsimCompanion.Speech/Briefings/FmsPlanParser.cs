using System.Xml.Linq;

namespace ProsimCompanion.Speech.Briefings;

/// <summary>The MCDU-configured route fields parsed from <c>aircraft.fms.flightPlanXml</c>.
/// Any field the crew hasn't set (no runway/SID/STAR selected yet) is null.</summary>
public sealed record FmsPlan(
    string? Origin,
    string? Destination,
    string? OriginRunway,
    string? DestinationRunway,
    string? Alternate = null,
    string? Sid = null,
    string? Star = null)
{
    public static FmsPlan Empty { get; } = new(null, null, null, null);
}

/// <summary>
/// Parses ProSim's <c>aircraft.fms.flightPlanXml</c> into the route identifiers a briefing
/// needs. Real ProSim output carries origin/destination/runways as CHILD ELEMENTS of the
/// route node (the predecessor's proven parse); attributes are kept as a fallback for older
/// payload shapes. Never throws — an unparseable plan returns <see cref="FmsPlan.Empty"/> so
/// the procedure source falls through to the next tier.
/// </summary>
public static class FmsPlanParser
{
    public static FmsPlan Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return FmsPlan.Empty;
        }

        try
        {
            var doc = XDocument.Parse(xml);
            var route = doc.Descendants("route")
                .FirstOrDefault(r => (string?)r.Attribute("type") == "act")
                ?? doc.Descendants("route").FirstOrDefault();
            if (route is null)
            {
                return FmsPlan.Empty;
            }

            var (sid, star) = ParseSidStar(
                route.Element("flightplan")?.Value ?? (string?)route.Attribute("flightplan"));
            return new FmsPlan(
                Field(route, "origin"),
                Field(route, "destination"),
                Field(route, "originRunway"),
                Field(route, "destinationRunway"),
                ParseAlternate(route),
                sid,
                star);
        }
        catch
        {
            return FmsPlan.Empty;
        }
    }

    /// <summary>Element first (real ProSim payloads), attribute as fallback.</summary>
    private static string? Field(XElement route, string name)
        => NullIfBlank(route.Element(name)?.Value) ?? NullIfBlank((string?)route.Attribute(name));

    /// <summary>The alternate is the first leg of type "Destination" AFTER the
    /// "EndOfFlightPlan" marker (the alternate route section).</summary>
    private static string? ParseAlternate(XElement route)
    {
        var legs = route.Element("legs")?.Elements("leg").ToList();
        if (legs is null)
        {
            return null;
        }

        var end = legs.FindIndex(l => string.Equals(
            (string?)l.Element("type"), "EndOfFlightPlan", StringComparison.OrdinalIgnoreCase));
        if (end < 0)
        {
            return null;
        }

        for (var i = end + 1; i < legs.Count; i++)
        {
            if (string.Equals((string?)legs[i].Element("type"), "Destination", StringComparison.OrdinalIgnoreCase))
            {
                return NullIfBlank(legs[i].Element("displayString")?.Value);
            }
        }

        return null;
    }

    /// <summary>
    /// SID/STAR from the LNAV route string, e.g. <c>EGLL MODM1J.MODMI M185 MID … TUNUR LFMN</c>.
    /// Procedures use <c>IDENT.TRANSITION</c> notation: the SID is the dotted token right after
    /// the origin and the STAR the dotted token right before the destination; the identifier is
    /// the part BEFORE the dot (the DFD <c>procedure_identifier</c> — taking the part after it
    /// yields the transition, which no DFD lookup matches). Airways / DCT / plain waypoints
    /// have no dot, so a non-dotted token yields no procedure (no false positives).
    /// </summary>
    public static (string? Sid, string? Star) ParseSidStar(string? flightplan)
    {
        if (string.IsNullOrWhiteSpace(flightplan))
        {
            return (null, null);
        }

        var tokens = flightplan.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 3)
        {
            return (null, null);
        }

        static string? Proc(string token)
            => token.Contains('.', StringComparison.Ordinal) ? NullIfBlank(token.Split('.')[0]) : null;

        return (Proc(tokens[1]), Proc(tokens[^2]));
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
