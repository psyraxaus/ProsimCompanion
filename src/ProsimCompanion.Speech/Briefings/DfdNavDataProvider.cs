using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Speech.Briefings;

/// <summary>Nav-data facts for one briefing (all fields optional — a failed query nulls one
/// field only, never the briefing). The found-flags report whether the resolved SID/STAR/
/// approach identifiers actually exist in the database, so a stale manual entry is caught.</summary>
public sealed record NavDataFacts(
    string? AiracCycle,
    double? TransitionAltitudeFt,
    double? TransitionLevel,
    double? RunwayTrueHeading,
    double? RunwayLengthFt,
    double? RunwayElevationFt,
    string? IlsIdent,
    double? IlsFrequencyMhz,
    double? GlideSlopeAngle,
    bool SidFound = false,
    bool StarFound = false,
    bool ApproachFound = false)
{
    public static NavDataFacts None { get; } = new(null, null, null, null, null, null, null, null, null);
}

/// <summary>
/// A published approach candidate for a runway (from the DFD IAP identifiers). The FMS route
/// doesn't carry the approach, so these are offered for the crew to confirm the intended one.
/// Kind is the human type ("ILS", "RNAV", …); Variant is the trailing designator (Y/Z/…).
/// </summary>
public sealed record ApproachOption(string Identifier, string Kind, char? Variant)
{
    private static readonly Dictionary<char, string> Phonetics = new()
    {
        ['A'] = "Alpha", ['B'] = "Bravo", ['C'] = "Charlie", ['D'] = "Delta", ['E'] = "Echo",
        ['F'] = "Foxtrot", ['G'] = "Golf", ['H'] = "Hotel", ['I'] = "India", ['J'] = "Juliet",
        ['K'] = "Kilo", ['L'] = "Lima", ['M'] = "Mike", ['N'] = "November", ['O'] = "Oscar",
        ['P'] = "Papa", ['Q'] = "Quebec", ['R'] = "Romeo", ['S'] = "Sierra", ['T'] = "Tango",
        ['U'] = "Uniform", ['V'] = "Victor", ['W'] = "Whiskey", ['X'] = "X-ray", ['Y'] = "Yankee",
        ['Z'] = "Zulu",
    };

    /// <summary>Spoken form, e.g. "ILS Yankee" or "RNAV".</summary>
    public string Spoken => Variant is { } v && Phonetics.TryGetValue(char.ToUpperInvariant(v), out var word)
        ? $"{Kind} {word}"
        : Kind;
}

/// <summary>One leg of a published missed-approach procedure. Phrase is the deterministic
/// spoken form (numbers locked); the raw fields feed the event log.</summary>
public sealed record MissedApproachLeg(
    string PathTermination,
    string? WaypointId,
    int? CourseDeg,
    int? AltitudeFt,
    char? TurnDirection,
    string Phrase);

/// <summary>The published missed approach for one airport/approach — the legs after the MAP.</summary>
public sealed record MissedApproachProcedure(string? Airport, string? Approach, IReadOnlyList<MissedApproachLeg> Legs)
{
    public static MissedApproachProcedure None(string? airport, string? approach) => new(airport, approach, []);
}

/// <summary>
/// Navigraph DFD SQLite reader (read-only; user-supplied database, never redistributed).
/// Auto-detects both schema generations (new tbl_p*_ prefixes with header "cycle" vs legacy
/// tbl_* with "current_airac" — against the wrong generation every lookup silently returned
/// null in the predecessor, so both are always probed). Every field is its own defensive
/// scalar query; airport idents are uppercased before binding (SQLite TEXT '=' is
/// case-sensitive); numeric fields tolerate string forms like "FL110" and treat 0 as absent.
/// </summary>
public sealed class DfdNavDataProvider
{
    /// <summary>DFD table/column names for one schema generation.</summary>
    private sealed record DfdSchema(
        string Name, string Header, string AiracColumn,
        string Airports, string Runways, string Ils, string Sids, string Stars, string Iaps);

    private static readonly DfdSchema NewSchema = new(
        "new", "tbl_hdr_header", "cycle",
        "tbl_pa_airports", "tbl_pg_runways", "tbl_pi_localizers_glideslopes",
        "tbl_pd_sids", "tbl_pe_stars", "tbl_pf_iaps");

    private static readonly DfdSchema LegacySchema = new(
        "legacy", "tbl_header", "current_airac",
        "tbl_airports", "tbl_runways", "tbl_localizers_glideslopes",
        "tbl_sids", "tbl_stars", "tbl_iaps");

    private static readonly Regex ApproachIdPattern = new(@"^([A-Z])(\d{2})([LRC]?)(.*)$", RegexOptions.Compiled);

    private readonly IOptionsMonitor<BriefingOptions> _options;
    private readonly ILogger<DfdNavDataProvider> _logger;

    public DfdNavDataProvider(IOptionsMonitor<BriefingOptions> options, ILogger<DfdNavDataProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
    }

    /// <summary>The configured DFD path with whitespace and Explorer "Copy as path" quotes
    /// stripped — what every open actually uses.</summary>
    public string ConfiguredPath => _options.CurrentValue.DfdPath.Trim().Trim('"');

    public bool IsConfigured => ConfiguredPath.Length > 0 && File.Exists(ConfiguredPath);

    /// <summary>AIRAC cycle from the DFD header — the cheap "is this database usable"
    /// diagnostic. Null when absent/unreadable.</summary>
    public string? AiracCycle
    {
        get
        {
            using var connection = Open();
            if (connection is null)
            {
                return null;
            }

            var schema = DetectSchema(connection);
            return ScalarString(connection, $"SELECT {schema.AiracColumn} FROM {schema.Header} LIMIT 1");
        }
    }

    public NavDataFacts Lookup(string airport, string? runway, string? sid = null, string? star = null, string? approach = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(airport);
        using var connection = Open();
        if (connection is null)
        {
            return NavDataFacts.None;
        }

        try
        {
            var schema = DetectSchema(connection);
            var apt = airport.Trim().ToUpperInvariant();
            var rw = NormalizeRunway(runway);

            var airac = ScalarString(connection, $"SELECT {schema.AiracColumn} FROM {schema.Header} LIMIT 1");
            var transAlt = ScalarDouble(connection, $"SELECT transition_altitude FROM {schema.Airports} WHERE airport_identifier=@a LIMIT 1", ("@a", apt));
            var transLvl = ScalarDouble(connection, $"SELECT transition_level FROM {schema.Airports} WHERE airport_identifier=@a LIMIT 1", ("@a", apt));
            // Airport elevation is the fallback when the runway threshold elevation is absent.
            var aptElev = ScalarDouble(connection, $"SELECT elevation FROM {schema.Airports} WHERE airport_identifier=@a LIMIT 1", ("@a", apt));

            double? rwyHdg = null, rwyLen = null, thrElev = null, ilsFreq = null, gs = null;
            string? ilsId = null;
            if (rw is not null)
            {
                rwyHdg = ScalarDouble(connection, $"SELECT runway_true_bearing FROM {schema.Runways} WHERE airport_identifier=@a AND runway_identifier=@r LIMIT 1", ("@a", apt), ("@r", rw));
                rwyLen = ScalarDouble(connection, $"SELECT runway_length FROM {schema.Runways} WHERE airport_identifier=@a AND runway_identifier=@r LIMIT 1", ("@a", apt), ("@r", rw));
                thrElev = ScalarDouble(connection, $"SELECT landing_threshold_elevation FROM {schema.Runways} WHERE airport_identifier=@a AND runway_identifier=@r LIMIT 1", ("@a", apt), ("@r", rw));
                ilsId = ScalarString(connection, $"SELECT llz_identifier FROM {schema.Ils} WHERE airport_identifier=@a AND runway_identifier=@r LIMIT 1", ("@a", apt), ("@r", rw));
                ilsFreq = ScalarDouble(connection, $"SELECT llz_frequency FROM {schema.Ils} WHERE airport_identifier=@a AND runway_identifier=@r LIMIT 1", ("@a", apt), ("@r", rw));
                gs = ScalarDouble(connection, $"SELECT gs_angle FROM {schema.Ils} WHERE airport_identifier=@a AND runway_identifier=@r LIMIT 1", ("@a", apt), ("@r", rw));
            }

            var facts = new NavDataFacts(
                airac, transAlt, transLvl, rwyHdg, rwyLen, thrElev ?? aptElev, ilsId, ilsFreq, gs,
                SidFound: ProcExists(connection, schema.Sids, apt, sid),
                StarFound: ProcExists(connection, schema.Stars, apt, star),
                ApproachFound: ProcExists(connection, schema.Iaps, apt, approach));
            _logger.LogInformation(
                "DFD lookup {Airport}/{Runway} [{Schema}]: airac={Airac} ils={Ils} gs={Gs} sid={Sid} star={Star} appr={Appr}",
                apt, rw ?? "-", schema.Name, airac ?? "-", ilsId ?? "-", gs, facts.SidFound, facts.StarFound, facts.ApproachFound);
            return facts;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DFD lookup failed for {Airport}", airport);
            return NavDataFacts.None;
        }
    }

    /// <summary>Published approaches serving a runway, ILS first (then GLS, LOC, LDA, RNAV,
    /// GPS, VOR, NDB, rest). The FMS route doesn't carry the approach, so these are the
    /// candidates offered for confirmation. Empty when the DFD is absent or none serve it.</summary>
    public IReadOnlyList<ApproachOption> ApproachesForRunway(string? airport, string? runway)
    {
        if (string.IsNullOrWhiteSpace(airport) || string.IsNullOrWhiteSpace(runway))
        {
            return [];
        }

        using var connection = Open();
        if (connection is null)
        {
            return [];
        }

        try
        {
            var schema = DetectSchema(connection);
            var apt = airport.Trim().ToUpperInvariant();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT DISTINCT procedure_identifier FROM {schema.Iaps} WHERE airport_identifier=@a";
            cmd.Parameters.AddWithValue("@a", apt);

            var ranked = new List<(ApproachOption Option, int Rank)>();
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (!reader.IsDBNull(0)
                        && TryDecodeApproachIdentifier(reader.GetString(0), runway, out var option, out var rank))
                    {
                        ranked.Add((option, rank));
                    }
                }
            }

            var result = ranked
                .OrderBy(x => x.Rank).ThenBy(x => x.Option.Identifier, StringComparer.Ordinal)
                .Select(x => x.Option)
                .ToList();
            _logger.LogInformation("Approaches for {Airport}/{Runway}: {List}", apt, runway,
                result.Count == 0 ? "-" : string.Join(", ", result.Select(o => o.Identifier)));
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ApproachesForRunway failed for {Airport}/{Runway}", airport, runway);
            return [];
        }
    }

    /// <summary>Decodes one DFD IAP identifier (&lt;type&gt;&lt;2-digit runway&gt;&lt;side?&gt;&lt;variant?&gt;,
    /// e.g. I04LY / R04LA / D22LB) against a runway; exact side match, both empty for a
    /// no-side runway. Public+static so the ranking is testable without a database.</summary>
    public static bool TryDecodeApproachIdentifier(
        string identifier, string runway, out ApproachOption option, out int rank)
    {
        option = null!;
        rank = int.MaxValue;
        var (num, side) = RunwayKey(runway);
        if (num is null)
        {
            return false;
        }

        var id = identifier.Trim().ToUpperInvariant();
        var match = ApproachIdPattern.Match(id);
        if (!match.Success || match.Groups[2].Value != num || match.Groups[3].Value != side)
        {
            return false;
        }

        var type = match.Groups[1].Value[0];
        char? variant = match.Groups[4].Value.Length > 0 ? match.Groups[4].Value[0] : null;
        option = new ApproachOption(id, ApproachKind(type), variant);
        rank = ApproachRank(type);
        return true;
    }

    /// <summary>Published missed-approach legs for an approach — the legs after the MAP.
    /// Empty when the DFD/approach is absent or no MAP marker exists (caller degrades to a
    /// prompt).</summary>
    public MissedApproachProcedure MissedApproach(string? airport, string? approach)
    {
        var none = MissedApproachProcedure.None(airport, approach);
        if (string.IsNullOrWhiteSpace(airport) || string.IsNullOrWhiteSpace(approach))
        {
            return none;
        }

        using var connection = Open();
        if (connection is null)
        {
            return none;
        }

        try
        {
            var schema = DetectSchema(connection);
            if (!TableExists(connection, schema.Iaps))
            {
                return none;
            }

            // "course" vs "magnetic_course" is the one IAP column that differs between DFD
            // schema generations.
            var courseColumn = schema == NewSchema ? "course" : "magnetic_course";
            var apt = airport.Trim().ToUpperInvariant();
            var proc = approach.Trim().ToUpperInvariant();

            using var cmd = connection.CreateCommand();
            // Final-approach legs only (transition_identifier IS NULL OR ''), sequence order.
            cmd.CommandText =
                $"SELECT seqno, waypoint_identifier, waypoint_description_code, path_termination, "
                + $"turn_direction, {courseColumn} AS course, altitude1 FROM {schema.Iaps} "
                + "WHERE airport_identifier=@a AND procedure_identifier=@p "
                + "AND (transition_identifier IS NULL OR transition_identifier='') ORDER BY seqno";
            cmd.Parameters.AddWithValue("@a", apt);
            cmd.Parameters.AddWithValue("@p", proc);

            var rows = new List<(string? Wpt, string? Desc, string Pt, char? Turn, int? Course, int? Alt)>();
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    rows.Add((
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(3) ? "" : reader.GetString(3),
                        reader.IsDBNull(4) || reader.GetString(4).Length == 0 ? null : reader.GetString(4)[0],
                        reader.IsDBNull(5) ? null : (int?)Math.Round(Convert.ToDouble(reader.GetValue(5), CultureInfo.InvariantCulture)),
                        reader.IsDBNull(6) ? null : (int?)Math.Round(Convert.ToDouble(reader.GetValue(6), CultureInfo.InvariantCulture))));
                }
            }

            // The missed-approach point is the final-approach leg whose description code has
            // 'M' at index 3 (e.g. "G  M" on the runway leg — ARINC 424); the missed approach
            // is every leg after it.
            var mapIndex = rows.FindIndex(x => x.Desc is { Length: >= 4 } d && d[3] == 'M');
            if (mapIndex < 0 || mapIndex >= rows.Count - 1)
            {
                _logger.LogInformation("Missed approach {Airport}/{Proc}: no MAP marker / no legs after it", apt, proc);
                return none;
            }

            var legs = new List<MissedApproachLeg>();
            for (var i = mapIndex + 1; i < rows.Count; i++)
            {
                var x = rows[i];
                legs.Add(new MissedApproachLeg(
                    x.Pt, x.Wpt, x.Course, x.Alt, x.Turn, DescribeLeg(x.Pt, x.Wpt, x.Course, x.Alt, x.Turn)));
            }

            _logger.LogInformation("Missed approach {Airport}/{Proc}: {Count} legs", apt, proc, legs.Count);
            return new MissedApproachProcedure(apt, proc, legs);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Missed-approach read failed for {Airport}/{Approach}", airport, approach);
            return none;
        }
    }

    /// <summary>Deterministic spoken form of one missed-approach leg (numbers locked: course
    /// as aviation digits, altitude as a plain figure). Covers the common ARINC 424 leg types.</summary>
    public static string DescribeLeg(string pathTermination, string? waypoint, int? course, int? altitude, char? turn)
    {
        var turnWord = turn switch { 'L' => "left turn", 'R' => "right turn", _ => null };
        var crs = course is { } c ? Callouts.Aviation.ToDigits(c.ToString("000", CultureInfo.InvariantCulture)) : null;
        var toAlt = altitude is { } a ? $"climb to {a} feet" : null;
        var lead = turnWord is null ? "" : turnWord + ", ";

        switch (pathTermination.ToUpperInvariant())
        {
            case "CA" or "FA" or "VA": // climb to an altitude
                return crs is not null && toAlt is not null ? $"{lead}heading {crs}, {toAlt}" : $"{lead}{toAlt ?? "climb"}";
            case "VR" or "VM": // heading
                return $"{lead}heading {crs ?? "as published"}";
            case "VI" or "CI": // heading/course to an intercept
                return $"{lead}heading {crs ?? "as published"} to intercept";
            case "CF": // course to a fix
                return Join($"{lead}course {crs ?? "as published"}", waypoint is not null ? $"to {waypoint}" : null, toAlt);
            case "DF": // direct to a fix
                return Join($"{lead}direct {waypoint ?? "the fix"}", null, toAlt);
            case "TF": // track to a fix
                return Join($"{lead}to {waypoint ?? "the next fix"}", null, toAlt);
            case "FM": // from a fix, heading
                return $"{lead}from {waypoint ?? "the fix"}, heading {crs ?? "as published"}";
            case "HM" or "HF" or "HA": // hold
                return $"hold at {waypoint ?? "the fix"}";
            default:
                if (waypoint is not null)
                {
                    return Join($"{lead}to {waypoint}", null, toAlt);
                }

                return crs is not null ? $"{lead}heading {crs}" : toAlt ?? "as published";
        }
    }

    private static string Join(string a, string? b, string? c)
        => string.Join(", ", new[] { a, b, c }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>"16R"/"6R"/"RW16R" → "RW16R"; more than two leading digits truncates to two.</summary>
    public static string? NormalizeRunway(string? runway)
    {
        var (num, side) = RunwayKey(runway);
        return num is null ? null : "RW" + num + side;
    }

    /// <summary>Runway string → ("04", "L") / ("27", ""). Null number when unparseable.</summary>
    private static (string? Num, string Side) RunwayKey(string? runway)
    {
        if (string.IsNullOrWhiteSpace(runway))
        {
            return (null, "");
        }

        var s = runway.Trim().ToUpperInvariant();
        if (s.StartsWith("RW", StringComparison.Ordinal))
        {
            s = s[2..];
        }

        var digits = new string(s.TakeWhile(char.IsAsciiDigit).ToArray());
        if (digits.Length == 0)
        {
            return (null, "");
        }

        digits = digits.Length switch
        {
            1 => "0" + digits,
            > 2 => digits[..2],
            _ => digits,
        };
        var side = s.SkipWhile(char.IsAsciiDigit).FirstOrDefault();
        return (digits, side is 'L' or 'R' or 'C' ? side.ToString() : "");
    }

    /// <summary>ARINC approach-type letter (identifier's first char) → human name.</summary>
    private static string ApproachKind(char type) => type switch
    {
        'I' => "ILS",
        'L' => "localizer",
        'B' => "localizer back course",
        'X' => "L D A",
        'J' or 'G' => "G L S",
        'R' => "RNAV",
        'P' => "G P S",
        'D' or 'S' or 'V' => "VOR",
        'N' or 'Q' => "NDB",
        'T' => "TACAN",
        'U' => "S D F",
        'M' or 'W' => "MLS",
        _ => "approach",
    };

    /// <summary>Ranking so the ILS is offered first, then the other precision/nav aids.</summary>
    private static int ApproachRank(char type) => type switch
    {
        'I' => 0,
        'J' or 'G' => 1,
        'L' => 2,
        'X' => 3,
        'R' => 4,
        'P' => 5,
        'D' or 'S' or 'V' => 6,
        'N' or 'Q' => 7,
        _ => 8,
    };

    private SqliteConnection? Open()
    {
        var path = ConfiguredPath;
        if (path.Length == 0 || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Cache=Shared");
            connection.Open();
            return connection;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open DFD navdata at {Path}", path);
            return null;
        }
    }

    private DfdSchema DetectSchema(SqliteConnection connection)
        => TableExists(connection, "tbl_pa_airports") ? NewSchema : LegacySchema;

    private bool TableExists(SqliteConnection connection, string table)
        => Scalar(connection, "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@n LIMIT 1", ("@n", table)) is not null;

    private bool ProcExists(SqliteConnection connection, string table, string airport, string? procedure)
    {
        if (string.IsNullOrWhiteSpace(procedure))
        {
            return false;
        }

        // table is one of the internal schema constants — never user input.
        return Scalar(connection,
            $"SELECT 1 FROM {table} WHERE airport_identifier=@a AND procedure_identifier=@p LIMIT 1",
            ("@a", airport), ("@p", procedure.Trim().ToUpperInvariant())) is not null;
    }

    private object? Scalar(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            var result = cmd.ExecuteScalar();
            return result is null or DBNull ? null : result;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DFD query failed: {Sql}", sql);
            return null;
        }
    }

    private string? ScalarString(SqliteConnection connection, string sql, params (string, object?)[] parameters)
    {
        var value = Scalar(connection, sql, parameters)?.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>DFD numeric fields sometimes arrive as strings ("FL110", "110.30") and use 0
    /// for "not published" — both normalize to null here.</summary>
    private double? ScalarDouble(SqliteConnection connection, string sql, params (string, object?)[] parameters)
    {
        var value = Scalar(connection, sql, parameters);
        switch (value)
        {
            case null:
                return null;
            case double d:
                return d == 0 ? null : d;
            case long l:
                return l == 0 ? null : l;
            case int i:
                return i == 0 ? null : i;
            default:
                var s = value.ToString();
                if (string.IsNullOrWhiteSpace(s))
                {
                    return null;
                }

                var cleaned = new string(s.Where(c => char.IsAsciiDigit(c) || c is '.' or '-').ToArray());
                return double.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                    && parsed != 0 ? parsed : null;
        }
    }
}
