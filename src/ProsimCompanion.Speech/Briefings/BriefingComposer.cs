using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Speech.Briefings;

/// <summary>Everything a briefing may speak — null fields are simply omitted. ATIS letter and
/// active runway (SayIntentions-sourced, via the composite weather provider) feed the LLM
/// fact block only; the deterministic template deliberately ignores them so its clause
/// structure stays byte-stable. AirportName is the spoken name ("Sydney") resolved by
/// <see cref="Core.Airports.IAirportNames"/> — null falls back to the ICAO ident. TechLogItems
/// are the open tech-log defects this briefing consults (2026-10-09; already filtered for the
/// briefing kind by <see cref="Core.TechLog.TechLogConsultation"/>) — null or empty = no clause.</summary>
public sealed record BriefingFacts(
    bool IsDeparture,
    string? Airport,
    string? Runway,
    string? Sid,
    string? Star,
    string? Approach,
    NavDataFacts Nav,
    int? V1,
    int? Vr,
    int? V2,
    int? WindDirDeg,
    int? WindSpeedKt,
    int? QnhHpa,
    ArrivalMinima? Minima,
    string? AtisLetter = null,
    string? ActiveRunway = null,
    int? FlexTempC = null,
    int? VisibilityM = null,
    int? TemperatureC = null,
    string? AirportName = null,
    IReadOnlyList<Core.TechLog.TechLogDefect>? TechLogItems = null,
    EmergencyBriefFacts? Emergency = null,
    GoAroundBriefFacts? GoAround = null,
    Core.Weather.WindLimitVerdict? WindLimits = null,
    string? TimeOfDay = null);

/// <summary>The facts of the emergency half of a takeoff briefing (issue #180). Present only
/// while <c>briefing.takeoffEmergencyBrief</c> is on — null keeps the departure template
/// exactly as it was. Each field is omitted from the clauses when unknown.</summary>
/// <param name="Surface">The runway surface the METAR precipitation implies.</param>
/// <param name="Thunderstorms">Thunderstorms reported at the airport.</param>
/// <param name="MsaFt">The airport's highest published MSA sector altitude (DFD).</param>
/// <param name="AccelerationAltitudeFt">The acceleration altitude as spoken: MSL when the
/// elevation is known, else above the aerodrome (see <paramref name="AltitudesAboveAirport"/>).</param>
/// <param name="ThrustReductionAltitudeFt">The thrust reduction altitude, same rule.</param>
/// <param name="AltitudesAboveAirport">True when the two altitudes are AAL figures because no
/// elevation was known; the clause then says "above the airport".</param>
/// <param name="EngineOutProcedure">The engine-out routing as spoken: the DFD EOSID legs when
/// one is published for the runway, else the straight-ahead default.</param>
/// <param name="EngineOutIsPublished">True when the routing came from a published EOSID.</param>
/// <param name="ReturnRunway">The runway a return would land on (the departure runway when no
/// landing-runway fact exists).</param>
/// <param name="ThrustMode">"FLEX 52" / "TOGA" from the FMS or the takeoff performance result; null when neither has it.</param>
/// <param name="CeilingFt">METAR ceiling, when reported.</param>
/// <param name="IcingLikely">The METAR ground-icing predicate (<see cref="Core.Weather.MetarConditions.GroundIcingLikely"/>).</param>
/// <param name="AirportElevationFt">The elevation the MSA is compared with for the terrain threat.</param>
public sealed record EmergencyBriefFacts(
    Core.Weather.RunwaySurfaceState Surface,
    bool Thunderstorms,
    double? MsaFt,
    int AccelerationAltitudeFt,
    int ThrustReductionAltitudeFt,
    bool AltitudesAboveAirport,
    string EngineOutProcedure,
    bool EngineOutIsPublished,
    string? ReturnRunway,
    string? ThrustMode,
    int? CeilingFt,
    bool IcingLikely,
    double? AirportElevationFt,
    IReadOnlyList<string>? NotamThreats = null)
{
    /// <summary>The engine-out routing when the DFD publishes none for the runway.</summary>
    public const string DefaultEngineOutProcedure = "straight ahead, runway heading, to the acceleration altitude";

    /// <summary>The departure NOTAM highlights as threat lines (issue #189, wired at the merge); empty = none.</summary>
    public IReadOnlyList<string> NotamThreats { get; init; } = NotamThreats ?? [];
}

/// <summary>The last departure briefing's emergency facts with their station and clock (issue
/// #187 wire): the EFATO drill speaks the briefed engine-out routing after gear up, only when it
/// was published and briefed for this airport within the last few hours.</summary>
public sealed record DepartureEmergencyBrief(string? Airport, string? Runway, EmergencyBriefFacts Facts, DateTimeOffset AtUtc);

/// <summary>The go-around additions of an arrival briefing (issue #180). Present only while
/// <c>briefing.arrivalGoAroundBrief</c> is on.</summary>
/// <param name="MissedApproachAltitudeFt">The highest altitude in the published missed-approach legs.</param>
/// <param name="MsaFt">The airport's highest published MSA sector altitude (DFD).</param>
/// <param name="MissedApproachLegs">The published legs' spoken phrases, in order; empty = not published.</param>
public sealed record GoAroundBriefFacts(
    int? MissedApproachAltitudeFt,
    double? MsaFt,
    IReadOnlyList<string> MissedApproachLegs);

/// <summary>
/// The deterministic briefing template (Prosim2FO's exact clause structure) plus the number
/// verifier that keeps optional LLM prose honest: every significant number in a narrative
/// (≥3 digits or decimal) must appear in the source facts within 0.06 — 1–2 digit tokens are
/// deliberately ignored (runways/flaps/ordinals false-positive).
/// </summary>
public static class BriefingComposer
{
    /// <summary>The whole template as one text: the sections joined by a space.</summary>
    public static string Template(BriefingFacts f)
        => string.Join(" ", Sections(f).Select(section => section.Text));

    /// <summary>The template clause by clause (issue #147), each with the figures and words
    /// that identify it in other wording — see <see cref="Core.Speech.NarrationSection"/>. A
    /// streamed LLM briefing that is cut short is finished with exactly the clauses the pilot
    /// has not heard yet. The clause texts and their order are the template, unchanged.</summary>
    /// <summary>"Good afternoon, Captain. Departure briefing." when the sim's local time of day
    /// is known, the bare header otherwise — never a guessed greeting (owner report 2026-10-25).</summary>
    internal static string Opening(BriefingFacts f, string header)
        => f.TimeOfDay is { Length: > 0 } word ? $"Good {word}, Captain. {header}" : header;

    public static IReadOnlyList<Core.Speech.NarrationSection> Sections(BriefingFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);

        var s = new List<Core.Speech.NarrationSection>();
        if (f.IsDeparture)
        {
            s.Add(new("header", Opening(f, "Departure briefing.")) { IsOpening = true });
            if (f.Airport is { } airport)
            {
                s.Add(new("airport", $"Departing {f.AirportName ?? airport}.") { AnyOf = Names(f.AirportName, airport) });
            }

            if (f.Runway is { } runway)
            {
                s.Add(new("runway", $"Runway {runway}.") { Runway = runway });
            }

            // Identifiers render phonetically (issue #68): kokoro swallowed the designator
            // letter of "VOLA3V" — "VOLA three Victor" survives synthesis.
            if (f.Sid is { } sid)
            {
                var spoken = Core.Speech.NatoPhonetics.SpeakIdentifier(sid);
                s.Add(new("sid", $"Standard instrument departure {spoken}.") { AnyOf = LeadWord(spoken) });
            }

            if (f.Nav.RunwayTrueHeading is { } heading)
            {
                s.Add(new("track", $"Initial track {heading:0} degrees.") { Numbers = [Math.Round(heading)], AnyOf = ["track", "heading"] });
            }

            if (f.Nav.TransitionAltitudeFt is { } transitionAltitude)
            {
                s.Add(new("transition-altitude", $"Transition altitude {transitionAltitude:0} feet.")
                {
                    Numbers = [Math.Round(transitionAltitude)],
                    AnyOf = ["transition"],
                });
            }

            if (f is { V1: { } v1, Vr: { } vr, V2: { } v2 })
            {
                s.Add(new("v-speeds", $"V1 {v1}, rotate {vr}, V2 {v2}.") { Numbers = [v1, vr, v2] });
            }
        }
        else
        {
            s.Add(new("header", Opening(f, "Arrival briefing.")) { IsOpening = true });
            if (f.Airport is { } airport)
            {
                s.Add(new("airport", $"Arriving {f.AirportName ?? airport}.") { AnyOf = Names(f.AirportName, airport) });
            }

            if (f.Runway is { } runway)
            {
                s.Add(new("runway", $"Runway {runway}.") { Runway = runway });
            }

            if (f.Approach is { } approach)
            {
                var spoken = SpokenApproach(approach, f.Runway);
                s.Add(new("approach", $"Approach {spoken}.") { AnyOf = [spoken] });
            }

            if (f.Nav is { IlsIdent: { } ilsIdent, IlsFrequencyMhz: { } ilsFrequency })
            {
                s.Add(new("ils", $"ILS {ilsIdent}, frequency {ilsFrequency:0.00}.") { Numbers = [ilsFrequency] });
            }

            if (f.Nav.GlideSlopeAngle is { } glideSlope)
            {
                s.Add(new("glideslope", $"Glideslope {glideSlope:0.0} degrees.") { Numbers = [glideSlope], AnyOf = ["glide"] });
            }

            if (f.Star is { } star)
            {
                var spoken = Core.Speech.NatoPhonetics.SpeakIdentifier(star);
                s.Add(new("star", $"Arrival via {spoken}.") { AnyOf = LeadWord(spoken) });
            }

            if (f.Nav.TransitionLevel is { } transitionLevel)
            {
                s.Add(new("transition-level", $"Transition level {transitionLevel:0}.")
                {
                    Numbers = [Math.Round(transitionLevel)],
                    AnyOf = ["transition"],
                });
            }
        }

        if (f is { WindDirDeg: { } windDir, WindSpeedKt: { } windSpeed })
        {
            s.Add(new("wind", $"Wind {windDir:000} at {windSpeed} knots.") { Numbers = [windDir, windSpeed], AnyOf = ["wind"] });

            // Wind against the aircraft limits (issue #182): present only with the wind-limits
            // feature on and a runway heading; the pre-#182 clause above is untouched.
            if (f.WindLimits is { } limits)
            {
                s.Add(new("wind-limits", Core.Weather.WindLimitCheck.Spoken(limits))
                {
                    Numbers = [Math.Round(limits.Wind.CrosswindKt)],
                    AnyOf = ["crosswind", "tailwind", "headwind", "variable wind"],
                });
            }
        }

        if (f.QnhHpa is { } qnh)
        {
            s.Add(new("qnh", $"QNH {qnh:0}.") { Numbers = [qnh], AnyOf = ["qnh", "altimeter"] });
        }

        if (!f.IsDeparture)
        {
            s.Add(f.Minima is { } m
                ? new("minimums", $"Minimums, {MinimaCallout(m)}.")
                {
                    Numbers = [Math.Round(m.AltitudeFt)],
                    AnyOf = ["minimum", "decision", "descent altitude"],
                }
                : new("minimums", "Minimums not briefed.") { AnyOf = ["minimum"] });

            // Go-around additions (issue #180): after the minimums, before the tech log, so the
            // pre-#180 arrival clauses keep their order and the minimums stay last when off.
            if (f.GoAround is { } g)
            {
                s.AddRange(GoAroundSections(g));
            }
        }

        // Tech-log consultation (2026-10-09): after every existing clause so the byte-stable
        // structure above is untouched when the log is clean. Recognised in the model's words
        // by the first named title (or the phrase itself), so a streamed briefing that already
        // mentioned the deferred item is not told it twice.
        if (f.TechLogItems is { Count: > 0 } items
            && Core.TechLog.TechLogConsultation.BriefingClause(items, f.IsDeparture) is { } clause)
        {
            s.Add(new("tech-log", clause)
            {
                AnyOf = ["tech log", "techlog", "deferred", "MEL", items[0].Title],
            });
        }

        // The emergency half of the takeoff briefing (issue #180) is the tail: the standard
        // clauses, the tech log, then the briefing the crew actually trains — RTO split, EFATO,
        // altitudes, MSA, return — and the threats of the day as the closing summary.
        if (f.IsDeparture && f.Emergency is { } e)
        {
            s.AddRange(EmergencySections(f, e));
        }

        return s;
    }

    /// <summary>The departure's emergency section, clause by clause: <c>rto-split</c>,
    /// <c>efato</c>, <c>accel-alt</c>, <c>msa</c>, <c>return</c>, <c>threats</c>. The RTO and
    /// EFATO wording is fixed airline phraseology (the one hundred knot split, the four hundred
    /// foot ECAM gate) — those two figures are part of the allowed set.</summary>
    public static IReadOnlyList<Core.Speech.NarrationSection> EmergencySections(BriefingFacts f, EmergencyBriefFacts e)
    {
        ArgumentNullException.ThrowIfNull(f);
        ArgumentNullException.ThrowIfNull(e);
        var s = new List<Core.Speech.NarrationSection>
        {
            new("rto-split",
                "Below one hundred knots, we stop for any ECAM caution or master warning, engine failure, fire, "
                + "or if the aircraft is unsafe to fly. Above one hundred knots, we stop only for engine failure, "
                + "fire, or if the aircraft will not fly.")
            {
                Numbers = [RtoSplitKt],
                AnyOf = ["stop", "reject"],
            },
            new("efato",
                "Engine failure after V1: continue the takeoff, rotate at V R, climb at V2, positive climb gear up, "
                + $"at four hundred feet ECAM actions. Engine-out routing: {e.EngineOutProcedure}.")
            {
                Numbers = [EcamActionsAglFt],
                AnyOf = ["engine failure", "engine-out", "engine out"],
            },
            new("accel-alt",
                e.AltitudesAboveAirport
                    ? $"Acceleration altitude {e.AccelerationAltitudeFt} feet above the airport, thrust reduction {e.ThrustReductionAltitudeFt} feet above the airport."
                    : $"Acceleration altitude {e.AccelerationAltitudeFt} feet, thrust reduction {e.ThrustReductionAltitudeFt} feet.")
            {
                Numbers = [e.AccelerationAltitudeFt, e.ThrustReductionAltitudeFt],
                AnyOf = ["acceleration"],
            },
        };

        if (e.MsaFt is { } msa)
        {
            s.Add(new("msa", $"Minimum safe altitude {msa:0} feet.") { Numbers = [Math.Round(msa)], AnyOf = ["safe altitude", "MSA"] });
        }

        if (e.ReturnRunway is { } returnRunway)
        {
            var where = f.AirportName ?? f.Airport;
            s.Add(new("return", where is null
                ? $"In case of return, runway {returnRunway}."
                : $"In case of return, {where} runway {returnRunway}.")
            {
                Runway = returnRunway,
                AnyOf = ["return"],
            });
        }

        var threats = ThreatListBuilder.Build(f, e);
        s.Add(new("threats", threats.Count == 0 ? "No specific threats." : $"Threats today: {JoinList(threats)}.")
        {
            Numbers = [.. ThreatListBuilder.Numbers(f, e)],
            AnyOf = ["threat"],
        });
        return s;
    }

    /// <summary>The arrival's go-around additions: <c>missed-approach-altitude</c>, <c>msa</c>
    /// and the <c>go-around</c> split, the latter ending in the published legs in the same
    /// wording the missed-approach re-brief uses.</summary>
    public static IReadOnlyList<Core.Speech.NarrationSection> GoAroundSections(GoAroundBriefFacts g)
    {
        ArgumentNullException.ThrowIfNull(g);
        var s = new List<Core.Speech.NarrationSection>();
        if (g.MissedApproachAltitudeFt is { } maAlt)
        {
            s.Add(new("missed-approach-altitude", $"Missed approach altitude {maAlt} feet.") { Numbers = [maAlt], AnyOf = ["missed approach altitude"] });
        }

        if (g.MsaFt is { } msa)
        {
            s.Add(new("msa", $"Minimum safe altitude {msa:0} feet.") { Numbers = [Math.Round(msa)], AnyOf = ["safe altitude", "MSA"] });
        }

        var legs = g.MissedApproachLegs.Count == 0 ? "missed approach as published" : MissedApproachLegsText(g.MissedApproachLegs);
        s.Add(new("go-around", $"Go around: go around, flaps, positive climb gear up, navigation, {legs}.") { AnyOf = ["go around", "go-around"] });
        return s;
    }

    /// <summary>The published missed-approach legs as one spoken run ("climb to 600 feet;
    /// left turn, direct SOSIJ, climb to 3000 feet") — shared by the arrival briefing's
    /// go-around clause and the <see cref="MissedApproachRebrief"/>, so the two never drift.</summary>
    public static string MissedApproachLegsText(IReadOnlyList<string> legPhrases)
    {
        ArgumentNullException.ThrowIfNull(legPhrases);
        return string.Join("; ", legPhrases);
    }

    /// <summary>The rejected-takeoff decision speed of the briefing's fixed wording.</summary>
    public const int RtoSplitKt = 100;

    /// <summary>The height at which ECAM actions start after an engine failure, feet AGL.</summary>
    public const int EcamActionsAglFt = 400;

    /// <summary>"a", "a and b", "a, b, and c" — a list the pilot absorbs by ear.</summary>
    private static string JoinList(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        2 => $"{items[0]}, and {items[1]}",
        _ => $"{string.Join(", ", items.Take(items.Count - 1))}, and {items[^1]}",
    };

    /// <summary>The names an airport may have been spoken by: its friendly name and its ident.</summary>
    private static string[] Names(string? name, string icao)
        => name is null ? [icao] : [name, icao];

    /// <summary>The leading word of a spoken identifier ("VOLA three Victor" → "VOLA"): the
    /// part a listener recognises the procedure by. Empty when there is none of useful length.</summary>
    private static string[] LeadWord(string spokenIdentifier)
    {
        var lead = spokenIdentifier.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return lead is { Length: >= 3 } ? [lead] : [];
    }

    /// <summary>Spoken form of the approach fact. DFD identifiers ("I16RY") decode through
    /// the same path as the "which approach" answer so both say "ILS Yankee"; anything else
    /// (manual entries like "ILS 16R") renders phonetically. Public so tests can pin both
    /// branches.</summary>
    public static string SpokenApproach(string approach, string? runway)
    {
        ArgumentNullException.ThrowIfNull(approach);
        return runway is not null
            && DfdNavDataProvider.TryDecodeApproachIdentifier(approach, runway, out var option, out _)
                ? option.Spoken
                : Core.Speech.NatoPhonetics.SpeakIdentifier(approach);
    }

    public static string MinimaCallout(ArrivalMinima minima)
    {
        ArgumentNullException.ThrowIfNull(minima);
        var kind = minima.Kind switch
        {
            ArrivalMinimumKind.DecisionAltitude => "decision altitude",
            ArrivalMinimumKind.DecisionHeight => "decision height",
            _ => "minimum descent altitude",
        };
        return $"{kind} {Callouts.Aviation.ToDigits(minima.AltitudeFt.ToString("F0", CultureInfo.InvariantCulture))} feet";
    }

    /// <summary>Checks a narrative's significant numbers against the allowed fact values
    /// (delegating to the shared <see cref="Llm.NumberVerifier"/>). Returns the offending
    /// tokens (empty = verified) and the allowed set for the retry prompt.</summary>
    public static (IReadOnlyList<string> Offending, IReadOnlyList<double> Allowed) VerifyNumbers(
        string narrative, BriefingFacts f)
    {
        ArgumentNullException.ThrowIfNull(narrative);
        ArgumentNullException.ThrowIfNull(f);

        var check = Llm.NumberVerifier.Check(narrative, BuildAllowed(f));
        return (check.Offending, check.Allowed);
    }

    /// <summary>Every number a briefing built from these facts may speak — the set the
    /// streamed path verifies each sentence against (issue #147).</summary>
    public static IReadOnlyList<double> AllowedNumbers(BriefingFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);
        return BuildAllowed(f);
    }

    private static List<double> BuildAllowed(BriefingFacts f)
    {
        var allowed = new List<double>();

        void AddValue(double? value)
        {
            if (value is { } v)
            {
                allowed.Add(v);
                allowed.Add(Math.Round(v));
            }
        }

        void AddDigits(string? text)
        {
            if (text is null)
            {
                return;
            }

            foreach (Match match in Regex.Matches(text, @"\d+(?:\.\d+)?"))
            {
                allowed.Add(double.Parse(match.Value, CultureInfo.InvariantCulture));
            }
        }

        AddDigits(f.Runway);
        AddDigits(f.ActiveRunway);
        AddDigits(f.Sid);
        AddDigits(f.Star);
        AddDigits(f.Approach);
        AddDigits(f.Nav.AiracCycle);
        AddDigits(f.Nav.IlsIdent);
        AddValue(f.Nav.RunwayTrueHeading);
        AddValue(f.Nav.RunwayLengthFt);
        AddValue(f.Nav.RunwayElevationFt);
        AddValue(f.Nav.IlsFrequencyMhz);
        AddValue(f.Nav.GlideSlopeAngle);
        AddValue(f.Nav.TransitionAltitudeFt);
        AddValue(f.Nav.TransitionLevel);
        AddValue(f.WindDirDeg);
        AddValue(f.WindSpeedKt);
        AddValue(f.QnhHpa);
        AddValue(f.V1);
        AddValue(f.Vr);
        AddValue(f.V2);
        AddValue(f.FlexTempC);
        AddValue(f.VisibilityM);
        AddValue(f.TemperatureC);
        AddValue(f.Minima?.AltitudeFt);
        if (f.TechLogItems is { Count: > 0 } items)
        {
            // Titles are locked facts ("Engine 1 anti-ice valve"), so their digits are allowed;
            // the "and N more" count too, in case the model says it in its own words.
            foreach (var item in items)
            {
                AddDigits(item.Title);
            }

            AddValue(items.Count);
            AddValue(Math.Max(0, items.Count - Core.TechLog.TechLogConsultation.MaxBriefedTitles));
        }

        // Issue #180: every figure the emergency section and the go-around additions speak —
        // the fixed 100 kt / 400 ft of the RTO and EFATO wording included, since the model
        // may well repeat them, and the EOSID legs' courses and altitudes.
        if (f.Emergency is { } e)
        {
            AddValue(RtoSplitKt);
            AddValue(EcamActionsAglFt);
            AddValue(e.AccelerationAltitudeFt);
            AddValue(e.ThrustReductionAltitudeFt);
            AddValue(e.MsaFt);
            AddValue(e.CeilingFt);
            AddValue(e.AirportElevationFt);
            AddDigits(e.EngineOutProcedure);
            AddDigits(e.ReturnRunway);
            AddDigits(e.ThrustMode);
            foreach (var number in ThreatListBuilder.Numbers(f, e))
            {
                AddValue(number);
            }
        }

        if (f.WindLimits is { } limits)
        {
            foreach (var number in Core.Weather.WindLimitCheck.Numbers(limits))
            {
                AddValue(number);
            }
        }

        if (f.GoAround is { } g)
        {
            AddValue(g.MissedApproachAltitudeFt);
            AddValue(g.MsaFt);
            foreach (var leg in g.MissedApproachLegs)
            {
                AddDigits(leg);
            }
        }

        return allowed;
    }

    /// <summary>Builds the LLM system prompt (predecessor wording — facts-only, TTS-ready).
    /// <paramref name="timeOfDay"/> is the sim's local time-of-day word (morning / afternoon /
    /// evening) or null: the opening instruction quotes the exact greeting to use, never an
    /// example with another word — the 2026-10-29 EGLL briefings (sim 04:50 and 05:35) both
    /// opened "Good afternoon" because the old prompt's example read 'Good afternoon,
    /// Captain.' and the model copied it. <see cref="FixGreeting"/> is the belt to this brace.</summary>
    public static string SystemPrompt(bool departure, string? timeOfDay)
        => "You are the First Officer of an Airbus A320, giving a concise, professional spoken "
            + (departure ? "departure" : "arrival/approach")
            + " briefing to the Captain. Use ONLY the facts provided below — never invent or alter "
            + "runways, headings, frequencies, altitudes, speeds, levels, or weather. If a fact is "
            + "missing, simply omit it; do not guess or fill gaps. Keep it under about 120 words, "
            + "plain spoken English suitable for text-to-speech (no markdown, no lists, no headings). "
            + "Spell out aviation terms for the synthesizer: write 'flight level one one zero' (never "
            + "'FL' or 'FL110'); 'I L S', 'Q N H', 'R V R', 'V O R', 'D M E'; and units as words "
            + "('feet', 'knots', 'degrees'). Read flight levels, headings, frequencies, squawk codes, "
            + "wind direction and runway numbers digit by digit (e.g. 'heading one six three', 'one "
            + "one eight decimal one zero', 'runway one six right'); read altitudes, distances and "
            + "speeds normally. Speak single letters as their NATO phonetic words: 'information "
            + "Mike' (never 'information M'), 'VOLA three Victor' (never 'VOLA3V')."
            + (timeOfDay is { Length: > 0 } word
                ? $" It is {word} at the aircraft. Your first words must be exactly 'Good {word}, Captain.'"
                  + " — no other time-of-day word."
                : " The time of day at the aircraft is unknown. Open with 'Captain,' and use no "
                  + "time-of-day greeting at all — never guess morning, afternoon or evening.");

    private static readonly Regex GreetingOpening = new(
        @"^\s*Good\s+(morning|afternoon|evening|day)\b[,.!]?\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Deterministic guard on the model's opening (2026-10-29): a sentence that opens
    /// "Good &lt;word&gt;" gets the SIM's word when they differ, and loses the greeting entirely
    /// when the clock is unknown ("Good afternoon, Captain. We're departing…" → "Captain, we're
    /// departing…"). Any other sentence is returned unchanged. Pure; applied to every streamed
    /// sentence and to a whole non-streamed narrative (anchored at the start, so only an
    /// opening greeting is touched).</summary>
    public static string FixGreeting(string sentence, string? timeOfDay)
    {
        ArgumentNullException.ThrowIfNull(sentence);
        var match = GreetingOpening.Match(sentence);
        if (!match.Success)
        {
            return sentence;
        }

        if (timeOfDay is { Length: > 0 } word)
        {
            if (match.Groups[1].Value.Equals(word, StringComparison.OrdinalIgnoreCase))
            {
                return sentence;
            }

            // Keep the model's own punctuation after the word (", Captain." / ". ") — only
            // the time-of-day word changes.
            var afterWord = sentence[(match.Groups[1].Index + match.Groups[1].Length)..];
            return "Good " + word + afterWord;
        }

        var rest = sentence[(match.Index + match.Length)..];
        if (rest.StartsWith("Captain", StringComparison.OrdinalIgnoreCase))
        {
            return rest;
        }

        return rest.Length == 0 ? "Captain." : "Captain, " + char.ToLowerInvariant(rest[0]) + rest[1..];
    }

    /// <summary>Builds the fact block ("- Label: value" lines, blanks omitted).</summary>
    public static string FactBlock(BriefingFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var sb = new StringBuilder();
        sb.AppendLine("BRIEFING: " + (f.IsDeparture ? "Departure" : "Arrival/Approach"));

        void Line(string label, object? value)
        {
            var text = value?.ToString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {label}: {text}");
            }
        }

        // Identifiers/letters are handed to the LLM pre-formatted for speech (issue #68) —
        // asking the model to phoneticize "VOLA3V" itself proved unreliable.
        // The sim's LOCAL time of day as a word only — a clock figure would invite the model
        // to read it out, and the narrator's number lock would then reject the sentence.
        Line("Time of day at the aircraft", f.TimeOfDay);
        Line("Airport", f.AirportName is { } name ? $"{name} ({f.Airport})" : f.Airport);
        Line("Runway", f.Runway);
        Line("SID", Core.Speech.NatoPhonetics.SpeakIdentifier(f.Sid));
        Line("STAR", Core.Speech.NatoPhonetics.SpeakIdentifier(f.Star));
        Line("Approach", f.Approach is { } approach ? SpokenApproach(approach, f.Runway) : null);
        Line("AIRAC", f.Nav.AiracCycle);
        Line("Runway true heading (deg)", f.Nav.RunwayTrueHeading?.ToString("0", CultureInfo.InvariantCulture));
        Line("Runway length (ft)", f.Nav.RunwayLengthFt?.ToString("0", CultureInfo.InvariantCulture));
        Line("ILS identifier", f.Nav.IlsIdent);
        Line("ILS frequency (MHz)", f.Nav.IlsFrequencyMhz?.ToString("0.00", CultureInfo.InvariantCulture));
        Line("Glideslope (deg)", f.Nav.GlideSlopeAngle?.ToString("0.0", CultureInfo.InvariantCulture));
        Line("Transition altitude (ft)", f.Nav.TransitionAltitudeFt?.ToString("0", CultureInfo.InvariantCulture));
        Line("Transition level", f.Nav.TransitionLevel?.ToString("0", CultureInfo.InvariantCulture));
        Line("Runway elevation (ft)", f.Nav.RunwayElevationFt?.ToString("0", CultureInfo.InvariantCulture));
        if (f.IsDeparture)
        {
            Line("V1 (kt)", f.V1);
            Line("VR (kt)", f.Vr);
            Line("V2 (kt)", f.V2);
            Line("Flex temp (C)", f.FlexTempC);
        }

        if (f is { WindDirDeg: not null, WindSpeedKt: not null })
        {
            Line("Wind", $"{f.WindDirDeg:000} at {f.WindSpeedKt} kt");
        }

        if (f.WindLimits is { } limits)
        {
            // Issue #182: the components and the verdict as data; the limit figures are the
            // operator's settings, locked like every other number.
            Line("Wind components (kt)", $"crosswind {Math.Round(limits.Wind.MaxCrosswindKt)}, {(limits.Wind.HeadwindKt >= 0 ? "headwind" : "tailwind")} {Math.Round(Math.Abs(limits.Wind.HeadwindKt))}");
            Line("Wind limits", $"crosswind {limits.CrosswindLimitKt} kt ({limits.CrosswindLimitName}), tailwind {limits.TailwindLimitKt} kt — {(limits.Exceeded ? "EXCEEDED" : limits.Tone == Core.Weather.WindLimitTone.Amber ? "close to a limit" : "within limits")}");
        }

        Line("Visibility (m)", f.VisibilityM);
        Line("Temperature (C)", f.TemperatureC);
        Line("QNH (hPa)", f.QnhHpa);
        Line("ATIS information", Core.Speech.NatoPhonetics.Letter(f.AtisLetter)); // "M" → "Mike"
        Line("Active runway (per ATC)", f.ActiveRunway);
        if (!f.IsDeparture)
        {
            Line("Minimums", f.Minima is { } m ? MinimaCallout(m) : "NOT BRIEFED");
        }

        if (f.TechLogItems is { Count: > 0 } items)
        {
            // Facts, not prose: the model may mention them, never embellish them. Same cap as
            // the template so the spoken list stays short either way.
            var titles = items.Take(Core.TechLog.TechLogConsultation.MaxBriefedTitles)
                .Select(d => $"{d.Title} (MEL category {d.Category})");
            var more = items.Count - Math.Min(items.Count, Core.TechLog.TechLogConsultation.MaxBriefedTitles);
            Line(f.IsDeparture ? "Open tech log items (deferred defects)" : "Open tech log items affecting landing",
                string.Join("; ", titles) + (more > 0 ? $"; and {more} more" : ""));
        }

        // Issue #180: the emergency facts as data. The RTO / EFATO phraseology itself is
        // handed over as fixed text — the model may restyle around it, never alter it.
        if (f.IsDeparture && f.Emergency is { } e)
        {
            Line("Rejected takeoff split", "below 100 knots stop for any ECAM caution or master warning, engine failure, fire or unsafe to fly; above 100 knots stop only for engine failure, fire or the aircraft will not fly");
            Line("Engine failure after V1", "continue, rotate at VR, climb at V2, positive climb gear up, ECAM actions at 400 feet");
            Line(e.EngineOutIsPublished ? "Engine-out routing (published EOSID)" : "Engine-out routing (no EOSID published)", e.EngineOutProcedure);
            Line(e.AltitudesAboveAirport ? "Acceleration altitude (ft above airport)" : "Acceleration altitude (ft)", e.AccelerationAltitudeFt);
            Line(e.AltitudesAboveAirport ? "Thrust reduction altitude (ft above airport)" : "Thrust reduction altitude (ft)", e.ThrustReductionAltitudeFt);
            Line("MSA (ft)", e.MsaFt?.ToString("0", CultureInfo.InvariantCulture));
            Line("Return runway", e.ReturnRunway);
            Line("Thrust", e.ThrustMode);
            Line("Runway surface (from METAR)", e.Surface == Core.Weather.RunwaySurfaceState.Unknown ? null : e.Surface.ToString().ToUpperInvariant());
            Line("Ceiling (ft)", e.CeilingFt);
            var threats = ThreatListBuilder.Build(f, e);
            Line("Threats today", threats.Count == 0 ? "none specific" : string.Join("; ", threats));
        }

        if (!f.IsDeparture && f.GoAround is { } g)
        {
            Line("Missed approach altitude (ft)", g.MissedApproachAltitudeFt);
            Line("MSA (ft)", g.MsaFt?.ToString("0", CultureInfo.InvariantCulture));
            Line("Go-around", "go around, flaps, positive climb gear up, navigation");
            Line("Missed approach legs", g.MissedApproachLegs.Count == 0 ? "as published" : MissedApproachLegsText(g.MissedApproachLegs));
        }

        return sb.ToString();
    }
}
