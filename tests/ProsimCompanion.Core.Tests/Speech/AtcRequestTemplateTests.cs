using ProsimCompanion.Speech.SayIntentions;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Issue #140: the request text with and without a stand. The templates are the
/// shipped atc-requests.json strings; the callsign is the 2026-10-10 flight's.</summary>
public sealed class AtcRequestTemplateTests
{
    private const string Callsign = "Finnair-one-three-six-one";
    private const string PushIcao = "Ground, {callsign}, stand {gate}, request push and start";
    private const string PushFaa = "Ground, {callsign}, gate {gate}, request push and start";
    private const string TaxiIcao = "Ground, {callsign}, stand {gate}, request taxi";
    private const string StartIcao = "Ground, {callsign}, stand {gate}, request start up";
    private const string TakeoffIcao = "Tower, {callsign}, holding short runway {runway}, ready for departure";
    private const string ClearanceIcao = "Delivery, {callsign}, request clearance";

    [Theory]
    [InlineData(PushIcao, "Ground, Finnair-one-three-six-one, request push and start")]
    [InlineData(TaxiIcao, "Ground, Finnair-one-three-six-one, request taxi")]
    [InlineData(StartIcao, "Ground, Finnair-one-three-six-one, request start up")]
    [InlineData(PushFaa, "Ground, Finnair-one-three-six-one, request push and start")]
    public void NoStandAnywhere_DropsTheStandClauseWhole(string template, string expected)
    {
        var text = AtcRequestTemplate.Fill(template, new AtcRequestValues(Callsign, null, "04R", "EGCC", "EGCC"));

        Assert.Equal(expected, text);
        Assert.DoesNotContain("stand ,", text, StringComparison.Ordinal);
        Assert.DoesNotContain(", ,", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SayIntentionsStand_IsUsedAsWritten()
    {
        var text = AtcRequestTemplate.Fill(PushIcao, new AtcRequestValues(Callsign, "PIER 2 GATE STAND 208", null, null, null));

        Assert.Equal("Ground, Finnair-one-three-six-one, stand PIER 2 GATE STAND 208, request push and start", text);
    }

    [Theory]
    [InlineData(PushIcao, "Gate 208", "Ground, Finnair-one-three-six-one, stand 208, request push and start")]
    [InlineData(PushIcao, "Stand 546R", "Ground, Finnair-one-three-six-one, stand 546R, request push and start")]
    [InlineData(PushFaa, "Gate 35", "Ground, Finnair-one-three-six-one, gate 35, request push and start")]
    [InlineData(PushFaa, "Dock 4", "Ground, Finnair-one-three-six-one, gate Dock 4, request push and start")]
    public void GsxStandName_DropsItsOwnLabelBehindTheTemplateLabel(string template, string gsxStand, string expected)
        => Assert.Equal(expected, AtcRequestTemplate.Fill(template, new AtcRequestValues(Callsign, gsxStand, null, null, null)));

    [Fact]
    public void Runway_Filled_AndDroppedWhenUnknown()
    {
        Assert.Equal(
            "Tower, Finnair-one-three-six-one, holding short runway 04R, ready for departure",
            AtcRequestTemplate.Fill(TakeoffIcao, new AtcRequestValues(Callsign, null, "04R", null, null)));
        Assert.Equal(
            "Tower, Finnair-one-three-six-one, ready for departure",
            AtcRequestTemplate.Fill(TakeoffIcao, new AtcRequestValues(Callsign, null, "  ", null, null)));
    }

    [Fact]
    public void Callsign_Missing_DropsOnlyItsClause()
        => Assert.Equal("Delivery, request clearance", AtcRequestTemplate.Fill(ClearanceIcao, new AtcRequestValues(null, null, null, null, null)));

    [Fact]
    public void UnknownToken_IsLeftForTheUserToSee()
        => Assert.Equal("Ground, X, via {taxiway}", AtcRequestTemplate.Fill("Ground, {callsign}, via {taxiway}", new AtcRequestValues("X", null, null, null, null)));

    [Fact]
    public void LegacyTowerLine_StillFills()
        => Assert.Equal("Contact tower, Finnair-one-three-six-one", AtcRequestTemplate.Fill("Contact tower, {callsign}", new AtcRequestValues(Callsign, null, null, null, null)));
}
