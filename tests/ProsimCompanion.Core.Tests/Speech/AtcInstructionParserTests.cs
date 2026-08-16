using ProsimCompanion.Speech.Fcu;
using ProsimCompanion.Speech.Radios;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

public sealed class AtcInstructionParserTests
{
    [Fact]
    public void Heading_DigitWords()
    {
        var i = AtcInstructionParser.Parse("fly heading two five zero");
        Assert.Equal(FcuInstructionType.SetValue, i.Type);
        Assert.Equal(FcuField.Heading, i.Field);
        Assert.Equal(250, i.Value);
    }

    [Fact]
    public void FlightLevel_DigitWords_TimesHundred()
    {
        var i = AtcInstructionParser.Parse("descend flight level three five zero");
        Assert.Equal(FcuField.Altitude, i.Field);
        Assert.Equal(35_000, i.Value);
    }

    [Fact]
    public void FlightLevel_ArabicNumerals_BackstopWorks()
    {
        // The predecessor's unclosed gap: whisper emits digits.
        var i = AtcInstructionParser.Parse("descend flight level 120");
        Assert.Equal(FcuInstructionType.SetValue, i.Type);
        Assert.Equal(12_000, i.Value);
    }

    [Fact]
    public void Altitude_MagnitudeWords()
    {
        var i = AtcInstructionParser.Parse("climb altitude eleven thousand feet");
        Assert.Equal(FcuField.Altitude, i.Field);
        Assert.Equal(11_000, i.Value);
    }

    [Fact]
    public void VerticalSpeed_DescendIsNegative()
    {
        var i = AtcInstructionParser.Parse("descend at vertical speed one thousand per minute");
        Assert.Equal(FcuField.VerticalSpeed, i.Field);
        Assert.Equal(-1000, i.Value);
    }

    [Fact]
    public void Speed_OutOfRange_QueriesNeverClamps()
    {
        var i = AtcInstructionParser.Parse("reduce speed four five zero knots");
        Assert.Equal(FcuInstructionType.Query, i.Type);
        Assert.Null(i.Value);
    }

    [Fact]
    public void Qnh_IsNeverAnFcuAction()
        => Assert.Equal(FcuInstructionType.Unknown, AtcInstructionParser.Parse("QNH one zero one three").Type);

    [Fact]
    public void Conditional_RelayOnly()
        => Assert.Equal(FcuInstructionType.Conditional,
            AtcInstructionParser.Parse("after passing DOSEL descend flight level one two zero").Type);

    [Theory]
    [InlineData("after start checklist")]
    [InlineData("after takeoff checklist")]
    [InlineData("after landing checklist")]
    [InlineData("when ready")]
    public void ConditionWordAlone_IsNotAnInstruction(string utterance)
        // Issue #47: "after start checklist" was swallowed as "Copied — conditional" and the
        // checklist never ran. A condition word needs FCU content to classify.
        => Assert.Equal(FcuInstructionType.Unknown, AtcInstructionParser.Parse(utterance).Type);

    [Theory]
    [InlineData("when established on the localizer descend two thousand feet")]
    [InlineData("once passing three thousand climb flight level eight zero")]
    public void ConditionWithFcuContent_StillConditional(string utterance)
        => Assert.Equal(FcuInstructionType.Conditional, AtcInstructionParser.Parse(utterance).Type);

    [Theory]
    [InlineData("flaps two")]
    [InlineData("flaps one")]
    [InlineData("starting engine one")]
    [InlineData("starting engine two")]
    [InlineData("engine two start")]
    public void BareNumberWithoutFcuContent_IsUnknown_NeverAWhichFieldQuery(string utterance)
        // Issue #66: the bare-number fallback fired unconditionally, so every flap call and
        // engine-start call all flight was consumed by the FCU and answered
        // "Say again — which field?". The clarifier now requires actual FCU content.
        => Assert.Equal(FcuInstructionType.Unknown, AtcInstructionParser.Parse(utterance).Type);

    [Fact]
    public void NumberWithFcuContentButNoField_StillClarifies()
    {
        // "descend one two zero" names no settable field, but it IS an FCU-shaped
        // instruction — the "which field?" query stays useful exactly here.
        var i = AtcInstructionParser.Parse("descend one two zero");
        Assert.Equal(FcuInstructionType.Query, i.Type);
        Assert.Equal("which field?", i.Reason);
    }

    [Fact]
    public void OpenDescent_NowParses()
    {
        // Dead grammar phrase in the predecessor — fixed.
        var i = AtcInstructionParser.Parse("open descent");
        Assert.Equal(FcuInstructionType.Selected, i.Type);
        Assert.Equal(FcuField.Altitude, i.Field);
    }

    [Fact]
    public void Engagements_Classify()
    {
        Assert.Equal(FcuInstructionType.Autopilot, AtcInstructionParser.Parse("engage autopilot one").Type);
        Assert.Equal(FcuInstructionType.Approach, AtcInstructionParser.Parse("arm approach").Type);
        Assert.Equal(FcuInstructionType.Managed,
            AtcInstructionParser.Parse("resume own navigation").Type);
    }

    [Fact]
    public void Altitude_LeadingTo_IsNotADigit()
    {
        // "to" maps to 2 for digit strings ("one to tree" = 123) but must not join a
        // magnitude run: this used to parse as (2+3)*1000 = 5000.
        var i = AtcInstructionParser.Parse("descend to three thousand feet");
        Assert.Equal(FcuField.Altitude, i.Field);
        Assert.Equal(3000, i.Value);
    }

    [Fact]
    public void Altitude_TrailingFor_IsNotADigit()
    {
        // Used to parse as 2000 + 4 = 2004 feet.
        var i = AtcInstructionParser.Parse("descend two thousand for traffic");
        Assert.Equal(FcuField.Altitude, i.Field);
        Assert.Equal(2000, i.Value);
    }

    [Fact]
    public void Heading_InteriorFor_StillADigit()
    {
        var i = AtcInstructionParser.Parse("fly heading two for zero");
        Assert.Equal(FcuField.Heading, i.Field);
        Assert.Equal(240, i.Value);
    }

    [Theory]
    [InlineData("tune box one 121.5", 121_500)]
    [InlineData("set standby one one eight decimal one zero", 118_100)]
    [InlineData("tune standby 136.975", 136_975)]
    public void FrequencyParser_ValidChannels(string utterance, int expectedKhz)
        => Assert.Equal(expectedKhz, FrequencyParser.Parse(utterance));

    [Theory]
    [InlineData("set box standby one one eight decimal one zero", 118_100)]
    [InlineData("tune box 121.5", 121_500)]
    public void FrequencyParser_BoxStrippedForm_Parses(string utterance, int expectedKhz)
    {
        // RadioExecutor strips "box one"/"box two" before parsing — the box digit must not
        // become the "first number run" and shadow the spoken frequency.
        Assert.Equal(expectedKhz, FrequencyParser.Parse(utterance));
    }

    [Theory]
    [InlineData("tune box one 117.5")]    // below band
    [InlineData("tune box one 121.512")]  // impossible channel
    [InlineData("set standby")]           // no number
    public void FrequencyParser_RejectsInvalid(string utterance)
        => Assert.Null(FrequencyParser.Parse(utterance));
}
