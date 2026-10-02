using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Monitoring;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Issue #148, module 4: standalone read-backs — what counts as one, and the FO's
/// answer against the aircraft.</summary>
public sealed class ReadbackCoreTests
{
    private static readonly ReadbackOptions Options = new() { Enabled = true };

    private static ReadbackActuals Actuals(double? baro = 1013, int v1 = 141, int vr = 144, int v2 = 147, string? runway = "27R", ArrivalMinima? minima = null)
        => new(baro, v1, vr, v2, runway, minima ?? new ArrivalMinima(ArrivalMinimumKind.DecisionAltitude, 410));

    [Theory]
    [InlineData("altimeter one zero one three", ReadbackKind.Altimeter, 1013)]
    [InlineData("QNH 1013", ReadbackKind.Altimeter, 1013)]
    [InlineData("q n h one zero zero five", ReadbackKind.Altimeter, 1005)]
    [InlineData("minimums four one zero", ReadbackKind.Minimums, 410)]
    [InlineData("decision altitude 410", ReadbackKind.Minimums, 410)]
    [InlineData("decision height two hundred", ReadbackKind.Minimums, 200)]
    public void Parse_SingleFigureReadbacks(string said, ReadbackKind kind, double number)
    {
        var request = ReadbackCore.Parse(said);

        Assert.NotNull(request);
        Assert.Equal(kind, request.Kind);
        Assert.Equal(number, Assert.Single(request.Numbers));
    }

    [Fact]
    public void Parse_VSpeeds_ReadsUpToThreeFigures_InOrder()
    {
        var full = ReadbackCore.Parse("v speeds one four one, one four four, one four seven");
        var spoken = ReadbackCore.Parse("V one one four one rotate one four four V two one four seven");
        var partial = ReadbackCore.Parse("v speeds 141 and 144");

        Assert.Equal([141, 144, 147], full!.Numbers);
        Assert.Equal([141, 144, 147], spoken!.Numbers);
        Assert.Equal([141, 144], partial!.Numbers);
        Assert.Null(ReadbackCore.Parse("v speeds"));
    }

    [Theory]
    [InlineData("runway two seven right", "27R")]
    [InlineData("runway 27 left", "27L")]
    [InlineData("runway zero four", "04")]
    [InlineData("runway 9", "09")]
    [InlineData("runway one six centre", "16C")]
    public void Parse_Runway(string said, string expected)
    {
        var request = ReadbackCore.Parse(said);

        Assert.Equal(ReadbackKind.Runway, request!.Kind);
        Assert.Equal(expected, request.Runway);
    }

    [Theory]
    [InlineData("")]
    [InlineData("minimums check")]                // another feature's phrase: a lead-in with no figure
    [InlineData("altimeter check")]
    [InlineData("runway")]
    [InlineData("runway four zero")]              // not a runway number
    [InlineData("set altimeter one zero one three")]
    [InlineData("fuel check")]
    public void Parse_NotAReadback_IsNull(string said) => Assert.Null(ReadbackCore.Parse(said));

    [Fact]
    public void Answer_Altimeter_ChecksWithinTheTolerance_OrReadsItsOwn()
    {
        var ok = ReadbackCore.Answer(ReadbackCore.Parse("altimeter one zero one three")!, Actuals(baro: 1013.4), Options);
        var wrong = ReadbackCore.Answer(ReadbackCore.Parse("altimeter one zero one three")!, Actuals(baro: 1017), Options);
        var none = ReadbackCore.Answer(ReadbackCore.Parse("altimeter one zero one three")!, Actuals(baro: 0), Options);

        Assert.True(ok.Ok);
        Assert.Equal("QNH one zero one three, checked.", ok.Text);
        Assert.Equal("checked", ok.Outcome);
        Assert.Equal("Negative. I read QNH one zero one seven.", wrong.Text);
        Assert.Equal("mismatch", wrong.Outcome);
        Assert.Equal("I can't read the altimeter setting.", none.Text);
        Assert.Equal("unavailable", none.Outcome);
    }

    [Fact]
    public void Answer_VSpeeds_NeedsAllThree_AndTheFms()
    {
        var ok = ReadbackCore.Answer(ReadbackCore.Parse("v speeds 141 144 147")!, Actuals(), Options);
        var oneOff = ReadbackCore.Answer(ReadbackCore.Parse("v speeds 142 145 148")!, Actuals(), Options);
        var wrong = ReadbackCore.Answer(ReadbackCore.Parse("v speeds 135 144 147")!, Actuals(), Options);
        var incomplete = ReadbackCore.Answer(ReadbackCore.Parse("v speeds 141 144")!, Actuals(), Options);
        var noFms = ReadbackCore.Answer(ReadbackCore.Parse("v speeds 141 144 147")!, Actuals(v2: 0), Options);

        Assert.Equal("V one one four one, rotate one four four, V two one four seven, checked.", ok.Text);
        Assert.True(oneOff.Ok);
        Assert.Equal("Negative. FMS has V one one four one, rotate one four four, V two one four seven.", wrong.Text);
        Assert.Equal("Say V one, rotate and V two.", incomplete.Text);
        Assert.Equal("incomplete", incomplete.Outcome);
        Assert.Equal("V speeds are not in the FMS.", noFms.Text);
    }

    [Fact]
    public void Answer_Runway_ComparesNormalizedDesignators()
    {
        var ok = ReadbackCore.Answer(ReadbackCore.Parse("runway two seven right")!, Actuals(runway: "RW27R"), Options);
        var wrong = ReadbackCore.Answer(ReadbackCore.Parse("runway two seven left")!, Actuals(runway: "27R"), Options);
        var none = ReadbackCore.Answer(ReadbackCore.Parse("runway two seven left")!, Actuals(runway: ""), Options);

        Assert.True(ok.Ok);
        Assert.Equal("Runway two seven right, checked.", ok.Text);
        Assert.Equal("Negative. FMS has runway two seven right.", wrong.Text);
        Assert.Equal("No runway in the FMS.", none.Text);
    }

    [Fact]
    public void Answer_Minimums_AgainstTheBriefedValue()
    {
        var ok = ReadbackCore.Answer(ReadbackCore.Parse("minimums four one zero")!, Actuals(), Options);
        var wrong = ReadbackCore.Answer(ReadbackCore.Parse("minimums five zero zero")!, Actuals(), Options);
        var none = ReadbackCore.Answer(ReadbackCore.Parse("minimums four one zero")!, Actuals() with { Minima = null }, Options);

        Assert.Equal("Minimums, decision altitude four one zero feet, checked.", ok.Text);
        Assert.Equal("Negative. Briefed minimums are decision altitude four one zero feet.", wrong.Text);
        Assert.Equal("Minimums not briefed.", none.Text);
    }

    [Theory]
    [InlineData("RW27R", "27R")]
    [InlineData("27r", "27R")]
    [InlineData("7R", "07R")]
    [InlineData("04", "04")]
    [InlineData("RW", null)]
    [InlineData(null, null)]
    public void NormalizeRunway(string? raw, string? expected) => Assert.Equal(expected, ReadbackCore.NormalizeRunway(raw));
}
