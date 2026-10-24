using System.Globalization;

namespace ProsimCompanion.Core.Aircraft.Acars;

/// <summary>What one movement message is about.</summary>
public enum MovementKind
{
    /// <summary>Airborne: AD (actual departure) with the out and off times and the EA (estimated arrival).</summary>
    Off,

    /// <summary>On blocks: AA (actual arrival) with the on and in times.</summary>
    In,
}

/// <summary>
/// The IATA-style movement (MVT) messages every airline flight exchanges (issue #185), composed
/// from the OOOI stamps the flight-times tracker already keeps. Pure, so the text is a table
/// test; the exact field layout is in docs/integrations/acars.md. Times are UTC <c>HHMM</c>, the
/// day of month from the OUT stamp (OFF) or the ON stamp (IN), so a midnight crossing between
/// out and off prints the out day with an off time past 2359 read as the next day — the AHM
/// convention. The delay suffix <c>DL {code}/{HHMM}</c> rides on whichever message is composed
/// after the code is known.
/// </summary>
public static class MovementMessageComposer
{
    /// <summary>The OFF message: <c>MVT / {flt}/{dd}.{reg}.{dep} / AD {out}/{off} EA {eta} {dest} [/ DL {code}/{HHMM}]</c>,
    /// one field group per line. A missing EA (no ETA yet) drops the EA clause; a missing STD
    /// leaves no DL even with a code (no schedule = no delay).</summary>
    public static string Off(
        string flightNumber, string registration, string departureIcao, string destinationIcao,
        DateTimeOffset outUtc, DateTimeOffset offUtc, DateTimeOffset? etaUtc,
        string? delayCode = null, int? delayMinutes = null)
    {
        var lines = new List<string>
        {
            "MVT",
            Header(flightNumber, outUtc, registration, departureIcao),
            etaUtc is { } eta
                ? $"AD {Time(outUtc)}/{Time(offUtc)} EA {Time(eta)} {Icao(destinationIcao)}"
                : $"AD {Time(outUtc)}/{Time(offUtc)} {Icao(destinationIcao)}".TrimEnd(),
        };
        if (DelayLine(delayCode, delayMinutes) is { } delay)
        {
            lines.Add(delay);
        }

        return string.Join("\n", lines);
    }

    /// <summary>The IN message: <c>MVT / {flt}/{dd}.{reg}.{dest} / AA {on}/{in} [/ DL …]</c>.</summary>
    public static string In(
        string flightNumber, string registration, string destinationIcao,
        DateTimeOffset onUtc, DateTimeOffset inUtc,
        string? delayCode = null, int? delayMinutes = null)
    {
        var lines = new List<string>
        {
            "MVT",
            Header(flightNumber, onUtc, registration, destinationIcao),
            $"AA {Time(onUtc)}/{Time(inUtc)}",
        };
        if (DelayLine(delayCode, delayMinutes) is { } delay)
        {
            lines.Add(delay);
        }

        return string.Join("\n", lines);
    }

    /// <summary>"DL 15/0022" — code and the delay as HHMM; null without a code or with no delay.</summary>
    public static string? DelayLine(string? delayCode, int? delayMinutes)
    {
        if (string.IsNullOrWhiteSpace(delayCode) || delayMinutes is not { } minutes || minutes <= 0)
        {
            return null;
        }

        var span = TimeSpan.FromMinutes(Math.Min(minutes, 99 * 60 + 59));
        return $"DL {delayCode.Trim()}/{(int)span.TotalHours:00}{span.Minutes:00}";
    }

    /// <summary>The FO's spoken line for a sent message, when the switch asks for one.</summary>
    public static string Spoken(MovementKind kind, bool sent)
        => sent
            ? kind == MovementKind.Off
                ? "Company copies, movement message sent — airborne."
                : "Company copies, movement message sent — on blocks."
            : "Movement message not sent — ACARS unavailable.";

    /// <summary>The delay the OUT stamp carries against the schedule, minutes (positive = late); null without a schedule.</summary>
    public static int? DelayMinutes(DateTimeOffset? scheduledOutUtc, DateTimeOffset? actualOutUtc)
        => scheduledOutUtc is { } std && actualOutUtc is { } actual ? (int)Math.Round((actual - std).TotalMinutes) : null;

    /// <summary>On time = off blocks no later than the schedule plus the tolerance (the industry
    /// OTP rule, 15 minutes by default). Null without a schedule.</summary>
    public static bool? OnTime(int? delayMinutes, int toleranceMinutes)
        => delayMinutes is { } d ? d <= toleranceMinutes : null;

    private static string Header(string flightNumber, DateTimeOffset day, string registration, string station)
        => $"{Clean(flightNumber)}/{day.Day:00}.{Clean(registration)}.{Icao(station)}";

    private static string Time(DateTimeOffset t) => t.ToUniversalTime().ToString("HHmm", CultureInfo.InvariantCulture);

    private static string Icao(string icao) => Clean(icao).ToUpperInvariant();

    private static string Clean(string text) => (text ?? "").Trim().Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
}
