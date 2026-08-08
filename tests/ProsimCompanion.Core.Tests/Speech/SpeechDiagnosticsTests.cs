using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Speech;
using ProsimCompanion.Speech.Playback;
using ProsimCompanion.Speech.Tts;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

public sealed class SpeechDiagnosticsTests
{
    private readonly SpeechOptions _speechOptions = new();
    private readonly BriefingOptions _briefingOptions = new();
    private readonly Mock<ISpeechPlayback> _playback = new();

    private SpeechDiagnosticsService Service(params ITtsProvider[] providers)
    {
        var speech = new Mock<IOptionsMonitor<SpeechOptions>>();
        speech.SetupGet(m => m.CurrentValue).Returns(() => _speechOptions);
        var briefing = new Mock<IOptionsMonitor<BriefingOptions>>();
        briefing.SetupGet(m => m.CurrentValue).Returns(() => _briefingOptions);
        return new SpeechDiagnosticsService(
            providers, _playback.Object, speech.Object, briefing.Object,
            NullLogger<SpeechDiagnosticsService>.Instance);
    }

    private static Mock<ITtsProvider> Provider(string name, bool configured = true, bool network = false)
    {
        var provider = new Mock<ITtsProvider>();
        provider.SetupGet(p => p.Name).Returns(name);
        provider.SetupGet(p => p.IsConfigured).Returns(configured);
        provider.SetupGet(p => p.IsNetworkProvider).Returns(network);
        return provider;
    }

    [Fact]
    public async Task TtsTest_UnknownProvider_SaysSo()
    {
        var result = await Service().TestTtsProviderAsync("kokoro", "hello", CancellationToken.None);
        Assert.Contains("Unknown provider", result);
    }

    [Fact]
    public async Task TtsTest_UnconfiguredProvider_DoesNotSynthesize()
    {
        var provider = Provider("kokoro", configured: false);
        var result = await Service(provider.Object).TestTtsProviderAsync("kokoro", "hello", CancellationToken.None);

        Assert.Contains("not configured", result);
        provider.Verify(
            p => p.SynthesizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task TtsTest_NetworkProviderInLocalOnlyMode_IsExcluded()
    {
        _speechOptions.LocalOnly = true;
        var provider = Provider("google", network: true);
        var result = await Service(provider.Object).TestTtsProviderAsync("google", "hello", CancellationToken.None);

        Assert.Contains("local-only", result);
    }

    [Fact]
    public async Task TtsTest_SynthesizesWithExactlyThatProviderAndPlays()
    {
        var wav = new byte[] { 1, 2, 3 };
        var provider = Provider("kokoro");
        provider.Setup(p => p.SynthesizeAsync("hello", It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new TtsAudio(wav, "kokoro"));
        var other = Provider("sapi");

        // Case-insensitive name match; the other provider is never touched (no fallback).
        var result = await Service(provider.Object, other.Object)
            .TestTtsProviderAsync("Kokoro", "hello", CancellationToken.None);

        Assert.Contains("played", result);
        _playback.Verify(p => p.PlayAsync(wav, It.IsAny<CancellationToken>(), null), Times.Once);
        other.Verify(
            p => p.SynthesizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task TtsTest_ProviderFailure_BecomesResultMessage()
    {
        var provider = Provider("kokoro");
        provider.Setup(p => p.SynthesizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), null))
            .ThrowsAsync(new HttpRequestException("connection refused"));

        var result = await Service(provider.Object).TestTtsProviderAsync("kokoro", "hello", CancellationToken.None);

        Assert.Contains("kokoro failed", result);
        Assert.Contains("connection refused", result);
    }

    [Fact]
    public async Task LlmTest_Unconfigured_ExplainsTheGate()
    {
        // Defaults: LlmEnabled=false, no model — the diagnostic must say why, not fail.
        var result = await Service().TestLlmAsync(CancellationToken.None);
        Assert.Contains("disabled", result);
    }
}
