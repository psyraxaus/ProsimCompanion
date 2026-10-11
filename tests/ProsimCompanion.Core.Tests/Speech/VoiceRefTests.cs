using ProsimCompanion.Core.Speech;
using ProsimCompanion.Speech.Tts;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>provider:id voice references (owner, 2026-10-12) and the catalog's row builders.</summary>
public sealed class VoiceRefTests
{
    [Theory]
    [InlineData("google:en-AU-Chirp3-HD-Aoede", "google", "en-AU-Chirp3-HD-Aoede")]
    [InlineData("  KOKORO:bm_george ", "kokoro", "bm_george")]
    [InlineData("winrt:Microsoft Catherine", "winrt", "Microsoft Catherine")]
    [InlineData("bm_george", null, "bm_george")]
    [InlineData("EXAVITQu4vr4xnSDxMaL", null, "EXAVITQu4vr4xnSDxMaL")]
    [InlineData("notaprovider:thing", null, "notaprovider:thing")]
    [InlineData("", null, "")]
    [InlineData(null, null, "")]
    public void Parse_recognises_only_known_provider_prefixes(string? value, string? provider, string id)
    {
        var parsed = VoiceRef.Parse(value);
        Assert.Equal(provider, parsed.Provider);
        Assert.Equal(id, parsed.Id);
        Assert.Equal(provider is not null, parsed.HasProvider);
        Assert.Equal(id.Length == 0, parsed.IsEmpty);
    }

    [Fact]
    public void Format_round_trips()
    {
        var stored = VoiceRef.Format("Google", " en-GB-Chirp3-HD-Charon ");
        Assert.Equal("google:en-GB-Chirp3-HD-Charon", stored);
        Assert.Equal(new VoiceRef("google", "en-GB-Chirp3-HD-Charon"), VoiceRef.Parse(stored));
    }

    [Fact]
    public void TryVoiceFor_hands_plain_ids_to_everyone_and_qualified_ids_only_to_their_provider()
    {
        Assert.True(VoiceRef.TryVoiceFor("af_heart", "kokoro", out var plain));
        Assert.Equal("af_heart", plain);
        Assert.True(VoiceRef.TryVoiceFor("af_heart", "google", out var plainElsewhere));
        Assert.Equal("af_heart", plainElsewhere);

        Assert.True(VoiceRef.TryVoiceFor("kokoro:af_heart", "kokoro", out var mine));
        Assert.Equal("af_heart", mine);
        Assert.False(VoiceRef.TryVoiceFor("kokoro:af_heart", "google", out var theirs));
        Assert.Null(theirs);

        Assert.True(VoiceRef.TryVoiceFor("", "kokoro", out var none));
        Assert.Null(none);
    }

    [Fact]
    public void Catalog_rows_carry_provider_region_and_a_blurb()
    {
        var kokoro = VoiceCatalog.FromKokoro("bm_george");
        Assert.Equal("kokoro:bm_george", kokoro.Value);
        Assert.Equal("British English", kokoro.Region);
        Assert.Equal("George (male), grade C", kokoro.Label);

        var eleven = VoiceCatalog.FromElevenLabs("abc123", "Rachel", "american", "female");
        Assert.Equal("elevenlabs:abc123", eleven.Value);
        Assert.Equal("American", eleven.Region);
        Assert.Equal("Rachel (female)", eleven.Label);
        Assert.Contains("ElevenLabs", eleven.Blurb);

        var google = VoiceCatalog.FromGoogle("en-AU-Chirp3-HD-Aoede", "en-AU", "Female");
        Assert.Equal("google:en-AU-Chirp3-HD-Aoede", google.Value);
        Assert.Equal("English (Australia)", google.Region);
        Assert.Equal("Aoede", google.Name);
        Assert.Equal("female", google.Gender);
        Assert.Contains("Chirp 3 HD", google.Blurb);

        var windows = VoiceCatalog.FromWindows("winrt", "Microsoft Catherine", "en-AU", "Female");
        Assert.Equal("winrt:Microsoft Catherine", windows.Value);
        Assert.Equal("Catherine", windows.Name);
        Assert.Equal("English (Australia)", windows.Region);
        Assert.Contains("Offline", windows.Blurb);

        var unknownCulture = VoiceCatalog.FromWindows("sapi", "Odd Voice", "zz-ZZ-nope", null);
        Assert.NotEqual("", unknownCulture.Region);
        Assert.Equal("", unknownCulture.Gender);
    }
}
