using ProsimCompanion.Core.Configuration;
using Xunit;

namespace ProsimCompanion.Core.Tests.Configuration.Fields;

/// <summary>The one enum ↔ settings-text bridge (C3) over the four enums the pages used to
/// bridge by hand (LlmApi, AsrApi, ProcedureSource, SterileNormalPolicy) and the audio ones.</summary>
public sealed class EnumTextTests
{
    [Theory]
    [InlineData(LlmApiKind.OpenAi, "openAi")]
    [InlineData(LlmApiKind.Ollama, "ollama")]
    [InlineData(AsrApiKind.FasterWhisper, "fasterWhisper")]
    [InlineData(AsrApiKind.WhisperCpp, "whisperCpp")]
    [InlineData(ProcedureSourceMode.Auto, "auto")]
    [InlineData(ProcedureSourceMode.FlightJson, "flightJson")]
    [InlineData(SterileNormalPolicy.Allow, "allow")]
    [InlineData(SterileNormalPolicy.Suppress, "suppress")]
    [InlineData(AudioBackend.VoiceMeeter, "voiceMeeter")]
    [InlineData(AcpSide.FirstOfficer, "firstOfficer")]
    [InlineData(NotificationTargetKind.Ntfy, "ntfy")]
    public void ToJsonName_IsCamelCase(object value, string expected)
        => Assert.Equal(expected, EnumText.ToJsonName(value));

    [Theory]
    [InlineData("openAi", LlmApiKind.OpenAi)]
    [InlineData("OPENAI", LlmApiKind.OpenAi)]
    [InlineData(" ollama ", LlmApiKind.Ollama)]
    [InlineData("Ollama", LlmApiKind.Ollama)]
    public void Parse_IsCaseInsensitive_LikeTheConfigBinder(string text, LlmApiKind expected)
        => Assert.Equal(expected, EnumText.Parse(text, LlmApiKind.Ollama));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("gpt")]
    [InlineData("7")]
    public void Parse_FallsBack_ForANameTheEnumLacks(string? text)
    {
        Assert.Equal(LlmApiKind.OpenAi, EnumText.Parse(text, LlmApiKind.OpenAi));
        Assert.Equal(AsrApiKind.FasterWhisper, EnumText.Parse(text, AsrApiKind.FasterWhisper));
        Assert.Equal(ProcedureSourceMode.Auto, EnumText.Parse(text, ProcedureSourceMode.Auto));
        Assert.Equal(SterileNormalPolicy.Allow, EnumText.Parse(text, SterileNormalPolicy.Allow));
        Assert.False(EnumText.TryParse(typeof(LlmApiKind), text, out _));
    }

    [Fact]
    public void Parse_RoundTripsEveryMemberOfTheFourBridgedEnums()
    {
        foreach (var value in Enum.GetValues<SterileNormalPolicy>())
        {
            Assert.Equal(value, EnumText.Parse(EnumText.ToJsonName(value), SterileNormalPolicy.Defer));
        }

        foreach (var value in Enum.GetValues<ProcedureSourceMode>())
        {
            Assert.Equal(value, EnumText.Parse(EnumText.ToJsonName(value), ProcedureSourceMode.Manual));
        }

        foreach (var value in Enum.GetValues<AsrApiKind>())
        {
            Assert.Equal(value, EnumText.Parse(EnumText.ToJsonName(value), AsrApiKind.WhisperCpp));
        }

        foreach (var value in Enum.GetValues<LlmApiKind>())
        {
            Assert.Equal(value, EnumText.Parse(EnumText.ToJsonName(value), LlmApiKind.Ollama));
        }
    }
}
