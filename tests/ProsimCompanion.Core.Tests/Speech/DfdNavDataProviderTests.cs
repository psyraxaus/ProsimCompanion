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
                transition_level REAL, elevation REAL, airport_ref_latitude REAL, airport_ref_longitude REAL);
            INSERT INTO tbl_pa_airports VALUES ('YSSY', 'FL110', 0, 21, -33.946111, 151.177222);
            INSERT INTO tbl_pa_airports VALUES ('XNOP', NULL, 0, 0, NULL, NULL);
            CREATE TABLE tbl_pg_runways (airport_identifier TEXT, runway_identifier TEXT,
                runway_true_bearing REAL, runway_length REAL, landing_threshold_elevation REAL);
            INSERT INTO tbl_pg_runways VALUES ('YSSY', 'RW16R', 163.0, 12999, 0);
            CREATE TABLE tbl_pi_localizers_glideslopes (airport_identifier TEXT,
                runway_identifier TEXT, llz_identifier TEXT, llz_frequency REAL, gs_angle REAL);
            INSERT INTO tbl_pi_localizers_glideslopes VALUES ('YSSY', 'RW16R', 'IMEA', 110.3, 3.0);
            CREATE TABLE tbl_pd_sids (airport_identifier TEXT, procedure_identifier TEXT, route_type TEXT,
                transition_identifier TEXT, seqno INTEGER, waypoint_identifier TEXT, path_termination TEXT,
                turn_direction TEXT, course REAL, altitude1 REAL);
            INSERT INTO tbl_pd_sids VALUES ('YSSY', 'FISHA1', '4', 'RW16R', 10, 'FISHA', 'TF', NULL, NULL, NULL);
            INSERT INTO tbl_pd_sids VALUES
                ('YSSY', 'EO16R', '0', 'RW16R', 10, NULL,  'CA', NULL, 163, 1500),
                ('YSSY', 'EO16R', '0', 'RW16R', 20, 'SYD', 'DF', 'L',  NULL, 3000),
                ('YSSY', 'EO34L', '0', 'RW34L', 10, NULL,  'CA', NULL, 343, 1500);
            CREATE TABLE tbl_ps_airport_msa (airport_identifier TEXT, msa_center TEXT, sector_altitude_1 REAL,
                sector_altitude_2 REAL, sector_altitude_3 REAL, sector_altitude_4 REAL, sector_altitude_5 REAL);
            INSERT INTO tbl_ps_airport_msa VALUES ('YSSY', 'SY', 2700, 3500, NULL, 0, NULL);
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

    private readonly ProsimOptions _prosimOptions = new();

    private DfdNavDataProvider Provider()
    {
        var monitor = new Mock<IOptionsMonitor<BriefingOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(() => _options);
        var prosim = new Mock<IOptionsMonitor<ProsimOptions>>();
        prosim.SetupGet(m => m.CurrentValue).Returns(() => _prosimOptions);
        return new DfdNavDataProvider(monitor.Object, prosim.Object, NullLogger<DfdNavDataProvider>.Instance);
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
    public void AirportMsa_IsTheHighestPublishedSector_AndNullWhenAbsent()
    {
        // Issue #180: 0 and NULL sectors are "not published"; the highest of the rest is spoken.
        Assert.Equal(3500, Provider().AirportMsaFt("yssy"));
        Assert.Null(Provider().AirportMsaFt("ZZZZ"));
        Assert.Null(Provider().AirportMsaFt(null));
    }

    [Fact]
    public void EngineOutProcedure_ReadsTheRunwaysEosidLegs_InOrder_AndIsEmptyWhenNonePublished()
    {
        // Issue #180: route_type '0' is the engine-out SID; only the legs of this runway's
        // procedure, never the 34L one or the ordinary FISHA1.
        var legs = Provider().EngineOutProcedure("yssy", "16R");

        Assert.Equal(2, legs.Count);
        Assert.Contains("heading one six three", legs[0], StringComparison.Ordinal);
        Assert.Contains("1500", legs[0], StringComparison.Ordinal);
        Assert.StartsWith("left turn", legs[1], StringComparison.Ordinal);
        Assert.Contains("3000", legs[1], StringComparison.Ordinal);
        Assert.Empty(Provider().EngineOutProcedure("yssy", "07"));
        Assert.Empty(Provider().EngineOutProcedure("ZZZZ", "16R"));
    }

    [Fact]
    public void AirportReferencePoint_ReadsCoordinatesAndElevation()
    {
        var point = Provider().AirportReferencePoint("yssy");

        Assert.NotNull(point);
        Assert.Equal(-33.946111, point.Value.LatitudeDeg, 6);
        Assert.Equal(151.177222, point.Value.LongitudeDeg, 6);
        Assert.Equal(21, point.Value.ElevationFt);
    }

    [Fact]
    public void AirportReferencePoint_UnknownAirportOrNoCoordinates_IsNull()
    {
        Assert.Null(Provider().AirportReferencePoint("ZZZZ"));
        Assert.Null(Provider().AirportReferencePoint("XNOP"));   // row exists, coordinates NULL
        Assert.Null(Provider().AirportReferencePoint(" "));
    }

    [Fact]
    public async Task DfdAirportCoordinates_IsTheSecondTier_AndDegradesWithoutADatabase()
    {
        var source = new DfdAirportCoordinates(Provider());

        var found = await source.FindAsync("yssy");

        Assert.Equal(20, source.Order);
        Assert.Equal("YSSY", found!.Icao);
        Assert.Equal("dfd", found.Source);
        Assert.Equal(-33.9461, found.Position.LatitudeDeg, 4);
        Assert.Equal(21, found.ElevationFt);
        Assert.Null(await source.FindAsync("ZZZZ"));

        _options.DfdPath = Path.Combine(Path.GetTempPath(), $"no-such-dfd-{Guid.NewGuid():N}.s3db");
        Assert.Null(await new DfdAirportCoordinates(Provider()).FindAsync("YSSY"));
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
    public void FolderPath_DiscoversTheDfdFile_SkippingNonDfdDecoys()
    {
        // ProSim's Navdata folder holds nd.db3 (its own display database, no DFD tables) and
        // NavData.dat beside the DFD export — discovery must schema-validate, not just glob.
        var folder = Path.Combine(Path.GetTempPath(), $"navdata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            File.Copy(_dbPath, Path.Combine(folder, "ng_jeppesen_prosim.s3db"));
            using (var decoy = new SqliteConnection($"Data Source={Path.Combine(folder, "aa_decoy.s3db")}"))
            {
                decoy.Open();
                using var cmd = decoy.CreateCommand();
                cmd.CommandText = "CREATE TABLE not_navdata (x TEXT);";
                cmd.ExecuteNonQuery();
            }

            File.WriteAllText(Path.Combine(folder, "NavData.dat"), "binary junk");
            SqliteConnection.ClearAllPools();

            _options.DfdPath = folder;
            var provider = Provider();
            Assert.Equal("ng_jeppesen_prosim.s3db", Path.GetFileName(provider.ResolvedPath));
            Assert.Equal("2508", provider.AiracCycle);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void EmptyPath_ProbesProsimSdkNavdataFolder()
    {
        var sdkRoot = Path.Combine(Path.GetTempPath(), $"prosim-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(sdkRoot, "Navdata"));
        try
        {
            File.Copy(_dbPath, Path.Combine(sdkRoot, "Navdata", "ng_jeppesen_prosim.s3db"));
            SqliteConnection.ClearAllPools();

            _options.DfdPath = "";
            _prosimOptions.SdkPath = sdkRoot;
            var provider = Provider();
            Assert.NotNull(provider.ResolvedPath);
            Assert.Equal("2508", provider.AiracCycle);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(sdkRoot, recursive: true);
        }
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
