using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Commands;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Crew;
using ProsimCompanion.Speech.Gsx;
using ProsimCompanion.Speech.Recognition;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

public sealed class CrewHailServiceTests : IDisposable
{
    private readonly CommandRegistry _registry = new();
    private readonly FakeArbiter _arbiter = new();
    private readonly Mock<IMicOwnership> _mic = new();
    private readonly GsxOptions _gsxOptions = new();
    private readonly GroundCrewOptions _groundOptions = new();
    private readonly CabinOptions _cabinOptions = new();
    private readonly List<string> _executed = [];
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "prosimcompanion-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Handler(string name, CommandResult result)
        => _registry.Register<EmptyCommandRequest, CommandResult>(name, (_, _) =>
        {
            lock (_executed)
            {
                _executed.Add(name);
            }

            return Task.FromResult(result);
        });

    private CrewHailService CreateService(string? heardUtterance)
    {
        _mic.Setup(m => m.Borrow(It.IsAny<string>())).Returns(Mock.Of<IDisposable>());
        _mic.Setup(m => m.ListenAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(heardUtterance);

        // Latches read as selected so dialogues run without the grace wait.
        var latch = new Mock<IDataRefSubscription>();
        latch.SetupGet(s => s.RawValue).Returns(1.0);
        latch.Setup(s => s.GetValue(It.IsAny<int>())).Returns(1);
        var dataRefs = new Mock<IProsimDataRefs>();
        dataRefs
            .Setup(d => d.Subscribe(It.IsAny<string>(), It.IsAny<DataRefTier>()))
            .Returns(latch.Object);

        var gsxOptions = new Mock<IOptionsMonitor<GsxOptions>>();
        gsxOptions.SetupGet(o => o.CurrentValue).Returns(() => _gsxOptions);
        var groundOptions = new Mock<IOptionsMonitor<GroundCrewOptions>>();
        groundOptions.SetupGet(o => o.CurrentValue).Returns(() => _groundOptions);
        var cabinOptions = new Mock<IOptionsMonitor<CabinOptions>>();
        cabinOptions.SetupGet(o => o.CurrentValue).Returns(() => _cabinOptions);

        var gsxVoice = new GsxVoiceService(
            _registry, _arbiter, gsxOptions.Object, NullLogger<GsxVoiceService>.Instance);

        return new CrewHailService(
            _mic.Object,
            _arbiter,
            gsxVoice,
            dataRefs.Object,
            gsxOptions.Object,
            groundOptions.Object,
            cabinOptions.Object,
            new JsonlEventLog(_tempDir, NullLogger<JsonlEventLog>.Instance),
            NullLogger<CrewHailService>.Instance);
    }

    private static void WaitFor(Func<bool> condition)
        => SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(2));

    [Fact]
    public void GroundHail_RecognizedRequest_DispatchesAndCrewAcks()
    {
        Handler("gsx.requestRefuel", CommandResult.Ok("requested"));
        var service = CreateService("request refueling");

        Assert.True(service.TryHandle("cockpit to ground"));
        WaitFor(() => _arbiter.Requests.Count >= 2);

        Assert.Equal(["gsx.requestRefuel"], _executed);
        Assert.Contains(_arbiter.Requests, r =>
            r.Role == SpeechRole.GroundCrew && r.Text == "Ground here — go ahead, captain.");
        Assert.Contains(_arbiter.Requests, r =>
            r.Role == SpeechRole.GroundCrew && r.Text == "Copied — fuel truck on the way.");
    }

    [Fact]
    public void GroundHail_Timeout_SpeaksStandingBy_NoDispatch()
    {
        var service = CreateService(heardUtterance: null);

        Assert.True(service.TryHandle("flight deck to ground"));
        WaitFor(() => _arbiter.Requests.Count >= 2);

        Assert.Empty(_executed);
        Assert.Contains(_arbiter.Requests, r =>
            r.Role == SpeechRole.GroundCrew && r.Text == _groundOptions.StandingByText);
    }

    [Fact]
    public void GroundHail_CancelWord_SpeaksStandingBy_NoDispatch()
    {
        Handler("gsx.requestRefuel", CommandResult.Ok("requested"));
        var service = CreateService("disregard");

        Assert.True(service.TryHandle("cockpit to ground"));
        WaitFor(() => _arbiter.Requests.Count >= 2);

        Assert.Empty(_executed);
    }

    [Fact]
    public void GroundHail_CommenceGroundServices_StartsTheSequence()
    {
        Handler("gsx.startDepartureServices", CommandResult.Ok("started"));
        var service = CreateService("commence ground services");

        Assert.True(service.TryHandle("cockpit to ground"));
        WaitFor(() => _executed.Count > 0);

        Assert.Equal(["gsx.startDepartureServices"], _executed);
        WaitFor(() => _arbiter.Requests.Count >= 2);
        Assert.Contains(_arbiter.Requests, r =>
            r.Role == SpeechRole.GroundCrew && r.Text == "Copied — commencing ground services.");
    }

    [Fact]
    public void GroundHail_Refusal_IsRelayedByTheFo()
    {
        Handler("gsx.requestRefuel", CommandResult.PreconditionFailed("No flight plan yet — import the OFP."));
        var service = CreateService("request refueling");

        Assert.True(service.TryHandle("cockpit to ground"));
        WaitFor(() => _arbiter.Requests.Count >= 2);

        Assert.Contains(_arbiter.Requests, r =>
            r.Role == SpeechRole.FirstOfficer && r.Text.Contains("No flight plan"));
    }

    [Fact]
    public void CabinHail_Boarding_AnswersAsPurser()
    {
        Handler("gsx.requestBoarding", CommandResult.Ok("requested"));
        var service = CreateService("start boarding");

        Assert.True(service.TryHandle("cockpit to crew"));
        WaitFor(() => _arbiter.Requests.Count >= 2);

        Assert.Equal(["gsx.requestBoarding"], _executed);
        Assert.Contains(_arbiter.Requests, r =>
            r.Role == SpeechRole.Purser && r.Text == _cabinOptions.HailReplyText);
        Assert.Contains(_arbiter.Requests, r =>
            r.Role == SpeechRole.Purser && r.Text == "Copied — we'll start boarding.");
    }

    [Fact]
    public void VoiceControlDisabled_ConsumesNothing()
    {
        _gsxOptions.VoiceControlEnabled = false;
        var service = CreateService("request refueling");

        Assert.False(service.TryHandle("cockpit to ground"));
        Assert.Empty(_arbiter.Requests);
    }

    [Fact]
    public void GroundCrewDisabled_GroundHailNotConsumed_CabinStillWorks()
    {
        Handler("gsx.requestBoarding", CommandResult.Ok("requested"));
        _groundOptions.Enabled = false;
        var service = CreateService("start boarding");

        Assert.False(service.TryHandle("cockpit to ground"));
        Assert.True(service.TryHandle("cockpit to cabin"));
    }

    [Fact]
    public void MicAlreadyBorrowed_HailIsDroppedSilently()
    {
        var service = CreateService("request refueling");
        // AFTER CreateService — it installs the default Borrow setup; the throw must win.
        _mic.Setup(m => m.Borrow(It.IsAny<string>()))
            .Throws(new InvalidOperationException("borrowed"));

        Assert.True(service.TryHandle("cockpit to ground"));
        Thread.Sleep(100); // give the fire-and-forget path a moment to (not) act

        Assert.Empty(_executed);
        Assert.Empty(_arbiter.Requests);
    }
}
