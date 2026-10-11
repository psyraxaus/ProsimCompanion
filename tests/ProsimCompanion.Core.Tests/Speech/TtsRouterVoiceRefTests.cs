using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Tts;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>provider:id voices in the chain (owner, 2026-10-12): the named provider speaks
/// first, the others cover with their own voice, plain ids keep the legacy behaviour.</summary>
public sealed class TtsRouterVoiceRefTests
{
    private sealed class FakeProvider(string name, bool network = false) : ITtsProvider
    {
        public string Name { get; } = name;
        public bool IsConfigured { get; set; } = true;
        public bool IsNetworkProvider { get; } = network;
        public bool Fail { get; set; }
        public List<string?> VoicesSeen { get; } = [];

        public Task<TtsAudio> SynthesizeAsync(string text, CancellationToken cancellationToken, string? voiceOverride = null)
        {
            VoicesSeen.Add(voiceOverride);
            if (Fail)
            {
                throw new InvalidOperationException("down");
            }

            return Task.FromResult(new TtsAudio([1, 2, 3], Name));
        }
    }

    private readonly SpeechOptions _options = new();

    private TtsRouter Router(params ITtsProvider[] providers)
    {
        var monitor = new Mock<IOptionsMonitor<SpeechOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(() => _options);
        return new TtsRouter(providers, monitor.Object, new SpeechStatusStore(), NullLogger<TtsRouter>.Instance);
    }

    [Fact]
    public async Task Qualified_role_voice_promotes_its_provider_ahead_of_the_chain()
    {
        var kokoro = new FakeProvider("kokoro", network: true);
        var google = new FakeProvider("google", network: true);
        var router = Router(kokoro, google);

        var audio = await router.SynthesizeAsync("cabin secure", CancellationToken.None, "google:en-AU-Chirp3-HD-Aoede");

        Assert.Equal("google", audio!.ProviderName);
        Assert.Equal(["en-AU-Chirp3-HD-Aoede"], google.VoicesSeen);
        Assert.Empty(kokoro.VoicesSeen);
    }

    [Fact]
    public async Task When_the_named_provider_is_down_the_chain_covers_with_its_own_voice()
    {
        var kokoro = new FakeProvider("kokoro", network: true);
        var google = new FakeProvider("google", network: true) { Fail = true };
        var router = Router(kokoro, google);

        var audio = await router.SynthesizeAsync("cabin secure", CancellationToken.None, "google:en-AU-Chirp3-HD-Aoede");

        Assert.Equal("kokoro", audio!.ProviderName);
        Assert.Equal([null], kokoro.VoicesSeen); // its own configured voice, never the Google name
    }

    [Fact]
    public async Task The_FO_voice_setting_names_the_provider_when_no_role_voice_is_given()
    {
        _options.FoVoice = "elevenlabs:EXAVITQu4vr4xnSDxMaL";
        var kokoro = new FakeProvider("kokoro", network: true);
        var eleven = new FakeProvider("elevenlabs", network: true);
        var router = Router(kokoro, eleven);

        var audio = await router.SynthesizeAsync("flaps one", CancellationToken.None);

        Assert.Equal("elevenlabs", audio!.ProviderName);
        Assert.Equal(["EXAVITQu4vr4xnSDxMaL"], eleven.VoicesSeen);
        Assert.Empty(kokoro.VoicesSeen);
    }

    [Fact]
    public async Task A_plain_id_is_handed_to_every_provider_as_before()
    {
        var kokoro = new FakeProvider("kokoro", network: true) { Fail = true };
        var google = new FakeProvider("google", network: true);
        var router = Router(kokoro, google);

        var audio = await router.SynthesizeAsync("cabin secure", CancellationToken.None, "af_heart");

        Assert.Equal("google", audio!.ProviderName);
        Assert.Equal(["af_heart"], kokoro.VoicesSeen);
        Assert.Equal(["af_heart"], google.VoicesSeen);
    }

    [Fact]
    public async Task A_per_provider_selector_still_wins_over_the_qualified_voice()
    {
        var kokoro = new FakeProvider("kokoro", network: true);
        var google = new FakeProvider("google", network: true);
        var router = Router(kokoro, google);

        var audio = await router.SynthesizeAsync("ground, cockpit", CancellationToken.None, "google:en-AU-Chirp3-HD-Aoede",
            provider => provider == "google" ? "en-GB-Chirp3-HD-Charon" : null);

        Assert.Equal("google", audio!.ProviderName);
        Assert.Equal(["en-GB-Chirp3-HD-Charon"], google.VoicesSeen);
    }

    [Fact]
    public async Task An_unconfigured_named_provider_is_skipped_and_the_chain_speaks()
    {
        var kokoro = new FakeProvider("kokoro", network: true);
        var google = new FakeProvider("google", network: true) { IsConfigured = false };
        var router = Router(kokoro, google);

        var audio = await router.SynthesizeAsync("cabin secure", CancellationToken.None, "google:en-AU-Chirp3-HD-Aoede");

        Assert.Equal("kokoro", audio!.ProviderName);
        Assert.Empty(google.VoicesSeen);
    }
}
