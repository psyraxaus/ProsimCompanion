using System.Globalization;
using System.Text.RegularExpressions;

namespace ProsimCompanion.Speech.Mcdu;

// The page readers of the MCDU check. Every layout below was captured from the owner's rig on
// 2026-11-15 (ProSim A322 1.76, A320-200 / V2527-A5, EGLL at the gate, BAW504 EGLL-LPPT) by
// reading aircraft.mcdu2.display after each page key; the test fixtures are those captures.
// A reader returns null when the page is not the one it knows — a layout from another ProSim
// build is then "not checked", never a guessed value. Rows are the parser's cleaned text
// (colour codes stripped, trimmed), so nothing here depends on a column position.

/// <summary>DATA → A/C STATUS: the airframe, the engine and the navigation database cycle.</summary>
public sealed record AircraftStatusFacts(string AircraftType, string Engine, DateOnly? DatabaseFrom, DateOnly? DatabaseTo, string DatabaseText);

/// <summary>DATA → IRS MONITOR: the mode under each IRS ("NAV" when aligned).</summary>
public sealed record IrsMonitorFacts(IReadOnlyList<string> Modes);

/// <summary>DATA → GPS MONITOR: the mode and satellite count of each receiver.</summary>
public sealed record GpsMonitorFacts(IReadOnlyList<(string Mode, int Satellites)> Receivers);

/// <summary>INIT A. A field not entered (or not readable) is null.</summary>
public sealed record InitAFacts(string? Origin, string? Destination, string? Alternate, string? FlightNumber, int? CostIndex, int? CruiseFlightLevel);

/// <summary>INIT B (INIT FUEL PREDICTION), in the box's own weight unit.</summary>
public sealed record InitBFacts(double? Zfw, double? ZfwCg, double? Block);

/// <summary>One discontinuity of the active plan: the fixes on each side (null at an end).</summary>
public sealed record FlightPlanGap(string? After, string? Before);

/// <summary>The active F-PLN as scrolled: the origin line's runway, every gap, whether the end
/// of the plan was reached, and the number of legs seen.</summary>
public sealed record FlightPlanFacts(string? Origin, string? DepartureRunway, IReadOnlyList<FlightPlanGap> Gaps, bool EndReached, int Legs);

/// <summary>RAD NAV: the ILS line.</summary>
public sealed record RadNavFacts(string? IlsIdent, string? IlsFrequency);

/// <summary>PERF TAKE OFF. A field not entered is null.</summary>
public sealed record PerfTakeoffFacts(
    string? Runway, int? V1, int? Vr, int? V2, int? Flaps, bool ThsEntered, int? FlexTempC,
    int? TransitionAltitudeFt, int? ThrustReductionFt, int? AccelerationFt);

/// <summary>PROG: the GPS PRIMARY flag and the estimated accuracy ("HIGH" / "LOW").</summary>
public sealed record ProgFacts(bool GpsPrimary, string? Accuracy);

/// <summary>Pure readers from a parsed <see cref="McduPage"/> to the facts of one page.</summary>
public static partial class BoxCheckPages
{
    public const string EndOfPlan = "END OF F-PLN";
    public const string Discontinuity = "F-PLN DISCONTINUITY";

    private static readonly string[] Months = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    public static bool IsDataIndex(McduPage page) => Title(page).Contains("DATA INDEX", StringComparison.Ordinal);

    public static bool IsSecIndex(McduPage page) => Title(page).Contains("SEC INDEX", StringComparison.Ordinal);

    public static bool IsInitA(McduPage page)
        => Title(page).StartsWith("INIT", StringComparison.Ordinal) && !Title(page).Contains("FUEL", StringComparison.Ordinal);

    public static bool IsInitB(McduPage page) => Title(page).Contains("INIT FUEL", StringComparison.Ordinal);

    /// <summary>The LSK row (1..6) of a page link whose data line — or, when
    /// <paramref name="label"/> is given, whose small label — carries the text. Null when the
    /// page has no such row: the check then leaves that sub-page out.</summary>
    public static int? LinkRow(McduPage page, string data, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        var hit = page.Rows.FirstOrDefault(r => r.Data.Contains(data, StringComparison.Ordinal)
            && (label is null || r.Label.Contains(label, StringComparison.Ordinal)));
        return hit?.Row;
    }

    public static AircraftStatusFacts? AircraftStatus(McduPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var database = page.Rows.FirstOrDefault(r => r.Label.Contains("ACTIVE NAV DATA BASE", StringComparison.Ordinal));
        if (database is null)
        {
            return null;
        }

        var engine = page.Rows.FirstOrDefault(r => r.Label.Contains("ENG", StringComparison.Ordinal))?.Data ?? "";
        var match = DatabaseCycle().Match(database.Data);
        return new AircraftStatusFacts(
            Title(page), engine,
            match.Success ? Date(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value) : null,
            match.Success ? Date(match.Groups[4].Value, match.Groups[5].Value, match.Groups[6].Value) : null,
            database.Data);
    }

    public static IrsMonitorFacts? IrsMonitor(McduPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!Title(page).Contains("IRS MONITOR", StringComparison.Ordinal))
        {
            return null;
        }

        // The mode of an IRS is the small line UNDER its "<IRSn" line, which the parser files
        // as the label of the next row.
        var modes = new List<string>();
        for (var i = 0; i < page.Rows.Count - 1; i++)
        {
            if (page.Rows[i].Data.Contains("IRS", StringComparison.Ordinal))
            {
                modes.Add(page.Rows[i + 1].Label);
            }
        }

        return modes.Count == 0 ? null : new IrsMonitorFacts(modes);
    }

    public static GpsMonitorFacts? GpsMonitor(McduPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!Title(page).Contains("GPS MONITOR", StringComparison.Ordinal))
        {
            return null;
        }

        var receivers = page.Rows
            .Where(r => r.Label.Contains("MODE/SAT", StringComparison.Ordinal))
            .Select(r => ModeAndSatellites().Match(r.Data))
            .Where(m => m.Success)
            .Select(m => (m.Groups[1].Value, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))
            .ToList();
        return receivers.Count == 0 ? null : new GpsMonitorFacts(receivers);
    }

    public static InitAFacts? InitA(McduPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!IsInitA(page))
        {
            return null;
        }

        var cityPair = CityPair().Match(Data(page, "FROM/TO"));
        var costIndexTokens = Tokens(Data(page, "COST INDEX"));

        // "5   34421" = cost index and tropopause; one number alone is the tropopause default.
        int? costIndex = costIndexTokens.Length >= 2 && int.TryParse(costIndexTokens[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ci) && ci <= 999
            ? ci
            : null;
        var cruise = CruiseLevel().Match(Data(page, "CRZ FL"));
        return new InitAFacts(
            cityPair.Success ? cityPair.Groups[1].Value : null,
            cityPair.Success ? cityPair.Groups[2].Value : null,
            Ident(Tokens(Data(page, "ALTN")).FirstOrDefault(), 4, 4),
            // "BAW504   IRS INIT>": with no number entered the first word is the prompt's.
            Ident(Tokens(Data(page, "FLT NBR")).FirstOrDefault(), 2, 8) is { } number && number != "IRS" ? number : null,
            costIndex,
            cruise.Success ? int.Parse(cruise.Groups[1].Value, CultureInfo.InvariantCulture) : null);
    }

    public static InitBFacts? InitB(McduPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!IsInitB(page))
        {
            return null;
        }

        var weights = ZfwAndCg().Match(Data(page, "ZFW/ZFWCG"));
        var block = Tokens(Data(page, "BLOCK")).LastOrDefault();
        return new InitBFacts(
            weights.Success ? Number(weights.Groups[1].Value) : null,
            weights.Success ? Number(weights.Groups[2].Value) : null,
            block is not null && Decimal().IsMatch(block) ? Number(block) : null);
    }

    public static RadNavFacts? RadNav(McduPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!Title(page).Contains("RADIO NAV", StringComparison.Ordinal))
        {
            return null;
        }

        var ils = IlsLine().Match(Data(page, "ILS"));
        return new RadNavFacts(ils.Success ? ils.Groups[1].Value : null, ils.Success ? ils.Groups[2].Value : null);
    }

    public static PerfTakeoffFacts? PerfTakeoff(McduPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var title = TakeoffTitle().Match(Title(page));
        if (!title.Success)
        {
            return null;
        }

        var v2Row = Data(page, "V2");
        var flapsAndThs = FlapsWithThs().Match(v2Row);
        var flaps = flapsAndThs.Success ? flapsAndThs : FlapsWithoutThs().Match(v2Row);
        var transitionRow = Data(page, "TRANS ALT");
        var flex = transitionRow.Contains('[', StringComparison.Ordinal) ? Match.Empty : FlexAtEnd().Match(transitionRow);
        var reduction = TwoAltitudes().Match(Data(page, "THR RED/ACC"));
        return new PerfTakeoffFacts(
            title.Groups[1].Success ? title.Groups[1].Value : null,
            Speed(Data(page, "V1")), Speed(Data(page, "VR")), Speed(v2Row),
            flaps.Success ? int.Parse(flaps.Groups[1].Value, CultureInfo.InvariantCulture) : null,
            flapsAndThs.Success,
            flex.Success ? int.Parse(flex.Groups[1].Value, CultureInfo.InvariantCulture) : null,
            int.TryParse(Tokens(transitionRow).FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var transition) && transition >= 1000 ? transition : null,
            reduction.Success ? int.Parse(reduction.Groups[1].Value, CultureInfo.InvariantCulture) : null,
            reduction.Success ? int.Parse(reduction.Groups[2].Value, CultureInfo.InvariantCulture) : null);
    }

    public static ProgFacts? Prog(McduPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var accuracy = page.Rows.FirstOrDefault(r => r.Label.Contains("ACCUR", StringComparison.Ordinal));
        if (accuracy is null)
        {
            return null;
        }

        return new ProgFacts(
            page.Rows.Any(r => r.Data.Contains("GPS PRIMARY", StringComparison.Ordinal)),
            accuracy.Data.Contains("HIGH", StringComparison.Ordinal) ? "HIGH"
                : accuracy.Data.Contains("LOW", StringComparison.Ordinal) ? "LOW" : null);
    }

    /// <summary>True when the first leg line of a SEC F-PLN page is the end marker — nothing
    /// was copied or built there.</summary>
    public static bool SecondaryIsEmpty(McduPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return page.Rows.Count > 0 && page.Rows[0].Data.Contains(EndOfPlan, StringComparison.Ordinal);
    }

    /// <summary>The five leg lines of an F-PLN screen (the sixth row is the destination summary).</summary>
    public static IReadOnlyList<string> LegLines(McduPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return [.. page.Rows.Take(5).Select(r => r.Data)];
    }

    /// <summary>Appends a scrolled screen to the lines seen so far. The screens overlap (the
    /// scroll key moves one line; several presses may land between two reads), so the new
    /// screen is joined at the longest run that ends the old lines and starts the new ones;
    /// with no such run every line is taken. Returns how many lines were added.</summary>
    public static int Merge(List<string> seen, IReadOnlyList<string> screen)
    {
        ArgumentNullException.ThrowIfNull(seen);
        ArgumentNullException.ThrowIfNull(screen);
        var overlap = 0;
        for (var size = Math.Min(seen.Count, screen.Count); size > 0; size--)
        {
            if (seen.Skip(seen.Count - size).SequenceEqual(screen.Take(size), StringComparer.Ordinal))
            {
                overlap = size;
                break;
            }
        }

        seen.AddRange(screen.Skip(overlap));
        return screen.Count - overlap;
    }

    /// <summary>The active plan from its scrolled leg lines: everything up to the first
    /// "END OF F-PLN" (the alternate plan after it is not judged).</summary>
    public static FlightPlanFacts FlightPlan(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var gaps = new List<FlightPlanGap>();
        string? origin = null;
        string? runway = null;
        string? lastFix = null;
        var pendingGap = false;
        var legs = 0;
        var end = false;
        foreach (var line in lines)
        {
            if (line.Contains(EndOfPlan, StringComparison.Ordinal))
            {
                end = true;
                break;
            }

            if (line.Contains(Discontinuity, StringComparison.Ordinal))
            {
                pendingGap = true;
                continue;
            }

            var fix = Tokens(line).FirstOrDefault();
            if (string.IsNullOrEmpty(fix))
            {
                continue;
            }

            if (legs == 0 && OriginLine().Match(fix) is { Success: true } first)
            {
                origin = first.Groups[1].Value;
                runway = first.Groups[2].Success ? first.Groups[2].Value : null;
            }

            legs++;

            // (T/D), (DECEL), (LIM) … are computed points, not fixes a pilot would name.
            if (fix.StartsWith('('))
            {
                continue;
            }

            if (pendingGap)
            {
                gaps.Add(new FlightPlanGap(lastFix, fix));
                pendingGap = false;
            }

            lastFix = fix;
        }

        if (pendingGap)
        {
            gaps.Add(new FlightPlanGap(lastFix, null));
        }

        return new FlightPlanFacts(origin, runway, gaps, end, legs);
    }

    private static string Title(McduPage page) => McduDisplayParser.Collapse(page.Title);

    /// <summary>The data line of the row whose small label carries the text ("" when absent).</summary>
    private static string Data(McduPage page, string label)
        => page.Rows.FirstOrDefault(r => r.Label.Contains(label, StringComparison.Ordinal))?.Data ?? "";

    private static string[] Tokens(string line) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static string? Ident(string? token, int min, int max)
        => token is not null && token.Length >= min && token.Length <= max && token.All(char.IsAsciiLetterOrDigit)
            && !string.Equals(token, "NONE", StringComparison.Ordinal)
            ? token
            : null;

    private static int? Speed(string data)
        => int.TryParse(Tokens(data).FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var speed) && speed is >= 80 and <= 220
            ? speed
            : null;

    private static double Number(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static DateOnly? Date(string day, string month, string year)
    {
        var index = Array.IndexOf(Months, month);
        try
        {
            return index < 0
                ? null
                : new DateOnly(2000 + int.Parse(year, CultureInfo.InvariantCulture), index + 1, int.Parse(day, CultureInfo.InvariantCulture));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"(\d{2})([A-Z]{3})(\d{2})-(\d{2})([A-Z]{3})(\d{2})")]
    private static partial Regex DatabaseCycle();

    [GeneratedRegex(@"([A-Z]+)/(\d+)\s*$")]
    private static partial Regex ModeAndSatellites();

    [GeneratedRegex(@"\b([A-Z][A-Z0-9]{3})/([A-Z][A-Z0-9]{3})\b")]
    private static partial Regex CityPair();

    [GeneratedRegex(@"^FL(\d{2,3})\b")]
    private static partial Regex CruiseLevel();

    [GeneratedRegex(@"(\d{1,3}\.\d)\s*/\s*(\d{1,2}\.\d)\s*$")]
    private static partial Regex ZfwAndCg();

    [GeneratedRegex(@"^\d{1,3}\.\d$")]
    private static partial Regex Decimal();

    [GeneratedRegex(@"^([A-Z]{2,4})/(\d{3}\.\d{2})")]
    private static partial Regex IlsLine();

    [GeneratedRegex(@"TAKE OFF(?:\s+RWY\s+(\d{2}[LRC]?))?")]
    private static partial Regex TakeoffTitle();

    // "1/UP0.5" entered; "[]/[   ]" empty; "1/[   ]" flaps without the trim. The entered forms
    // are the real aircraft's — not yet seen on the rig (the capture had both fields empty).
    [GeneratedRegex(@"\b(\d)\s*/\s*(?:UP|DN)?\s*\d\.\d\s*(?:UP|DN)?\s*$")]
    private static partial Regex FlapsWithThs();

    [GeneratedRegex(@"\b(\d)\s*/\s*\[")]
    private static partial Regex FlapsWithoutThs();

    [GeneratedRegex(@"(\d{1,2})°\s*$")]
    private static partial Regex FlexAtEnd();

    [GeneratedRegex(@"(\d{3,5})\s*/\s*(\d{3,5})")]
    private static partial Regex TwoAltitudes();

    [GeneratedRegex(@"^([A-Z]{4})(\d{2}[LRC]?)?$")]
    private static partial Regex OriginLine();
}
