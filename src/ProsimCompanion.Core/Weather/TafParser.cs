using System.Globalization;
using System.Text.RegularExpressions;

namespace ProsimCompanion.Core.Weather;

/// <summary>The forecast conditions at one moment, as a TAF reads them (issue #183).</summary>
/// <param name="VisibilityM">Prevailing visibility in metres; null when the TAF gives none.</param>
/// <param name="CeilingFt">Lowest BKN / OVC / vertical-visibility layer, feet AGL; null = no ceiling.</param>
/// <param name="Thunderstorm">TS in any group valid at the moment.</param>
/// <param name="WindDirDeg">Wind direction; null for variable or unknown.</param>
/// <param name="WindSpeedKt">Wind speed; null when unknown.</param>
/// <param name="WindGustKt">Gust; null when none.</param>
/// <param name="Precip">Dominant precipitation.</param>
/// <param name="GroupsUsed">The groups that shaped this reading, for the log ("base, TEMPO 1215/1218").</param>
public sealed record TafForecast(
    int? VisibilityM,
    int? CeilingFt,
    bool Thunderstorm,
    int? WindDirDeg,
    int? WindSpeedKt,
    int? WindGustKt,
    PrecipKind Precip,
    string GroupsUsed);

/// <summary>
/// A small TAF reader (issue #183): the base forecast, then FM groups that have begun, BECMG
/// groups that have begun (taken as in effect — the conservative side), and TEMPO / PROB groups
/// whose window covers the moment as a worst case (lowest visibility and ceiling, any TS, the
/// strongest wind). Enough for "what does the destination look like at our ETA"; not a
/// decoder. Times are day/hour pairs resolved against the moment asked for (a TAF never spans a
/// month boundary by more than a day or two). Pure; a TAF it cannot read yields null.
/// </summary>
public static partial class TafParser
{
    private sealed class Segment
    {
        public string Kind = "base";
        public DateTimeOffset? From;
        public DateTimeOffset? To;
        public readonly List<string> Tokens = [];
        public string Label = "base";
    }

    public static TafForecast? ForecastAt(string? taf, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(taf))
        {
            return null;
        }

        var tokens = taf.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.TrimEnd('=')).Where(t => t.Length > 0).ToList();
        if (tokens.Count == 0)
        {
            return null;
        }

        var segments = new List<Segment>();
        var current = new Segment();
        segments.Add(current);
        var headerDone = false;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!headerDone)
            {
                if (token is "TAF" or "AMD" or "COR" or "NIL" or "CNL" || IssueTime().IsMatch(token) || (token.Length == 4 && token.All(char.IsAsciiLetterUpper)))
                {
                    continue;
                }

                if (Validity().Match(token) is { Success: true } validity)
                {
                    (current.From, current.To) = Window(validity, at);
                    headerDone = true;
                    continue;
                }

                headerDone = true; // a TAF without a validity window: everything is base
            }

            if (FromGroup().Match(token) is { Success: true } fm)
            {
                current = new Segment { Kind = "FM", From = Resolve(fm.Groups[1].Value, fm.Groups[2].Value, fm.Groups[3].Value, at), Label = token };
                segments.Add(current);
                continue;
            }

            if (token is "BECMG" or "TEMPO" || ProbGroup().IsMatch(token))
            {
                var kind = token == "BECMG" ? "BECMG" : "TEMPO";
                var label = token;
                var next = i + 1 < tokens.Count ? tokens[i + 1] : "";
                if (ProbGroup().IsMatch(token) && next == "TEMPO")
                {
                    label += " TEMPO";
                    i++;
                    next = i + 1 < tokens.Count ? tokens[i + 1] : "";
                }

                var window = Validity().Match(next);
                current = new Segment { Kind = kind, Label = label };
                if (window.Success)
                {
                    (current.From, current.To) = Window(window, at);
                    current.Label += " " + next;
                    i++;
                }

                segments.Add(current);
                continue;
            }

            current.Tokens.Add(token);
        }

        var basis = segments[0];
        var state = Read(basis.Tokens);
        var used = new List<string> { "base" };
        foreach (var segment in segments.Skip(1))
        {
            if (segment.Kind is "FM" or "BECMG" && segment.From is { } from && from <= at && (segment.To is not { } to || segment.Kind == "FM" || at <= to.AddHours(6)))
            {
                var change = Read(segment.Tokens);
                state = Merge(state, change, worstCase: false);
                used.Add(segment.Label);
            }
        }

        foreach (var segment in segments.Skip(1))
        {
            if (segment.Kind == "TEMPO" && segment.From is { } from && segment.To is { } to && from <= at && at <= to)
            {
                var change = Read(segment.Tokens);
                state = Merge(state, change, worstCase: true);
                used.Add(segment.Label);
            }
        }

        return new TafForecast(state.Visibility, state.Ceiling, state.Thunderstorm, state.WindDir, state.WindSpeed, state.WindGust, state.Precip, string.Join(", ", used));
    }

    private sealed record Elements(int? Visibility, int? Ceiling, bool Thunderstorm, int? WindDir, int? WindSpeed, int? WindGust, PrecipKind Precip, bool WindGiven, bool VisibilityGiven, bool CloudsGiven, bool WeatherGiven, bool Nsw);

    private static Elements Read(IReadOnlyList<string> tokens)
    {
        int? visibility = null, ceiling = null, windDir = null, windSpeed = null, windGust = null;
        bool thunderstorm = false, windGiven = false, visibilityGiven = false, cloudsGiven = false, weatherGiven = false, nsw = false;
        var precip = PrecipKind.None;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (Wind().Match(token) is { Success: true } wind)
            {
                windGiven = true;
                windDir = wind.Groups[1].Value == "VRB" ? null : int.Parse(wind.Groups[1].Value, CultureInfo.InvariantCulture);
                windSpeed = int.Parse(wind.Groups[2].Value, CultureInfo.InvariantCulture);
                windGust = wind.Groups[4].Success ? int.Parse(wind.Groups[4].Value, CultureInfo.InvariantCulture) : null;
                continue;
            }

            if (token == "CAVOK")
            {
                visibilityGiven = true;
                cloudsGiven = true;
                visibility = 9999;
                ceiling = null;
                continue;
            }

            if (VisibilityMetres().Match(token) is { Success: true } vis)
            {
                visibilityGiven = true;
                visibility = int.Parse(vis.Groups[1].Value, CultureInfo.InvariantCulture);
                continue;
            }

            if (token == "P6SM")
            {
                visibilityGiven = true;
                visibility = 9999;
                continue;
            }

            if (VisibilityMiles().Match(token) is { Success: true } sm)
            {
                visibilityGiven = true;
                var whole = sm.Groups[1].Success ? double.Parse(sm.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                if (sm.Groups[2].Success)
                {
                    whole += double.Parse(sm.Groups[2].Value, CultureInfo.InvariantCulture) / double.Parse(sm.Groups[3].Value, CultureInfo.InvariantCulture);
                }

                visibility = (int)Math.Round(Math.Min(9999, whole * 1609.344));
                continue;
            }

            if (Cloud().Match(token) is { Success: true } cloud)
            {
                cloudsGiven = true;
                var layer = cloud.Groups[1].Value;
                var height = int.Parse(cloud.Groups[2].Value, CultureInfo.InvariantCulture) * 100;
                if (layer is "BKN" or "OVC" or "VV" && (ceiling is null || height < ceiling))
                {
                    ceiling = height;
                }

                if (cloud.Groups[3].Success && cloud.Groups[3].Value.Contains("CB", StringComparison.Ordinal))
                {
                    // CB in a layer is not a TS group, but it is the thunderstorm the crew plans for.
                    thunderstorm = true;
                }

                continue;
            }

            if (token is "NSC" or "SKC" or "CLR" or "NCD")
            {
                cloudsGiven = true;
                ceiling = null;
                continue;
            }

            if (token == "NSW")
            {
                weatherGiven = true;
                nsw = true;
                precip = PrecipKind.None;
                thunderstorm = false;
                continue;
            }

            if (WeatherGroup().IsMatch(token))
            {
                weatherGiven = true;
                if (token.Contains("TS", StringComparison.Ordinal))
                {
                    thunderstorm = true;
                }

                precip = Worse(precip, Classify(token));
            }
        }

        return new Elements(visibility, ceiling, thunderstorm, windDir, windSpeed, windGust, precip, windGiven, visibilityGiven, cloudsGiven, weatherGiven, nsw);
    }

    /// <summary>A change group replaces only what it states; a worst-case group (TEMPO/PROB)
    /// takes the lower visibility and ceiling, any thunderstorm and the stronger wind.</summary>
    private static Elements Merge(Elements state, Elements change, bool worstCase)
    {
        if (!worstCase)
        {
            return state with
            {
                Visibility = change.VisibilityGiven ? change.Visibility : state.Visibility,
                Ceiling = change.CloudsGiven ? change.Ceiling : state.Ceiling,
                Thunderstorm = change.WeatherGiven ? change.Thunderstorm : state.Thunderstorm,
                Precip = change.WeatherGiven ? change.Precip : state.Precip,
                WindDir = change.WindGiven ? change.WindDir : state.WindDir,
                WindSpeed = change.WindGiven ? change.WindSpeed : state.WindSpeed,
                WindGust = change.WindGiven ? change.WindGust : state.WindGust,
            };
        }

        var strongerWind = change.WindGiven && (change.WindGust ?? change.WindSpeed ?? 0) > (state.WindGust ?? state.WindSpeed ?? 0);
        return state with
        {
            Visibility = change.VisibilityGiven && change.Visibility is { } v && (state.Visibility is not { } sv || v < sv) ? v : state.Visibility,
            Ceiling = change.CloudsGiven && change.Ceiling is { } c && (state.Ceiling is not { } sc || c < sc) ? c : state.Ceiling,
            Thunderstorm = state.Thunderstorm || change.Thunderstorm,
            Precip = Worse(state.Precip, change.Precip),
            WindDir = strongerWind ? change.WindDir : state.WindDir,
            WindSpeed = strongerWind ? change.WindSpeed : state.WindSpeed,
            WindGust = strongerWind ? change.WindGust : state.WindGust,
        };
    }

    private static PrecipKind Classify(string group)
    {
        if (group.Contains("FZ", StringComparison.Ordinal))
        {
            return PrecipKind.Freezing;
        }

        if (group.Contains("TS", StringComparison.Ordinal))
        {
            return PrecipKind.Thunderstorm;
        }

        if (group.Contains("SN", StringComparison.Ordinal) || group.Contains("SG", StringComparison.Ordinal) || group.Contains("PL", StringComparison.Ordinal) || group.Contains("GR", StringComparison.Ordinal) || group.Contains("GS", StringComparison.Ordinal))
        {
            return PrecipKind.Snow;
        }

        if (group.Contains("RA", StringComparison.Ordinal) || group.Contains("DZ", StringComparison.Ordinal) || group.Contains("SH", StringComparison.Ordinal))
        {
            return PrecipKind.Rain;
        }

        return PrecipKind.None;
    }

    private static PrecipKind Worse(PrecipKind a, PrecipKind b) => (Rank(a) >= Rank(b)) ? a : b;

    private static int Rank(PrecipKind kind) => kind switch
    {
        PrecipKind.Freezing => 5,
        PrecipKind.Thunderstorm => 4,
        PrecipKind.Snow => 3,
        PrecipKind.Rain => 2,
        PrecipKind.Other => 1,
        _ => 0,
    };

    private static (DateTimeOffset From, DateTimeOffset To) Window(Match validity, DateTimeOffset at)
    {
        var from = Resolve(validity.Groups[1].Value, validity.Groups[2].Value, "00", at);
        var to = Resolve(validity.Groups[3].Value, validity.Groups[4].Value, "00", at);
        if (to < from)
        {
            to = to.AddMonths(1);
        }

        return (from, to);
    }

    /// <summary>A TAF day/hour against the moment asked for: the month of <paramref name="at"/>,
    /// shifted one month either way when the day is clearly across a boundary. Hour 24 is the
    /// next day's 00.</summary>
    private static DateTimeOffset Resolve(string day, string hour, string minute, DateTimeOffset at)
    {
        var d = int.Parse(day, CultureInfo.InvariantCulture);
        var h = int.Parse(hour, CultureInfo.InvariantCulture);
        var m = int.Parse(minute, CultureInfo.InvariantCulture);
        var month = new DateTimeOffset(at.Year, at.Month, 1, 0, 0, 0, TimeSpan.Zero);
        if (d - at.Day > 15)
        {
            month = month.AddMonths(-1);
        }
        else if (at.Day - d > 15)
        {
            month = month.AddMonths(1);
        }

        var days = Math.Clamp(d, 1, DateTime.DaysInMonth(month.Year, month.Month));
        var moment = month.AddDays(days - 1);
        return h >= 24 ? moment.AddDays(1).AddMinutes(m) : moment.AddHours(h).AddMinutes(m);
    }

    [GeneratedRegex(@"^\d{6}Z$")]
    private static partial Regex IssueTime();

    [GeneratedRegex(@"^(\d{2})(\d{2})/(\d{2})(\d{2})$")]
    private static partial Regex Validity();

    [GeneratedRegex(@"^FM(\d{2})(\d{2})(\d{2})$")]
    private static partial Regex FromGroup();

    [GeneratedRegex(@"^PROB\d{2}$")]
    private static partial Regex ProbGroup();

    [GeneratedRegex(@"^(\d{3}|VRB)(\d{2,3})(G(\d{2,3}))?(KT|MPS)$")]
    private static partial Regex Wind();

    [GeneratedRegex(@"^(\d{4})(NDV)?$")]
    private static partial Regex VisibilityMetres();

    [GeneratedRegex(@"^(?:(\d+)(?!/))?(?:(\d)/(\d))?SM$")]
    private static partial Regex VisibilityMiles();

    [GeneratedRegex(@"^(FEW|SCT|BKN|OVC|VV)(\d{3})(CB|TCU)?$")]
    private static partial Regex Cloud();

    [GeneratedRegex(@"^[-+]?(VC)?(MI|BC|PR|DR|BL|SH|TS|FZ)?(DZ|RA|SN|SG|PL|GR|GS|UP|BR|FG|FU|VA|DU|SA|HZ|PO|SQ|FC|SS|DS|TS)+$")]
    private static partial Regex WeatherGroup();
}
