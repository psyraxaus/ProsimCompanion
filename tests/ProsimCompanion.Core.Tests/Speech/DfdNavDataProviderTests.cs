using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Speech.Briefings;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>End-to-end reader test against a real temp SQLite file in the NEW DFD schema —
/// proves the whole chain (open, schema detect, scalar tolerance, approaches, missed
/// approach) so a live "DFD not working" report can be isolated to path/config issues.</summary>
public sealed class DfdNavDataProviderTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"dfd-test-{Guid.NewGuid():N}.s3db");
    private readonly BriefingOptions _options = new();

    public DfdNavDataProviderTests()
    {
        _options.DfdPath = _dbPath;
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE tbl_hdr_header (cycle TEXT);
            INSERT INTO tbl_hdr_header VALUES ('2508');
            CREATE TABLE tbl_pa_airports (airport_identifier TEXT, transition_altitude TEXT,
                transition_level REAL, elevation REAL);
            INSERT INTO tbl_pa_airports VALUES ('YSSY', 'FL110', 0, 21);
            CREATE TABLE tbl_pg_runways (airport_identifier TEXT, runway_identifier TEXT,
                runway_true_bearing REAL, runway_length REAL, landing_threshold_elevation REAL);
            INSERT INTO tbl_pg_runways VALUES ('YSSY', 'RW16R', 163.0, 12999, 0);
            CREATE TABLE tbl_pi_localizers_glideslopes (airport_identifier TEXT,
                runway_identifier TEXT, llz_identifier TEXT, llz_frequency REAL, gs_angle REAL);
            INSERT INTO tbl_pi_localizers_glideslopes VALUES ('YSSY', 'RW16R', 'IMEA', 110.3, 3.0);
            CREATE TABLE tbl_pd_sids (airport_identifier TEXT, procedure_identifier TEXT);
            INSERT INTO tbl_pd_sids VALUES ('YSSY', 'FISHA1');
            CREATE TABLE tbl_pe_stars (airport_identifier TEXT, procedure_identifier TEXT);
            CREATE TABLE tbl_pf_iaps (airport_identifier TEXT, procedure_identifier TEXT,
                transition_identifier TEXT, seqno INTEGER, waypoint_identifier TEXT,
                waypoint_description_code TEXT, path_termination TEXT, turn_direction TEXT,
                course REAL, altitude1 REAL);
            INSERT INTO tbl_pf_iaps VALUES
                ('YSSY', 'I16RY', NULL, 10, 'SANAD', 'E  F', 'IF', NULL, NULL, NULL),
                ('YSSY', 'I16RY', NULL, 20, 'RW16R', 'G  M', 'TF', NULL, NULL, NULL),
                ('YSSY', 'I16RY', NULL, 30, NULL,    NULL,  'CA', NULL, 163, 600),
                ('YSSY', 'I16RY', NULL, 40, 'SOSIJ', 'E  E', 'DF', 'L',  NULL, 3000),
                ('YSSY', 'R16RZ', NULL, 10, 'RW16R', 'G  M', 'TF', NULL, NULL, NULL);
            """;
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private DfdNavDataProvider Provider()
    {
        var monitor = new Mock<IOptionsMonitor<BriefingOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(() => _options);
        return new DfdNavDataProvider(monitor.Object, NullLogger<DfdNavDataProvider>.Instance);
    }

    [Fact]
    public void Lookup_ReadsAllFacts_WithScalarTolerance()
    {
        // Lower-case ident and quoted path (Explorer "Copy as path") must both still work.
        _options.DfdPath = $"\"{_dbPath}\"";
        var facts = Provider().Lookup("yssy", "16R", sid: "FISHA1", star: "MARLN4", approach: "I16RY");

        Assert.Equal("2508", facts.AiracCycle);
        Assert.Equal(110, facts.TransitionAltitudeFt);      // "FL110" string tolerated → 110
        Assert.Null(facts.TransitionLevel);                 // 0 = not published → null
        Assert.Equal(163.0, facts.RunwayTrueHeading);
        Assert.Equal(21, facts.RunwayElevationFt);          // threshold 0 → airport elevation fallback
        Assert.Equal("IMEA", facts.IlsIdent);
        Assert.Equal(3.0, facts.GlideSlopeAngle);
        Assert.True(facts.SidFound);
        Assert.False(facts.StarFound);                      // MARLN4 not in the DB
        Assert.True(facts.ApproachFound);
    }

    [Fact]
    public void AiracCycle_ReadsHeader()
        => Assert.Equal("2508", Provider().AiracCycle);

    [Fact]
    public void ApproachesForRunway_RanksIlsFirst()
    {
        var options = Provider().ApproachesForRunway("YSSY", "16R");
        Assert.Equal(2, options.Count);
        Assert.Equal("I16RY", options[0].Identifier); // ILS before RNAV
        Assert.Equal("R16RZ", options[1].Identifier);
    }

    [Fact]
    public void MissedApproach_LegsAfterTheMapMarker()
    {
        var procedure = Provider().MissedApproach("YSSY", "I16RY");
        Assert.Equal(2, procedure.Legs.Count);
        Assert.Equal("heading one six three, climb to 600 feet", procedure.Legs[0].Phrase);
        Assert.Equal("left turn, direct SOSIJ, climb to 3000 feet", procedure.Legs[1].Phrase);
    }

    [Fact]
    public void MissingFile_DegradesEverywhere()
    {
        _options.DfdPath = Path.Combine(Path.GetTempPath(), "does-not-exist.s3db");
        var provider = Provider();
        Assert.False(provider.IsConfigured);
        Assert.Null(provider.AiracCycle);
        Assert.Equal(NavDataFacts.None, provider.Lookup("YSSY", "16R"));
        Assert.Empty(provider.ApproachesForRunway("YSSY", "16R"));
        Assert.Empty(provider.MissedApproach("YSSY", "I16RY").Legs);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // Temp-dir leftovers are harmless.
        }
    }
}
