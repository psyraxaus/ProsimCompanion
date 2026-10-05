using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx;
using ProsimCompanion.Gsx.Menu;
using ProsimCompanion.Gsx.Mirror;
using ProsimCompanion.Gsx.Sync;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>
/// The parking wake (issue #141). 2026-10-05 LKPR, 0.6.0-rc.17: GSX sat in "taxing on
/// ground" with no parking for 2 min 10 s, the FO said "GSX doesn't recognise our parking
/// position", and the pilot's GSX toolbar click made GSX name "Gate C 28" 0.7 s later. The
/// prep hold now asks for the menu itself; a conflict is only what survives that.
/// </summary>
public sealed class GsxParkingWakeTests : IAsyncDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 1, 4, 13, TimeSpan.Zero);

    private readonly string _dir = Directory.CreateTempSubdirectory("pc-wake-").FullName;
    private readonly JsonlEventLog _eventLog;
    private readonly FakeGsxApi _api = new();
    private readonly Mock<ISimVars> _simVars = new();
    private readonly Mock<IFlightPhaseSource> _flight = new();
    private readonly GsxParkingWakeService _wake;

    public GsxParkingWakeTests()
    {
        _eventLog = new JsonlEventLog(_dir, NullLogger<JsonlEventLog>.Instance);
        _flight.Setup(f => f.Snapshot()).Returns(new FlightStateView(
            FlightPhase.ColdAndDark,
            new FlightDataSnapshot { IsValid = true, OnGround = true, ParkBrakeSet = true },
            false,
            true));
        _wake = new GsxParkingWakeService(
            _api,
            new GsxMenuOpener(_api, _simVars.Object, NullLogger<GsxMenuOpener>.Instance),
            _flight.Object,
            new GsxDiagnosticsStore(),
            _eventLog,
            NullLogger<GsxParkingWakeService>.Instance)
        {
            RungWait = TimeSpan.FromMilliseconds(250),
            SettleWait = TimeSpan.FromMilliseconds(250),
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _eventLog.DisposeAsync();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void ShowMenu(string title, params string[] entries)
    {
        _api.Mirror.ApplyState("menu", new JsonObject
        {
            ["title"] = title,
            ["entries"] = new JsonArray([.. entries.Select(e => JsonValue.Create(e))]),
        });
        _api.Mirror.ApplyState("menuShown", JsonValue.Create(true));
    }

    private void NameParking(string parking) => _api.Mirror.ApplyState("parking", JsonValue.Create(parking));

    private Task<GsxParkingWakeStatus> Step(double holdSeconds)
        => _wake.RunStepAsync(T0, T0.AddSeconds(holdSeconds));

    // --- the plan (pure) ---------------------------------------------------------------

    [Theory]
    [InlineData(0, nameof(ParkingWakeAction.Wait))]      // first cycle of the hold: GSX may still name it
    [InlineData(2.9, nameof(ParkingWakeAction.Wait))]
    [InlineData(3, nameof(ParkingWakeAction.OpenMenu))]
    [InlineData(5, nameof(ParkingWakeAction.OpenMenu))]  // the second prep cycle
    public void FirstAttempt_WaitsOneCycle_ThenOpens(double holdSeconds, string expected)
        => Assert.Equal(
            expected,
            ParkingWakePlan.Next(TimeSpan.FromSeconds(holdSeconds), true, false, attempts: 0, sinceLastAttempt: null).ToString());

    [Theory]
    [InlineData(1, 5, nameof(ParkingWakeAction.Wait))]
    [InlineData(1, 29.9, nameof(ParkingWakeAction.Wait))]
    [InlineData(1, 30, nameof(ParkingWakeAction.OpenMenu))]
    [InlineData(2, 30, nameof(ParkingWakeAction.OpenMenu))]
    [InlineData(3, 30, nameof(ParkingWakeAction.GiveUp))]
    [InlineData(3, 600, nameof(ParkingWakeAction.GiveUp))]
    public void Retries_AreSpaced_AndBounded(int attempts, double sinceLastSeconds, string expected)
        => Assert.Equal(
            expected,
            ParkingWakePlan.Next(
                TimeSpan.FromMinutes(5), true, false, attempts, TimeSpan.FromSeconds(sinceLastSeconds)).ToString());

    [Fact]
    public void AMenuOnScreen_IsNeverOpenedOver()
        => Assert.Equal(
            ParkingWakeAction.Stand,
            ParkingWakePlan.Next(TimeSpan.FromSeconds(10), true, menuShown: true, attempts: 0, sinceLastAttempt: null));

    [Fact]
    public void MovingOrRunning_NothingIsSent()
        => Assert.Equal(
            ParkingWakeAction.Stand,
            ParkingWakePlan.Next(TimeSpan.FromSeconds(10), parkedEnginesOff: false, false, attempts: 0, sinceLastAttempt: null));

    // --- the step ----------------------------------------------------------------------

    [Fact]
    public async Task TooEarlyInTheHold_SendsNothing()
    {
        Assert.Equal(GsxParkingWakeStatus.Waiting, await Step(0));
        Assert.Empty(_api.Commands);
    }

    /// <summary>The 2026-10-04 arrival order: menu shown 21:44:05.98Z, parking 21:44:06.37Z.</summary>
    [Fact]
    public async Task MenuOpen_NamesTheParking_AndTheRootMenuIsClosedAgain()
    {
        _api.OnCommand = (verb, _) =>
        {
            if (verb == "menu.open")
            {
                ShowMenu("Activate Services at LKPR/Ruzyne", "Request Deboarding", "Reposition Aircraft");
                NameParking("Gate C 28");
            }
            return new GsxCommandResult(true, "ok", null, null);
        };

        Assert.Equal(GsxParkingWakeStatus.Named, await Step(5));

        Assert.Equal(["menu.open", "menu.close"], _api.Commands.Select(c => c.Verb));
        _simVars.Verify(s => s.WriteAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>The 2026-10-05 toolbar-click order: parking 0.7 s after the request, menu 3 s
    /// after it — the parking alone ends the rung.</summary>
    [Fact]
    public async Task ParkingNamedBeforeAnyMenuShows_IsNamed()
    {
        _api.OnCommand = (verb, _) =>
        {
            if (verb == "menu.open")
            {
                NameParking("Gate C 28");
            }
            return new GsxCommandResult(true, "ok", null, null);
        };

        Assert.Equal(GsxParkingWakeStatus.Named, await Step(5));
        Assert.DoesNotContain(_api.Commands, c => c.Verb == "menu.close"); // nothing shown, nothing to close
    }

    /// <summary>Issue #141's other half: menu.open acknowledged, nothing shown — the legacy
    /// menu LVAR is the rung that wakes GSX.</summary>
    [Fact]
    public async Task ApiOpenShowsNothing_TheMenuLvarWakesGsx()
    {
        _simVars
            .Setup(s => s.WriteAsync(GsxLvarNames.MenuOpen, 1, It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                NameParking("Gate C 28");
                ShowMenu("Activate Services at LKPR/Ruzyne", "Reposition Aircraft");
            })
            .Returns(Task.CompletedTask);

        Assert.Equal(GsxParkingWakeStatus.Named, await Step(5));

        _simVars.Verify(s => s.WriteAsync(GsxLvarNames.MenuOpen, 1, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(["menu.open", "menu.close"], _api.Commands.Select(c => c.Verb));
    }

    /// <summary>The genuine issue #44 conflict: the menu comes up and GSX still names no
    /// parking. The menu is GSX's question — it stays up and nothing is picked.</summary>
    [Fact]
    public async Task MenuUpWithoutAParking_IsUnresolved_AndTheMenuStaysForThePilot()
    {
        _api.OnCommand = (verb, _) =>
        {
            if (verb == "menu.open")
            {
                ShowMenu("Select Position at EGLL/Heathrow", "Reposition from Map", "Terminal 5B");
            }
            return new GsxCommandResult(true, "ok", null, null);
        };

        Assert.Equal(GsxParkingWakeStatus.Unresolved, await Step(5));

        Assert.Equal(["menu.open"], _api.Commands.Select(c => c.Verb));
        Assert.True(_api.Mirror.MenuShown);
    }

    [Fact]
    public async Task NoRungShowsAnything_IsMenuUnreachable_AndStaysSoBetweenRetries()
    {
        Assert.Equal(GsxParkingWakeStatus.MenuUnreachable, await Step(5));
        Assert.Single(_api.Commands);

        // The next prep cycle is inside the retry spacing: same verdict, nothing re-sent.
        Assert.Equal(GsxParkingWakeStatus.MenuUnreachable, await Step(10));
        Assert.Single(_api.Commands);

        // A later attempt that works ends the episode.
        _api.OnCommand = (_, _) =>
        {
            NameParking("Gate C 28");
            return new GsxCommandResult(true, "ok", null, null);
        };
        Assert.Equal(GsxParkingWakeStatus.Named, await Step(40));
    }

    [Fact]
    public async Task ThePilotsOwnMenu_IsNeverOpenedOverOrClosed()
    {
        ShowMenu("Activate Services at LKPR/Ruzyne", "Reposition Aircraft");

        Assert.Equal(GsxParkingWakeStatus.Unresolved, await Step(5));
        Assert.Empty(_api.Commands);
    }

    [Fact]
    public async Task EnginesRunning_NothingIsSent()
    {
        _flight.Setup(f => f.Snapshot()).Returns(new FlightStateView(
            FlightPhase.Shutdown,
            new FlightDataSnapshot { IsValid = true, OnGround = true, AnyEngineRunning = true },
            false,
            true));

        Assert.Equal(GsxParkingWakeStatus.Unresolved, await Step(5));
        Assert.Empty(_api.Commands);
    }

    [Fact]
    public async Task Reset_StartsTheNextHoldFromTheFirstRung()
    {
        Assert.Equal(GsxParkingWakeStatus.MenuUnreachable, await Step(5));

        _wake.Reset();

        Assert.Equal(GsxParkingWakeStatus.Waiting, await Step(0));
        Assert.Equal(GsxParkingWakeStatus.MenuUnreachable, await Step(5));
        Assert.Equal(2, _api.Commands.Count(c => c.Verb == "menu.open"));
    }

    // --- what the coordinator makes of it ---------------------------------------------------

    /// <summary>2026-10-05 LKPR: the FO spoke 28 s into the hold at a stand GSX named the
    /// moment its menu opened. A hold whose wake has not concluded publishes nothing, however
    /// long it stands; one the wake resolved never does.</summary>
    [Theory]
    [InlineData(GsxParkingWakeStatus.Waiting, 60, false)]
    [InlineData(GsxParkingWakeStatus.Named, 60, false)]
    [InlineData(GsxParkingWakeStatus.Unresolved, 19.9, false)]
    [InlineData(GsxParkingWakeStatus.Unresolved, 20, true)]
    [InlineData(GsxParkingWakeStatus.MenuUnreachable, 19.9, false)]
    [InlineData(GsxParkingWakeStatus.MenuUnreachable, 20, true)]
    public void Conflict_NeedsAFailedWake_AndTheGrace(GsxParkingWakeStatus wake, double heldSeconds, bool expected)
        => Assert.Equal(
            expected,
            GsxGroundPrepCoordinator.ConflictDue(wake, T0, T0.AddSeconds(heldSeconds)));

    [Fact]
    public void AHoldThatIsNotAboutTheParking_IsNeverAConflict()
        => Assert.False(GsxGroundPrepCoordinator.ConflictDue(null, T0, T0.AddMinutes(5)));

    [Fact]
    public void HoldReason_SaysWhatTheAppIsDoing_OrTheOneActionLeft()
    {
        const string machine = "GSX has not identified the parking — select the stand in the GSX menu, or reposition";

        Assert.Contains(
            "opening the GSX menu",
            GsxGroundPrepCoordinator.UnknownParkingReason(machine, GsxParkingWakeStatus.Waiting));
        Assert.Contains(
            "open the GSX menu once from the toolbar",
            GsxGroundPrepCoordinator.UnknownParkingReason(machine, GsxParkingWakeStatus.MenuUnreachable));
        Assert.Equal(machine, GsxGroundPrepCoordinator.UnknownParkingReason(machine, GsxParkingWakeStatus.Unresolved));
    }

    private sealed class FakeGsxApi : IGsxRemoteApi
    {
#pragma warning disable CS0067 // raised by the real client; not needed by these scenarios
        public event Action<GsxReadiness>? ReadinessChanged;
#pragma warning restore CS0067

        public GsxReadiness Readiness { get; set; } = GsxReadiness.Ready;
        public GsxStateMirror Mirror { get; } = new();
        public List<(string Verb, JsonObject? Args)> Commands { get; } = [];
        public Func<string, JsonObject?, GsxCommandResult>? OnCommand { get; set; }

        public bool HasCapability(string token) => true;

        public Task<GsxCommandResult> SendCommandAsync(string verb, JsonObject? args, CancellationToken cancellationToken = default)
        {
            Commands.Add((verb, args));
            return Task.FromResult(OnCommand?.Invoke(verb, args) ?? new GsxCommandResult(true, "ok", null, null));
        }
    }
}
