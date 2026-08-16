using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Debrief;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Logbook;
using ProsimCompanion.Core.Sessions;
using ProsimCompanion.Core.Tests.Debrief;
using ProsimCompanion.Core.Tests.TechLog;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Debrief;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

public sealed class DebriefServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pc-debrief-svc-").FullName;
    private readonly DebriefOptions _options = new();
    private readonly FakePhaseSource _phases = new();
    private readonly FakeArbiter _arbiter = new();
    private readonly Mock<ILogbookService> _logbook = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best effort — the event-log writer may still hold its file
        }
    }

    /// <summary>An event log whose session file is seeded with synthetic events. The log is
    /// disposed first so its background writer releases the file (Record() then degrades to a
    /// silent no-op, which these tests never assert on) and the seed can take its place.</summary>
    private async Task<(DebriefService Service, string SessionPath)> CreateAsync(bool withData = true)
    {
        var eventLog = new Core.EventLog.JsonlEventLog(_dir, NullLogger<Core.EventLog.JsonlEventLog>.Instance);
        await eventLog.DisposeAsync();
        var sessionPath = eventLog.Path;
        if (withData)
        {
            new SessionLogBuilder()
                .At("10:00:00").Event("callout.fired", new { id = "a" })
                .At("10:01:00").Event("callout.fired", new { id = "b" })
                .Write(_dir, Path.GetFileNameWithoutExtension(sessionPath));
        }

        var service = new DebriefService(
            new DebriefFactExtractor(NullLogger<DebriefFactExtractor>.Instance),
            _logbook.Object,
            _arbiter,
            _phases,
            eventLog,
            OptionsSupport.Monitor(_options),
            OptionsSupport.Monitor(new BriefingOptions()),
            NullLogger<DebriefService>.Instance,
            new ProsimCompanion.Core.Speech.SpokenText());
        service.Start();
        return (service, sessionPath);
    }

    private static Task RunStepAsync(DebriefService service, string sessionPath)
        => ((ISessionFinalizationStep)service).RunAsync(
            new SessionFinalizationContext(sessionPath, Path.GetFileNameWithoutExtension(sessionPath)),
            CancellationToken.None);

    [Fact]
    public async Task Step_SpeaksLowPriorityDebrief_AndPersistsTextBesideLog()
    {
        _logbook
            .Setup(l => l.DescribeComparison(It.IsAny<DebriefFacts>(), It.IsAny<string?>()))
            .Returns("That's your first landing into YMML.");
        var (service, sessionPath) = await CreateAsync();

        await RunStepAsync(service, sessionPath);

        var request = Assert.Single(_arbiter.Requests);
        Assert.Equal(
            "Debrief. 2 callouts made. Good flight. That's your first landing into YMML.",
            request.Text);
        Assert.Equal(SpeechPriority.Low, request.Priority);
        Assert.Equal(TimeSpan.FromMinutes(10), request.Ttl);
        Assert.Equal("debrief", request.Tag);

        // The validity window keeps a stale debrief silent once a new flight is underway.
        Assert.NotNull(request.IsStillValid);
        _phases.SetPhase(FlightPhase.Shutdown);
        Assert.True(request.IsStillValid());
        _phases.SetPhase(FlightPhase.TakeoffRoll);
        Assert.False(request.IsStillValid());

        var debriefPath = Path.ChangeExtension(sessionPath, ".debrief.txt");
        Assert.True(File.Exists(debriefPath));
        Assert.Equal(request.Text, File.ReadAllText(debriefPath));

        // The comparison excluded this session's own id.
        _logbook.Verify(l => l.DescribeComparison(
            It.Is<DebriefFacts>(f => f.CalloutsFired == 2),
            Path.GetFileNameWithoutExtension(sessionPath)));
    }

    [Fact]
    public async Task Step_OncePerFlight_RearmedByNewFlight()
    {
        var (service, sessionPath) = await CreateAsync();

        await RunStepAsync(service, sessionPath);
        await RunStepAsync(service, sessionPath); // guard holds
        Assert.Single(_arbiter.Requests);

        _phases.SetPhase(FlightPhase.TakeoffRoll); // new flight re-arms
        await RunStepAsync(service, sessionPath);
        Assert.Equal(2, _arbiter.Requests.Count);
    }

    [Fact]
    public async Task TriggerNow_BypassesOnceGuard()
    {
        var (service, sessionPath) = await CreateAsync();
        await RunStepAsync(service, sessionPath);
        service.TriggerNow();
        Assert.Equal(2, _arbiter.Requests.Count);
    }

    [Fact]
    public async Task BriefVerbosity_OmitsCounts()
    {
        _options.Verbosity = "brief";
        var (service, _) = await CreateAsync();
        service.TriggerNow();
        Assert.Equal("Debrief. Good flight.", Assert.Single(_arbiter.Requests).Text);
    }

    [Fact]
    public async Task NoUsableData_StaysSilent()
    {
        var (service, sessionPath) = await CreateAsync(withData: false);
        await RunStepAsync(service, sessionPath);
        Assert.Empty(_arbiter.Requests);
        Assert.False(File.Exists(Path.ChangeExtension(sessionPath, ".debrief.txt")));
    }

    [Fact]
    public async Task Disabled_StaysSilent()
    {
        _options.Enabled = false;
        var (service, _) = await CreateAsync();
        service.TriggerNow();
        Assert.Empty(_arbiter.Requests);
    }

    [Theory]
    [InlineData("debrief")]
    [InlineData("debrief now")]
    [InlineData("flight debrief")]
    [InlineData("post flight debrief")]
    [InlineData("give me the debrief")]
    public async Task TryHandle_DebriefPhrases_Trigger(string phrase)
    {
        var (service, _) = await CreateAsync();
        Assert.True(service.TryHandle(phrase));
        Assert.Single(_arbiter.Requests);
    }

    [Fact]
    public async Task TryHandle_UnknownPhrase_NotConsumed()
    {
        var (service, _) = await CreateAsync();
        Assert.False(service.TryHandle("debrief me maybe"));
        Assert.Empty(_arbiter.Requests);
    }
}
