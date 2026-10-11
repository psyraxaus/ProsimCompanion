using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Speech;
using ProsimCompanion.Speech.Tts;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>The voice picker's catalog (2026-10-12): every shipped default must be a listed
/// voice, server ids the sheet does not know still file under their region, and the server
/// list parser reads both kokoro-fastapi response shapes.</summary>
public sealed class KokoroVoicesTests
{
    [Theory]
    [InlineData("bm_george")]   // SpeechOptions.KokoroVoice default
    [InlineData("af_heart")]    // VoicesOptions.Purser default
    [InlineData("am_onyx")]     // VoicesOptions.Company default
    [InlineData("am_michael")]  // VoicesOptions.Ground default
    public void Shipped_defaults_are_on_the_sheet(string id)
    {
        Assert.True(KokoroVoices.IsKnown(id));
        Assert.Contains(KokoroVoices.Known, v => v.Id == id);
    }

    [Fact]
    public void Sheet_has_no_duplicate_ids_and_every_id_parses_to_its_region()
    {
        var ids = KokoroVoices.Known.Select(v => v.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(KokoroVoices.Known, v => Assert.NotEqual(KokoroVoices.Other, v.Region));
        Assert.All(KokoroVoices.Known, v => Assert.True(v.Gender is "female" or "male"));
    }

    [Fact]
    public void Describe_reads_grade_and_training_into_the_blurb()
    {
        var george = KokoroVoices.Describe("bm_george");
        Assert.Equal("George", george.Name);
        Assert.Equal(KokoroVoices.BritishEnglish, george.Region);
        Assert.Equal("male", george.Gender);
        Assert.Equal("C", george.Grade);
        Assert.Equal("George (male), grade C", george.Label);
        Assert.Contains("British English, male.", george.Blurb);
        Assert.Contains("Overall grade C", george.Blurb);
        Assert.Contains("10 to 100 minutes", george.Blurb);
        Assert.False(george.NonEnglish);
    }

    [Fact]
    public void Describe_files_an_unknown_server_id_by_its_prefix()
    {
        var legacy = KokoroVoices.Describe("bm_v0george");
        Assert.Equal(KokoroVoices.BritishEnglish, legacy.Region);
        Assert.Equal("male", legacy.Gender);
        Assert.Equal("George (v0)", legacy.Name);
        Assert.Null(legacy.Grade);
        Assert.Contains("v0", legacy.Blurb);

        var blend = KokoroVoices.Describe("af_mycustomblend");
        Assert.Equal(KokoroVoices.AmericanEnglish, blend.Region);
        Assert.Contains("no grade", blend.Blurb);

        var foreign = KokoroVoices.Describe("Rachel-ElevenLabs");
        Assert.Equal(KokoroVoices.Other, foreign.Region);
        Assert.Equal("", foreign.Gender);
    }

    [Fact]
    public void Non_english_voices_warn_in_the_blurb()
    {
        var kumo = KokoroVoices.Describe("jm_kumo");
        Assert.True(kumo.NonEnglish);
        Assert.Contains("not for the First Officer", kumo.Blurb);

        var dora = KokoroVoices.Describe("ef_dora");
        Assert.Null(dora.Grade);
        Assert.Contains("no grade", dora.Blurb);
    }

    [Fact]
    public void Group_orders_english_first_then_female_before_male_and_drops_duplicates()
    {
        var groups = KokoroVoices.Group(["zm_yunxi", "bm_george", "BM_GEORGE", "bf_emma", "af_heart", "", "weird", "bm_v0lewis"]);

        Assert.Equal(
            [KokoroVoices.AmericanEnglish, KokoroVoices.BritishEnglish, "Mandarin Chinese", KokoroVoices.Other],
            groups.Select(g => g.Region).ToArray());

        var british = groups.Single(g => g.Region == KokoroVoices.BritishEnglish).Voices;
        Assert.Equal(["bf_emma", "bm_george", "bm_v0lewis"], british.Select(v => v.Id).ToArray());
        Assert.Equal(["weird"], groups.Single(g => g.Region == KokoroVoices.Other).Voices.Select(v => v.Id).ToArray());
    }

    [Fact]
    public async Task Catalog_reads_object_entries()
    {
        const string body = """{"voices":[{"id":"af_heart","name":"af_heart"},{"id":"bm_v0george","name":"bm_v0george"},{"name":"only_name"},{"id":""}]}""";
        var handler = new StubHandler(HttpStatusCode.OK, body);
        var catalog = new KokoroVoiceCatalog(NullLogger<KokoroVoiceCatalog>.Instance, new HttpClient(handler));

        var ids = await catalog.ListVoiceIdsAsync("http://10.0.1.30:8880/", CancellationToken.None);

        Assert.Equal(["af_heart", "bm_v0george", "only_name"], ids);
        Assert.Equal("http://10.0.1.30:8880/v1/audio/voices", handler.Requests.Single().RequestUri!.ToString());
    }

    [Fact]
    public async Task Catalog_reads_plain_string_entries_from_older_servers()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"voices":["af_heart","bm_george"]}""");
        var catalog = new KokoroVoiceCatalog(NullLogger<KokoroVoiceCatalog>.Instance, new HttpClient(handler));

        var ids = await catalog.ListVoiceIdsAsync("http://10.0.1.30:8880", CancellationToken.None);

        Assert.Equal(["af_heart", "bm_george"], ids);
    }

    [Fact]
    public async Task Catalog_throws_with_status_on_http_failure()
    {
        var handler = new StubHandler(HttpStatusCode.NotFound, "no such route");
        var catalog = new KokoroVoiceCatalog(NullLogger<KokoroVoiceCatalog>.Instance, new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => catalog.ListVoiceIdsAsync("http://10.0.1.30:8880", CancellationToken.None));

        Assert.Contains("404", ex.Message);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
            });
        }
    }
}
