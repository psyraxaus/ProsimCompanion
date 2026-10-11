using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Tts;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>The Status page's provider pills must follow a settings save and the Test button
/// at once (2026-10-12: Kokoro configured, tested fine, still "Disabled / not configured"
/// until the FO next spoke through the chain).</summary>
public sealed class TtsRouterStatusTests
{
    private sealed class FakeProvider(string name, bool network = false) : ITtsProvider
    {
        public string Name { get; } = name;
        public bool IsConfigured { get; set; }
        public bool IsNetworkProvider { get; } = network;

        public Task<TtsAudio> SynthesizeAsync(string text, CancellationToken cancellationToken, string? voiceOverride = null)
            => throw new InvalidOperationException("the chain is not exercised here");
    }

    private readonly SpeechOptions _options = new();
    private Action<SpeechOptions, string?>? _onChange;

    private (TtsRouter Router, SpeechStatusStore Store) Build(params ITtsProvider[] providers)
    {
        var monitor = new Mock<IOptionsMonitor<SpeechOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(() => _options);
        monitor.Setup(m => m.OnChange(It.IsAny<Action<SpeechOptions, string?>>()))
            .Callback<Action<SpeechOptions, string?>>(cb => _onChange = cb)
            .Returns(Mock.Of<IDisposable>());
        var store = new SpeechStatusStore();
        return (new TtsRouter(providers, monitor.Object, store, NullLogger<TtsRouter>.Instance), store);
    }

    private static TtsProviderView View(SpeechStatusStore store, string name)
        => store.Snapshot().Providers.Single(p => p.Name == name);

    [Fact]
    public void Settings_save_republishes_health_without_an_utterance()
    {
        var kokoro = new FakeProvider("kokoro", network: true);
        var (_, store) = Build(kokoro);
        Assert.Equal(TtsProviderHealth.Disabled, View(store, "kokoro").Health);
        Assert.Equal("not configured", View(store, "kokoro").Detail);

        // The pilot enters the base URL and saves: the provider now reads configured and the
        // options monitor fires its change callback.
        kokoro.IsConfigured = true;
        Assert.NotNull(_onChange);
        _onChange!(_options, null);

        Assert.Equal(TtsProviderHealth.Unknown, View(store, "kokoro").Health);
        Assert.Equal("", View(store, "kokoro").Detail);
    }

    [Fact]
    public void A_passed_test_marks_the_provider_healthy_and_a_failed_one_starts_no_cooldown()
    {
        var kokoro = new FakeProvider("kokoro", network: true) { IsConfigured = true };
        var (router, store) = Build(kokoro);
        Assert.Equal(TtsProviderHealth.Unknown, View(store, "kokoro").Health);

        router.RecordDirectResult("kokoro", succeeded: true);
        Assert.Equal(TtsProviderHealth.Healthy, View(store, "kokoro").Health);

        router.RecordDirectResult("kokoro", succeeded: false);
        // Still healthy, never "Cooling down": the test is a probe, not an utterance.
        Assert.Equal(TtsProviderHealth.Healthy, View(store, "kokoro").Health);
    }

    [Fact]
    public void Dispose_releases_the_options_subscription()
    {
        var handle = new Mock<IDisposable>();
        var monitor = new Mock<IOptionsMonitor<SpeechOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(() => _options);
        monitor.Setup(m => m.OnChange(It.IsAny<Action<SpeechOptions, string?>>())).Returns(handle.Object);

        var router = new TtsRouter([new FakeProvider("sapi")], monitor.Object, new SpeechStatusStore(), NullLogger<TtsRouter>.Instance);
        router.Dispose();

        handle.Verify(h => h.Dispose(), Times.Once);
    }
}
