using System.Globalization;
using Microsoft.Extensions.Localization;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Web.Components;

/// <summary>
/// The captain's PA script (issue #186): short airline-style sentences from the pack, each one
/// filled with a live fact and dropped when its fact is missing — a script never reads "flight
/// time — minutes". The captain reads it over the cabin PA; nothing speaks it. Pure, for tests.
/// </summary>
public static class PaScriptPresentation
{
    /// <summary>The pack id of the prompt kind's label.</summary>
    public static string KindTextId(PaPromptKind kind) => kind switch
    {
        PaPromptKind.AfterTakeoff => "flightStatus.pa.kind.afterTakeoff",
        PaPromptKind.TopOfDescent => "flightStatus.pa.kind.topOfDescent",
        _ => "flightStatus.pa.kind.seatbeltsOn",
    };

    /// <summary>The script, one sentence per entry.</summary>
    public static IReadOnlyList<string> Lines(PaPromptKind kind, PaScriptFacts facts, IStringLocalizer T)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(T);
        var lines = new List<string>();
        var destination = facts.DestinationName ?? facts.DestinationIcao;

        switch (kind)
        {
            case PaPromptKind.AfterTakeoff:
                lines.Add(facts.FlightNumber is { } flight && destination is { } to
                    ? T["flightStatus.pa.script.welcome", flight, to].Value
                    : destination is { } onlyTo
                        ? T["flightStatus.pa.script.welcomeNoFlight", onlyTo].Value
                        : T["flightStatus.pa.script.welcomePlain"].Value);
                if (facts.FlightMinutes is { } minutes)
                {
                    lines.Add(T["flightStatus.pa.script.flightTime", Duration(minutes)].Value);
                }

                if (facts.CruiseFlightLevel is { } fl)
                {
                    lines.Add(T["flightStatus.pa.script.cruise", (fl * 100).ToString("N0", CultureInfo.InvariantCulture)].Value);
                }

                AddWeather(lines, facts, destination, T);
                AddEta(lines, facts, T);
                lines.Add(T["flightStatus.pa.script.sitBack"].Value);
                break;

            case PaPromptKind.TopOfDescent:
                lines.Add(destination is { } dest
                    ? T["flightStatus.pa.script.descent", dest].Value
                    : T["flightStatus.pa.script.descentPlain"].Value);
                AddEta(lines, facts, T);
                AddWeather(lines, facts, destination, T);
                lines.Add(T["flightStatus.pa.script.seatbeltsForLanding"].Value);
                lines.Add(T["flightStatus.pa.script.thanks"].Value);
                break;

            default:
                lines.Add(T["flightStatus.pa.script.turbulence"].Value);
                lines.Add(T["flightStatus.pa.script.returnToSeats"].Value);
                lines.Add(T["flightStatus.pa.script.crewSuspend"].Value);
                break;
        }

        return lines;
    }

    /// <summary>"1 h 52 min" / "48 min".</summary>
    public static string Duration(int minutes)
        => minutes >= 60
            ? $"{minutes / 60} h {minutes % 60:00} min"
            : $"{minutes} min";

    /// <summary>"12:40Z".</summary>
    public static string Stamp(DateTimeOffset utc) => utc.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture) + "Z";

    private static void AddEta(List<string> lines, PaScriptFacts facts, IStringLocalizer T)
    {
        if (facts.EtaUtc is { } eta)
        {
            lines.Add(T["flightStatus.pa.script.eta", Stamp(eta)].Value);
        }
    }

    private static void AddWeather(List<string> lines, PaScriptFacts facts, string? destination, IStringLocalizer T)
    {
        if (facts.WeatherSkyLabel is not { Length: > 0 } sky || destination is null)
        {
            return;
        }

        var parts = new List<string> { sky.ToLowerInvariant() };
        if (facts.WeatherTemperatureC is { } temperature)
        {
            parts.Add(T["flightStatus.pa.script.temperature", temperature].Value);
        }

        if (facts.WeatherWindDirDeg is { } dir && facts.WeatherWindSpeedKt is { } kt)
        {
            parts.Add(T["flightStatus.pa.script.wind", dir.ToString("000", CultureInfo.InvariantCulture), kt].Value);
        }

        lines.Add(T["flightStatus.pa.script.weather", destination, string.Join(", ", parts)].Value);
    }
}
