using Moq;
using ProsimCompanion.App.Hosting;
using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.App;

public sealed class StatusApiTests
{
    private static IGsxDepartureControl Departure(bool started, bool complete = false)
    {
        var departure = new Mock<IGsxDepartureControl>();
        departure.SetupGet(d => d.Started).Returns(started);
        departure.SetupGet(d => d.Complete).Returns(complete);
        return departure.Object;
    }

    private static StatusResponse Build(
        FlightPhase? phase = null,
        IReadOnlyList<KeyValuePair<string, ConnectionState>>? connections = null,
        IGsxDepartureControl? departure = null,
        GsxDiagnosticsSnapshot? snapshot = null,
        double? refuelPercent = null,
        int? paxBoarded = null,
        int? paxTotal = null,
        int? paxRemaining = null,
        ChecklistView? checklist = null,
        SpeechStatusSnapshot? speech = null)
        => StatusApiEndpoints.BuildStatus(
            phase, connections ?? [], departure, snapshot, refuelPercent, paxBoarded, paxTotal, paxRemaining, checklist, speech);

    [Fact]
    public void AbsentPillars_YieldNullsAndSafeDefaults()
    {
        var response = Build();

        Assert.Equal("unknown", response.Phase);
        Assert.False(response.Connections.Prosim);
        Assert.False(response.Connections.Msfs);
        Assert.False(response.Connections.Gsx);
        Assert.Null(response.Gsx);
        Assert.Null(response.Checklist);
    }

    [Fact]
    public void Voice_IsNullWithoutTheSpeechPillar_AndMirrorsTheStoreOtherwise()
    {
        Assert.Null(Build().Voice);

        var snapshot = SpeechStatusSnapshot.Empty with { Listening = true, ListeningPaused = false };
        var listening = Build(speech: snapshot).Voice;
        Assert.NotNull(listening);
        Assert.True(listening.Listening);
        Assert.False(listening.Paused);

        var paused = Build(speech: snapshot with { Listening = false, ListeningPaused = true }).Voice;
        Assert.NotNull(paused);
        Assert.False(paused.Listening);
        Assert.True(paused.Paused);
    }

    [Fact]
    public void Phase_IsCamelCased()
    {
        var response = Build(phase: FlightPhase.PushbackAndStart);

        Assert.Equal("pushbackAndStart", response.Phase);
    }

    [Fact]
    public void Connections_MapFromTheStoreSnapshot()
    {
        var response = Build(connections:
        [
            new(Subsystems.Prosim, ConnectionState.Connected),
            new(Subsystems.SimConnect, ConnectionState.Connecting),
            new(Subsystems.Gsx, ConnectionState.Connected),
        ]);

        Assert.True(response.Connections.Prosim);
        Assert.False(response.Connections.Msfs);
        Assert.True(response.Connections.Gsx);
    }

    [Fact]
    public void AutomationActive_TracksStartedAndNotComplete()
    {
        Assert.True(Build(departure: Departure(started: true)).Gsx!.AutomationActive);
        Assert.False(Build(departure: Departure(started: false)).Gsx!.AutomationActive);
        Assert.False(Build(departure: Departure(started: true, complete: true)).Gsx!.AutomationActive);
    }

    [Fact]
    public void ServiceBoard_MapsStagesToWireStates()
    {
        var snapshot = GsxDiagnosticsSnapshot.Empty with
        {
            ServiceBoard =
            [
                new GsxServiceBoardRow("Refueling", GsxServiceStage.Completed, null),
                new GsxServiceBoardRow("Catering", GsxServiceStage.Active, "Loading trolleys"),
                new GsxServiceBoardRow("Water", GsxServiceStage.Called, null),
                new GsxServiceBoardRow("Cleaning", GsxServiceStage.Skipped, "first leg"),
                new GsxServiceBoardRow("Boarding", GsxServiceStage.Held, "waiting for refuel"),
                new GsxServiceBoardRow("Lavatory", GsxServiceStage.Waiting, null),
            ],
        };

        var gsx = Build(departure: Departure(started: true), snapshot: snapshot).Gsx!;

        Assert.Equal(
            new[]
            {
                ("Refueling", StatusServiceState.Completed, ""),
                ("Catering", StatusServiceState.Active, "Loading trolleys"),
                ("Water", StatusServiceState.Requested, ""),
                ("Cleaning", StatusServiceState.Skipped, "first leg"),
                ("Boarding", StatusServiceState.Callable, "waiting for refuel"),
                // Waiting = not yet reached in the sequence, still manually callable —
                // notAvailable would dim its Stream Deck key.
                ("Lavatory", StatusServiceState.Callable, ""),
            },
            gsx.Services.Select(row => (row.Type, row.State, row.Detail)).ToArray());
    }

    [Fact]
    public void NextService_IsTheFirstWaitingOrHeldRow()
    {
        var snapshot = GsxDiagnosticsSnapshot.Empty with
        {
            ServiceBoard =
            [
                new GsxServiceBoardRow("Refueling", GsxServiceStage.Completed, null),
                new GsxServiceBoardRow("Catering", GsxServiceStage.Held, "hold reason"),
                new GsxServiceBoardRow("Boarding", GsxServiceStage.Waiting, null),
            ],
        };

        Assert.Equal("Catering", Build(departure: Departure(true), snapshot: snapshot).Gsx!.NextService);
    }

    [Fact]
    public void NextService_NullWhenEverythingIsUnderwayOrDone()
    {
        var snapshot = GsxDiagnosticsSnapshot.Empty with
        {
            ServiceBoard =
            [
                new GsxServiceBoardRow("Refueling", GsxServiceStage.Completed, null),
                new GsxServiceBoardRow("Boarding", GsxServiceStage.Active, null),
            ],
        };

        Assert.Null(Build(departure: Departure(true), snapshot: snapshot).Gsx!.NextService);
    }

    [Fact]
    public void EmptyBoard_FallsBackToTheMirrorServiceList()
    {
        var snapshot = GsxDiagnosticsSnapshot.Empty with
        {
            Services =
            [
                new GsxServiceView("Refueling", "Refueling", "available", "Callable", true, false, null, GsxServiceStage.Waiting),
                new GsxServiceView("Departure", "Departure", "unavailable", "NotAvailable", false, false, null, GsxServiceStage.Skipped),
            ],
        };

        var gsx = Build(departure: Departure(false), snapshot: snapshot).Gsx!;

        Assert.Equal(
            new[]
            {
                ("Refueling", StatusServiceState.Callable),
                ("Departure", StatusServiceState.Skipped),
            },
            gsx.Services.Select(row => (row.Type, row.State)).ToArray());
    }

    [Fact]
    public void Board_IsUnionedWithUncoveredMirrorServices()
    {
        // Board-else-mirror used to hide jetway/stairs/GPU whenever automation had published a
        // board (they are never configured departure services) — the union keeps them, and the
        // mirror rows carry the LATCHED stage so completed quick services stay completed.
        var snapshot = GsxDiagnosticsSnapshot.Empty with
        {
            ServiceBoard =
            [
                new GsxServiceBoardRow("Refueling", GsxServiceStage.Completed, null),
                new GsxServiceBoardRow("Boarding", GsxServiceStage.Active, null),
            ],
            Services =
            [
                new GsxServiceView("Refueling", "Refueling", "available", "Callable", true, false, null, GsxServiceStage.Completed),
                new GsxServiceView("OperateJetways", "Operate Jetways", "completed", "Completed", true, false, null, GsxServiceStage.Completed),
                new GsxServiceView("GPU", "GPU", "available", "Callable", true, false, null, GsxServiceStage.Waiting),
            ],
        };

        var gsx = Build(departure: Departure(true), snapshot: snapshot).Gsx!;

        Assert.Equal(
            new[]
            {
                ("Refueling", StatusServiceState.Completed),
                ("Boarding", StatusServiceState.Active),
                ("OperateJetways", StatusServiceState.Completed),
                ("GPU", StatusServiceState.Callable),
            },
            gsx.Services.Select(row => (row.Type, row.State)).ToArray());
    }

    [Fact]
    public void ProgressFigures_PassThrough()
    {
        var gsx = Build(
            departure: Departure(true), refuelPercent: 62.5,
            paxBoarded: 87, paxTotal: 180, paxRemaining: 93).Gsx!;

        Assert.Equal(62.5, gsx.RefuelPercent);
        Assert.Equal(87, gsx.PaxBoarded);
        Assert.Equal(180, gsx.PaxTotal);
        Assert.Equal(93, gsx.PaxRemaining);
    }

    [Fact]
    public void Checklist_ProjectsTheActiveItem()
    {
        var view = new ChecklistView(
            "Before Start",
            IsComplete: false,
            ActiveIndex: 1,
            Items:
            [
                new ChecklistItemView("Parking Brake", "Set", ChecklistItemStatus.Done, false, false),
                new ChecklistItemView("Beacon", "On", ChecklistItemStatus.Active, false, false),
                new ChecklistItemView("Doors", "Closed", ChecklistItemStatus.Pending, false, false),
            ]);

        var checklist = Build(checklist: view).Checklist!;

        Assert.Equal("Before Start", checklist.Name);
        Assert.Equal("Beacon", checklist.Item);
        Assert.Equal(1, checklist.Index);
        Assert.Equal(3, checklist.Count);
    }

    [Fact]
    public void CompletedChecklist_ReportsAnEmptyItem()
    {
        var view = new ChecklistView(
            "After Landing",
            IsComplete: true,
            ActiveIndex: 1,
            Items: [new ChecklistItemView("Flaps", "Up", ChecklistItemStatus.Done, false, false)]);

        Assert.Equal("", Build(checklist: view).Checklist!.Item);
    }
}
