using ProsimCompanion.Core.Aircraft.WeightAndBalance;
using Xunit;

namespace ProsimCompanion.Core.Tests.Aircraft.WeightAndBalance;

public sealed class LoadsheetFormatterTests
{
    private static LoadsheetContext Context() => new()
    {
        Ident = "1722",
        Callsign = "FIN536",
        DepartureIata = "RVN",
        ArrivalIata = "HEL",
        AircraftTailNumber = "OH-LXK",
        ScheduledDepartureTime = "10 10May26",
        EditionNumber = 1,
        MaxZfwKg = 62500,
        MaxTowKg = 79000,
        MaxLawKg = 64500,
        WaterWasteKg = 1080,
        DispatcherFirstName = "Judith",
        DispatcherLastName = "Nguyen",
        Time = new DateTime(2026, 5, 10, 9, 41, 0, DateTimeKind.Utc),
    };

    private static LoadsheetData Data(double zfw = 59000, double tow = 66000, double law = 61600) => new()
    {
        ZeroFuelWeight = zfw,
        ZeroFuelWeightMac = 26.9,
        TakeoffWeight = tow,
        TakeoffWeightMac = 25.4,
        FuelWeight = 7000,
        LandingWeight = law,
        LizfwIndex = 706.35,
        LitowIndex = 704.10,
        TotalPassengers = 99,
        PassengersByZone = [18, 22, 27, 32],
    };

    [Fact]
    public void Preliminary_RendersTheAirlineTemplate()
    {
        var text = LoadsheetFormatter.FormatPreliminary(Context(), Data());
        var lines = text.Split(Environment.NewLine);

        Assert.Equal("- LOADSHEET PRELIM 1722 EDNO 1", lines[0]);
        Assert.Equal("FIN536/10 10MAY26", lines[1]);
        Assert.Equal("RVN HEL OH-LXK 2/4", lines[2]);
        Assert.Equal("ZFW  59000  MAX  62500  ", lines[3]);       // no L marker (3.5 t margin)
        Assert.Equal("TOF  7000", lines[4]);
        Assert.Equal("TIF  4400", lines[6]);
        Assert.Equal("UNDLD  2900", lines[8]);
        Assert.Equal("PAX/0/99 TTL 99", lines[9]);
        Assert.Equal("MACZFW  26.9", lines[10]);
        Assert.Equal("MACTOW  25.4", lines[11]);
        Assert.Equal("A18  B22  C59", lines[12]);                 // C aggregates zones 3+4
        Assert.Equal("HEL POTABLE WATER 10/10 100PCT", lines[16]);
        Assert.Equal(" 1080 0.5-", lines[17]);                    // leading space is part of the format
        Assert.Equal("PREPARED BY JUDITH/NGUYEN 44 20543 53533", lines[21]);
        Assert.Equal("FUEL IN TANKS 7000", lines[^1]);            // last line has no trailing newline
    }

    [Fact]
    public void Preliminary_MarksWeightsWithinAThousandKgOfMax()
    {
        var data = Data(zfw: 62000, tow: 78500, law: 64000); // all within 1000 kg of their max
        var text = LoadsheetFormatter.FormatPreliminary(Context(), data);
        var lines = text.Split(Environment.NewLine);

        Assert.Equal("ZFW  62000  MAX  62500  L", lines[3]);
        Assert.Equal("TOW  78500  MAX  79000  L", lines[5]);
        Assert.Equal("LAW  64000  MAX  64500  L", lines[7]);
    }

    [Fact]
    public void Final_Unchanged_IsCompliance_WithNoChangeFlags()
    {
        var prelim = Data();
        var final = Data();
        var text = LoadsheetFormatter.FormatFinal(Context(), prelim, final);
        var lines = text.Split(Environment.NewLine);

        Assert.Equal("COMPLIANCE WITH EDNO 1", lines[0]);
        Assert.Equal("RVN  HEL  OH-LXK  2/4", lines[2]);          // two-space separators on FINAL
        Assert.Equal("........................", lines[3]);       // exactly 24 dots
        Assert.Equal("PAX  99 no change", lines[6]);
        Assert.StartsWith("LIZFW   706.", lines[9]);              // F1 rendering of the 2 dp index
        Assert.EndsWith("  ", lines[9]);                          // unchanged → empty flag slot
        Assert.Equal("09:41Z", lines[^2]);
        Assert.Equal("END", lines[^1]);
    }

    [Fact]
    public void Final_Changed_IsRevisions_WithFlagsOnChangedFieldsOnly()
    {
        var prelim = Data();
        var final = Data(zfw: 60500, tow: 67500);                 // +1500 kg — over the 1000 kg tolerance
        final.TotalPassengers = 101;
        var text = LoadsheetFormatter.FormatFinal(Context(), prelim, final);
        var lines = text.Split(Environment.NewLine);

        Assert.Equal("REVISIONS TO EDNO 1", lines[0]);
        Assert.Equal("ZFW  60500  //", lines[4]);
        Assert.Equal("TOW  67500  //", lines[5]);
        Assert.Equal("PAX  101 plus 2", lines[6]);
        Assert.Equal("MACZFW  26.9  ", lines[7]);                 // MAC unchanged → no flag
    }

    [Fact]
    public void Final_DeltaEqualToTolerance_IsNotAChange()
    {
        var prelim = Data();
        var final = Data(zfw: prelim.ZeroFuelWeight + 1000);      // exactly the tolerance
        var text = LoadsheetFormatter.FormatFinal(Context(), prelim, final);

        Assert.StartsWith("COMPLIANCE WITH EDNO 1", text);
    }
}
