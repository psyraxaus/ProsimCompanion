using System.Globalization;
using System.Text;

namespace ProsimCompanion.Core.Logbook;

/// <summary>
/// The logbook as CSV (issue #146) — one row per flight, oldest first. Built for a
/// spreadsheet on any PC: every number is invariant-culture (a decimal point, no thousands
/// separator, whatever the Windows region says), every time is UTC ISO 8601 with a trailing
/// Z, an unknown value is an empty cell, and the column set only ever grows at the end so a
/// saved import mapping keeps working. RFC 4180 quoting; CRLF line ends.
/// </summary>
public static class LogbookCsv
{
    /// <summary>Column order. Append only.</summary>
    public static readonly IReadOnlyList<string> Columns =
    [
        "sessionId", "date", "origin", "destination", "departureRunway", "arrivalRunway",
        "offBlocksUtc", "takeoffUtc", "landingUtc", "onBlocksUtc",
        "blockMinutes", "flightMinutes", "landed",
        "liftoffIasKt", "touchdownGroundSpeedKt", "touchdownVerticalSpeedFpm", "touchdownIasKt",
        "touchdownPitchDeg", "bounces", "approachResult",
        "abnormals", "defectsRaised", "defectsRectified", "defectsCarried",
    ];

    public static string Build(IEnumerable<LogbookFlight> flights)
    {
        ArgumentNullException.ThrowIfNull(flights);

        var csv = new StringBuilder();
        csv.Append(string.Join(',', Columns)).Append("\r\n");
        foreach (var f in flights)
        {
            string[] cells =
            [
                Text(f.SessionId), Text(f.Date), Text(f.Origin), Text(f.Destination),
                Text(f.DepartureRunway), Text(f.ArrivalRunway),
                Time(f.OffBlocksUtc), Time(f.TakeoffUtc), Time(f.LandingUtc), Time(f.OnBlocksUtc),
                Number(f.BlockMinutes), Number(f.FlightMinutes), f.Landed ? "true" : "false",
                Number(f.LiftoffIasKt, "0.#"), Number(f.TouchdownGroundSpeedKt, "0.#"),
                Number(f.TouchdownVerticalSpeedFpm, "0"), Number(f.TouchdownIasKt, "0.#"),
                Number(f.TouchdownPitchDeg, "0.#"), Number(f.Bounces), Text(f.ApproachResult),
                Text(string.Join("; ", f.Abnormals ?? [])),
                Number(f.DefectsRaised), Number(f.DefectsRectified), Number(f.DefectsCarried),
            ];
            csv.Append(string.Join(',', cells)).Append("\r\n");
        }

        return csv.ToString();
    }

    /// <summary>The download name: sortable, and safe on every file system.</summary>
    public static string FileName(DateTimeOffset nowUtc)
        => "prosimcompanion-logbook-"
            + nowUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv";

    private static string Time(DateTimeOffset? value)
        => value is { } time
            ? time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : "";

    private static string Number(int? value)
        => value is { } number ? number.ToString(CultureInfo.InvariantCulture) : "";

    private static string Number(double? value, string format)
        => value is { } number && double.IsFinite(number)
            ? number.ToString(format, CultureInfo.InvariantCulture)
            : "";

    /// <summary>A text cell: quoted when it holds a comma, quote or line break. A value that
    /// starts with a formula character gets a leading apostrophe — abnormal titles come from
    /// user-editable files, and a spreadsheet would otherwise run "=…" as a formula.</summary>
    private static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var text = value[0] is '=' or '+' or '-' or '@' ? "'" + value : value;
        return text.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : text;
    }
}
