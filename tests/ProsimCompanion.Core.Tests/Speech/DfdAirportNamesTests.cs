using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Speech.Briefings;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>ICAO → spoken airport name (issue #70): curated overrides beat the DFD, DFD
/// names get title-cased, and a missing DFD degrades to null (callers keep spelling).</summary>
public sealed class DfdAirportNamesTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"dfd-names-{Guid.NewGuid():N}.s3db");
    private readonly BriefingOptions _options = new();
    private readonly ProsimOptions _prosimOptions = new();

    public DfdAirportNamesTests()
    {
        _options.DfdPath = _dbPath;
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE tbl_pa_airports (airport_identifier TEXT, airport_name TEXT,
                transition_altitude TEXT, transition_level REAL, elevation REAL);
            INSERT INTO tbl_pa_airports VALUES
                ('EGPH', 'EDINBURGH', NULL, 0, 135),
                ('EGLL', 'LONDON HEATHROW', NULL, 0, 83),
                ('LFMN', 'NICE-COTE D AZUR', NULL, 0, 12);
            """;
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private DfdAirportNames Names()
    {
        var monitor = new Mock<IOptionsMonitor<BriefingOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(() => _options);
        var prosim = new Mock<IOptionsMonitor<ProsimOptions>>();
        prosim.SetupGet(m => m.CurrentValue).Returns(() => _prosimOptions);
        return new DfdAirportNames(new DfdNavDataProvider(
            monitor.Object, prosim.Object, NullLogger<DfdNavDataProvider>.Instance));
    }

    [Fact]
    public void DfdName_IsTitleCasedForTts()
        => Assert.Equal("Edinburgh", Names().SpokenName("egph")); // lower-case ident tolerated

    [Fact]
    public void Override_WinsOverTheDfdFormalName()
        // The DFD says "LONDON HEATHROW" but crews say "Heathrow" — curated form wins.
        => Assert.Equal("Heathrow", Names().SpokenName("EGLL"));

    [Fact]
    public void HyphenatedName_TitleCasesEachWord()
        => Assert.Equal("Nice-Cote D Azur", Names().SpokenName("LFMN"));

    [Fact]
    public void MissingDfd_ReturnsNull_ButOverridesStillWork()
    {
        _options.DfdPath = Path.Combine(Path.GetTempPath(), "does-not-exist.s3db");
        var names = Names();

        Assert.Null(names.SpokenName("EGPH"));        // no DFD → degrade, never throw
        Assert.Equal("Sydney", names.SpokenName("YSSY")); // curated list needs no DFD
    }

    [Fact]
    public void UnknownAirport_AndBlankInput_ReturnNull()
    {
        var names = Names();
        Assert.Null(names.SpokenName("ZZZZ"));
        Assert.Null(names.SpokenName(null));
        Assert.Null(names.SpokenName("  "));
    }

    [Theory]
    [InlineData("LONDON HEATHROW", "London Heathrow")]
    [InlineData("AMSTERDAM SCHIPHOL", "Amsterdam Schiphol")]
    [InlineData("PARIS-ORLY", "Paris-Orly")]
    [InlineData("already Mixed", "Already Mixed")]
    public void TitleCase_WordBoundaries(string input, string expected)
        => Assert.Equal(expected, DfdAirportNames.TitleCase(input));

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
