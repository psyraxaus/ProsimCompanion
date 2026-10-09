using ProsimCompanion.Audio.Mixer;
using Xunit;

namespace ProsimCompanion.Core.Tests.Mixer;

public sealed class MixerFrameTests
{
    [Fact]
    public void BuildHello_CarriesTokenAndProtocol_RedactedCopyDoesNot()
    {
        var hello = MixerFrame.BuildHello("cc4e-secret");

        Assert.Contains("\"op\":\"hello\"", hello, StringComparison.Ordinal);
        Assert.Contains("\"token\":\"cc4e-secret\"", hello, StringComparison.Ordinal);
        Assert.Contains("\"protocol\":1", hello, StringComparison.Ordinal);
        Assert.DoesNotContain("cc4e", MixerFrame.RedactedHello(), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSet_UsesTheSpecFieldNames_AndInvariantNumbers()
    {
        Assert.Equal("{\"op\":\"set\",\"id\":\"s1\",\"param\":\"Strip[0].Gain\",\"value\":-6.5}", MixerFrame.BuildSet("s1", "Strip[0].Gain", -6.5));
        Assert.Equal("{\"op\":\"watch\",\"params\":[\"Strip[0].Gain\",\"Bus[1].Mute\"]}", MixerFrame.BuildWatch(["Strip[0].Gain", "Bus[1].Mute"]));
    }

    [Fact]
    public void Parse_Welcome_WithAndWithoutVoicemeeter()
    {
        var up = Assert.IsType<MixerWelcomeFrame>(MixerFrame.Parse(
            "{\"op\":\"welcome\",\"protocol\":1,\"voicemeeter\":{\"connected\":true,\"kind\":\"potato\",\"version\":\"3.1.1.2\"}}"));
        Assert.Equal(1, up.Protocol);
        Assert.True(up.Voicemeeter.Connected);
        Assert.Equal("potato", up.Voicemeeter.Kind);
        Assert.Equal("3.1.1.2", up.Voicemeeter.Version);

        var down = Assert.IsType<MixerWelcomeFrame>(MixerFrame.Parse("{\"op\":\"welcome\",\"protocol\":1,\"voicemeeter\":{\"connected\":false}}"));
        Assert.False(down.Voicemeeter.Connected);
        Assert.Null(down.Voicemeeter.Kind);
    }

    [Fact]
    public void Parse_Snapshot_KeepsNumbersAndTextInTheirNaturalType_AndErrors()
    {
        var frame = Assert.IsType<MixerValuesFrame>(MixerFrame.Parse(
            "{\"op\":\"snapshot\",\"values\":{\"Strip[0].Gain\":-6.0,\"Strip[0].Label\":\"Mic\"},\"errors\":{\"Strip[9].Gain\":\"unknown parameter\"}}"));

        Assert.Equal(-6.0, frame.Values["Strip[0].Gain"].Number);
        Assert.Equal("Mic", frame.Values["Strip[0].Label"].Text);
        Assert.Equal("unknown parameter", frame.Errors["Strip[9].Gain"]);
    }

    [Fact]
    public void Parse_Changed_Result_Error_Pong()
    {
        var changed = Assert.IsType<MixerChangedFrame>(MixerFrame.Parse("{\"op\":\"changed\",\"param\":\"Bus[1].Mute\",\"value\":1}"));
        Assert.Equal("Bus[1].Mute", changed.Parameter);
        Assert.Equal(1, changed.Value.Number);

        var ok = Assert.IsType<MixerResultFrame>(MixerFrame.Parse("{\"op\":\"result\",\"id\":\"abc\",\"ok\":true}"));
        Assert.Equal("abc", ok.Id);
        Assert.True(ok.Ok);

        var failed = Assert.IsType<MixerResultFrame>(MixerFrame.Parse("{\"op\":\"result\",\"id\":\"abc\",\"ok\":false,\"error\":\"voicemeeter not running\"}"));
        Assert.False(failed.Ok);
        Assert.Equal("voicemeeter not running", failed.Error);

        Assert.Equal("invalid parameter name 'x'", Assert.IsType<MixerErrorFrame>(MixerFrame.Parse("{\"op\":\"error\",\"error\":\"invalid parameter name 'x'\"}")).Error);
        Assert.IsType<MixerPongFrame>(MixerFrame.Parse("{\"op\":\"pong\"}"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"nope\":1}")]
    [InlineData("{\"op\":\"levels\",\"values\":[]}")]
    [InlineData("{\"op\":\"changed\",\"value\":1}")]
    public void Parse_IgnoresWhatItDoesNotConsume(string text)
    {
        Assert.Null(MixerFrame.Parse(text));
    }
}
