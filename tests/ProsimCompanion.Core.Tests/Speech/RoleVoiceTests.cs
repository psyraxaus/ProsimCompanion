using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Tests.TechLog;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Tts;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

public sealed class RoleVoiceTests
{
    // ---- SpeechRequest surface ----

    [Fact]
    public void SpeechRequest_DefaultsToFirstOfficer()
        // Additive contract: every existing call site that doesn't name a Role speaks as the FO.
        => Assert.Equal(SpeechRole.FirstOfficer, new SpeechRequest("hello").Role);

    // ---- Role → voice resolution ----

    [Fact]
    public void Resolve_FirstOfficer_NoOverrides()
    {
        var resolved = RoleVoiceResolver.Resolve(SpeechRole.FirstOfficer, new VoicesOptions());

        Assert.Null(resolved.VoiceOverride);
        Assert.Null(resolved.IntercomOverride);
        Assert.False(resolved.FellBackToFoVoice);
    }

    [Fact]
    public void Resolve_ConfiguredRoles_UseTheirVoiceAndFilterSettings()
    {
        var voices = new VoicesOptions(); // defaults: af_heart / am_onyx, purser filtered

        var purser = RoleVoiceResolver.Resolve(SpeechRole.Purser, voices);
        Assert.Equal("af_heart", purser.VoiceOverride);
        Assert.Equal(true, purser.IntercomOverride);
        Assert.False(purser.FellBackToFoVoice);

        var company = RoleVoiceResolver.Resolve(SpeechRole.Company, voices);
        Assert.Equal("am_onyx", company.VoiceOverride);
        Assert.Equal(false, company.IntercomOverride); // ACARS readout — never band-passed
        Assert.False(company.FellBackToFoVoice);
    }

    [Fact]
    public void Resolve_BlankVoice_FallsBackToFoVoice_KeepingTheRoleFilter()
    {
        var voices = new VoicesOptions { Purser = "  " };

        var purser = RoleVoiceResolver.Resolve(SpeechRole.Purser, voices);

        Assert.Null(purser.VoiceOverride); // null = the provider's own FO voice
        Assert.Equal(true, purser.IntercomOverride); // still interphone audio
        Assert.True(purser.FellBackToFoVoice); // the caller logs this once per role
    }

    [Fact]
    public void Resolve_VoiceEqualToFoVoice_PassesThroughUnchanged()
    {
        // The resolver has no provider knowledge, so an id that happens to equal the FO voice
        // is passed as-is — it lands in the same cache namespace, costing nothing.
        var voices = new VoicesOptions { Company = "bm_george" };

        var company = RoleVoiceResolver.Resolve(SpeechRole.Company, voices);

        Assert.Equal("bm_george", company.VoiceOverride);
        Assert.False(company.FellBackToFoVoice);
    }

    [Fact]
    public void Resolve_FilterSettingsAreHonoured_NotHardcoded()
    {
        var voices = new VoicesOptions { PurserIntercomFilter = false, CompanyIntercomFilter = true };

        Assert.Equal(false, RoleVoiceResolver.Resolve(SpeechRole.Purser, voices).IntercomOverride);
        Assert.Equal(true, RoleVoiceResolver.Resolve(SpeechRole.Company, voices).IntercomOverride);
    }

    // ---- Router passthrough ----

    private sealed class CapturingProvider : ITtsProvider
    {
        public string Name => "kokoro";
        public bool IsConfigured => true;
        public bool IsNetworkProvider => false;
        public string? LastVoiceOverride { get; private set; } = "unset";

        public Task<TtsAudio> SynthesizeAsync(string text, CancellationToken cancellationToken, string? voiceOverride = null)
        {
            LastVoiceOverride = voiceOverride;
            return Task.FromResult(new TtsAudio([1], Name));
        }
    }

    [Fact]
    public async Task Router_PassesVoiceOverrideToTheProvider()
    {
        var provider = new CapturingProvider();
        var router = new TtsRouter(
            [provider], OptionsSupport.Monitor(new SpeechOptions()),
            new SpeechStatusStore(), NullLogger<TtsRouter>.Instance);

        await router.SynthesizeAsync("cabin secure", CancellationToken.None, "af_heart");
        Assert.Equal("af_heart", provider.LastVoiceOverride);

        // And the default stays null — FO speech never carries an override.
        await router.SynthesizeAsync("v one", CancellationToken.None);
        Assert.Null(provider.LastVoiceOverride);
    }

    // ---- Per-voice disk-cache isolation ----

    [Fact]
    public async Task DiskCache_NamespacesByVoice_SoRoleCachesStayIsolated()
    {
        var root = Directory.CreateTempSubdirectory("pc-ttscache-").FullName;
        try
        {
            var cache = new TtsDiskCache(NullLogger<TtsDiskCache>.Instance);
            byte[] foWav = [1, 2, 3];
            byte[] purserWav = [9, 9, 9];

            // Same provider, same text, two voices — two independent entries.
            await cache.PutAsync(root, "kokoro", "bm_george", "Cabin is secure.", foWav, 0);
            await cache.PutAsync(root, "kokoro", "af_heart", "Cabin is secure.", purserWav, 0);

            Assert.Equal(foWav, await cache.GetAsync(root, "kokoro", "bm_george", "Cabin is secure."));
            Assert.Equal(purserWav, await cache.GetAsync(root, "kokoro", "af_heart", "Cabin is secure."));
            Assert.Null(await cache.GetAsync(root, "kokoro", "am_onyx", "Cabin is secure."));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
