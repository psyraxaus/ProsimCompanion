using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Speech.Recognition;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>The two LAN ASR wire shapes must land on the same normalised transcript, and the
/// wrong-flavour failure mode (2026-08-29) must be detectable.</summary>
public sealed class AsrServerApiTests
{
    [Theory]
    [InlineData(AsrApiKind.FasterWhisper, "/transcribe", "hotwords")]
    [InlineData(AsrApiKind.WhisperCpp, "/v1/audio/transcriptions", "prompt")]
    public void Paths_and_bias_field_follow_the_flavour(AsrApiKind api, string path, string biasField)
    {
        Assert.Equal(path, AsrServerApi.TranscribePath(api));
        Assert.Equal(biasField, AsrServerApi.BiasField(api));
    }

    [Fact]
    public void WhisperCpp_asks_for_verbose_json_at_temperature_zero()
    {
        var fields = AsrServerApi.ExtraFields(AsrApiKind.WhisperCpp);

        Assert.Contains(fields, f => f.Key == "response_format" && f.Value == "verbose_json");
        Assert.Contains(fields, f => f.Key == "temperature" && f.Value == "0");
        Assert.Empty(AsrServerApi.ExtraFields(AsrApiKind.FasterWhisper));
    }

    [Theory]
    [InlineData(AsrApiKind.FasterWhisper, 200, true)]
    [InlineData(AsrApiKind.FasterWhisper, 404, false)]
    [InlineData(AsrApiKind.WhisperCpp, 200, true)]
    [InlineData(AsrApiKind.WhisperCpp, 404, true)]
    [InlineData(AsrApiKind.WhisperCpp, 503, false)]
    public void Readiness_tolerates_missing_health_route_only_for_whisper_cpp(AsrApiKind api, int status, bool ready)
        => Assert.Equal(ready, AsrServerApi.IsReady(api, status));

    [Fact]
    public void FasterWhisper_flat_reply_maps_straight_through()
    {
        const string json = """{"text":" gear up ","confidence":0.83,"no_speech_prob":0.02,"duration":1.2}""";

        var transcript = AsrServerApi.Parse(AsrApiKind.FasterWhisper, json);

        Assert.NotNull(transcript);
        Assert.Equal("gear up", transcript.Text);
        Assert.Equal(0.83, transcript.Confidence!.Value, 6);
        Assert.Equal(0.02, transcript.NoSpeechProb!.Value, 6);
    }

    [Fact]
    public void WhisperCpp_verbose_json_derives_confidence_like_the_wrapper()
    {
        // Two segments: 1 s at logprob -0.2, 3 s at logprob -0.6 → weighted -0.5 → exp = 0.6065.
        const string json = """
            {"task":"transcribe","language":"english","duration":4.0,"text":" flaps one\n",
             "segments":[
               {"id":0,"text":" flaps","start":0.0,"end":1.0,"avg_logprob":-0.2,"no_speech_prob":0.01},
               {"id":1,"text":" one","start":1.0,"end":4.0,"avg_logprob":-0.6,"no_speech_prob":0.30}]}
            """;

        var transcript = AsrServerApi.Parse(AsrApiKind.WhisperCpp, json);

        Assert.NotNull(transcript);
        Assert.Equal("flaps one", transcript.Text);
        Assert.Equal(Math.Exp(-0.5), transcript.Confidence!.Value, 6);
        Assert.Equal(0.30, transcript.NoSpeechProb!.Value, 6);
    }

    [Fact]
    public void WhisperCpp_with_no_segments_is_silence()
    {
        var transcript = AsrServerApi.Parse(AsrApiKind.WhisperCpp, """{"text":"","segments":[]}""");

        Assert.NotNull(transcript);
        Assert.Equal("", transcript.Text);
        Assert.Equal(0.0, transcript.Confidence);
        Assert.Equal(1.0, transcript.NoSpeechProb);
    }

    [Fact]
    public void WhisperCpp_plain_json_reply_keeps_text_with_no_grades()
    {
        var transcript = AsrServerApi.Parse(AsrApiKind.WhisperCpp, """{"text":" (beep)\n"}""");

        Assert.NotNull(transcript);
        Assert.Equal("(beep)", transcript.Text);
        Assert.Null(transcript.Confidence);
        Assert.Null(transcript.NoSpeechProb);
    }

    [Theory]
    [InlineData("")]
    [InlineData("File Not Found (/transcribe)")]
    [InlineData("[1,2,3]")]
    public void Non_json_or_non_object_bodies_are_reported_as_unparseable(string body)
    {
        Assert.Null(AsrServerApi.Parse(AsrApiKind.FasterWhisper, body));
        Assert.Null(AsrServerApi.Parse(AsrApiKind.WhisperCpp, body));
    }
}
