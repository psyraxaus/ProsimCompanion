using System.Globalization;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Callouts;

namespace ProsimCompanion.Speech.Mcdu;

/// <summary>What the FO can enter for a finding. Route, SID and gap findings have none — the FO
/// reports those and the captain repairs them.</summary>
public enum BoxCheckFixKind
{
    /// <summary>MCDU entry on INIT A (needs <c>mcdu.allowActuation</c>).</summary>
    FlightNumber,

    /// <summary>MCDU entry on INIT A (needs <c>mcdu.allowActuation</c>).</summary>
    CostIndex,

    /// <summary>MCDU entry on INIT A (needs <c>mcdu.allowActuation</c>).</summary>
    CruiseLevel,

    /// <summary>ZFW, ZFWCG and block through the INIT B sync (datarefs, no key).</summary>
    InitB,

    /// <summary>V speeds, flex and flaps through the PERF datarefs (no key).</summary>
    PerfSpeeds,

    /// <summary>COPY ACTIVE on the SEC INDEX page (needs <c>mcdu.allowActuation</c>).</summary>
    CopyActive,
}

/// <summary>One offered correction: what to enter, and the question the FO asks.</summary>
public sealed record BoxCheckFix(BoxCheckFixKind Kind, string Value, string Prompt)
{
    /// <summary>True for the entries that press MCDU keys.</summary>
    public bool NeedsKeys => Kind is BoxCheckFixKind.FlightNumber or BoxCheckFixKind.CostIndex or BoxCheckFixKind.CruiseLevel or BoxCheckFixKind.CopyActive;
}

/// <summary>One thing the FO has to say about a page. <see cref="Text"/> is the card line;
/// <see cref="Spoken"/> is a full sentence for a difference and a short name ("V speeds") for
/// a field not entered yet.</summary>
public sealed record BoxCheckFinding(string ItemId, BoxCheckItemState State, string Text, string Spoken, BoxCheckFix? Fix = null);

/// <summary>One judged item: the card item and its findings.</summary>
public sealed record BoxCheckItemResult(BoxCheckItem Item, IReadOnlyList<BoxCheckFinding> Findings);

/// <summary>What the pages are compared with. Every part may be absent: a missing reference
/// removes its comparison, never the reading.</summary>
/// <param name="Loadsheet">The final loadsheet when one was sent, else the preliminary.</param>
/// <param name="Today">The sim date (the real date when the sim clock is not read).</param>
public sealed record BoxCheckReference(
    OfpData? Ofp,
    LoadsheetSlotView? Loadsheet,
    string LoadsheetName,
    TakeoffPerfSnapshot? Perf,
    DateOnly Today,
    Func<string?, string> AirportName);

/// <summary>
/// The judgement of the MCDU check, pure: page facts in, card item and findings out. A page
/// that was not read (null facts) is "not checked". Tolerances: weights 0.2 t, ZFWCG 0.3 %.
/// </summary>
public static class BoxCheckEvaluator
{
    public const double WeightToleranceTonnes = 0.2;
    public const double CgTolerancePercent = 0.3;
    private const double PoundsPerKg = 2.20462;

    public static BoxCheckItemResult Data(AircraftStatusFacts? status, IrsMonitorFacts? irs, GpsMonitorFacts? gps, BoxCheckReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var item = new Builder(BoxCheckItemIds.Data, "D", status is not null || irs is not null || gps is not null);
        if (status is not null)
        {
            item.Read($"{status.AircraftType}, {status.Engine}, database {status.DatabaseText}");
            if (status.DatabaseFrom is { } from && status.DatabaseTo is { } to && (reference.Today < from || reference.Today > to))
            {
                item.Difference(
                    $"Navigation database {status.DatabaseText} is not valid on {reference.Today.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)}",
                    "The navigation database is out of date.");
            }
        }

        if (irs is not null)
        {
            item.Read("IRS " + string.Join(" / ", irs.Modes.Select(m => m.Length == 0 ? "—" : m)));
            for (var i = 0; i < irs.Modes.Count; i++)
            {
                if (!string.Equals(irs.Modes[i], "NAV", StringComparison.Ordinal))
                {
                    item.Difference($"IRS {i + 1} is not in NAV ({(irs.Modes[i].Length == 0 ? "no mode" : irs.Modes[i])})", $"I R S {Word(i + 1)} is not in nav.");
                }
            }
        }

        if (gps is not null)
        {
            item.Read("GPS " + string.Join(" / ", gps.Receivers.Select(r => $"{r.Mode} {r.Satellites} sat")));
            for (var i = 0; i < gps.Receivers.Count; i++)
            {
                if (!string.Equals(gps.Receivers[i].Mode, "NAV", StringComparison.Ordinal))
                {
                    item.Difference($"GPS {i + 1} is not in NAV ({gps.Receivers[i].Mode})", $"G P S {Word(i + 1)} is not in nav.");
                }
            }
        }

        return item.Build();
    }

    public static BoxCheckItemResult InitA(InitAFacts? init, BoxCheckReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var item = new Builder(BoxCheckItemIds.InitA, "I", init is not null);
        if (init is null)
        {
            return item.Build();
        }

        var ofp = reference.Ofp;
        if (init.Origin is null || init.Destination is null)
        {
            item.NotEntered("FROM/TO not entered", "the route");
        }
        else
        {
            item.Read($"{init.Origin}/{init.Destination}, ALTN {init.Alternate ?? "none"}, {init.FlightNumber ?? "no flight number"}, CI {Text(init.CostIndex)}, FL{Text(init.CruiseFlightLevel)}");
            if (ofp is not null && Has(ofp.OriginIcao) && Has(ofp.DestinationIcao)
                && (!Same(init.Origin, ofp.OriginIcao) || !Same(init.Destination, ofp.DestinationIcao)))
            {
                item.Difference(
                    $"FROM/TO {init.Origin}/{init.Destination} — the plan is {ofp.OriginIcao}/{ofp.DestinationIcao}",
                    $"The route is {reference.AirportName(init.Origin)} to {reference.AirportName(init.Destination)}, the plan is {reference.AirportName(ofp.OriginIcao)} to {reference.AirportName(ofp.DestinationIcao)}.");
            }
        }

        if (ofp is not null && Has(ofp.AlternateIcao) && !Same(init.Alternate, ofp.AlternateIcao))
        {
            item.Difference(
                $"ALTN {init.Alternate ?? "none"} — the plan has {ofp.AlternateIcao}",
                init.Alternate is null
                    ? $"No alternate is entered, the plan has {reference.AirportName(ofp.AlternateIcao)}."
                    : $"The alternate is {reference.AirportName(init.Alternate)}, the plan has {reference.AirportName(ofp.AlternateIcao)}.");
        }

        var planned = PlannedFlightNumbers(ofp);
        if (init.FlightNumber is null)
        {
            var fix = planned.Count > 0
                ? new BoxCheckFix(BoxCheckFixKind.FlightNumber, planned[0], $"The flight number is not entered. Shall I enter {SpellIdent(planned[0])}?")
                : null;
            item.NotEntered("FLT NBR not entered", "the flight number", fix);
        }
        else if (planned.Count > 0 && !planned.Any(p => Same(p, init.FlightNumber)))
        {
            item.Difference(
                $"FLT NBR {init.FlightNumber} — the plan has {planned[0]}",
                $"The flight number is {SpellIdent(init.FlightNumber)}, the plan has {SpellIdent(planned[0])}.",
                new BoxCheckFix(BoxCheckFixKind.FlightNumber, planned[0], $"Shall I enter flight number {SpellIdent(planned[0])}?"));
        }

        var plannedCostIndex = ofp is not null && int.TryParse(ofp.CostIndex, NumberStyles.None, CultureInfo.InvariantCulture, out var ci) ? ci : (int?)null;
        if (init.CostIndex is null)
        {
            item.NotEntered(
                "COST INDEX not entered", "the cost index",
                plannedCostIndex is { } wanted
                    ? new BoxCheckFix(BoxCheckFixKind.CostIndex, Text((int?)wanted), $"The cost index is not entered, the plan says {Digits(wanted)}. Shall I enter it?")
                    : null);
        }
        else if (plannedCostIndex is { } wanted && wanted != init.CostIndex)
        {
            item.Difference(
                $"COST INDEX {init.CostIndex} — the plan says {wanted}",
                $"Cost index is {Digits(init.CostIndex.Value)}, the plan says {Digits(wanted)}.",
                new BoxCheckFix(BoxCheckFixKind.CostIndex, Text((int?)wanted), $"Shall I enter cost index {Digits(wanted)}?"));
        }

        var plannedLevel = ofp is { CruiseFlightLevel: > 0 } ? ofp.CruiseFlightLevel : (int?)null;
        if (init.CruiseFlightLevel is null)
        {
            item.NotEntered(
                "CRZ FL not entered", "the cruise level",
                plannedLevel is { } wanted
                    ? new BoxCheckFix(BoxCheckFixKind.CruiseLevel, Text((int?)wanted), $"The cruise level is not entered, the plan has flight level {Digits(wanted)}. Shall I enter it?")
                    : null);
        }
        else if (plannedLevel is { } wanted && wanted != init.CruiseFlightLevel)
        {
            item.Difference(
                $"CRZ FL{init.CruiseFlightLevel} — the plan has FL{wanted}",
                $"Cruise level is flight level {Digits(init.CruiseFlightLevel.Value)}, the plan has flight level {Digits(wanted)}.",
                new BoxCheckFix(BoxCheckFixKind.CruiseLevel, Text((int?)wanted), $"Shall I enter flight level {Digits(wanted)}?"));
        }

        return item.Build();
    }

    public static BoxCheckItemResult FlightPlan(FlightPlanFacts? plan, BoxCheckReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var item = new Builder(BoxCheckItemIds.FlightPlan, "F", plan is not null);
        if (plan is null)
        {
            return item.Build();
        }

        if (plan.Legs <= 1 && plan.Origin is null)
        {
            item.NotEntered("No flight plan entered", "the flight plan");
            return item.Build();
        }

        item.Read($"{plan.Origin}{plan.DepartureRunway}, {plan.Legs} line(s){(plan.EndReached ? "" : ", end of plan not reached")}");
        if (plan.DepartureRunway is null)
        {
            item.NotEntered("No departure runway selected", "the departure runway");
        }
        else if (reference.Perf is { Runway.Length: > 0 } perf && McduSpeech.NormalizeRunway(perf.Runway) is { } computed
            && !Same(McduSpeech.NormalizeRunway(plan.DepartureRunway), computed))
        {
            item.Difference(
                $"Departure runway {plan.DepartureRunway} — the takeoff figures are for {computed}",
                $"The flight plan departs {McduSpeech.SpeakRunway(plan.DepartureRunway)}, the takeoff figures are for {McduSpeech.SpeakRunway(computed)}.");
        }

        foreach (var gap in plan.Gaps)
        {
            item.Difference(
                gap.After is null ? $"Discontinuity before {gap.Before}"
                    : gap.Before is null ? $"Discontinuity after {gap.After}"
                    : $"Discontinuity between {gap.After} and {gap.Before}",
                gap.After is null ? $"A discontinuity before {SpeakFix(gap.Before!)}."
                    : $"A discontinuity after {SpeakFix(gap.After)}.");
        }

        return item.Build();
    }

    public static BoxCheckItemResult Secondary(bool? empty)
    {
        var item = new Builder(BoxCheckItemIds.Secondary, "S", empty is not null);
        if (empty == true)
        {
            item.NotEntered(
                "Secondary flight plan is empty", "the secondary flight plan",
                new BoxCheckFix(BoxCheckFixKind.CopyActive, "", "The secondary flight plan is empty. Shall I copy the active plan?"));
        }
        else if (empty == false)
        {
            item.Read("Secondary flight plan holds a route");
        }

        return item.Build();
    }

    public static BoxCheckItemResult RadNav(RadNavFacts? radNav)
    {
        var item = new Builder(BoxCheckItemIds.RadNav, "R", radNav is not null);
        if (radNav is not null)
        {
            item.Read(radNav.IlsIdent is null ? "No ILS tuned" : $"ILS {radNav.IlsIdent} {radNav.IlsFrequency}");
        }

        return item.Build();
    }

    public static BoxCheckItemResult InitB(InitBFacts? init, BoxCheckReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var item = new Builder(BoxCheckItemIds.InitB, "I", init is not null);
        if (init is null)
        {
            return item.Build();
        }

        var sheet = reference.Loadsheet is { ZfwKg: > 0 } ? reference.Loadsheet : null;
        var fix = sheet is null
            ? null
            : new BoxCheckFix(BoxCheckFixKind.InitB, "", $"Shall I enter the weights from the {reference.LoadsheetName} loadsheet?");
        if (init.Zfw is null || init.ZfwCg is null)
        {
            item.NotEntered("ZFW/ZFWCG not entered", "the zero fuel weight", fix);
        }
        else
        {
            item.Read($"ZFW {Tenths(init.Zfw.Value)} / CG {Tenths(init.ZfwCg.Value)}{(init.Block is { } b ? $", block {Tenths(b)}" : "")}");
            if (sheet is not null)
            {
                var sheetZfw = InBoxUnit(sheet.ZfwKg, init.Zfw.Value);
                if (Math.Abs(init.Zfw.Value - sheetZfw) > WeightToleranceTonnes)
                {
                    item.Difference(
                        $"ZFW {Tenths(init.Zfw.Value)} — the {reference.LoadsheetName} loadsheet has {Tenths(sheetZfw)}",
                        $"Zero fuel weight is {Decimal(init.Zfw.Value)}, the {reference.LoadsheetName} loadsheet has {Decimal(sheetZfw)}.",
                        fix);
                }

                if (sheet.MacZfw > 0 && Math.Abs(init.ZfwCg.Value - sheet.MacZfw) > CgTolerancePercent)
                {
                    item.Difference(
                        $"ZFWCG {Tenths(init.ZfwCg.Value)} — the {reference.LoadsheetName} loadsheet has {Tenths(sheet.MacZfw)}",
                        $"Zero fuel C G is {Decimal(init.ZfwCg.Value)}, the {reference.LoadsheetName} loadsheet has {Decimal(sheet.MacZfw)}.",
                        fix);
                }
            }
        }

        if (init.Block is null)
        {
            item.NotEntered("BLOCK not entered", "the block fuel", fix);
        }

        return item.Build();
    }

    public static BoxCheckItemResult Perf(PerfTakeoffFacts? perf, BoxCheckReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var item = new Builder(BoxCheckItemIds.Perf, "P", perf is not null);
        if (perf is null)
        {
            return item.Build();
        }

        var computed = reference.Perf;
        var fix = computed is null
            ? null
            : new BoxCheckFix(
                BoxCheckFixKind.PerfSpeeds, "",
                $"Shall I enter the computed figures: V one {Digits(computed.V1)}, V R {Digits(computed.Vr)}, V two {Digits(computed.V2)}"
                + $"{(computed.FlexTempC is { } flex ? $", flex {Digits(flex)}" : ", TOGA")}?");
        item.Read(
            $"RWY {perf.Runway ?? "—"}, V1 {Text(perf.V1)} VR {Text(perf.Vr)} V2 {Text(perf.V2)}, FLAPS {Text(perf.Flaps)}, FLEX {Text(perf.FlexTempC)}, "
            + $"TRANS ALT {Text(perf.TransitionAltitudeFt)}, THR RED/ACC {Text(perf.ThrustReductionFt)}/{Text(perf.AccelerationFt)}");

        if (perf.V1 is null || perf.Vr is null || perf.V2 is null)
        {
            item.NotEntered("V speeds not entered", "the V speeds", fix);
        }
        else if (computed is not null)
        {
            Compare("V1", "V one", perf.V1.Value, computed.V1);
            Compare("VR", "V R", perf.Vr.Value, computed.Vr);
            Compare("V2", "V two", perf.V2.Value, computed.V2);
        }

        if (perf.Flaps is null)
        {
            item.NotEntered("FLAPS not entered", "the flaps", fix);
        }
        else if (computed is not null && computed.FlapsConf != perf.Flaps)
        {
            item.Difference(
                $"FLAPS {perf.Flaps} — the computed configuration is {computed.FlapsConf}",
                $"Flaps {Digits(perf.Flaps.Value)} is entered, the computed configuration is {Digits(computed.FlapsConf)}.",
                fix);
        }

        if (!perf.ThsEntered)
        {
            item.NotEntered("THS not entered", "the trim");
        }

        if (perf.FlexTempC is null)
        {
            // TOGA is a valid entry state: an empty flex is a finding only when one was computed.
            if (computed?.FlexTempC is not null)
            {
                item.NotEntered("FLEX TO TEMP not entered", "the flex temperature", fix);
            }
        }
        else if (computed?.FlexTempC is { } computedFlex && computedFlex != perf.FlexTempC)
        {
            Compare("FLEX", "flex", perf.FlexTempC.Value, computedFlex);
        }

        if (perf.Runway is not null && computed is { Runway.Length: > 0 } && McduSpeech.NormalizeRunway(computed.Runway) is { } runway
            && !Same(McduSpeech.NormalizeRunway(perf.Runway), runway))
        {
            item.Difference(
                $"TAKE OFF RWY {perf.Runway} — the takeoff figures are for {runway}",
                $"The takeoff page is for {McduSpeech.SpeakRunway(perf.Runway)}, the computed figures are for {McduSpeech.SpeakRunway(runway)}.");
        }

        return item.Build();

        void Compare(string label, string spokenLabel, int entered, int wanted)
        {
            if (entered != wanted)
            {
                item.Difference(
                    $"{label} {entered} — the computed figure is {wanted}",
                    $"{spokenLabel} is {Digits(entered)}, the computed figure is {Digits(wanted)}.",
                    fix);
            }
        }
    }

    public static BoxCheckItemResult Prog(ProgFacts? prog)
    {
        var item = new Builder(BoxCheckItemIds.Prog, "P", prog is not null);
        if (prog is null)
        {
            return item.Build();
        }

        item.Read($"{(prog.GpsPrimary ? "GPS PRIMARY" : "no GPS PRIMARY")}, accuracy {prog.Accuracy ?? "—"}");
        if (!prog.GpsPrimary)
        {
            item.Difference("GPS PRIMARY is not shown", "G P S primary is not available.");
        }

        if (string.Equals(prog.Accuracy, "LOW", StringComparison.Ordinal))
        {
            item.Difference("Navigation accuracy LOW", "Navigation accuracy is low.");
        }

        return item.Build();
    }

    /// <summary>
    /// The FO's report: the differences as sentences, then what is not entered yet as one list.
    /// With nothing to say: "Box checked, no differences."
    /// </summary>
    public static string Report(IReadOnlyList<BoxCheckFinding> findings, IReadOnlyList<string> notChecked)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(notChecked);
        var differences = findings.Where(f => f.State == BoxCheckItemState.Difference).Select(f => f.Spoken).Distinct(StringComparer.Ordinal).ToList();
        var missing = findings.Where(f => f.State == BoxCheckItemState.NotEntered).Select(f => f.Spoken).Distinct(StringComparer.Ordinal).ToList();
        var parts = new List<string>();
        if (differences.Count == 0 && missing.Count == 0)
        {
            parts.Add(notChecked.Count == 0 ? "Box checked, no differences." : "Box checked, no differences on the pages I could read.");
        }
        else
        {
            parts.Add(differences.Count switch
            {
                0 => "Box checked, no differences.",
                1 => "Box checked, one difference.",
                _ => $"Box checked, {Digits(differences.Count)} differences.",
            });
            parts.AddRange(differences);
            if (missing.Count > 0)
            {
                parts.Add($"Not entered yet: {JoinList(missing)}.");
            }
        }

        if (notChecked.Count > 0)
        {
            parts.Add($"I could not read {JoinList(notChecked)}.");
        }

        return string.Join(" ", parts);
    }

    /// <summary>The spoken name of an item for the "could not read" line.</summary>
    public static string SpokenItem(string itemId) => itemId switch
    {
        BoxCheckItemIds.Data => "the data pages",
        BoxCheckItemIds.InitA => "the init page",
        BoxCheckItemIds.FlightPlan => "the flight plan",
        BoxCheckItemIds.Secondary => "the secondary plan",
        BoxCheckItemIds.RadNav => "the rad nav page",
        BoxCheckItemIds.InitB => "the fuel prediction page",
        BoxCheckItemIds.Perf => "the takeoff page",
        BoxCheckItemIds.Prog => "the progress page",
        _ => itemId,
    };

    /// <summary>The flight numbers the plan would accept on INIT A, the preferred entry first:
    /// the ATC callsign, then airline + number.</summary>
    public static IReadOnlyList<string> PlannedFlightNumbers(OfpData? ofp)
    {
        if (ofp is null)
        {
            return [];
        }

        var numbers = new List<string>();
        foreach (var candidate in new[] { ofp.Callsign, ofp.AirlineIcao + ofp.FlightNumber, ofp.Ident })
        {
            var cleaned = new string([.. (candidate ?? "").ToUpperInvariant().Where(char.IsAsciiLetterOrDigit)]);
            if (cleaned.Length is >= 3 and <= 8 && cleaned.Any(char.IsAsciiDigit) && !numbers.Contains(cleaned, StringComparer.Ordinal))
            {
                numbers.Add(cleaned);
            }
        }

        return numbers;
    }

    /// <summary>A loadsheet weight in the unit the box shows: tonnes, or thousands of pounds
    /// when the entered figure is plainly in pounds (ProSim's Units → Weight option).</summary>
    private static double InBoxUnit(double kg, double boxValue)
    {
        var tonnes = kg / 1000.0;
        var kiloPounds = tonnes * PoundsPerKg;
        return Math.Abs(boxValue - kiloPounds) < Math.Abs(boxValue - tonnes) ? Math.Round(kiloPounds, 1) : Math.Round(tonnes, 1);
    }

    private static string JoinList(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    /// <summary>A five-letter fix is said as the word it is; anything else is spelled.</summary>
    private static string SpeakFix(string fix)
        => fix.Length == 5 && fix.All(char.IsAsciiLetter) ? fix : McduSpeech.SpeakLetters(fix);

    private static string SpellIdent(string ident) => McduSpeech.SpeakLetters(ident);

    private static bool Has(string? value) => !string.IsNullOrWhiteSpace(value);

    private static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Digits(int value) => Aviation.ToDigits(value.ToString(CultureInfo.InvariantCulture));

    private static string Decimal(double value) => Aviation.ToDigits(value.ToString("0.0", CultureInfo.InvariantCulture));

    private static string Text(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "—";

    private static string Tenths(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    private static string Word(int n) => n switch { 1 => "one", 2 => "two", 3 => "three", _ => n.ToString(CultureInfo.InvariantCulture) };

    /// <summary>Collects one item's read lines and findings; the verdict is the worst finding.</summary>
    private sealed class Builder(string id, string letter, bool read)
    {
        private readonly List<string> _lines = [];
        private readonly List<BoxCheckFinding> _findings = [];

        public void Read(string line) => _lines.Add(line);

        public void Difference(string text, string spoken, BoxCheckFix? fix = null)
            => _findings.Add(new BoxCheckFinding(id, BoxCheckItemState.Difference, text, spoken, fix));

        public void NotEntered(string text, string spoken, BoxCheckFix? fix = null)
            => _findings.Add(new BoxCheckFinding(id, BoxCheckItemState.NotEntered, text, spoken, fix));

        public BoxCheckItemResult Build()
        {
            var state = !read ? BoxCheckItemState.NotChecked
                : _findings.Any(f => f.State == BoxCheckItemState.Difference) ? BoxCheckItemState.Difference
                : _findings.Count > 0 ? BoxCheckItemState.NotEntered
                : BoxCheckItemState.Ok;
            return new BoxCheckItemResult(new BoxCheckItem(id, letter, state, [.. _lines, .. _findings.Select(f => f.Text)]), _findings);
        }
    }
}
