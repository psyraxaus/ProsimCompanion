using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ProsimCompanion.Core.Aircraft.WeightAndBalance;

/// <summary>
/// Builds the JSON envelope written to <c>efb.prelimLoadsheet</c>/<c>efb.finalLoadsheet</c> —
/// the exact shape ProSim's own generators emit, so the ProSim EFB W&amp;B page renders our
/// values unmodified. Two non-obvious encodings verified against ProSim's serialized entity:
/// <c>type</c> is an enum int (Preliminary=1, Final=2) and every weight's <c>unit</c> is an
/// enum int (Kg=0); <c>macZfw</c>/<c>macTow</c> are flat doubles, not weight objects.
/// </summary>
public static class LoadsheetEnvelope
{
    private static readonly JsonSerializerOptions Wire = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Build(
        LoadsheetData data,
        LoadsheetContext ctx,
        bool isFinal,
        LoadsheetData? prelimForChk)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(ctx);

        // CHK flags are FINAL-only (vs the cached prelim); fuel is deliberately excluded here
        // even though it participates in the ACARS REVISIONS title.
        var zfwChk = prelimForChk is not null && Math.Abs(prelimForChk.ZeroFuelWeight - data.ZeroFuelWeight) > ctx.WeightChangeToleranceKg;
        var towChk = prelimForChk is not null && Math.Abs(prelimForChk.TakeoffWeight - data.TakeoffWeight) > ctx.WeightChangeToleranceKg;
        var macZfwChk = prelimForChk is not null && Math.Abs(prelimForChk.ZeroFuelWeightMac - data.ZeroFuelWeightMac) > ctx.MacChangeTolerancePercent;
        var macTowChk = prelimForChk is not null && Math.Abs(prelimForChk.TakeoffWeightMac - data.TakeoffWeightMac) > ctx.MacChangeTolerancePercent;

        // PRELIM-only near-limit markers; note the leading space (" L") in the JSON variant.
        string zfwLim = "", towLim = "", lawLim = "";
        if (prelimForChk is null)
        {
            var (zfw, tow, law) = LoadsheetFormatter.ComputeWeightLimitations(ctx, data);
            zfwLim = zfw.Length > 0 ? " L" : "";
            towLim = tow.Length > 0 ? " L" : "";
            lawLim = law.Length > 0 ? " L" : "";
        }

        var paxC1 = data.PassengersByZone.ElementAtOrDefault(0);
        var paxC2 = data.PassengersByZone.ElementAtOrDefault(1);
        var paxC3 = data.PassengersByZone.ElementAtOrDefault(2) + data.PassengersByZone.ElementAtOrDefault(3);

        var tofKg = data.TakeoffWeight - data.ZeroFuelWeight;
        var tifKg = data.TakeoffWeight - data.LandingWeight;
        const int unitKg = 0;

        var envelope = new
        {
            type = isFinal ? 2 : 1,
            ident = ctx.Ident,
            callsign = ctx.Callsign,
            editionNumber = ctx.EditionNumber,
            departureIata = ctx.DepartureIata,
            arrivalIata = ctx.ArrivalIata,
            aircraftTailNumber = ctx.AircraftTailNumber,
            zfw = new { value = data.ZeroFuelWeight, unit = unitKg },
            maxZfw = new { value = ctx.MaxZfwKg, unit = unitKg },
            zfwChk,
            tow = new { value = data.TakeoffWeight, unit = unitKg },
            maxTow = new { value = ctx.MaxTowKg, unit = unitKg },
            towChk,
            macZfw = data.ZeroFuelWeightMac,
            macZfwChk,
            macTow = data.TakeoffWeightMac,
            macTowChk,
            pax = data.TotalPassengers,
            infants = ctx.Infants,
            infantsOutput = string.IsNullOrEmpty(ctx.InfantsOutput) ? null : ctx.InfantsOutput,
            totalCargo = new { value = data.ForwardCargoWeight + data.AftCargoWeight, unit = unitKg },
            lizfw = new { value = data.LizfwIndex, unit = unitKg },
            litow = new { value = data.LitowIndex, unit = unitKg },
            fuelInTanks = new { value = data.FuelWeight, unit = unitKg },
            time = ctx.Time.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture),
            scheduledDepartureTime = ctx.ScheduledDepartureTime,
            paxC1,
            paxC2,
            paxC3,
            tif = new { value = tifKg, unit = unitKg },
            tof = new { value = tofKg, unit = unitKg },
            law = new { value = data.LandingWeight, unit = unitKg },
            maxLaw = new { value = ctx.MaxLawKg, unit = unitKg },
            lawLim,
            towLim,
            zfwLim,
            paxBusiness = paxC1,
            paxEconomy = paxC2 + paxC3,
            undld = new { value = ctx.MaxLawKg - data.LandingWeight, unit = unitKg },
            waterWaste = new { value = ctx.WaterWasteKg, unit = unitKg },
            dispatcherFirstName = ctx.DispatcherFirstName,
            dispatcherLastName = ctx.DispatcherLastName,
            units = "KG",
        };

        return JsonSerializer.Serialize(envelope, Wire);
    }

    /// <summary>
    /// Parses an envelope previously written to the EFB datarefs back into a usable loadsheet
    /// (issue #30): after an app restart mid-turnaround the dataref still holds the sent
    /// prelim, and restoring it re-enables the final (trip fuel, CHK comparisons, edition
    /// inheritance) without regenerating and re-uplinking a duplicate. Zones 3+4 were merged
    /// into <c>paxC3</c> at build time and total cargo is restored as forward — sums and
    /// comparisons stay exact, only the internal split is lost. Returns false for empty
    /// content, ProSim's literal "Null" placeholder, or unparseable JSON.
    /// </summary>
    public static bool TryParse(string? json, out bool isFinal, out LoadsheetData data, out LoadsheetContext ctx)
    {
        isFinal = false;
        data = new LoadsheetData();
        ctx = new LoadsheetContext();

        if (string.IsNullOrWhiteSpace(json) || json.Equals("Null", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type))
            {
                return false;
            }

            static double Weight(JsonElement parent, string name)
                => parent.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Object
                    && el.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Number
                    ? value.GetDouble()
                    : 0.0;
            static double Number(JsonElement parent, string name)
                => parent.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
                    ? el.GetDouble()
                    : 0.0;
            static string Text(JsonElement parent, string name)
                => parent.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
                    ? el.GetString() ?? ""
                    : "";

            isFinal = type.ValueKind == JsonValueKind.Number && type.GetInt32() == 2;

            data.ZeroFuelWeight = Weight(root, "zfw");
            data.ZeroFuelWeightMac = Number(root, "macZfw");
            data.TakeoffWeight = Weight(root, "tow");
            data.TakeoffWeightMac = Number(root, "macTow");
            data.FuelWeight = Weight(root, "fuelInTanks");
            data.LandingWeight = Weight(root, "law");
            data.LizfwIndex = Weight(root, "lizfw");
            data.LitowIndex = Weight(root, "litow");
            data.TotalPassengers = (int)Number(root, "pax");
            data.PassengersByZone =
                [(int)Number(root, "paxC1"), (int)Number(root, "paxC2"), (int)Number(root, "paxC3"), 0];
            data.ForwardCargoWeight = Weight(root, "totalCargo");
            data.AftCargoWeight = 0;

            ctx.Ident = Text(root, "ident");
            ctx.Callsign = Text(root, "callsign");
            ctx.EditionNumber = Math.Max(1, (int)Number(root, "editionNumber"));
            ctx.DepartureIata = Text(root, "departureIata");
            ctx.ArrivalIata = Text(root, "arrivalIata");
            ctx.AircraftTailNumber = Text(root, "aircraftTailNumber");
            ctx.ScheduledDepartureTime = Text(root, "scheduledDepartureTime");
            ctx.MaxZfwKg = Weight(root, "maxZfw");
            ctx.MaxTowKg = Weight(root, "maxTow");
            ctx.MaxLawKg = Weight(root, "maxLaw");
            ctx.WaterWasteKg = Weight(root, "waterWaste");
            ctx.Infants = (int)Number(root, "infants");
            ctx.DispatcherFirstName = Text(root, "dispatcherFirstName");
            ctx.DispatcherLastName = Text(root, "dispatcherLastName");
            if (DateTime.TryParseExact(
                    Text(root, "time"), "MM/dd/yyyy HH:mm:ss",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var time))
            {
                ctx.Time = time;
            }

            // A sheet without weights is a primed placeholder, not a loadsheet.
            return data.ZeroFuelWeight > 0 && data.TakeoffWeight > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>Random dispatcher identity for loadsheet flavour — the same 36×20 pool ProSim's
/// DispatcherNameGenerator draws from. Chosen once per prelim; the final inherits it.</summary>
public static class DispatcherNamePool
{
    private static readonly string[] FirstNames =
    [
        "Aamir", "Alex", "Brandon", "Joe", "Joshua", "Katie", "Matt", "Markus", "Fabio", "John",
        "Andreas", "Dave", "Ronald", "Fraser", "Fabian", "James", "Jamie", "Jon", "Julius", "Mark",
        "Marty", "Mike", "Norbert", "Philipp", "Simon", "Slawomir", "Reginald", "Tobias", "Tom", "Taj",
        "Judith", "Peter", "Ian", "Timmy", "Mitch", "Carl",
    ];

    private static readonly string[] Surnames =
    [
        "Wright", "English", "Scott", "Torres", "Nguyen", "Hill", "Flores", "Green", "Adams", "Nelson",
        "Baker", "Hall", "Rivera", "Campbell", "Carter", "Roberts", "Gomez", "Evans", "Turner", "Diaz",
    ];

    public static (string First, string Last) GetRandom()
        => (FirstNames[Random.Shared.Next(FirstNames.Length)],
            Surnames[Random.Shared.Next(Surnames.Length)]);
}
