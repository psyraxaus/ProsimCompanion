using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Geo;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Immersion;
using ProsimCompanion.Speech.Llm;
using ProsimCompanion.Speech.Persona;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Issue #122, the pure scheduler: jittered gaps, the gates that hold a fact, the
/// per-flight cap, the no-repeat rule, the region key, and the re-arm.</summary>
public sealed class RegionFactCoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly Func<double> Mid = () => 0.5;

    private static RegionFactsOptions On(int min = 15, int max = 30, int cap = 4)
        => new() { Enabled = true, MinIntervalMinutes = min, MaxIntervalMinutes = max, MaxPerFlight = cap };

    private static RegionFactTick Cruise(int minutes, bool live = true, bool paused = false, bool sterile = false, bool quiet = false)
        => new(FlightPhase.Cruise, live, T0.AddMinutes(minutes), paused, sterile, quiet);

    [Theory]
    [InlineData(15, 30, 0.0, 15)]
    [InlineData(15, 30, 1.0, 30)]
    [InlineData(15, 30, 0.5, 22.5)]
    [InlineData(30, 10, 1.0, 30)]   // max below min reads as min
    [InlineData(0, 0, 0.0, 1)]      // never below a minute
    public void Gap_IsDrawnBetweenMinAndMax(int min, int max, double roll, double expectedMinutes)
        => Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), RegionFactCore.Gap(On(min, max), roll));

    [Fact]
    public void FirstFact_ComesOneGapAfterCruiseEntry_ThenEveryGap()
    {
        var core = new RegionFactCore();
        var options = On();

        Assert.False(core.IsDue(Cruise(0), options, Mid));          // cruise entry: gap drawn (22.5 min)
        Assert.Equal(T0.AddMinutes(22.5), core.NextDueUtc);
        Assert.False(core.IsDue(Cruise(22), options, Mid));
        Assert.True(core.IsDue(Cruise(23), options, Mid));
        Assert.True(core.IsDue(Cruise(24), options, Mid));          // still due until claimed

        Assert.True(core.TryClaim("FR", options, T0.AddMinutes(24), Mid));
        Assert.Equal(1, core.CountThisFlight);
        Assert.Equal(T0.AddMinutes(46.5), core.NextDueUtc);
        Assert.False(core.IsDue(Cruise(40), options, Mid));
        Assert.True(core.IsDue(Cruise(47), options, Mid));
    }

    [Fact]
    public void Gates_HoldADueFact_NeverConsumeIt()
    {
        var core = new RegionFactCore();
        var options = On();
        core.IsDue(Cruise(0), options, Mid);

        Assert.False(core.IsDue(Cruise(30, live: false), options, Mid));
        Assert.False(core.IsDue(Cruise(30, paused: true), options, Mid));
        Assert.False(core.IsDue(Cruise(30, sterile: true), options, Mid));
        Assert.False(core.IsDue(Cruise(30, quiet: true), options, Mid));
        Assert.False(core.IsDue(Cruise(30) with { Phase = FlightPhase.Climb }, options, Mid));   // step climb
        Assert.False(core.IsDue(Cruise(30), new RegionFactsOptions { Enabled = false }, Mid));
        Assert.True(core.IsDue(Cruise(31), options, Mid));
    }

    [Fact]
    public void LeavingTheCruiseFamily_DropsTheSchedule_UntilTheNextCruise()
    {
        var core = new RegionFactCore();
        var options = On();
        core.IsDue(Cruise(0), options, Mid);

        Assert.False(core.IsDue(Cruise(30) with { Phase = FlightPhase.Descent }, options, Mid));
        Assert.Null(core.NextDueUtc);
        // Back in the cruise (a go-around and re-climb would do it): a fresh gap from here.
        Assert.False(core.IsDue(Cruise(60), options, Mid));
        Assert.Equal(T0.AddMinutes(82.5), core.NextDueUtc);
    }

    [Fact]
    public void ARegion_IsSpokenAboutOnce_RepeatRetriesSooner_CapEndsIt()
    {
        var core = new RegionFactCore();
        var options = On(cap: 2);
        core.IsDue(Cruise(0), options, Mid);

        Assert.True(core.TryClaim("FR", options, T0.AddMinutes(23), Mid));
        Assert.False(core.TryClaim("fr", options, T0.AddMinutes(46), Mid));
        Assert.Equal(T0.AddMinutes(46) + RegionFactCore.Retry, core.NextDueUtc);
        Assert.Equal(1, core.CountThisFlight);

        Assert.True(core.TryClaim("DE", options, T0.AddMinutes(51), Mid));
        Assert.Equal(2, core.CountThisFlight);
        Assert.False(core.IsDue(Cruise(200), options, Mid));   // cap reached

        // A fact that produced no speech gives the region back.
        core.Release("DE");
        Assert.Equal(1, core.CountThisFlight);
        Assert.True(core.IsDue(Cruise(200), options, Mid));
        Assert.True(core.TryClaim("DE", options, T0.AddMinutes(200), Mid));
    }

    [Fact]
    public void Defer_LooksAgainAfterTheShortRetry()
    {
        var core = new RegionFactCore();
        var options = On();
        core.IsDue(Cruise(0), options, Mid);
        core.Defer(T0.AddMinutes(23));

        Assert.False(core.IsDue(Cruise(27), options, Mid));
        Assert.True(core.IsDue(Cruise(28), options, Mid));
    }

    [Fact]
    public void Rearm_OnColdAndDark_AndTurnaroundPreflight_NotMidFlight()
    {
        var core = new RegionFactCore();
        var options = On();
        core.IsDue(Cruise(0), options, Mid);
        core.TryClaim("FR", options, T0.AddMinutes(23), Mid);
        core.Remember("A fact.");

        core.OnPhaseChanged(FlightPhase.Climb, FlightPhase.Cruise);
        Assert.Equal(1, core.CountThisFlight);

        core.OnPhaseChanged(FlightPhase.TaxiIn, FlightPhase.Preflight);
        Assert.Equal(0, core.CountThisFlight);
        Assert.Empty(core.SpokenRegions);
        Assert.Empty(core.Told);
        Assert.Null(core.NextDueUtc);

        core.IsDue(Cruise(0), options, Mid);
        core.TryClaim("FR", options, T0.AddMinutes(23), Mid);
        core.OnPhaseChanged(FlightPhase.Shutdown, FlightPhase.ColdAndDark);
        Assert.Equal(0, core.CountThisFlight);
    }

    [Fact]
    public void RegionKey_IsTheCountryCode_ElseTheSea_ElseNull()
    {
        var country = new AtlasCountry("United Kingdom", "GB", "Northern Europe", []);
        var sea = new AtlasArea("North Sea", "Sea", []);
        var at = new GeoPoint(52, 2);

        Assert.Equal(("GB", "the United Kingdom"), RegionFactCore.RegionOf(new PlaceFix(at, country, "southern", null, null, null, null)));
        Assert.Equal(("sea:North Sea", "the North Sea"), RegionFactCore.RegionOf(new PlaceFix(at, null, null, null, sea, null, null)));
        Assert.Equal(("country:Somaliland", "Somaliland"), RegionFactCore.RegionOf(new PlaceFix(at, new AtlasCountry("Somaliland", "", "", []), null, null, null, null, null)));
        Assert.Null(RegionFactCore.RegionOf(new PlaceFix(at, null, null, null, null, null, null)));
        Assert.Null(RegionFactCore.RegionOf(null));
    }

    [Fact]
    public void Prompts_NameTheRegion_AndTheFactsAlreadyTold()
    {
        var system = RegionFactCore.SystemPrompt("PERSONA ");
        Assert.StartsWith("PERSONA ", system, StringComparison.Ordinal);
        Assert.Contains("two sentences", system, StringComparison.Ordinal);
        Assert.Contains("never say anything about this flight's fuel", system, StringComparison.Ordinal);

        var user = RegionFactCore.UserPrompt("northern France", "the Alps; Turin", ["One fact.", "Two."]);
        Assert.Contains("REGION: northern France", user, StringComparison.Ordinal);
        Assert.Contains("NEARBY: the Alps; Turin", user, StringComparison.Ordinal);
        Assert.Contains("ALREADY MENTIONED THIS FLIGHT: One fact. | Two.", user, StringComparison.Ordinal);
        Assert.DoesNotContain("ALREADY", RegionFactCore.UserPrompt("France", "", []), StringComparison.Ordinal);
    }
}

/// <summary>region-facts.json: the flat-object format, underscore comments, hot reload.</summary>
public sealed class RegionFactBankTests
{
    [Fact]
    public void Parse_ReadsStringArrays_SkipsCommentsAndJunk()
    {
        using var document = JsonDocument.Parse("""
            {
              "_comment": "ignored",
              "FR": ["Un.", " Deux. ", "", 3],
              "sea:North Sea": ["Water."],
              "DE": "not an array",
              "XX": []
            }
            """);
        var facts = RegionFactBank.Parse(document.RootElement);

        Assert.Equal(["Un.", "Deux."], facts["fr"]);
        Assert.Equal(["Water."], facts["sea:North Sea"]);
        Assert.False(facts.ContainsKey("_comment"));
        Assert.False(facts.ContainsKey("DE"));
        Assert.False(facts.ContainsKey("XX"));
    }

    [Fact]
    public void MissingFile_MeansNoFacts_AndAnUnreadableOne_IsReportedOnce()
    {
        var directory = Directory.CreateTempSubdirectory("pc-tests-region-").FullName;
        var problems = new ConfigProblemStore();
        var bank = new RegionFactBank(directory, NullLogger<RegionFactBank>.Instance, problems);

        Assert.Empty(bank.FactsFor("FR"));
        Assert.Equal(0, bank.RegionCount);

        File.WriteAllText(Path.Combine(directory, RegionFactBank.FileName), "{ not json");
        File.SetLastWriteTimeUtc(Path.Combine(directory, RegionFactBank.FileName), DateTime.UtcNow.AddSeconds(5));
        Assert.Empty(bank.FactsFor("FR"));
        Assert.Contains(problems.Snapshot(), p => p.Area == ConfigAreas.RegionFacts);

        File.WriteAllText(Path.Combine(directory, RegionFactBank.FileName), """{ "FR": ["Un."] }""");
        File.SetLastWriteTimeUtc(Path.Combine(directory, RegionFactBank.FileName), DateTime.UtcNow.AddSeconds(10));
        Assert.Equal(["Un."], bank.FactsFor("FR"));
        Assert.DoesNotContain(problems.Snapshot(), p => p.Area == ConfigAreas.RegionFacts);
    }

    [Fact]
    public void ShippedSeed_ParsesAndCoversTheUsualSuspects()
    {
        var root = RepoRoot();
        var path = Path.Combine(root, "src", "ProsimCompanion.App", "config", RegionFactBank.FileName);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var facts = RegionFactBank.Parse(document.RootElement);

        foreach (var key in new[] { "GB", "FR", "DE", "NL", "US", "sea:North Sea", "sea:English Channel", "sea:Mediterranean Sea" })
        {
            Assert.True(facts.TryGetValue(key, out var list) && list.Length > 0, key);
        }

        // Two sentences, passenger tone: no entry runs to an essay.
        Assert.All(facts.Values.SelectMany(f => f), fact => Assert.InRange(fact.Length, 20, 260));
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ProsimCompanion.slnx")))
        {
            directory = directory.Parent!;
        }

        return directory?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}

/// <summary>The shell: a due tick resolves the region and speaks a fact from the curated
/// file (model off) or the model (streamed, guarded), records the event, and stays silent
/// when disabled, when the atlas has nothing, and when the region repeats.</summary>
public sealed class RegionFactServiceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private sealed class SequenceHandler(params Func<Stream>?[] bodies) : HttpMessageHandler
    {
        private int _index;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = _index < bodies.Length ? bodies[_index++] : null;
            if (body is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                throw new TimeoutException("the test endpoint never answered");
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body()) };
        }
    }

    private static Func<Stream> Streamed(params string[] deltas) => () => LlmWire.Sliced(LlmWire.Sse(deltas), 13);

    private readonly DrainingArbiter _arbiter = new();
    private readonly FakePhaseSource _phases = new();
    private readonly LlmHealthStore _health = new();
    private readonly JsonlEventLog _eventLog = SpeechTestSupport.TempEventLog();
    private readonly SpeechOptions _speech = new();
    private readonly BriefingOptions _briefing = new() { LlmEnabled = false };
    private readonly FlightProgressStore _progress = new();
    private readonly SpeechStatusStore _status = new();
    private readonly QuietState _quiet = new();
    private readonly string _configDir = Directory.CreateTempSubdirectory("pc-tests-regionsvc-").FullName;

    public RegionFactServiceTests()
    {
        _speech.RegionFacts.Enabled = true;
        _speech.RegionFacts.MinIntervalMinutes = 10;
        _speech.RegionFacts.MaxIntervalMinutes = 10;
        _phases.SetPhase(FlightPhase.Cruise);
        _phases.Data = new FlightDataSnapshot { IsValid = true, AltitudeFt = 37000 };
        _progress.Update(s => s with { Position = new GeoPoint(45.0, 2.5), TrackTrueDeg = 90 });
        File.WriteAllText(Path.Combine(_configDir, RegionFactBank.FileName), """{ "TL": ["Testland has forty-two castles."] }""");
    }

    /// <summary>Testland over 0–10°E / 40–50°N; everything else is open water with no sea named.</summary>
    private static PlaceLookup Places() => new(new Atlas(
        [new AtlasCountry("Testland", "TL", "Nowhere", [new AtlasPolygon([[0, 40, 10, 40, 10, 50, 0, 50]])])],
        [],
        [],
        [new AtlasTown("Testville", "TL", new GeoPoint(45.0, 2.0), 1_500_000, TownRank.Capital)]));

    private RegionFactService Service(HttpMessageHandler? handler = null, bool withLlm = false)
    {
        if (withLlm)
        {
            _briefing.LlmEnabled = true;
            _briefing.LlmBaseUrl = "http://llm.test/v1";
            _briefing.LlmModel = "test-model";
            _briefing.LlmTimeoutSeconds = 0;
        }

        var briefing = SpeechTestSupport.BriefingMonitor(_briefing);
        var client = new OpenAiChatClient(briefing, new HttpClient(handler ?? new SequenceHandler()), _health) { StreamTimeoutFloorSeconds = 20 };
        var narrator = new StreamingNarrator(client, _arbiter, _eventLog, briefing, NullLogger<StreamingNarrator>.Instance)
        {
            WatchInterval = TimeSpan.FromMilliseconds(20),
        };
        var personaOptions = new Mock<IOptionsMonitor<PersonaOptions>>();
        personaOptions.SetupGet(m => m.CurrentValue).Returns(new PersonaOptions());
        var styled = new StyledSpeechService(SpeechTestSupport.Persona(), personaOptions.Object, briefing, _eventLog, NullLogger<StyledSpeechService>.Instance, client);
        var bank = new RegionFactBank(_configDir, NullLogger<RegionFactBank>.Instance);
        return new RegionFactService(
            SpeechTestSupport.SpeechMonitor(_speech), briefing, _phases, _progress, bank, client, narrator, _health,
            _arbiter, styled, _status, _quiet, _eventLog, NullLogger<RegionFactService>.Instance, Places())
        {
            Roll = () => 0.5,
            ModelBudget = TimeSpan.FromSeconds(5),
        };
    }

    private static async Task Settle(RegionFactService service)
    {
        if (service.InFlight is { } task)
        {
            await task;
        }
    }

    private List<JsonElement> Events()
    {
        _eventLog.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return File.ReadAllLines(_eventLog.Path)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("type").GetString() == RegionFactService.EventType)
            .Select(e => e.GetProperty("payload"))
            .ToList();
    }

    private string[] Spoken => [.. _arbiter.Segments.Select(s => s.Text).Concat(_arbiter.Requests.Where(r => r.Stream is null).Select(r => r.Text))];

    public void Dispose() => _eventLog.DisposeAsync().AsTask().GetAwaiter().GetResult();

    [Fact]
    public async Task CuratedFact_IsSpokenLow_OnceTheGapHasRun_AndRecorded()
    {
        var service = Service();

        Assert.Null(service.ProcessTick(T0));                       // cruise entry: gap drawn
        Assert.Null(service.ProcessTick(T0.AddMinutes(9)));
        Assert.Equal("TL", service.ProcessTick(T0.AddMinutes(10)));
        await Settle(service);

        var request = Assert.Single(_arbiter.Requests);
        Assert.Equal("Testland has forty-two castles.", request.Text);
        Assert.Equal(SpeechPriority.Low, request.Priority);
        Assert.Equal(RegionFactService.Tag, request.Tag);
        Assert.Equal(SpeechRole.FirstOfficer, request.Role);

        var fact = Assert.Single(Events());
        Assert.Equal("TL", fact.GetProperty("regionKey").GetString());
        Assert.Equal("Testland", fact.GetProperty("region").GetString());
        Assert.Equal("curated", fact.GetProperty("source").GetString());
        Assert.Equal("spoken", fact.GetProperty("outcome").GetString());
        Assert.Equal(45.0, fact.GetProperty("lat").GetDouble());
        Assert.Equal(1, fact.GetProperty("countThisFlight").GetInt32());
    }

    [Fact]
    public async Task SameRegion_IsNotRepeated_ThisFlight()
    {
        var service = Service();
        service.ProcessTick(T0);
        service.ProcessTick(T0.AddMinutes(10));
        await Settle(service);

        Assert.Null(service.ProcessTick(T0.AddMinutes(20)));
        await Settle(service);

        Assert.Single(_arbiter.Requests);
        var events = Events();
        Assert.Equal(2, events.Count);
        Assert.Equal("repeated", events[1].GetProperty("outcome").GetString());
    }

    [Fact]
    public void Disabled_ProducesNoEvents_AndNoSpeech()
    {
        _speech.RegionFacts.Enabled = false;
        var service = Service();

        Assert.Null(service.ProcessTick(T0));
        Assert.Null(service.ProcessTick(T0.AddHours(2)));
        Assert.Empty(_arbiter.Requests);
        Assert.Empty(Events());
    }

    [Fact]
    public void OverOpenWater_StaysSilent_AndLooksAgainSoon()
    {
        _progress.Update(s => s with { Position = new GeoPoint(30.0, -40.0) });
        var service = Service();
        service.ProcessTick(T0);

        Assert.Null(service.ProcessTick(T0.AddMinutes(10)));
        Assert.Empty(_arbiter.Requests);
        var skip = Assert.Single(Events());
        Assert.Equal("unresolved", skip.GetProperty("outcome").GetString());
        // Null payload fields are left out of the line.
        Assert.False(skip.TryGetProperty("regionKey", out var key) && key.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public void Gates_HoldTheFact()
    {
        var service = Service();
        service.ProcessTick(T0);

        _status.Update(s => s with { ListeningPaused = true });
        Assert.Null(service.ProcessTick(T0.AddMinutes(10)));
        _status.Update(s => s with { ListeningPaused = false });

        _phases.SetLive(false);
        Assert.Null(service.ProcessTick(T0.AddMinutes(11)));
        _phases.SetLive(true);

        _quiet.Engage();
        Assert.Null(service.ProcessTick(T0.AddMinutes(12)));
        _quiet.Reset();

        Assert.Equal("TL", service.ProcessTick(T0.AddMinutes(13)));
    }

    [Fact]
    public async Task ModelFact_IsStreamed_UnderThePlaceGuard_AndRecordedAsModel()
    {
        var handler = new SequenceHandler(Streamed("We're over Testland now. ", "The castles here were built by the river lords."));
        var service = Service(handler, withLlm: true);
        service.ProcessTick(T0);
        service.ProcessTick(T0.AddMinutes(10));
        await Settle(service);

        Assert.Equal(["We're over Testland now.", "The castles here were built by the river lords."], Spoken);
        var request = Assert.Single(_arbiter.Requests);
        Assert.Equal(SpeechPriority.Low, request.Priority);
        var fact = Assert.Single(Events());
        Assert.Equal("model", fact.GetProperty("source").GetString());
        Assert.Equal("spoken", fact.GetProperty("outcome").GetString());
        Assert.Contains("river lords", fact.GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelSilent_FallsBackToTheCuratedFact()
    {
        // The endpoint never answers: the budget runs out, the curated tier speaks instead.
        var service = Service(new SequenceHandler((Func<Stream>?)null), withLlm: true);
        service.ProcessTick(T0);
        service.ProcessTick(T0.AddMinutes(10));
        await Settle(service);

        Assert.Equal(["Testland has forty-two castles."], Spoken);
        Assert.Equal("curated", Assert.Single(Events()).GetProperty("source").GetString());
    }

    [Fact]
    public async Task ModelSentenceAboutTheFlight_IsRefused_CuratedSpeaks()
    {
        // The place guard (#153): a figure next to a flight-data word is refused before it
        // is spoken; nothing of the model's is heard, so the curated fact speaks.
        var handler = new SequenceHandler(Streamed("Our fuel is six thousand two hundred kilos over Testland."));
        var service = Service(handler, withLlm: true);
        service.ProcessTick(T0);
        service.ProcessTick(T0.AddMinutes(10));
        await Settle(service);

        Assert.Equal(["Testland has forty-two castles."], Spoken);
        Assert.Equal("curated", Assert.Single(Events()).GetProperty("source").GetString());
    }
}
