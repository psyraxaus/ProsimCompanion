using ProsimCompanion.Core.Configuration;
using Xunit;

namespace ProsimCompanion.Core.Tests.Configuration.Fields;

/// <summary>The save-time normalisation that replaced fifty hand-written OnBeforeSave lines (C3).</summary>
public sealed class OptionFieldNormalizerTests
{
    [Theory]
    [InlineData("  Left ", FieldNormalization.None, "  Left ")]
    [InlineData("  Left ", FieldNormalization.Trim, "Left")]
    [InlineData("  Left ", FieldNormalization.TrimLower, "left")]
    [InlineData(" egll ", FieldNormalization.TrimUpper, "EGLL")]
    [InlineData(" \"C:\\Sim\\dfd.s3db\" ", FieldNormalization.Path, "C:\\Sim\\dfd.s3db")]
    [InlineData("\"quoted\"", FieldNormalization.StripQuotes, "quoted")]
    [InlineData("MiXeD", FieldNormalization.LowerCase, "mixed")]
    [InlineData("", FieldNormalization.TrimUpper, "")]
    public void Normalize_AppliesTrimThenQuotesThenCase(string value, FieldNormalization rule, string expected)
        => Assert.Equal(expected, OptionFieldNormalizer.Normalize(value, rule));

    [Fact]
    public void Apply_NormalisesEveryDescribedText_AsThePagesUsedTo()
    {
        var speech = new SpeechOptions
        {
            PilotSeat = " Right ",
            AsrBaseUrl = " http://10.0.1.31:8000 ",
            KokoroBaseUrl = " http://k ",
            FoVoice = " kokoro:bm_george ",
            CacheFolder = " C:\\cache ",
        };

        OptionFieldNormalizer.Apply(speech);

        Assert.Equal("right", speech.PilotSeat);
        Assert.Equal("http://10.0.1.31:8000", speech.AsrBaseUrl);
        Assert.Equal("http://k", speech.KokoroBaseUrl);
        Assert.Equal("kokoro:bm_george", speech.FoVoice);
        Assert.Equal("C:\\cache", speech.CacheFolder);
    }

    [Fact]
    public void Apply_UpperCasesTheProcedureIdentifiers_AndStripsPathQuotes()
    {
        var briefing = new BriefingOptions { DepartureAirport = " egll ", ArrivalRunway = "27l", DfdPath = "\"C:\\dfd\\e_dfd.s3db\"", LlmBaseUrl = " http://llm " };

        OptionFieldNormalizer.Apply(briefing);

        Assert.Equal("EGLL", briefing.DepartureAirport);
        Assert.Equal("27L", briefing.ArrivalRunway);
        Assert.Equal("C:\\dfd\\e_dfd.s3db", briefing.DfdPath);
        Assert.Equal("http://llm", briefing.LlmBaseUrl);
    }

    [Fact]
    public void Apply_LeavesUndescribedAndNoneTexts_Alone()
    {
        var speech = new SpeechOptions { PttKey = "  F12 ", AsrHallucinationPhrases = [" thank you "] };
        var groundCrew = new GroundCrewOptions { HailReplyText = "  Go ahead  " };

        OptionFieldNormalizer.Apply(speech);
        OptionFieldNormalizer.Apply(groundCrew);

        Assert.Equal("  F12 ", speech.PttKey);
        Assert.Equal(" thank you ", speech.AsrHallucinationPhrases[0]);
        Assert.Equal("  Go ahead  ", groundCrew.HailReplyText); // spoken wording saves as typed
    }

    [Fact]
    public void Apply_ReachesNestedBlocks_AndListItems()
    {
        var wake = new WakeTargetsOptions { Targets = [new WakeTarget { Name = " LLM box ", MacAddress = " aa:bb:cc:dd:ee:ff ", BroadcastAddress = " 192.168.1.255 " }] };
        var sayIntentions = new SayIntentionsOptions { ApiKeySource = " Manual ", ManualApiKey = " k " };

        OptionFieldNormalizer.Apply(wake);
        OptionFieldNormalizer.Apply(sayIntentions);

        Assert.Equal("LLM box", wake.Targets[0].Name);
        Assert.Equal("aa:bb:cc:dd:ee:ff", wake.Targets[0].MacAddress);
        Assert.Equal("192.168.1.255", wake.Targets[0].BroadcastAddress);
        Assert.Equal("manual", sayIntentions.ApiKeySource);
        Assert.Equal("k", sayIntentions.ManualApiKey);
    }

    [Fact]
    public void Apply_SkipsANullOptionalString()
    {
        var prosim = new ProsimOptions { ApiKey = null, Host = " sim-pc " };

        OptionFieldNormalizer.Apply(prosim);

        Assert.Null(prosim.ApiKey);
        Assert.Equal("sim-pc", prosim.Host);
    }

    [Fact]
    public void Draft_NormalizesItsClone_NotTheLiveOptions()
    {
        var live = new GsxOptions { ArrivalGate = " B12 " };
        var draft = new OptionsDraft<GsxOptions>(() => live);
        draft.Value.GsxProfileFolder = "\"C:\\GSX\"";

        draft.Normalize();

        Assert.Equal("B12", draft.Value.ArrivalGate);
        Assert.Equal("C:\\GSX", draft.Value.GsxProfileFolder);
        Assert.Equal(" B12 ", live.ArrivalGate);
    }

    [Fact]
    public void Draft_RaisesEdited_WhenAFieldMarksIt()
    {
        var draft = new OptionsDraft<GsxOptions>(() => new GsxOptions());
        var raised = 0;
        draft.Edited += () => raised++;

        draft.MarkEdited();

        Assert.Equal(1, raised);
    }
}
