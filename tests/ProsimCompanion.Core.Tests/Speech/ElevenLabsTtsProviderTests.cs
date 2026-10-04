using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NAudio.MediaFoundation;
using NAudio.Wave;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Speech.Tts;
using Xunit;
using Xunit.Abstractions;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>
/// The ElevenLabs provider against a stubbed handler: request shape, the pre-call guards
/// (cap, budget, rejected-key latch) that must never reach the network, and the two
/// response-to-WAV paths. No real key is ever needed.
/// </summary>
public sealed class ElevenLabsTtsProviderTests : IDisposable
{
    // Made at run time: a fixed text here reads as a hardcoded credential to the code scanner.
    private static readonly string ApiKey = Guid.NewGuid().ToString("N");
    private const string VoiceId = "voice1";

    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), $"elevenlabs-tests-{Guid.NewGuid():N}");
    private readonly ITestOutputHelper _output;
    private Action<SpeechOptions, string?>? _optionsListener;

    public ElevenLabsTtsProviderTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_cacheDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Records every request and answers with a fixed status + body.</summary>
    private sealed class StubHandler(HttpStatusCode status, byte[] body) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];
        public HttpStatusCode Status { get; set; } = status;
        public byte[] Body { get; set; } = body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(Status) { Content = new ByteArrayContent(Body) };
        }
    }

    private SpeechOptions Options(string format = "pcm_24000", bool withApiKey = true, string voice = VoiceId)
        => new()
        {
            ElevenLabsApiKey = withApiKey ? ApiKey : string.Empty,
            ElevenLabsVoiceId = voice,
            ElevenLabsOutputFormat = format,
            CacheFolder = _cacheDir,
        };

    private ElevenLabsTtsProvider Provider(SpeechOptions options, HttpMessageHandler handler, TtsUsageTracker? usage = null)
    {
        var monitor = new Mock<IOptionsMonitor<SpeechOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(options);
        monitor.Setup(m => m.OnChange(It.IsAny<Action<SpeechOptions, string?>>()))
            .Callback<Action<SpeechOptions, string?>>(listener => _optionsListener = listener)
            .Returns(Mock.Of<IDisposable>());
        return new ElevenLabsTtsProvider(
            monitor.Object,
            new TtsDiskCache(NullLogger<TtsDiskCache>.Instance),
            usage ?? new TtsUsageTracker(NullLogger<TtsUsageTracker>.Instance),
            NullLogger<ElevenLabsTtsProvider>.Instance,
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });
    }

    private static byte[] FakePcm(int samples = 480)
    {
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(i % 200 * 100));
        }

        return pcm;
    }

    [Fact]
    public void Unconfigured_WithoutKeyOrVoice_IsNotConfigured()
    {
        var handler = new StubHandler(HttpStatusCode.OK, FakePcm());

        Assert.False(Provider(Options(withApiKey: false), handler).IsConfigured);
        Assert.False(Provider(Options(voice: " "), handler).IsConfigured);
        Assert.True(Provider(Options(), handler).IsConfigured);
    }

    [Fact]
    public async Task Request_CarriesKeyHeader_PathVoice_Format_AndSpokenFormBody()
    {
        var handler = new StubHandler(HttpStatusCode.OK, FakePcm());
        var provider = Provider(Options(), handler);

        await provider.SynthesizeAsync("gear down", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.elevenlabs.io/v1/text-to-speech/voice1?output_format=pcm_24000", request.RequestUri!.ToString());
        Assert.Equal(ApiKey, Assert.Single(request.Headers.GetValues("xi-api-key")));
        Assert.DoesNotContain(ApiKey, request.RequestUri.ToString());

        using var body = JsonDocument.Parse(handler.Bodies[0]);
        var root = body.RootElement;
        Assert.Equal("gear down", root.GetProperty("text").GetString());
        Assert.Equal("eleven_flash_v2_5", root.GetProperty("model_id").GetString());
        Assert.Equal("off", root.GetProperty("apply_text_normalization").GetString());
        var settings = root.GetProperty("voice_settings");
        Assert.Equal(0.6, settings.GetProperty("stability").GetDouble());
        Assert.Equal(0.75, settings.GetProperty("similarity_boost").GetDouble());
        Assert.Equal(1.0, settings.GetProperty("speed").GetDouble());
    }

    [Fact]
    public async Task VoiceOverride_IsTheVoiceInThePath()
    {
        var handler = new StubHandler(HttpStatusCode.OK, FakePcm());
        var provider = Provider(Options(), handler);

        await provider.SynthesizeAsync("cabin secure", CancellationToken.None, "purser-voice");

        Assert.Contains("/v1/text-to-speech/purser-voice?", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task BudgetExceeded_Throws_BeforeAnyHttpCall()
    {
        var handler = new StubHandler(HttpStatusCode.OK, FakePcm());
        var usage = new TtsUsageTracker(NullLogger<TtsUsageTracker>.Instance);
        usage.Increment(_cacheDir, 8_999, "elevenlabs");
        var options = Options();
        options.ElevenLabsMonthlyCharBudget = 9_000;
        var provider = Provider(options, handler, usage);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.SynthesizeAsync("gear down", CancellationToken.None));

        Assert.Contains("budget", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PerRequestCapExceeded_Throws_BeforeAnyHttpCall()
    {
        var handler = new StubHandler(HttpStatusCode.OK, FakePcm());
        var options = Options();
        options.ElevenLabsMaxCharsPerRequest = 10;
        var provider = Provider(options, handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.SynthesizeAsync("this text is longer than ten characters", CancellationToken.None));

        Assert.Contains("39", ex.Message);
        Assert.Contains("10", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Unauthorized_LatchesUntilOptionsChange_AndNeverLeaksTheKey()
    {
        var handler = new StubHandler(HttpStatusCode.Unauthorized, "{\"detail\":\"bad key\"}"u8.ToArray());
        var options = Options();
        var provider = Provider(options, handler);

        var first = await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.SynthesizeAsync("one", CancellationToken.None));
        Assert.Contains("401", first.Message);
        Assert.DoesNotContain(ApiKey, first.Message);
        Assert.Single(handler.Requests);

        // Latched: the second call never reaches the API.
        var second = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.SynthesizeAsync("two", CancellationToken.None));
        Assert.DoesNotContain(ApiKey, second.Message);
        Assert.Single(handler.Requests);

        // A settings save clears the latch and the API is tried again.
        Assert.NotNull(_optionsListener);
        handler.Status = HttpStatusCode.OK;
        handler.Body = FakePcm();
        _optionsListener!(options, null);

        var audio = await provider.SynthesizeAsync("three", CancellationToken.None);
        Assert.Equal("elevenlabs", audio.ProviderName);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task OtherHttpError_ThrowsWithStatusAndSnippet_WithoutLatching()
    {
        var handler = new StubHandler(HttpStatusCode.TooManyRequests, "{\"detail\":{\"status\":\"quota_exceeded\"}}"u8.ToArray());
        var provider = Provider(Options(), handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.SynthesizeAsync("one", CancellationToken.None));
        Assert.Contains("429", ex.Message);
        Assert.Contains("quota_exceeded", ex.Message);

        // Not a key problem — the next call goes out again (the router owns the cooldown).
        await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.SynthesizeAsync("two", CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task PcmResponse_IsWrappedInACompleteWavHeader()
    {
        var pcm = FakePcm(1000);
        var handler = new StubHandler(HttpStatusCode.OK, pcm);
        var provider = Provider(Options("pcm_24000"), handler);

        var audio = await provider.SynthesizeAsync("gear down", CancellationToken.None);
        var wav = audio.WavBytes;

        Assert.Equal(44 + pcm.Length, wav.Length);
        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Equal("WAVE"u8.ToArray(), wav[8..12]);
        Assert.Equal("fmt "u8.ToArray(), wav[12..16]);
        Assert.Equal("data"u8.ToArray(), wav[36..40]);
        Assert.Equal((uint)(wav.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(4)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(20)));        // PCM
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)));        // mono
        Assert.Equal(24_000u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)));  // sample rate
        Assert.Equal(48_000u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(28)));  // byte rate
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(32)));        // block align
        Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)));       // bits
        Assert.Equal((uint)pcm.Length, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40)));
        Assert.Equal(pcm, wav[44..]);
    }

    [Fact]
    public async Task CacheHit_MakesNoHttpCall_AndCountsUsageOnce()
    {
        var handler = new StubHandler(HttpStatusCode.OK, FakePcm());
        var usage = new TtsUsageTracker(NullLogger<TtsUsageTracker>.Instance);
        var provider = Provider(Options(), handler, usage);

        await provider.SynthesizeAsync("gear down", CancellationToken.None);
        await provider.SynthesizeAsync("gear down", CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Equal("gear down".Length, usage.CharactersThisMonth(_cacheDir, "elevenlabs"));
        Assert.Equal(0, usage.CharactersThisMonth(_cacheDir)); // Google's file untouched.
    }

    [Fact]
    public async Task FormatChange_MissesTheCache()
    {
        // The model/format ride in the cache's voice segment — switching must re-synthesize.
        var handler = new StubHandler(HttpStatusCode.OK, FakePcm());
        var options = Options("pcm_24000");
        var provider = Provider(options, handler);

        await provider.SynthesizeAsync("gear down", CancellationToken.None);
        options.ElevenLabsOutputFormat = "pcm_16000";
        await provider.SynthesizeAsync("gear down", CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task UnknownFormat_IsRefused_BeforeAnyHttpCall()
    {
        var handler = new StubHandler(HttpStatusCode.OK, FakePcm());
        var provider = Provider(Options("ulaw_8000"), handler);

        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => provider.SynthesizeAsync("gear down", CancellationToken.None));

        Assert.Contains("ulaw_8000", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Mp3Response_IsDecodedToWav()
    {
        byte[] mp3;
        try
        {
            mp3 = EncodeFixtureMp3();
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or NotSupportedException)
        {
            // No MP3 encoder MFT on this host (Server Core / N edition): the fixture cannot
            // be produced without external tools, so the decode path is not exercised here.
            _output.WriteLine($"SKIPPED: no Media Foundation MP3 encoder on this host ({ex.GetType().Name}: {ex.Message})");
            return;
        }

        var handler = new StubHandler(HttpStatusCode.OK, mp3);
        var provider = Provider(Options("mp3_44100_128"), handler);

        var audio = await provider.SynthesizeAsync("gear down", CancellationToken.None);
        var wav = audio.WavBytes;

        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Equal("WAVE"u8.ToArray(), wav[8..12]);
        Assert.Equal("fmt "u8.ToArray(), wav[12..16]);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(20)));       // PCM
        Assert.Equal(44_100u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24))); // sample rate kept
        Assert.True(wav.Length > 44 + 4096, "decoded audio should be a few thousand samples");

        // And it round-trips through NAudio's strict reader (the playback layer's parser).
        using var reader = new WaveFileReader(new MemoryStream(wav));
        Assert.True(reader.TotalTime > TimeSpan.FromMilliseconds(100));
    }

    /// <summary>0.2 s of a 440 Hz tone, 44.1 kHz stereo 16-bit, encoded with the Windows
    /// Media Foundation MP3 encoder — the same stack the decode path uses.</summary>
    private static byte[] EncodeFixtureMp3()
    {
        MediaFoundationApi.Startup();
        var format = new WaveFormat(44_100, 16, 2);
        var samples = (int)(format.SampleRate * 0.2);
        var pcm = new byte[samples * format.BlockAlign];
        for (var i = 0; i < samples; i++)
        {
            var value = (short)(Math.Sin(2 * Math.PI * 440 * i / format.SampleRate) * 12_000);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4), value);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2), value);
        }

        var path = Path.Combine(Path.GetTempPath(), $"elevenlabs-fixture-{Guid.NewGuid():N}.mp3");
        try
        {
            using (var source = new RawSourceWaveStream(new MemoryStream(pcm), format))
            {
                MediaFoundationEncoder.EncodeToMp3(source, path, 128_000);
            }

            return File.ReadAllBytes(path);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }
}
