using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Speech.Briefings;

/// <summary>Nav-data facts for one briefing (all fields optional — a failed query nulls one
/// field only, never the briefing).</summary>
public sealed record NavDataFacts(
    string? AiracCycle,
    double? TransitionAltitudeFt,
    double? TransitionLevel,
    double? RunwayTrueHeading,
    double? RunwayLengthFt,
    double? RunwayElevationFt,
    string? IlsIdent,
    double? IlsFrequencyMhz,
    double? GlideSlopeAngle);

/// <summary>
/// Navigraph DFD SQLite reader (read-only; user-supplied database, never redistributed).
/// Auto-detects both schema generations (tbl_pa_airports new / tbl_airports legacy). Every
/// field is its own defensive scalar query. Runway keys normalize to "RW16R".
/// </summary>
public sealed class DfdNavDataProvider
{
    private readonly IOptionsMonitor<BriefingOptions> _options;
    private readonly ILogger<DfdNavDataProvider> _logger;

    public DfdNavDataProvider(IOptionsMonitor<BriefingOptions> options, ILogger<DfdNavDataProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
    }

    public bool IsConfigured => File.Exists(_options.CurrentValue.DfdPath);

    public NavDataFacts Lookup(string airport, string? runway)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(airport);
        if (!IsConfigured)
        {
            return new NavDataFacts(null, null, null, null, null, null, null, null, null);
        }

        try
        {
            using var connection = new SqliteConnection(
                $"Data Source={_options.CurrentValue.DfdPath};Mode=ReadOnly;Cache=Shared");
            connection.Open();

            var newSchema = TableExists(connection, "tbl_pa_airports");
            var header = newSchema ? ("tbl_hdr_header", "cycle") : ("tbl_header", "current_airac");
            var airports = newSchema ? "tbl_pa_airports" : "tbl_airports";
            var runways = newSchema ? "tbl_pg_runways" : "tbl_runways";
            var ils = newSchema ? "tbl_pi_localizers_glideslopes" : "tbl_localizers_glideslopes";

            var rw = NormalizeRunway(runway);
            return new NavDataFacts(
                Scalar<string>(connection, $"SELECT {header.Item2} FROM {header.Item1} LIMIT 1"),
                ScalarD(connection, $"SELECT transition_altitude FROM {airports} WHERE airport_identifier=@a LIMIT 1", airport),
                ScalarD(connection, $"SELECT transition_level FROM {airports} WHERE airport_identifier=@a LIMIT 1", airport),
                rw is null ? null : ScalarD(connection, $"SELECT runway_true_bearing FROM {runways} WHERE airport_identifier=@a AND runway_identifier=@r LIMIT 1", airport, rw),
                rw is null ? null : ScalarD(connection, $"SELECT runway_length FROM {runways} WHERE airport_identifier=@a AND runway_identifier=@r LIMIT 1", airport, rw),
                rw is null ? null : ScalarD(connection, $"SELECT landing_threshold_elevation FROM {runways} WHERE airport_identifier=@a AND runway_identifier=@r LIMIT 1", airport, rw),
                rw is null ? null : Scalar<string>(connection, $"SELECT llz_identifier FROM {ils} WHERE airport_identifier=@a AND runway_identifier=@r LIMIT 1", airport, rw),
                rw is null ? null : ScalarD(connection, $"SELECT llz_frequency FROM {ils} WHERE airport_identifier=@a AND runway_identifier=@r LIMIT 1", airport, rw),
                rw is null ? null : ScalarD(connection, $"SELECT gs_angle FROM {ils} WHERE airport_identifier=@a AND runway_identifier=@r LIMIT 1", airport, rw));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DFD lookup failed for {Airport}", airport);
            return new NavDataFacts(null, null, null, null, null, null, null, null, null);
        }
    }

    /// <summary>"16R"/"6R"/"RW16R" → "RW16R".</summary>
    public static string? NormalizeRunway(string? runway)
    {
        if (string.IsNullOrWhiteSpace(runway))
        {
            return null;
        }

        var r = runway.Trim().ToUpperInvariant();
        if (r.StartsWith("RW", StringComparison.Ordinal))
        {
            r = r[2..];
        }

        var digits = new string(r.TakeWhile(char.IsAsciiDigit).ToArray());
        var side = new string(r.SkipWhile(char.IsAsciiDigit).Take(1).Where(c => c is 'L' or 'R' or 'C').ToArray());
        if (digits.Length is 0 or > 2)
        {
            return null;
        }

        return "RW" + digits.PadLeft(2, '0') + side;
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@t LIMIT 1";
        cmd.Parameters.AddWithValue("@t", table);
        return cmd.ExecuteScalar() is not null;
    }

    private T? Scalar<T>(SqliteConnection connection, string sql, string? a = null, string? r = null)
        where T : class
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            if (a is not null)
            {
                cmd.Parameters.AddWithValue("@a", a);
            }

            if (r is not null)
            {
                cmd.Parameters.AddWithValue("@r", r);
            }

            return cmd.ExecuteScalar() as T ?? cmd.ExecuteScalar()?.ToString() as T;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DFD scalar failed: {Sql}", sql);
            return null;
        }
    }

    private double? ScalarD(SqliteConnection connection, string sql, string? a = null, string? r = null)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            if (a is not null)
            {
                cmd.Parameters.AddWithValue("@a", a);
            }

            if (r is not null)
            {
                cmd.Parameters.AddWithValue("@r", r);
            }

            var value = cmd.ExecuteScalar();
            return value is null or DBNull
                ? null
                : Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DFD scalar failed: {Sql}", sql);
            return null;
        }
    }
}
