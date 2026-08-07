using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Commands;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Gsx;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

public sealed class GsxVoiceServiceTests
{
    private readonly CommandRegistry _registry = new();
    private readonly FakeArbiter _arbiter = new();
    private readonly Mock<IGsxDepartureControl> _departure = new();
    private readonly GsxOptions _options = new();
    private readonly List<string> _executed = [];

    /// <summary>Registers a fake handler that records the invocation and answers the given
    /// result — the REAL registry dispatches, per the test brief.</summary>
    private void Handler(string name, CommandResult result)
        => _registry.Register<EmptyCommandRequest, CommandResult>(name, (_, _) =>
        {
            lock (_executed)
            {
                _executed.Add(name);
            }

            return Task.FromResult(result);
        });

    private GsxVoiceService CreateService(bool withDepartureControl = true)
    {
        var options = new Mock<IOptionsMonitor<GsxOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(() => _options);
        return new GsxVoiceService(
            _registry,
            _arbiter,
            options.Object,
            NullLogger<GsxVoiceService>.Instance,
            withDepartureControl ? _departure.Object : null);
    }

    private static void WaitForDispatch(Func<bool> condition)
    {
        // TryHandle fires the dispatch without awaiting; every fake in the chain completes
        // synchronously, but spin briefly to stay deterministic under scheduler variance.
        SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("request boarding", "gsx.requestBoarding")]
    [InlineData("start boarding", "gsx.requestBoarding")]
    [InlineData("cabin crew start boarding", "gsx.requestBoarding")]
    [InlineData("request refueling", "gsx.requestRefuel")]
    [InlineData("call the fuel truck", "gsx.requestRefuel")]
    [InlineData("request catering", "gsx.requestCatering")]
    [InlineData("request pushback", "gsx.requestPushback")]
    [InlineData("request de-icing", "gsx.requestDeice")]
    [InlineData("call the next service", "gsx.forceNextService")]
    [InlineData("next service", "gsx.forceNextService")]
    public void Phrase_DispatchesItsCommand(string phrase, string command)
    {
        Handler(command, CommandResult.Ok("done"));
        var service = CreateService();

        Assert.True(service.TryHandle(phrase));
        WaitForDispatch(() => _executed.Count > 0);

        Assert.Equal([command], _executed);
    }

    [Fact]
    public void Phrases_MatchTrimmedAndCaseInsensitive()
    {
        Handler("gsx.requestBoarding", CommandResult.Ok("done"));
        var service = CreateService();

        Assert.True(service.TryHandle("  Request BOARDING  "));
        WaitForDispatch(() => _executed.Count > 0);
        Assert.Equal(["gsx.requestBoarding"], _executed);
    }

    [Fact]
    public void UnknownPhrase_IsNotConsumed()
    {
        var service = CreateService();

        Assert.False(service.TryHandle("request a limousine"));
        Assert.Empty(_arbiter.Requests);
    }

    [Fact]
    public void VoiceControlDisabled_ConsumesNothing()
    {
        Handler("gsx.requestBoarding", CommandResult.Ok("done"));
        _options.VoiceControlEnabled = false;
        var service = CreateService();

        Assert.False(service.TryHandle("request boarding"));
        Assert.Empty(_executed);
    }

    [Fact]
    public void CockpitToGround_NotStarted_StartsDepartureServices()
    {
        Handler("gsx.startDepartureServices", CommandResult.Ok("started"));
        Handler("gsx.forceNextService", CommandResult.Ok("forced"));
        _departure.SetupGet(d => d.Started).Returns(false);
        var service = CreateService();

        Assert.True(service.TryHandle("cockpit to ground"));
        WaitForDispatch(() => _executed.Count > 0);

        Assert.Equal(["gsx.startDepartureServices"], _executed);
        Assert.Contains(_arbiter.Requests, r => r.Tag == "gsx.voice" && r.Text == "Ground services started.");
    }

    [Fact]
    public void StartGroundServices_AlreadyStarted_ForcesNextService()
    {
        Handler("gsx.startDepartureServices", CommandResult.Ok("started"));
        Handler("gsx.forceNextService", CommandResult.Ok("forced"));
        _departure.SetupGet(d => d.Started).Returns(true);
        var service = CreateService();

        Assert.True(service.TryHandle("start ground services"));
        WaitForDispatch(() => _executed.Count > 0);

        Assert.Equal(["gsx.forceNextService"], _executed);
    }

    [Fact]
    public void CockpitToGround_NoDepartureSeam_FallsBackToForceNext_OnAlreadySatisfied()
    {
        Handler("gsx.startDepartureServices", CommandResult.AlreadySatisfied("already started"));
        Handler("gsx.forceNextService", CommandResult.Ok("forced"));
        var service = CreateService(withDepartureControl: false);

        Assert.True(service.TryHandle("cockpit to ground"));
        WaitForDispatch(() => _executed.Count >= 2);

        Assert.Equal(["gsx.startDepartureServices", "gsx.forceNextService"], _executed);
    }

    [Fact]
    public void Success_SpeaksTheShortConfirmation_TaggedGsxVoice()
    {
        Handler("gsx.requestBoarding", CommandResult.Ok("Boarding requested — awaiting GSX confirmation."));
        var service = CreateService();

        service.TryHandle("request boarding");
        WaitForDispatch(() => _arbiter.Requests.Count > 0);

        var spoken = Assert.Single(_arbiter.Requests);
        Assert.Equal("Boarding requested.", spoken.Text);
        Assert.Equal("gsx.voice", spoken.Tag);
    }

    [Fact]
    public void NonSuccess_SpeaksTheReason()
    {
        Handler("gsx.requestDeice", CommandResult.PreconditionFailed("GSX does not offer de-icing in the current gate context."));
        var service = CreateService();

        service.TryHandle("request de-icing");
        WaitForDispatch(() => _arbiter.Requests.Count > 0);

        var spoken = Assert.Single(_arbiter.Requests);
        Assert.Equal("GSX does not offer de-icing in the current gate context.", spoken.Text);
        Assert.Equal("gsx.voice", spoken.Tag);
    }

    [Theory]
    [InlineData("start boarding")]
    [InlineData("cabin crew start boarding")]
    public void CabinPhrases_OnSuccess_AddTheCabinAck(string phrase)
    {
        Handler("gsx.requestBoarding", CommandResult.Ok("requested"));
        var service = CreateService();

        service.TryHandle(phrase);
        WaitForDispatch(() => _arbiter.Requests.Count >= 2);

        Assert.Contains(_arbiter.Requests, r => r.Tag == "cabin.boarding.ack" && r.Text == "Boarding underway.");
    }

    [Fact]
    public void CabinPhrase_OnAlreadySatisfied_StillAcks()
    {
        Handler("gsx.requestBoarding", CommandResult.AlreadySatisfied("boarding is already in progress."));
        var service = CreateService();

        service.TryHandle("start boarding");
        WaitForDispatch(() => _arbiter.Requests.Count >= 2);

        Assert.Contains(_arbiter.Requests, r => r.Tag == "cabin.boarding.ack");
    }

    [Fact]
    public void CabinPhrase_OnRefusal_NeverAcks()
    {
        Handler("gsx.requestBoarding", CommandResult.PreconditionFailed("not callable right now."));
        var service = CreateService();

        service.TryHandle("cabin crew start boarding");
        WaitForDispatch(() => _arbiter.Requests.Count >= 1);

        Assert.DoesNotContain(_arbiter.Requests, r => r.Tag == "cabin.boarding.ack");
    }

    [Fact]
    public void PlainRequestBoarding_NeverAcksAsCabin()
    {
        Handler("gsx.requestBoarding", CommandResult.Ok("requested"));
        var service = CreateService();

        service.TryHandle("request boarding");
        WaitForDispatch(() => _arbiter.Requests.Count >= 1);

        Assert.DoesNotContain(_arbiter.Requests, r => r.Tag == "cabin.boarding.ack");
    }

    [Fact]
    public void Phrases_ContainEveryMappedUtterance()
    {
        var service = CreateService();

        string[] expected =
        [
            "cockpit to ground", "start ground services", "call the next service", "next service",
            "request boarding", "start boarding", "cabin crew start boarding",
            "request refueling", "call the fuel truck", "request catering",
            "request pushback", "request de-icing",
        ];
        foreach (var phrase in expected)
        {
            Assert.Contains(phrase, service.Phrases, StringComparer.OrdinalIgnoreCase);
        }
    }
}
