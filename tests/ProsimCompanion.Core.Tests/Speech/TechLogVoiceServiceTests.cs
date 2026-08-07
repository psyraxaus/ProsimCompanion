using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.TechLog;
using ProsimCompanion.Core.Tests.TechLog;
using ProsimCompanion.Speech.Abnormals;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.TechLog;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

public sealed class TechLogVoiceServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly TechLogOptions _options;
    private readonly FakePhaseSource _phases = new();
    private readonly FakeArbiter _arbiter = new();
    private readonly TechLogService _core;
    private readonly TechLogVoiceService _voice;

    public TechLogVoiceServiceTests()
    {
        _dir = Directory.CreateTempSubdirectory("pc-techlog-voice-").FullName;
        _options = new TechLogOptions { Path = Path.Combine(_dir, "techlog.json") };

        var eventLog = SpeechTestSupport.TempEventLog();
        _core = new TechLogService(
            OptionsSupport.Monitor(_options), eventLog, _phases,
            NullLogger<TechLogService>.Instance, new TestTimeProvider());
        _core.Start();

        var failures = new FailureMonitor(
            _arbiter, Mock.Of<IProsimDataRefs>(), _phases, eventLog,
            NullLogger<FailureMonitor>.Instance);
        _voice = new TechLogVoiceService(
            _core, _arbiter, _phases, failures,
            OptionsSupport.Monitor(_options), eventLog,
            NullLogger<TechLogVoiceService>.Instance);
        _voice.Start();
    }

    public void Dispose()
    {
        _voice.Dispose();
        _core.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private void RaiseOpenDefect(string title = "APU inoperative")
    {
        var draft = _core.NewDraft("manual", title, MelCategory.C);
        draft.OperationalImplications = "ground air required";
        _core.RaiseDefect(draft);
    }

    // ---- on-command brief ----

    [Theory]
    [InlineData("tech log")]
    [InlineData("read the tech log")]
    [InlineData("tech log brief")]
    [InlineData("brief the tech log")]
    [InlineData("any open items")]
    [InlineData("open items")]
    [InlineData("  Tech Log  ")] // trimmed + case-insensitive
    public void TryHandle_BriefPhrases_SpeakTheBrief(string phrase)
    {
        RaiseOpenDefect();
        Assert.True(_voice.TryHandle(phrase));

        var request = Assert.Single(_arbiter.Requests);
        Assert.Equal(
            "We're carrying one M E L item. APU inoperative, MEL (SIM) CAT C, "
            + "ground air required, 10 days remaining.",
            request.Text);
        Assert.Equal(SpeechPriority.Normal, request.Priority);
        Assert.Equal(TimeSpan.FromMinutes(3), request.Ttl);
        Assert.Equal("techlog", request.Tag);
    }

    [Fact]
    public void TryHandle_UnknownOrPartialPhrase_NotConsumed()
    {
        Assert.False(_voice.TryHandle("tech log please"));
        Assert.False(_voice.TryHandle("logbook"));
        Assert.False(_voice.TryHandle(""));
        Assert.Empty(_arbiter.Requests);
    }

    [Fact]
    public void TryHandle_CleanLog_SaysClean()
    {
        Assert.True(_voice.TryHandle("tech log"));
        Assert.Equal("Tech log is clean.", Assert.Single(_arbiter.Requests).Text);
    }

    [Fact]
    public void TryHandle_Disabled_SaysSwitchedOff()
    {
        _options.Enabled = false;
        Assert.True(_voice.TryHandle("tech log"));
        Assert.Equal("The tech log is switched off.", Assert.Single(_arbiter.Requests).Text);
    }

    // ---- preflight auto-brief ----

    [Fact]
    public void Preflight_AutoBriefsOncePerFlight_OnlyWithOpenItems()
    {
        _phases.SetPhase(FlightPhase.Preflight);
        Assert.Empty(_arbiter.Requests); // clean log never speaks unsolicited

        RaiseOpenDefect();
        _phases.SetPhase(FlightPhase.ColdAndDark);
        _phases.SetPhase(FlightPhase.Preflight);
        Assert.Single(_arbiter.Requests);

        _phases.SetPhase(FlightPhase.PushbackAndStart);
        _phases.SetPhase(FlightPhase.Preflight); // same flight — no repeat
        Assert.Single(_arbiter.Requests);

        _phases.SetPhase(FlightPhase.Shutdown);  // flight over — re-armed
        _phases.SetPhase(FlightPhase.Preflight);
        Assert.Equal(2, _arbiter.Requests.Count);
    }

    [Fact]
    public void Preflight_Disabled_NoAutoBrief()
    {
        RaiseOpenDefect();
        _options.Enabled = false;
        _phases.SetPhase(FlightPhase.Preflight);
        Assert.Empty(_arbiter.Requests);
    }

    // ---- wear announcement ----

    [Fact]
    public void WearRaised_SpeaksLowPriorityNotice()
    {
        var poolPath = Path.Combine(_dir, "wear-pool.json");
        File.WriteAllText(poolPath, """{ "entries": [ { "title": "Overhead bin latch stiff, row 12 left" } ] }""");
        _options.WearPoolPath = poolPath;

        using var core = new TechLogService(
            OptionsSupport.Monitor(_options), SpeechTestSupport.TempEventLog(), _phases,
            NullLogger<TechLogService>.Instance, new TestTimeProvider());
        core.Start();
        using var voice = new TechLogVoiceService(
            core, _arbiter, _phases,
            new FailureMonitor(_arbiter, Mock.Of<IProsimDataRefs>(), _phases,
                SpeechTestSupport.TempEventLog(), NullLogger<FailureMonitor>.Instance),
            OptionsSupport.Monitor(_options), SpeechTestSupport.TempEventLog(),
            NullLogger<TechLogVoiceService>.Instance);
        voice.Start();

        core.TryRandomWear("session-x", () => 0.0, _ => 0);

        var wear = Assert.Single(_arbiter.Requests);
        Assert.Equal("Noticed Overhead bin latch stiff, row 12 left. I'll pop it in the tech log.", wear.Text);
        Assert.Equal(SpeechPriority.Low, wear.Priority);
        Assert.Equal("techlog", wear.Tag);
    }

    // ---- per-flight abnormal memory (for the deferred post-abnormal offer) ----

    [Fact]
    public void FiredAbnormals_CollectPerFlight_ClearOnNewFlight()
    {
        _voice.NoteAbnormal("apu-fault", "APU FAULT");
        _voice.NoteAbnormal("apu-fault", "APU FAULT"); // same id — idempotent
        _voice.NoteAbnormal("pack-1-fault", "PACK 1 FAULT");

        Assert.Equal(2, _voice.FiredAbnormals.Count);
        Assert.Equal("APU FAULT", _voice.FiredAbnormals["apu-fault"]);

        _phases.SetPhase(FlightPhase.Preflight); // new flight — memory cleared
        Assert.Empty(_voice.FiredAbnormals);
    }
}
