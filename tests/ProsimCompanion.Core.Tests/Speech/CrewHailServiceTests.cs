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
    private readonly SpeechOptions _speechOptions = new();
    private readonly FakeAcpChannel _acpTransmit = new();
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

        var gsxOptions = new Mock<IOptionsMonitor<GsxOptions>>();
        gsxOptions.SetupGet(o => o.CurrentValue).Returns(() => _gsxOptions);
        var groundOptions = new Mock<IOptionsMonitor<GroundCrewOptions>>();
        groundOptions.SetupGet(o => o.CurrentValue).Returns(() => _groundOptions);
        var cabinOptions = new Mock<IOptionsMonitor<CabinOptions>>();
        cabinOptions.SetupGet(o => o.CurrentValue).Returns(() => _cabinOptions);
        var speechOptions = new Mock<IOptionsMonitor<SpeechOptions>>();
        speechOptions.SetupGet(o => o.CurrentValue).Returns(() => _speechOptions);

        var gsxVoice = new GsxVoiceService(
            _registry, _arbiter, gsxOptions.Object, NullLogger<GsxVoiceService>.Instance);

        return new CrewHailService(
            _mic.Object,
            _arbiter,
            gsxVoice,
            _acpTransmit,
            gsxOptions.Object,
            groundOptions.Object,
            cabinOptions.Object,
            speechOptions.Object,
            new JsonlEventLog(_tempDir, NullLogger<JsonlEventLog>.Instance),
            NullLogger<CrewHailService>.Instance);
    }

    /// <summary>Settable ACP channel; transmit defaults to Unknown so the pre-#72 tests run
    /// through the degrade path (gating on, dataref absent → accept as before). Latches read
    /// as selected so dialogues run without the grace wait — one single-state fake (campaign
    /// #85), not a two-property read plus mocked dataref latches.</summary>
    private sealed class FakeAcpChannel : IAcpChannel
    {
        public AcpTransmitTarget Current { get; set; } = AcpTransmitTarget.Unknown;

        public bool IntKeyPushed { get; set; }

        public bool Latched { get; set; } = true;

        public AcpTransmitState Transmit => new(Current, IntKeyPushed);

        public event EventHandler? Changed;

        public bool IsReceiving(AcpChannelKind kind) => Latched;

        public Task<bool> AwaitReceiveAsync(AcpChannelKind kind, TimeSpan grace, CancellationToken cancellationToken)
            => Task.FromResult(Latched);

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
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

    // ---- ACP transmit gating (issue #72) ----

    [Fact]
    public void TransmitGating_IntSelected_GroundHailAccepted()
    {
        Handler("gsx.requestRefuel", CommandResult.Ok("requested"));
        _acpTransmit.Current = AcpTransmitTarget.Intercom;
        var service = CreateService("request refueling");

        Assert.True(service.TryHandle("cockpit to ground"));
        WaitFor(() => _executed.Count > 0);

        Assert.Equal(["gsx.requestRefuel"], _executed);
    }

    [Fact]
    public void TransmitGating_CabSelected_CabinHailAccepted()
    {
        Handler("gsx.requestBoarding", CommandResult.Ok("requested"));
        _acpTransmit.Current = AcpTransmitTarget.Cabin;
        var service = CreateService("start boarding");

        Assert.True(service.TryHandle("cockpit to cabin"));
        WaitFor(() => _executed.Count > 0);

        Assert.Equal(["gsx.requestBoarding"], _executed);
    }

    [Fact]
    public void TransmitGating_WrongChannel_CoachesOnce_ThenSilentlyUnanswered()
    {
        Handler("gsx.requestRefuel", CommandResult.Ok("requested"));
        _acpTransmit.Current = AcpTransmitTarget.Vhf1;
        var service = CreateService("request refueling");

        // Consumed (it IS a hail) but not answered — the FO coaches instead.
        Assert.True(service.TryHandle("cockpit to ground"));
        WaitFor(() => _arbiter.Requests.Count >= 1);

        Assert.Empty(_executed);
        Assert.Contains(_arbiter.Requests, r =>
            r.Role == SpeechRole.FirstOfficer && r.Tag == "crew.hail.coach");

        // Second unkeyed hail: still consumed, still unanswered, no second lesson.
        Assert.True(service.TryHandle("cockpit to ground"));
        Thread.Sleep(100);

        Assert.Empty(_executed);
        Assert.Single(_arbiter.Requests, r => r.Tag == "crew.hail.coach");
    }

    [Fact]
    public void TransmitGating_Disabled_WrongChannelStillAccepted()
    {
        Handler("gsx.requestRefuel", CommandResult.Ok("requested"));
        _speechOptions.AcpTransmitGating = false;
        _acpTransmit.Current = AcpTransmitTarget.Vhf1;
        var service = CreateService("request refueling");

        Assert.True(service.TryHandle("cockpit to ground"));
        WaitFor(() => _executed.Count > 0);

        Assert.Equal(["gsx.requestRefuel"], _executed);
    }

    [Fact]
    public void TransmitGating_UnknownState_AcceptsAsToday()
    {
        Handler("gsx.requestRefuel", CommandResult.Ok("requested"));
        _acpTransmit.Current = AcpTransmitTarget.Unknown; // dataref absent / connection stale
        var service = CreateService("request refueling");

        Assert.True(service.TryHandle("cockpit to ground"));
        WaitFor(() => _executed.Count > 0);

        Assert.Equal(["gsx.requestRefuel"], _executed);
    }

    [Fact]
    public void TransmitGating_IntKeyRequired_GroundHailNeedsTheKey()
    {
        Handler("gsx.requestRefuel", CommandResult.Ok("requested"));
        _speechOptions.AcpIntKeyRequired = true;
        _acpTransmit.Current = AcpTransmitTarget.Intercom;
        _acpTransmit.IntKeyPushed = false;
        var service = CreateService("request refueling");

        Assert.True(service.TryHandle("cockpit to ground"));
        WaitFor(() => _arbiter.Requests.Count >= 1);
        Assert.Empty(_executed);

        _acpTransmit.IntKeyPushed = true;
        Assert.True(service.TryHandle("cockpit to ground"));
        WaitFor(() => _executed.Count > 0);
        Assert.Equal(["gsx.requestRefuel"], _executed);
    }

    [Fact]
    public void TransmitGating_SelectorLeavesChannelMidDialogue_HangsUpWithStandingBy()
    {
        Handler("gsx.requestRefuel", CommandResult.Ok("requested"));
        _acpTransmit.Current = AcpTransmitTarget.Intercom;
        var service = CreateService(heardUtterance: null);
        // The pilot never speaks; the listen hangs until the hangup token cancels it.
        _mic.Setup(m => m.ListenAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyList<string> _, TimeSpan _, CancellationToken ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return (string?)null;
            });

        Assert.True(service.TryHandle("cockpit to ground"));
        WaitFor(() => _arbiter.Requests.Count >= 1); // the "go ahead" reply is out

        _acpTransmit.Current = AcpTransmitTarget.Vhf1;
        _acpTransmit.Raise();
        WaitFor(() => _arbiter.Requests.Any(r => r.Text == _groundOptions.StandingByText));

        Assert.Empty(_executed);
        Assert.Contains(_arbiter.Requests, r =>
            r.Role == SpeechRole.GroundCrew && r.Text == _groundOptions.StandingByText);
    }
}
