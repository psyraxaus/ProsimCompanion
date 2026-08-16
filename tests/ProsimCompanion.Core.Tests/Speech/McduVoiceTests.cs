using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Speech.Briefings;
using ProsimCompanion.Speech.Mcdu;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>MCDU voice features: the generated arrival-phrase grammar, runway parsing, the
/// display parser, and the pure spoken-composition helpers — all testable without ProSim.</summary>
public sealed class McduVoiceTests
{
    // ---- arrival phrase generation ----

    [Fact]
    public void ArrivalPhrases_Generates288UniquePhrases()
    {
        // 36 runway numbers × 4 side variants × 2 prefixes.
        var phrases = McduArrivalPhrases.Phrases;
        Assert.Equal(288, phrases.Count);
        Assert.Equal(288, phrases.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("change arrival runway zero four left", "04L")]
    [InlineData("change to runway zero four left", "04L")]
    [InlineData("change arrival runway three six", "36")]
    [InlineData("change to runway two seven center", "27C")]
    [InlineData("change arrival runway one six right", "16R")]
    [InlineData("change to runway zero one", "01")]
    public void ArrivalPhrases_ResolveToRunway(string phrase, string expected)
    {
        Assert.True(McduArrivalPhrases.TryResolve(phrase, out var runway));
        Assert.Equal(expected, runway);
    }

    [Theory]
    [InlineData("change arrival runway zero zero")]         // runway 00 doesn't exist
    [InlineData("change arrival runway three seven")]       // 37 doesn't exist
    [InlineData("change arrival runway zero four left please")] // exact match only — never a guess
    [InlineData("set heading two seven zero")]
    [InlineData("")]
    public void ArrivalPhrases_RejectNonPhrases(string text)
        => Assert.False(McduArrivalPhrases.TryResolve(text, out _));

    [Fact]
    public void ArrivalPhrases_EveryPhraseResolvesToAPlausibleRunway()
    {
        foreach (var phrase in McduArrivalPhrases.Phrases)
        {
            Assert.True(McduArrivalPhrases.TryResolve(phrase, out var runway));
            var normalized = McduSpeech.NormalizeRunway(runway);
            Assert.Equal(runway, normalized); // already canonical
            var number = int.Parse(runway[..2], System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(number, 1, 36);
        }
    }

    // ---- runway normalization ----

    [Theory]
    [InlineData("04L", "04L")]
    [InlineData("4L", "04L")]
    [InlineData("rw16r", "16R")]
    [InlineData("RW04", "04")]
    [InlineData("360", "36")]     // >2 digits truncates to two
    [InlineData("27X", "27")]     // invalid side letter dropped
    [InlineData("9", "09")]
    public void NormalizeRunway_CanonicalForms(string input, string expected)
        => Assert.Equal(expected, McduSpeech.NormalizeRunway(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("L")]
    [InlineData("RWL")]
    public void NormalizeRunway_RejectsUnparseable(string? input)
        => Assert.Null(McduSpeech.NormalizeRunway(input));

    // ---- spoken helpers ----

    [Theory]
    [InlineData("04L", "runway zero four left")]
    [InlineData("36", "runway three six")]
    [InlineData("27C", "runway two seven center")]
    [InlineData("09R", "runway zero niner right")]
    [InlineData(null, "the arrival runway")]
    [InlineData("", "the arrival runway")]
    public void SpeakRunway_DeterministicForms(string? runway, string expected)
        => Assert.Equal(expected, McduSpeech.SpeakRunway(runway));

    [Theory]
    [InlineData("110.30", "one one zero decimal three zero")]
    [InlineData("109.90", "one zero niner decimal niner zero")]
    public void SpeakFrequency_IcaoDigits(string frequency, string expected)
        => Assert.Equal(expected, McduSpeech.SpeakFrequency(frequency));

    [Fact]
    public void SpeakLetters_SpellsIdent()
        => Assert.Equal("I A D F", McduSpeech.SpeakLetters("iadf"));

    // ---- display parser ----

    private const string SampleXml =
        "<root>"
        + "<title>s F-PLN £¢</title>"
        + "<line>w FROM</line><line>gKODAP   1234  250/</line>"
        + "<line>w</line><line>c[    ]</line>"
        + "<line></line><line></line>"
        + "<line></line><line></line>"
        + "<line></line><line></line>"
        + "<line>w DEST</line><line>gYSSY04L  0855</line>"
        + "<scratchpad>a110.30</scratchpad>"
        + "</root>";

    [Fact]
    public void Parser_StripsColourCodesAndReadsRows()
    {
        var page = McduDisplayParser.Parse(SampleXml);
        Assert.Equal("F-PLN", page.Title);
        Assert.Equal(6, page.Rows.Count);
        Assert.Equal("FROM", page.Rows[0].Label);
        Assert.Equal("KODAP   1234  250/", page.Rows[0].Data);
        Assert.Equal("YSSY04L  0855", page.Rows[5].Data);
        Assert.Equal("110.30", page.Scratchpad);
        Assert.False(page.IsTemporary);
    }

    [Fact]
    public void Parser_ScrollFlagsComeFromRawTitleGlyphs()
    {
        // £ = up available, ¢ = down available — they live in the RAW title only.
        var page = McduDisplayParser.Parse(SampleXml);
        Assert.True(page.CanScrollUp);
        Assert.True(page.CanScrollDown);
        Assert.DoesNotContain('£', page.Title);
        Assert.DoesNotContain('¢', page.Title);
    }

    [Fact]
    public void Parser_DetectsTemporaryPlan()
    {
        var page = McduDisplayParser.Parse(
            "<root><title>yTMPY F-PLN</title><line>w</line><line>gDATA</line><scratchpad></scratchpad></root>");
        Assert.True(page.IsTemporary);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<not-xml")]
    [InlineData("plain text")]
    public void Parser_NeverThrows_BadInputYieldsEmpty(string? xml)
    {
        var page = McduDisplayParser.Parse(xml);
        Assert.False(page.HasData);
        Assert.Empty(page.Rows);
    }

    [Theory]
    [InlineData("<ACT  110.30/IAD  >", "ACT, 110.30/IAD")]
    [InlineData("[    ]  ----", "")]
    [InlineData("", "")]
    public void Parser_Speakable_DropsPlaceholders(string line, string expected)
        => Assert.Equal(expected, McduDisplayParser.Speakable(line));

    // ---- read-back composition ----

    [Fact]
    public void BuildReadback_BlankPage()
        => Assert.Equal("The M C D U is blank or unavailable.", McduReader.BuildReadback(McduPage.Empty));

    [Fact]
    public void BuildReadback_TitleRowsAndScratchpad()
    {
        var page = McduDisplayParser.Parse(SampleXml);
        var spoken = McduReader.BuildReadback(page);
        Assert.StartsWith("F-PLN.", spoken, StringComparison.Ordinal);
        Assert.Contains("KODAP", spoken, StringComparison.Ordinal);
        Assert.Contains("scratchpad, 110.30", spoken, StringComparison.Ordinal);
        Assert.EndsWith(".", spoken, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildReadback_TemporaryPlanIsAnnouncedFirst()
    {
        var page = McduDisplayParser.Parse(
            "<root><title>yTMPY F-PLN</title><line>w</line><line>gDATA</line><scratchpad></scratchpad></root>");
        Assert.StartsWith("Temporary flight plan.", McduReader.BuildReadback(page), StringComparison.Ordinal);
    }

    // ---- approach picking / on-screen matching ----

    [Fact]
    public void PickApproach_PrefersIls_ThenVariant()
    {
        var rnav = new ApproachOption("R04LZ", "RNAV", 'Z');
        var ilsY = new ApproachOption("I04LY", "ILS", 'Y');
        var ilsZ = new ApproachOption("I04LZ", "ILS", 'Z');
        var options = new[] { rnav, ilsY, ilsZ };

        Assert.Same(ilsY, McduArrivalChanger.PickApproach(options, null)); // first ILS wins
        Assert.Same(ilsZ, McduArrivalChanger.PickApproach(options, 'z')); // ILS at the variant
        Assert.Same(rnav, McduArrivalChanger.PickApproach([rnav], null)); // fall back to ranked first
        Assert.Null(McduArrivalChanger.PickApproach([], null));
    }

    [Theory]
    [InlineData("ILSY04L", "ILS", 'Y', true)]   // DFD "I04LY" shows as "ILSY04L"
    [InlineData("ILS04L", "ILS", null, true)]
    [InlineData("RNAV04L", "RNP", null, true)]  // RNP shows as RNAV on the box
    [InlineData("ILSZ04L", "ILS", 'Y', false)]  // wrong variant
    [InlineData("ILSY04R", "ILS", 'Y', false)]  // wrong runway
    [InlineData("VOR04L", "ILS", null, false)]  // wrong kind
    public void MatchesApproach_ByOnScreenParts(string rowText, string kind, char? variant, bool expected)
    {
        var row = new McduRow(3, "", rowText);
        var target = new ApproachOption("X", kind, variant);
        Assert.Equal(expected, McduArrivalChanger.MatchesApproach(row, target, "04L"));
    }

    [Fact]
    public void RowsContain_IgnoresSpacing()
    {
        var page = McduDisplayParser.Parse(
            "<root><title>wRADIO NAV</title><line>wILS/FREQ</line><line>gIAD/ 110.30</line><scratchpad></scratchpad></root>");
        Assert.True(McduRadNavTuner.RowsContain(page, "110.30"));
        Assert.True(McduRadNavTuner.RowsContain(page, "IAD"));
        Assert.False(McduRadNavTuner.RowsContain(page, "109.10"));
    }

    // ---- reader dispatch + degraded mode (Moq'd dataref seam) ----

    [Fact]
    public void Reader_DegradesToSpokenExplanation_WhenProsimAbsent()
    {
        var (reader, arbiter) = MakeReader(displayXml: null);

        Assert.True(reader.TryHandle("read the MCDU"));
        var request = Assert.Single(arbiter.Requests);
        Assert.Contains("ProSim", request.Text, StringComparison.Ordinal);
        Assert.Equal(ProsimCompanion.Speech.Arbiter.SpeechPriority.Normal, request.Priority);
        Assert.Equal("mcdu", request.Tag);
    }

    [Fact]
    public void Reader_ReadsBackThePage()
    {
        var (reader, arbiter) = MakeReader(SampleXml);

        Assert.True(reader.TryHandle("what's on the mcdu"));
        var request = Assert.Single(arbiter.Requests);
        Assert.Contains("F-PLN", request.Text, StringComparison.Ordinal);
        Assert.Contains("KODAP", request.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_ScratchpadPhraseReadsOnlyTheScratchpad()
    {
        var (reader, arbiter) = MakeReader(SampleXml);

        Assert.True(reader.TryHandle("read the scratchpad"));
        var request = Assert.Single(arbiter.Requests);
        Assert.Equal("Scratchpad, 110.30.", request.Text);
    }

    [Fact]
    public void Reader_IgnoresOtherUtterances_AndHonoursTheEnabledGate()
    {
        var (reader, arbiter) = MakeReader(SampleXml);
        Assert.False(reader.TryHandle("set heading two seven zero"));
        Assert.Empty(arbiter.Requests);

        var (disabled, disabledArbiter) = MakeReader(SampleXml, enabled: false);
        Assert.False(disabled.TryHandle("read the mcdu"));
        Assert.Empty(disabledArbiter.Requests);
    }

    private static (McduReader Reader, FakeArbiter Arbiter) MakeReader(string? displayXml, bool enabled = true)
    {
        var subscription = new Mock<IDataRefSubscription>();
        subscription.SetupGet(s => s.Name).Returns(ProsimDataRefNames.Mcdu2Display.Name);
        subscription.SetupGet(s => s.RawValue).Returns(displayXml);
        subscription.SetupGet(s => s.IsStale).Returns(false);
        subscription.Setup(s => s.GetValue<string?>(null)).Returns(displayXml);

        var dataRefs = new Mock<IProsimDataRefs>();
        dataRefs.Setup(d => d.SubscribeDynamic(ProsimDataRefNames.Mcdu2Display.Name, It.IsAny<DataRefTier>()))
            .Returns(subscription.Object);

        var options = new Mock<IOptionsMonitor<McduOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(new McduOptions { Enabled = enabled });

        var arbiter = new FakeArbiter();
        var reader = new McduReader(
            dataRefs.Object, options.Object, arbiter,
            SpeechTestSupport.TempEventLog(), NullLogger<McduReader>.Instance);
        return (reader, arbiter);
    }
}
