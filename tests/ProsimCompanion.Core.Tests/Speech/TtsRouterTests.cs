using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Tts;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

public sealed class TtsRouterTests
{
    private sealed class FakeProvider : ITtsProvider
    {
        public FakeProvider(string name, bool network = false)
        {
            Name = name;
            IsNetworkProvider = network;
        }

        public string Name { get; }
        public bool IsConfigured { get; set; } = true;
        public bool IsNetworkProvider { get; }
        public int Calls { get; private set; }
        public Func<CancellationToken, TtsAudio>? OnSynthesize { get; set; }

        public Task<TtsAudio> SynthesizeAsync(string text, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(
                OnSynthesize?.Invoke(cancellationToken) ?? new TtsAudio([1, 2, 3], Name));
        }
    }

    private readonly SpeechOptions _options = new();

    private TtsRouter Router(params ITtsProvider[] providers)
    {
        var monitor = new Mock<IOptionsMonitor<SpeechOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(() => _options);
        return new TtsRouter(providers, monitor.Object, new SpeechStatusStore(),
            NullLogger<TtsRouter>.Instance);
    }

    [Fact]
    public async Task FirstHealthyProvider_Wins()
    {
        var first = new FakeProvider("kokoro");
        var second = new FakeProvider("sapi");

        var audio = await Router(first, second).SynthesizeAsync("hello", CancellationToken.None);

        Assert.Equal("kokoro", audio?.ProviderName);
        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public async Task FailedProvider_FallsThrough_AndCoolsDown()
    {
        var first = new FakeProvider("kokoro")
        {
            OnSynthesize = _ => throw new HttpRequestException("down"),
        };
        var second = new FakeProvider("sapi");
        var router = Router(first, second);

        var audio = await router.SynthesizeAsync("hello", CancellationToken.None);
        Assert.Equal("sapi", audio?.ProviderName);
        Assert.Equal(1, first.Calls);

        // Within the cooldown window the dead provider is not retried.
        await router.SynthesizeAsync("again", CancellationToken.None);
        Assert.Equal(1, first.Calls);
        Assert.Equal(2, second.Calls);
    }

    [Fact]
    public async Task UnconfiguredProvider_SkippedWithoutFailure()
    {
        var first = new FakeProvider("kokoro") { IsConfigured = false };
        var second = new FakeProvider("sapi");

        var audio = await Router(first, second).SynthesizeAsync("hello", CancellationToken.None);

        Assert.Equal("sapi", audio?.ProviderName);
        Assert.Equal(0, first.Calls);
    }

    [Fact]
    public async Task LocalOnly_ExcludesNetworkProviders()
    {
        _options.LocalOnly = true;
        var network = new FakeProvider("google", network: true);
        var local = new FakeProvider("winrt");

        var audio = await Router(network, local).SynthesizeAsync("hello", CancellationToken.None);

        Assert.Equal("winrt", audio?.ProviderName);
        Assert.Equal(0, network.Calls);
    }

    [Fact]
    public async Task AllProvidersFail_ReturnsNull()
    {
        var only = new FakeProvider("sapi")
        {
            OnSynthesize = _ => throw new InvalidOperationException("broken"),
        };

        var audio = await Router(only).SynthesizeAsync("hello", CancellationToken.None);

        Assert.Null(audio);
    }

    [Fact]
    public async Task Preemption_Rethrows_WithoutPenalizingProvider()
    {
        using var cts = new CancellationTokenSource();
        var first = new FakeProvider("kokoro")
        {
            OnSynthesize = _ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            },
        };
        var second = new FakeProvider("sapi");
        var router = Router(first, second);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => router.SynthesizeAsync("hello", cts.Token));
        Assert.Equal(0, second.Calls); // No fallback on pre-emption.

        // And no cooldown either — the provider was not at fault.
        first.OnSynthesize = null;
        var audio = await router.SynthesizeAsync("again", CancellationToken.None);
        Assert.Equal(2, first.Calls);
        Assert.Equal("kokoro", audio?.ProviderName);
    }

    [Fact]
    public async Task ProviderTimeout_WithoutCallerCancel_IsAProviderFault()
    {
        // A Kokoro per-call timeout surfaces as OCE while the caller's token is NOT
        // cancelled — that must count as a failure and fall through.
        var first = new FakeProvider("kokoro")
        {
            OnSynthesize = _ => throw new OperationCanceledException(),
        };
        var second = new FakeProvider("sapi");

        var audio = await Router(first, second).SynthesizeAsync("hello", CancellationToken.None);

        Assert.Equal("sapi", audio?.ProviderName);
    }
}
