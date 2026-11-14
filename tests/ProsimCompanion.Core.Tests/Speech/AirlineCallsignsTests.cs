using ProsimCompanion.Core.Speech;
using ProsimCompanion.Speech.SayIntentions;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Flight numbers by the airline's radio callsign, and the pilot's radio phraseology
/// (owner decisions 2026-11-14).</summary>
public sealed class AirlineCallsignsTests
{
    private const string Json = """
        {
          "//": "comment",
          "airlines": [
            { "icao": "BAW", "iata": "BA", "callsign": "Speedbird", "name": "British Airways" },
            { "icao": "EZY", "iata": "U2", "callsign": "Easy", "name": "easyJet" },
            { "icao": "AWE", "iata": "HP", "callsign": "Cactus" },
            { "icao": "", "iata": "", "callsign": "Nobody" },
            { "icao": "XXX", "iata": "XX", "callsign": " " }
          ]
        }
        """;

    [Theory]
    [InlineData("BA124", "Speedbird one two four")]
    [InlineData("BAW124", "Speedbird one two four")]
    [InlineData("ba 124", "Speedbird one two four")]
    [InlineData("U2123", "Easy one two three")]
    [InlineData("EZY12AB", "Easy one two Alpha Bravo")]
    [InlineData("HP9", "Cactus niner")]
    [InlineData("ZZ124", "Z Z one two four")]       // not listed: spelled by its letters
    [InlineData("QQQ7", "Q Q Q seven")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Speak(string? flightNumber, string expected)
        => Assert.Equal(expected, AirlineCallsigns.FromJson(Json).Speak(flightNumber));

    [Fact]
    public void Parse_DropsEntriesWithoutACallsignOrACode()
        => Assert.Equal(3, AirlineCallsigns.Parse(Json).Count);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    public void AnUnreadableFile_SpellsEveryAirline(string? json)
        => Assert.Equal("B A one two four", AirlineCallsigns.FromJson(json).Speak("BA124"));

    [Fact]
    public void ShippedFile_ParsesAndKnowsTheCommonAirlines()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config", AirlineCallsigns.FileName);
        if (!File.Exists(path))
        {
            path = FindInRepo();
        }

        var catalogue = AirlineCallsigns.FromJson(File.ReadAllText(path));
        Assert.Equal("Speedbird", catalogue.Callsign("BAW"));
        Assert.Equal("Speedbird", catalogue.Callsign("BA"));
        Assert.Equal("Cactus", catalogue.Callsign("AWE"));
        Assert.Null(catalogue.Callsign("???"));
        Assert.True(AirlineCallsigns.Parse(File.ReadAllText(path)).Count > 500);
    }

    private static string FindInRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProsimCompanion.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "ProsimCompanion.App", "config", AirlineCallsigns.FileName);
    }

    // ---- radio phraseology: only the FO's spoken form changes, never the transmit text ----

    [Theory]
    [InlineData("icao", RadioPhraseology.Icao)]
    [InlineData("FAA", RadioPhraseology.Faa)]
    [InlineData(" uk ", RadioPhraseology.Uk)]
    [InlineData("", RadioPhraseology.Icao)]
    [InlineData(null, RadioPhraseology.Icao)]
    public void Style_ReadsTheSetting(string? setting, RadioPhraseology expected)
        => Assert.Equal(expected, RadioSpeech.Style(setting));

    [Theory]
    [InlineData(RadioPhraseology.Icao, "Climb FL100.", "Climb flight level one zero zero.")]
    [InlineData(RadioPhraseology.Faa, "Climb FL100.", "Climb flight level one zero zero.")]
    [InlineData(RadioPhraseology.Uk, "Climb FL100.", "Climb flight level one hundred.")]
    [InlineData(RadioPhraseology.Uk, "Climb FL180.", "Climb flight level one eight zero.")]
    [InlineData(RadioPhraseology.Icao, "Contacting Tower on 119.355.", "Contacting Tower on one one niner decimal three five five.")]
    [InlineData(RadioPhraseology.Uk, "Contacting Tower on 119.355.", "Contacting Tower on one one niner decimal three five five.")]
    [InlineData(RadioPhraseology.Faa, "Contacting Tower on 119.355.", "Contacting Tower on one one niner point three five five.")]
    public void Spoken_FollowsThePilotsPhraseology(RadioPhraseology style, string transmit, string expected)
        => Assert.Equal(expected, RadioSpeech.Spoken(transmit, style));

    // ICAO Annex 10, FAA AIM 4-2-9 and CAP 413 agree on altitudes.
    [Theory]
    [InlineData(RadioPhraseology.Icao)]
    [InlineData(RadioPhraseology.Faa)]
    [InlineData(RadioPhraseology.Uk)]
    public void Spoken_Altitudes_AreTheSameInEveryStyle(RadioPhraseology style)
    {
        Assert.Equal("climbing through two thousand five hundred on KEMA5C.", RadioSpeech.Spoken("climbing through 2,500 on KEMA5C.", style));
        Assert.Equal("descending through four thousand for three thousand five hundred feet.", RadioSpeech.Spoken("descending through 4,000 for 3,500 feet.", style));
        Assert.Equal("Climb altitude one zero thousand feet.", RadioSpeech.Spoken("Climb altitude 10,000 feet.", style));
        Assert.Equal("Descend altitude three thousand feet.", RadioSpeech.Spoken("Descend altitude 3000 feet.", style));
    }

    [Fact]
    public void Spoken_Faa_ReadsTheFlightNumberInGroupForm()
    {
        const string transmit = "Climb FL140, Speedbird-eight-five-zero.";
        Assert.Equal("Climb flight level one four zero, Speedbird eight fifty.", RadioSpeech.Spoken(transmit, RadioPhraseology.Faa, "Speedbird-eight-five-zero"));
        Assert.Equal("Climb flight level one four zero, Speedbird eight five zero.", RadioSpeech.Spoken(transmit, RadioPhraseology.Icao, "Speedbird-eight-five-zero"));
        Assert.Equal("Climb flight level one four zero, Speedbird eight five zero.", RadioSpeech.Spoken(transmit, RadioPhraseology.Uk, "Speedbird-eight-five-zero"));
    }

    [Theory]
    [InlineData("Finnair-niner-six-two", "Finnair nine sixty two")]
    [InlineData("Cactus-one-zero-zero-four", "Cactus ten zero four")]
    [InlineData("Speedbird-five-two", "Speedbird fifty two")]
    [InlineData("Speedbird-five-alpha", null)]       // a letter suffix: as SayIntentions wrote it
    [InlineData("Speedbird", null)]
    [InlineData("", null)]
    public void GroupFormCallsign(string callsign, string? expected)
        => Assert.Equal(expected, RadioSpeech.GroupFormCallsign(callsign));

    [Fact]
    public void Compose_KeepsTheTransmitText_WhateverTheStyle()
    {
        var icao = ReadbackComposer.Compose("Speedbird-eight-five-zero, climb FL100", "Speedbird-eight-five-zero", null);
        var faa = ReadbackComposer.Compose("Speedbird-eight-five-zero, climb FL100", "Speedbird-eight-five-zero", null, null, RadioPhraseology.Faa);
        var uk = ReadbackComposer.Compose("Speedbird-eight-five-zero, climb FL100", "Speedbird-eight-five-zero", null, null, RadioPhraseology.Uk);

        Assert.NotNull(icao);
        Assert.Equal(icao!.Transmit, faa!.Transmit);
        Assert.Equal(icao.Transmit, uk!.Transmit);
        Assert.Equal("Climb flight level one zero zero, Speedbird eight five zero.", icao.Spoken);
        Assert.Equal("Climb flight level one zero zero, Speedbird eight fifty.", faa.Spoken);
        Assert.Equal("Climb flight level one hundred, Speedbird eight five zero.", uk.Spoken);
    }
}
