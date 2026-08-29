using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Speech.Tts;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Issue #113: the connect budget must still catch a dead host, and a slow
/// streamed body within its own budget must succeed instead of tripping the cooldown.</summary>
public sealed class KokoroTtsProviderTests : IDisposable
{
    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), $"kokoro-tests-{Guid.NewGuid():N}");

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

    /// <summary>Fake kokoro-fastapi: headers after <paramref name="headerDelay"/>, then a
    /// WAV body whose bytes trickle out over <paramref name="bodyDelay"/>.</summary>
    private sealed class FakeKokoro(TimeSpan headerDelay, TimeSpan bodyDelay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(headerDelay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new TrickleStream(bodyDelay)),
            };
        }
    }

    /// <summary>A 4 KB "WAV" that becomes readable only after the delay.</summary>
    private sealed class TrickleStream(TimeSpan delay) : Stream
    {
        private readonly byte[] _payload = MakeWav();
        private int _position;
        private bool _delayed;

        private static byte[] MakeWav()
        {
            var bytes = new byte[4096];
            "RIFF"u8.CopyTo(bytes);
            "WAVE"u8.CopyTo(bytes.AsSpan(8));
            return bytes;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _payload.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_delayed)
            {
                _delayed = true;
                await Task.Delay(delay, cancellationToken);
            }

            var n = Math.Min(buffer.Length, _payload.Length - _position);
            _payload.AsSpan(_position, n).CopyTo(buffer.Span);
            _position += n;
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private KokoroTtsProvider Provider(SpeechOptions options, HttpMessageHandler handler)
    {
        options.KokoroBaseUrl = "http://kokoro.test";
        options.CacheFolder = _cacheDir;
        var monitor = new Mock<IOptionsMonitor<SpeechOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(options);
        return new KokoroTtsProvider(
            monitor.Object,
            new TtsDiskCache(NullLogger<TtsDiskCache>.Instance),
            NullLogger<KokoroTtsProvider>.Instance,
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });
    }

    [Fact]
    public async Task SleepingHost_StillFailsFast_OnTheConnectBudget()
    {
        var options = new SpeechOptions { KokoroTimeoutMs = 300, KokoroBodyTimeoutMs = 15000 };
        var provider = Provider(options, new FakeKokoro(TimeSpan.FromSeconds(5), TimeSpan.Zero));

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => provider.SynthesizeAsync("hello", CancellationToken.None));

        Assert.Contains("connect", ex.Message);
        Assert.Contains("300 ms", ex.Message);
    }

    [Fact]
    public async Task SlowStreamedBody_WithinBudget_Succeeds()
    {
        // The 2026-08-28 shape: headers at once, body streams for longer than the old 1.5 s.
        var options = new SpeechOptions { KokoroTimeoutMs = 300, KokoroBodyTimeoutMs = 5000 };
        var provider = Provider(options, new FakeKokoro(TimeSpan.Zero, TimeSpan.FromMilliseconds(900)));

        var audio = await provider.SynthesizeAsync("a long briefing that takes a while to synthesise", CancellationToken.None);

        Assert.Equal("kokoro", audio.ProviderName);
        Assert.Equal(4096, audio.WavBytes.Length);
    }

    [Fact]
    public async Task BodyPastItsBudget_TimesOut_WithTheBodyPhaseNamed()
    {
        var options = new SpeechOptions { KokoroTimeoutMs = 200, KokoroBodyTimeoutMs = 1000 };
        var provider = Provider(options, new FakeKokoro(TimeSpan.Zero, TimeSpan.FromSeconds(10)));

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => provider.SynthesizeAsync("x", CancellationToken.None));

        Assert.Contains("body", ex.Message);
    }

    [Fact]
    public async Task CallerCancellation_IsNotATimeout()
    {
        var options = new SpeechOptions { KokoroTimeoutMs = 5000, KokoroBodyTimeoutMs = 5000 };
        var provider = Provider(options, new FakeKokoro(TimeSpan.FromSeconds(5), TimeSpan.Zero));
        using var cts = new CancellationTokenSource(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.SynthesizeAsync("hello", cts.Token));
    }

    [Fact]
    public void BodyBudget_ScalesWithText_AndNeverDropsBelowConnect()
    {
        var options = new SpeechOptions { KokoroTimeoutMs = 1500, KokoroBodyTimeoutMs = 15000 };

        Assert.Equal(TimeSpan.FromMilliseconds(15000 + 100 * 40), KokoroTtsProvider.BodyBudget(options, 40));

        var tiny = new SpeechOptions { KokoroTimeoutMs = 3000, KokoroBodyTimeoutMs = 0 };
        Assert.Equal(TimeSpan.FromMilliseconds(3000), KokoroTtsProvider.BodyBudget(tiny, 0));
    }
}
