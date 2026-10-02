using System.Globalization;
using System.Text;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.TechLog;
using ProsimCompanion.Core.Weather;

namespace ProsimCompanion.Core.Speech;

/// <summary>Live figures the Core stores do not hold — the aircraft's own weights and fuel,
/// read from dataref subscriptions by the Speech pillar. Nulls = not available.</summary>
public interface ILiveAircraftFacts
{
    double? FuelOnBoardKg { get; }

    double? ZeroFuelWeightKg { get; }

    double? GrossWeightKg { get; }
}

/// <summary>One labelled line of the fact sheet and the numbers it contributes to the allowed set.</summary>
public sealed record FoFact(string Label, string Value, IReadOnlyList<double> Numbers);

/// <summary>
/// The fact sheet (issue #149): what the FO knows right now, as labelled lines built from the
/// existing stores, plus the set of EVERY number that appears in it. A free-form answer is
/// composed from this text and nothing else, and any number the model speaks must be in
/// <see cref="AllowedNumbers"/> — the same verifier rule as the briefings.
/// </summary>
public sealed record FoFactSheet(IReadOnlyList<FoFact> Facts)
{
    public static FoFactSheet Empty { get; } = new([]);

    /// <summary>Every number in the sheet (whole-number figures also allow their rounded
    /// tonnes, hours and minutes forms so "six point two tonnes" verifies against 6200 kg).</summary>
    public IReadOnlyList<double> AllowedNumbers
        => Facts.SelectMany(f => f.Numbers).Distinct().ToList();

    /// <summary>The "- Label: value" block handed to the model.</summary>
    public string Text
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var fact in Facts)
            {
                sb.Append("- ").Append(fact.Label).Append(": ").AppendLine(fact.Value);
            }

            return sb.ToString().TrimEnd();
        }
    }

    public bool IsEmpty => Facts.Count == 0;
}

/// <summary>Everything the builder reads. Any part may be null (store absent or empty).</summary>
public sealed record FoFactInputs(
    FlightStateView? Flight,
    FlightProgressSnapshot? Progress,
    FlightTimesSnapshot? Times,
    OfpData? Ofp,
    LoadsheetSnapshot? Loadsheet,
    ILiveAircraftFacts? Live,
    HeroWeatherSnapshot? Weather,
    ArrivalMinima? Minima,
    IReadOnlyList<TechLogDefect>? OpenDefects,
    DateTimeOffset NowUtc);

/// <summary>Pure builder: inputs in, fact sheet out. Blank facts are omitted, never guessed.</summary>
public static class FoFactSheetBuilder
{
    public static FoFactSheet Build(FoFactInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var facts = new List<FoFact>();

        void Add(string label, string? value, params double[] numbers)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                facts.Add(new FoFact(label, value, numbers));
            }
        }

        // ---- flight phase and the aircraft ----
        if (inputs.Flight is { } flight)
        {
            Add("Flight phase", PhaseWords(flight.Phase));
            if (flight.Data is { IsValid: true } data)
            {
                if (!data.OnGround)
                {
                    var altitude = Math.Round(data.AltitudeFt / 100) * 100;
                    Add("Altitude", $"{Int(altitude)} feet", altitude, Math.Round(altitude / 100));
                    Add("Ground speed", $"{Int(data.GroundSpeedKt)} knots", Math.Round(data.GroundSpeedKt));
                    Add("Indicated airspeed", $"{Int(data.IndicatedAirspeedKt)} knots", Math.Round(data.IndicatedAirspeedKt));
                    var vs = Math.Round(data.VerticalSpeedFpm / 100) * 100;
                    vs = vs == 0 ? 0 : vs; // never "-0"
                    Add("Vertical speed", $"{Int(vs)} feet per minute", vs, Math.Abs(vs));
                }

                Add("Outside air temperature", $"{Int(data.OatC)} degrees", Math.Round(data.OatC), Math.Abs(Math.Round(data.OatC)));
                if (data.FmsCruiseAltFt > 0)
                {
                    Add("Cruise altitude", $"flight level {Int(data.FmsCruiseAltFt / 100)}", Math.Round(data.FmsCruiseAltFt / 100), data.FmsCruiseAltFt);
                }
            }
        }

        // ---- fuel and weights ----
        if (inputs.Live is { } live)
        {
            if (live.FuelOnBoardKg is { } fob && fob > 0)
            {
                Add("Fuel on board", Kilos(fob), [.. KiloForms(fob)]);
            }

            if (live.ZeroFuelWeightKg is { } zfw && zfw > 0)
            {
                Add("Zero fuel weight", Kilos(zfw), [.. KiloForms(zfw)]);
            }

            if (live.GrossWeightKg is { } gw && gw > 0)
            {
                Add("Gross weight", Kilos(gw), [.. KiloForms(gw)]);
            }
        }

        if (inputs.Ofp is { } ofp)
        {
            if (ofp.FuelPlanRampKg > 0)
            {
                Add("Planned block fuel", Kilos(ofp.FuelPlanRampKg), [.. KiloForms(ofp.FuelPlanRampKg)]);
            }

            if (ofp.FuelPlanLandingKg > 0)
            {
                Add("Planned landing fuel", Kilos(ofp.FuelPlanLandingKg), [.. KiloForms(ofp.FuelPlanLandingKg)]);
            }

            if (ofp.FuelMinTakeoffKg > 0)
            {
                Add("Minimum takeoff fuel", Kilos(ofp.FuelMinTakeoffKg), [.. KiloForms(ofp.FuelMinTakeoffKg)]);
            }
        }

        if (inputs.Loadsheet is { } sheet && sheet.Final.Status == LoadsheetSlotStatus.Sent)
        {
            Add("Final loadsheet zero fuel weight", Kilos(sheet.Final.ZfwKg), [.. KiloForms(sheet.Final.ZfwKg)]);
            Add("Final loadsheet takeoff weight", Kilos(sheet.Final.TowKg), [.. KiloForms(sheet.Final.TowKg)]);
            Add("Passengers on the loadsheet", Int(sheet.Final.Pax), sheet.Final.Pax);
        }
        else if (inputs.Ofp is { PaxCount: > 0 } planned)
        {
            Add("Planned passengers", Int(planned.PaxCount), planned.PaxCount);
        }

        // ---- the plan ----
        if (inputs.Ofp is { } plan)
        {
            Add("Callsign", plan.Callsign, Digits(plan.Callsign));
            Add("Flight number", plan.FlightNumber, Digits(plan.FlightNumber));
            Add("Origin", Airport(plan.OriginIcao, plan.OriginName));
            Add("Destination", Airport(plan.DestinationIcao, plan.DestinationName));
            Add("Alternate", Airport(plan.AlternateIcao, plan.AlternateName));
            Add("Planned departure runway", plan.PlannedRunwayOut, RunwayNumber(plan.PlannedRunwayOut));
            Add("Planned arrival runway", plan.PlannedRunwayIn, RunwayNumber(plan.PlannedRunwayIn));
            if (plan.CruiseFlightLevel > 0)
            {
                Add("Planned cruise level", $"flight level {Int(plan.CruiseFlightLevel)}", plan.CruiseFlightLevel, plan.CruiseFlightLevel * 100.0);
            }

            Add("Cost index", plan.CostIndex, Number(plan.CostIndex));
            if (plan.ScheduledOutUtc is { } std)
            {
                Add("Scheduled departure", Clock(std), [.. ClockForms(std)]);
            }

            if (plan.EstimatedEnroute is { } enroute && enroute > TimeSpan.Zero)
            {
                Add("Planned flight time", Duration(enroute), [.. DurationForms(enroute)]);
            }
        }

        // ---- progress ----
        if (inputs.Progress is { } progress)
        {
            if (progress.DistanceToGoNm is { } toGo)
            {
                Add("Distance to destination", $"{Int(toGo)} nautical miles (great-circle direct)", Math.Round(toGo));
            }

            if (progress.DistanceFlownNm is { } flown)
            {
                Add("Distance flown", $"{Int(flown)} nautical miles", Math.Round(flown));
            }

            if (progress.EtaUtc is { } eta)
            {
                Add("Estimated arrival", Clock(eta) + (progress.EtaBasis == ProgressBasis.Position ? "" : " (time-based estimate)"), [.. ClockForms(eta)]);
                var remaining = eta - inputs.NowUtc;
                if (remaining > TimeSpan.Zero)
                {
                    Add("Time to destination", Duration(remaining), [.. DurationForms(remaining)]);
                }
            }

            if (progress.MinutesToTod is { } tod && tod > 0)
            {
                Add("Time to top of descent", $"about {Int(tod)} minutes (3 to 1 estimate)", Math.Round(tod));
            }
        }

        // ---- times ----
        if (inputs.Times is { } times)
        {
            if (times.OffBlocksUtc is { } off)
            {
                Add("Off blocks", Clock(off), [.. ClockForms(off)]);
            }

            if (times.TakeoffUtc is { } takeoff)
            {
                Add("Takeoff time", Clock(takeoff), [.. ClockForms(takeoff)]);
                var airborne = (times.LandingUtc ?? inputs.NowUtc) - takeoff;
                if (airborne > TimeSpan.Zero)
                {
                    Add(times.LandingUtc is null ? "Time airborne so far" : "Flight time", Duration(airborne), [.. DurationForms(airborne)]);
                }
            }

            if (times.LandingUtc is { } landing)
            {
                Add("Landing time", Clock(landing), [.. ClockForms(landing)]);
            }

            if (times.OnBlocksUtc is { } on)
            {
                Add("On blocks", Clock(on), [.. ClockForms(on)]);
            }
        }

        Add("Time now", Clock(inputs.NowUtc), [.. ClockForms(inputs.NowUtc)]);

        // ---- weather ----
        if (inputs.Weather is { } weather)
        {
            foreach (var card in new[] { weather.Local, weather.Second })
            {
                if (card.Status != WxProbeStatus.Found || string.IsNullOrWhiteSpace(card.Icao))
                {
                    continue;
                }

                var role = card.Role switch
                {
                    WeatherCardRole.Destination => "Destination weather",
                    WeatherCardRole.Alternate => "Alternate weather",
                    _ => "Local weather",
                };
                var (text, numbers) = WeatherLine(card);
                Add($"{role} ({card.Icao})", text, [.. numbers]);
            }
        }

        // ---- minima ----
        if (inputs.Minima is { } minima)
        {
            var kind = minima.Kind switch
            {
                ArrivalMinimumKind.DecisionAltitude => "decision altitude",
                ArrivalMinimumKind.DecisionHeight => "decision height",
                _ => "minimum descent altitude",
            };
            Add("Briefed minimums", $"{kind} {Int(minima.AltitudeFt)} feet", Math.Round(minima.AltitudeFt));
        }

        // ---- tech log ----
        if (inputs.OpenDefects is { Count: > 0 } defects)
        {
            Add("Open tech log items", Int(defects.Count), defects.Count);
            foreach (var defect in defects.Take(5))
            {
                var due = string.IsNullOrWhiteSpace(defect.DueDate) ? "" : $", due {defect.DueDate}";
                Add("Tech log item", $"{defect.Title} (MEL category {defect.Category}{due})");
            }
        }
        else if (inputs.OpenDefects is { Count: 0 })
        {
            Add("Open tech log items", "none", 0);
        }

        return new FoFactSheet(facts);
    }

    // ---- wording helpers -----------------------------------------------------------------------

    internal static string PhaseWords(FlightPhase phase) => phase switch
    {
        FlightPhase.ColdAndDark => "cold and dark at the gate",
        FlightPhase.Preflight => "preflight at the gate",
        FlightPhase.Departure => "departure preparation at the gate",
        FlightPhase.PushbackAndStart => "pushback and engine start",
        FlightPhase.TaxiOut => "taxiing out",
        FlightPhase.TakeoffRoll => "takeoff roll",
        FlightPhase.InitialClimb => "initial climb",
        FlightPhase.Climb => "climb",
        FlightPhase.Cruise => "cruise",
        FlightPhase.Descent => "descent",
        FlightPhase.Approach => "approach",
        FlightPhase.LandingRollout => "landing rollout",
        FlightPhase.TaxiIn => "taxiing in",
        FlightPhase.Shutdown => "shutdown at the gate",
        _ => "unknown",
    };

    private static string Int(double value) => Math.Round(value).ToString("0", CultureInfo.InvariantCulture);

    private static string Kilos(double kg)
        => $"{Int(kg)} kilograms ({Tonnes(kg).ToString("0.0", CultureInfo.InvariantCulture)} tonnes)";

    /// <summary>Tonnes to one decimal, half away from zero on the DECIMAL value (3050 kg is 3.1,
    /// not the 3.0 a binary 3.05 rounds to).</summary>
    private static double Tonnes(double kg) => (double)Math.Round((decimal)kg / 1000m, 1, MidpointRounding.AwayFromZero);

    /// <summary>A weight may be spoken as kilograms, as the rounded hundred, or as tonnes to one decimal.</summary>
    private static IEnumerable<double> KiloForms(double kg)
    {
        yield return Math.Round(kg);
        yield return Math.Round(kg / 100) * 100;
        yield return Tonnes(kg);
        yield return Math.Round(kg / 1000.0, MidpointRounding.AwayFromZero);
    }

    private static string Clock(DateTimeOffset utc) => utc.ToString("HH:mm", CultureInfo.InvariantCulture) + " zulu";

    /// <summary>"14:05" may be spoken as 1405, 14 and 05 — or as "fourteen oh five" (14, 5).</summary>
    private static IEnumerable<double> ClockForms(DateTimeOffset utc)
    {
        yield return utc.Hour * 100 + utc.Minute;
        yield return utc.Hour;
        yield return utc.Minute;
    }

    private static string Duration(TimeSpan span)
    {
        var hours = (int)span.TotalHours;
        var minutes = span.Minutes;
        return hours > 0
            ? $"{hours} hours {minutes} minutes"
            : $"{minutes} minutes";
    }

    private static IEnumerable<double> DurationForms(TimeSpan span)
    {
        yield return (int)span.TotalHours;
        yield return span.Minutes;
        yield return Math.Round(span.TotalMinutes);
        yield return Math.Round(span.TotalHours, 1);
    }

    private static string? Airport(string icao, string name)
        => string.IsNullOrWhiteSpace(icao) ? null
            : string.IsNullOrWhiteSpace(name) ? icao : $"{name} ({icao})";

    private static double[] RunwayNumber(string runway)
    {
        var digits = new string((runway ?? "").Trim().TrimStart('R', 'W', 'r', 'w').TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? [n] : [];
    }

    /// <summary>The digit run inside a callsign or flight number ("BAW552" → 552), so the model may say it.</summary>
    private static double[] Digits(string text)
    {
        var digits = new string((text ?? "").Where(char.IsAsciiDigit).ToArray());
        return digits.Length > 0 && double.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? [n] : [];
    }

    private static double[] Number(string text)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? [n] : [];

    private static (string Text, List<double> Numbers) WeatherLine(WeatherCard card)
    {
        var f = card.Facts;
        var parts = new List<string>();
        var numbers = new List<double>();
        if (f.WindSpeedKt is { } speed)
        {
            if (f.WindDirDeg is { } dir)
            {
                parts.Add($"wind {dir.ToString("000", CultureInfo.InvariantCulture)} degrees at {speed} knots");
                numbers.Add(dir);
            }
            else
            {
                parts.Add(speed == 0 ? "wind calm" : $"wind variable at {speed} knots");
            }

            numbers.Add(speed);
            if (f.WindGustKt is { } gust)
            {
                parts.Add($"gusting {gust} knots");
                numbers.Add(gust);
            }
        }

        if (f.VisibilityMeters is { } vis)
        {
            parts.Add(vis >= 9999 ? "visibility 10 kilometres or more" : $"visibility {vis} metres");
            numbers.Add(vis >= 9999 ? 10 : vis);
            numbers.Add(vis);
        }

        if (f.CeilingFt is { } ceiling)
        {
            parts.Add($"ceiling {ceiling} feet");
            numbers.Add(ceiling);
        }
        else if (!string.IsNullOrWhiteSpace(card.SkyLabel))
        {
            parts.Add(card.SkyLabel.ToLowerInvariant());
        }

        if (f.TemperatureC is { } temp)
        {
            parts.Add($"temperature {temp} degrees");
            numbers.Add(temp);
            numbers.Add(Math.Abs(temp));
        }

        if (f.QnhHpa is { } qnh)
        {
            parts.Add($"QNH {Int(qnh)}");
            numbers.Add(Math.Round(qnh));
        }

        if (!string.IsNullOrWhiteSpace(f.AtisLetter))
        {
            parts.Add($"ATIS information {f.AtisLetter}");
        }

        if (!string.IsNullOrWhiteSpace(f.ActiveRunway))
        {
            parts.Add($"runway in use {f.ActiveRunway}");
            numbers.AddRange(RunwayNumber(f.ActiveRunway));
        }

        return (string.Join(", ", parts), numbers);
    }
}
