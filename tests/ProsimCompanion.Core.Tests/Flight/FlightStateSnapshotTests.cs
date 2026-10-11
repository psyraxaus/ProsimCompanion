using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Tests.Speech;
using Xunit;

namespace ProsimCompanion.Core.Tests.Flight;

/// <summary>Issue #167: the flight-state snapshot — the startup rule, the file, and the
/// service's restore / write / invalidate / plan-only-fetch behaviour.</summary>
public sealed class FlightStateSnapshotTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 22, 37, 41, TimeSpan.Zero);
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(18);

    private readonly string _dir = Directory.CreateTempSubdirectory("pc-flightstate-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private static FlightStateSnapshotFile Snapshot(
        DateTimeOffset? writtenUtc = null,
        string? requestId = "189301241",
        string? title = "Airbus A320 FIN",
        Dictionary<string, JsonNode?>? parts = null)
        => new(
            FlightStateSnapshotFile.CurrentVersion,
            writtenUtc ?? Now.AddMinutes(-40),
            "session-20261010-205753",
            new FlightStateIdentity(requestId, "EFHK", "EGCC", "FIN1361", title),
            parts ?? []);

    // ---- the startup rule ----------------------------------------------------------------------

    [Fact]
    public void Rule_RestoresWhenTheEfbStillReportsThePlanImported_AndTheAircraftMatches()
    {
        var (verdict, _) = FlightStateSnapshotRules.Validate(Snapshot(), Now, MaxAge, planImported: true, null, null, "Airbus A320 FIN");
        Assert.Equal(FlightStateRestoreVerdict.Restore, verdict);
    }

    [Fact]
    public void Rule_RestoresOnTheMcduRoute_WhenTheEfbFlagIsOff()
    {
        var (verdict, _) = FlightStateSnapshotRules.Validate(Snapshot(), Now, MaxAge, planImported: false, "EFHK", "EGCC", "Airbus A320 FIN");
        Assert.Equal(FlightStateRestoreVerdict.Restore, verdict);
    }

    [Fact]
    public void Rule_WaitsWhileTheEvidenceHasNotArrived()
    {
        Assert.Equal(FlightStateRestoreVerdict.Wait, FlightStateSnapshotRules.Validate(Snapshot(), Now, MaxAge, true, null, null, aircraftTitle: null).Verdict);
        Assert.Equal(FlightStateRestoreVerdict.Wait, FlightStateSnapshotRules.Validate(Snapshot(), Now, MaxAge, null, null, null, "Airbus A320 FIN").Verdict);
        Assert.Equal(FlightStateRestoreVerdict.Wait, FlightStateSnapshotRules.Validate(Snapshot(), Now, MaxAge, false, "----", null, "Airbus A320 FIN").Verdict);
    }

    [Fact]
    public void Rule_DiscardsAStaleSnapshot_AnotherAircraft_OrAnotherRoute()
    {
        Assert.Equal(FlightStateRestoreVerdict.Discard, FlightStateSnapshotRules.Validate(Snapshot(writtenUtc: Now.AddHours(-19)), Now, MaxAge, true, null, null, "Airbus A320 FIN").Verdict);
        Assert.Equal(FlightStateRestoreVerdict.Discard, FlightStateSnapshotRules.Validate(Snapshot(), Now, MaxAge, true, null, null, "Boeing 737-800").Verdict);
        var (verdict, reason) = FlightStateSnapshotRules.Validate(Snapshot(), Now, MaxAge, false, "EGCC", "EFHK", "Airbus A320 FIN");
        Assert.Equal(FlightStateRestoreVerdict.Discard, verdict);
        Assert.Contains("EGCC-EFHK", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Rule_WithoutAnOfpIdentity_NeedsOnlyTheAircraftAndTheAge()
    {
        Assert.Equal(FlightStateRestoreVerdict.Restore, FlightStateSnapshotRules.Validate(Snapshot(requestId: null), Now, MaxAge, false, "----", "----", "Airbus A320 FIN").Verdict);
        Assert.Equal(FlightStateRestoreVerdict.Restore, FlightStateSnapshotRules.Validate(Snapshot(requestId: null, title: null), Now, MaxAge, null, null, null, null).Verdict);
    }

    // ---- the file ---------------------------------------------------------------------------------

    [Fact]
    public void Store_RoundTripsAtomically_AndMovesACorruptFileAside()
    {
        var path = Path.Combine(_dir, "state", "flight-state.json");
        var store = new FlightStateSnapshotStore(NullLogger<FlightStateSnapshotStore>.Instance, path);
        var parts = new Dictionary<string, JsonNode?>
        {
            ["flightTimes"] = JsonSerializer.SerializeToNode(new FlightTimesSnapshot(Now.AddHours(-2), Now.AddHours(-1.8), null, null, 9879, 9580), FlightStateSnapshotStore.JsonOptions),
        };

        Assert.True(store.Save(Snapshot(parts: parts)));
        Assert.False(File.Exists(path + ".tmp"));
        var loaded = store.Load()!;
        Assert.Equal("189301241", loaded.Identity.OfpRequestId);
        Assert.Equal("session-20261010-205753", loaded.SessionId);
        var times = loaded.Parts["flightTimes"]!.Deserialize<FlightTimesSnapshot>(FlightStateSnapshotStore.JsonOptions)!;
        Assert.Equal(9580, times.TakeoffFobKg);

        File.WriteAllText(path, "{ not json");
        Assert.Null(store.Load());
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.corrupt-*.bak"));

        store.Clear();
        Assert.Null(store.Load());
    }

    // ---- the service ------------------------------------------------------------------------------

    private sealed class FakePart(string key, int order) : IFlightStatePart
    {
        public string Key => key;

        public int RestoreOrder => order;

        public event Action? Changed;

        public JsonNode? Value { get; set; }

        public List<string> Log { get; } = [];

        public bool Throw { get; set; }

        public JsonNode? Capture() => Value?.DeepClone();

        public void Restore(JsonNode node)
        {
            if (Throw)
            {
                throw new InvalidOperationException("boom");
            }

            Value = node.DeepClone();
            Log.Add(key);
        }

        public void Touch() => Changed?.Invoke();
    }

    private sealed class Harness
    {
        public FakePhaseSource Phase { get; } = new();
        public GroundOpsSignals Signals { get; } = new();
        public OfpStore Ofp { get; } = new();
        public FakeDataRefs Refs { get; } = new();
        public FlightStateSnapshotStore File { get; }
        public FlightStateOptions Options { get; } = new();
        public Mock<ISimbriefImporter> Importer { get; } = new();
        public DateTimeOffset Now { get; set; } = FlightStateSnapshotTests.Now;
        public List<FakePart> Parts { get; }
        public FlightStateSnapshotService Service { get; }

        public Harness(string dir, IEnumerable<FakePart>? parts = null, bool withImporter = false)
        {
            File = new FlightStateSnapshotStore(NullLogger<FlightStateSnapshotStore>.Instance, Path.Combine(dir, "flight-state.json"));
            Parts = parts?.ToList() ?? [new FakePart("notifications", 5), new FakePart("flightTimes", 20), new FakePart("minima", 40)];
            var monitor = new Mock<Microsoft.Extensions.Options.IOptionsMonitor<FlightStateOptions>>();
            monitor.SetupGet(m => m.CurrentValue).Returns(() => Options);
            Service = new FlightStateSnapshotService(
                Parts, File, Ofp, Phase, Signals, SpeechTestSupport.TempEventLog(), monitor.Object,
                NullLogger<FlightStateSnapshotService>.Instance,
                finalizer: null,
                importer: withImporter ? Importer.Object : null,
                dataRefs: Refs,
                clock: () => Now);
        }

        public void Evidence(string title = "Airbus A320 FIN", bool imported = true, string? origin = null, string? destination = null)
        {
            Refs.Values[ProsimDataRefNames.AircraftTitle.Name] = title;
            Refs.Values[ProsimDataRefNames.EfbSimbriefPlanImported.Name] = imported;
            if (origin is not null)
            {
                Refs.Values[ProsimDataRefNames.FmsOrigin.Name] = origin;
                Refs.Values[ProsimDataRefNames.FmsDestination.Name] = destination;
            }
        }
    }

    private static OfpData Ofp(string requestId = "189301241") => new()
    {
        RequestId = requestId,
        OriginIcao = "EFHK",
        DestinationIcao = "EGCC",
        FlightNumber = "FIN1361",
        PaxCount = 122,
        Navlog = [new OfpFix("EFHK", new GeoPoint(60.3, 24.9), 9000, TimeSpan.Zero, 0, false), new OfpFix("EGCC", new GeoPoint(53.4, -2.3), 5000, TimeSpan.FromHours(3), 0, false)],
    };

    [Fact]
    public void Restore_WaitsForTheEvidence_ThenAppliesThePartsInOrder_AndRecordsIt()
    {
        var parts = new Dictionary<string, JsonNode?>
        {
            ["minima"] = JsonValue.Create("m"),
            ["flightTimes"] = JsonValue.Create("t"),
            ["notifications"] = JsonValue.Create("n"),
            ["unknownPart"] = JsonValue.Create("x"),
        };
        var h = new Harness(_dir);
        h.File.Save(Snapshot(parts: parts));
        h.Service.Start();

        Assert.False(h.Service.Decided);           // no title / EFB flag yet
        h.Evidence();
        h.Service.EvaluateRestore();

        Assert.True(h.Service.Decided);
        Assert.True(h.Service.Restored);
        var order = h.Parts.SelectMany(p => p.Log).ToList();
        Assert.Equal(["notifications", "flightTimes", "minima"], order);
        Assert.NotNull(h.File.Load());             // a restored file stays until the leg ends
    }

    [Fact]
    public void Restore_DiscardsAndDeletes_ForAnotherAircraft()
    {
        var h = new Harness(_dir);
        h.File.Save(Snapshot(parts: new() { ["minima"] = JsonValue.Create("m") }));
        h.Service.Start();
        h.Evidence(title: "Boeing 737-800");
        h.Service.EvaluateRestore();

        Assert.True(h.Service.Decided);
        Assert.False(h.Service.Restored);
        Assert.Null(h.File.Load());
        Assert.Empty(h.Parts.SelectMany(p => p.Log));
    }

    [Fact]
    public void Restore_OnAColdAndDarkAircraft_IsRefused()
    {
        var h = new Harness(_dir);
        h.File.Save(Snapshot(parts: new() { ["minima"] = JsonValue.Create("m") }));
        h.Service.Start();
        h.Phase.SetPhase(FlightPhase.ColdAndDark);
        h.Evidence();
        h.Service.EvaluateRestore();

        Assert.True(h.Service.Decided);
        Assert.False(h.Service.Restored);
        Assert.Null(h.File.Load());
    }

    [Fact]
    public void Restore_APartThatThrows_IsSkipped_TheOthersStillRestore()
    {
        var bad = new FakePart("flightTimes", 20) { Throw = true };
        var good = new FakePart("minima", 40);
        var h = new Harness(_dir, [bad, good]);
        h.File.Save(Snapshot(parts: new() { ["flightTimes"] = JsonValue.Create("t"), ["minima"] = JsonValue.Create("m") }));
        h.Service.Start();
        h.Evidence();
        h.Service.EvaluateRestore();

        Assert.Equal(["minima"], good.Log);
        Assert.True(h.Service.Restored);
    }

    [Fact]
    public async Task Writes_AreDebounced_CarryTheIdentity_AndStopUntilDecided()
    {
        var part = new FakePart("minima", 40) { Value = JsonValue.Create("m") };
        var h = new Harness(_dir, [part]);
        h.File.Save(Snapshot());                   // an undecided file must never be overwritten
        h.Service.Start();
        h.Ofp.Set(Ofp());
        part.Touch();
        await Task.Delay(FlightStateSnapshotService.WriteDelay + TimeSpan.FromMilliseconds(400));
        Assert.Equal("session-20261010-205753", h.File.Load()!.SessionId); // untouched

        h.Evidence();
        h.Service.EvaluateRestore();
        part.Touch();
        part.Touch();
        await Task.Delay(FlightStateSnapshotService.WriteDelay + TimeSpan.FromMilliseconds(400));

        var written = h.File.Load()!;
        Assert.Equal("189301241", written.Identity.OfpRequestId);
        Assert.Equal("EFHK", written.Identity.OriginIcao);
        Assert.Equal("Airbus A320 FIN", written.Identity.AircraftTitle);
        Assert.Equal(Now, written.WrittenUtc);
        Assert.Equal("m", written.Parts["minima"]!.GetValue<string>());
    }

    [Fact]
    public void Build_LeavesOutEmptyParts_AndAPartThatThrows()
    {
        var empty = new FakePart("chrono", 60);
        var full = new FakePart("minima", 40) { Value = JsonValue.Create("m") };
        var h = new Harness(_dir, [empty, full]);
        h.Service.Start();

        var built = h.Service.Build();

        Assert.Equal(["minima"], built.Parts.Keys);
        Assert.Equal(FlightStateSnapshotFile.CurrentVersion, built.Version);
    }

    [Fact]
    public async Task ANewOfpRequestId_InvalidatesTheFile_ThenTheNewStateIsWritten()
    {
        var part = new FakePart("minima", 40) { Value = JsonValue.Create("m") };
        var h = new Harness(_dir, [part]);
        h.Service.Start();                          // no file: decided at once
        h.Ofp.Set(Ofp("189301241"));
        part.Touch();
        await Task.Delay(FlightStateSnapshotService.WriteDelay + TimeSpan.FromMilliseconds(400));
        Assert.Equal("189301241", h.File.Load()!.Identity.OfpRequestId);

        h.Ofp.Set(Ofp("189328867"));
        part.Touch();
        await Task.Delay(FlightStateSnapshotService.WriteDelay + TimeSpan.FromMilliseconds(400));
        Assert.Equal("189328867", h.File.Load()!.Identity.OfpRequestId);
    }

    [Fact]
    public async Task ColdAndDark_DeletesTheFile_AndHoldsWritesUntilTheNextLegStarts()
    {
        var part = new FakePart("minima", 40) { Value = JsonValue.Create("m") };
        var h = new Harness(_dir, [part]);
        h.Service.Start();
        part.Touch();
        await Task.Delay(FlightStateSnapshotService.WriteDelay + TimeSpan.FromMilliseconds(400));
        Assert.NotNull(h.File.Load());

        h.Phase.SetPhase(FlightPhase.ColdAndDark);
        Assert.Null(h.File.Load());
        part.Touch();
        await Task.Delay(FlightStateSnapshotService.WriteDelay + TimeSpan.FromMilliseconds(400));
        Assert.Null(h.File.Load());                 // suspended

        h.Phase.SetPhase(FlightPhase.Preflight);
        h.Phase.SetPhase(FlightPhase.PushbackAndStart);
        part.Touch();
        await Task.Delay(FlightStateSnapshotService.WriteDelay + TimeSpan.FromMilliseconds(400));
        Assert.NotNull(h.File.Load());              // the next leg writes again
    }

    [Fact]
    public void CycleReset_DuringTheArrival_KeepsTheFile()
    {
        var part = new FakePart("minima", 40) { Value = JsonValue.Create("m") };
        var h = new Harness(_dir, [part]);
        h.Service.Start();
        h.File.Save(Snapshot());
        h.Phase.SetPhase(FlightPhase.Shutdown);

        h.Signals.RaiseFlightCycleReset();         // the GSX arrival reset on the Shutdown edge

        Assert.NotNull(h.File.Load());
        h.Phase.SetPhase(FlightPhase.Departure);
        h.Signals.RaiseFlightCycleReset();         // a reset at the gate: a leg starting over
        Assert.Null(h.File.Load());
    }

    [Fact]
    public void Disabled_RestoresNothing_AndWritesNothing()
    {
        var part = new FakePart("minima", 40) { Value = JsonValue.Create("m") };
        var h = new Harness(_dir, [part]);
        h.Options.SnapshotEnabled = false;
        h.File.Save(Snapshot(parts: new() { ["minima"] = JsonValue.Create("m") }));
        h.Service.Start();
        h.Evidence();
        h.Service.EvaluateRestore();

        Assert.Empty(part.Log);
        Assert.Equal("session-20261010-205753", h.File.Load()!.SessionId);
    }

    [Fact]
    public async Task InFlightWithoutASnapshot_FetchesThePlanOnly_WhenTheMcduRouteMatches()
    {
        var h = new Harness(_dir, withImporter: true);
        h.Importer.Setup(i => i.TryFetchPlanOnlyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ofp());
        h.Service.Start();
        h.Evidence(imported: false, origin: "EFHK", destination: "EGCC");

        h.Phase.SetPhase(FlightPhase.Cruise);
        await WaitFor(() => h.Ofp.Current is not null);

        Assert.Equal("189301241", h.Ofp.Current!.RequestId);
        h.Importer.Verify(i => i.TryFetchPlanOnlyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        h.Importer.Verify(i => i.TryImportAsync(It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InFlightFetch_RefusesAPlanForAnotherRoute_AndNeverRunsOnTheGround()
    {
        var h = new Harness(_dir, withImporter: true);
        h.Importer.Setup(i => i.TryFetchPlanOnlyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ofp());
        h.Service.Start();
        h.Evidence(imported: false, origin: "EGCC", destination: "EFHK");

        h.Phase.SetPhase(FlightPhase.Preflight);
        await Task.Delay(200);
        h.Importer.Verify(i => i.TryFetchPlanOnlyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        h.Phase.SetPhase(FlightPhase.Climb);
        await WaitFor(() => h.Importer.Invocations.Count > 0);
        await Task.Delay(200);
        Assert.Null(h.Ofp.Current);                 // EGCC-EFHK in the MCDU, EFHK-EGCC from SimBrief
    }

    [Fact]
    public async Task InFlightFetch_DoesNotRun_WhenASnapshotRestoredTheOfp()
    {
        var ofpPart = new FakePart("ofp", 10);
        var h = new Harness(_dir, [ofpPart], withImporter: true);
        h.File.Save(Snapshot(parts: new() { ["ofp"] = JsonSerializer.SerializeToNode(Ofp(), FlightStateSnapshotStore.JsonOptions) }));
        h.Service.Start();
        h.Phase.SetPhase(FlightPhase.Cruise);
        h.Evidence();
        // The fake part does not publish to the store; stand in for the real OFP part.
        h.Ofp.Set(Ofp());
        h.Service.EvaluateRestore();
        await Task.Delay(200);

        h.Importer.Verify(i => i.TryFetchPlanOnlyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void RealParts_RoundTripThroughTheirJson()
    {
        var ofpStore = new OfpStore();
        var ofpPart = new OfpSnapshotPart(ofpStore, NullLogger<OfpSnapshotPart>.Instance);
        ofpStore.Set(Ofp() with { PaxCount = 125, CargoKg = 1234, FuelPlanRampKg = 9600 });
        var node = ofpPart.Capture()!;
        var restoredStore = new OfpStore();
        new OfpSnapshotPart(restoredStore, NullLogger<OfpSnapshotPart>.Instance).Restore(node);
        Assert.Equal(125, restoredStore.Current!.PaxCount);
        Assert.Equal(2, restoredStore.Current.Navlog.Count);
        Assert.Equal(60.3, restoredStore.Current.Navlog[0].Position.LatitudeDeg);

        var minima = new ArrivalMinimaStore();
        var minimaPart = new ArrivalMinimaSnapshotPart(minima, NullLogger<ArrivalMinimaSnapshotPart>.Instance);
        Assert.Null(minimaPart.Capture());
        minima.Set(new ArrivalMinima(ArrivalMinimumKind.DecisionAltitude, 449));
        var restoredMinima = new ArrivalMinimaStore();
        new ArrivalMinimaSnapshotPart(restoredMinima, NullLogger<ArrivalMinimaSnapshotPart>.Instance).Restore(minimaPart.Capture()!);
        Assert.Equal(new ArrivalMinima(ArrivalMinimumKind.DecisionAltitude, 449), restoredMinima.Current);

        var cycle = new DepartureCycleState();
        var times = new FlightTimesStore();
        var cyclePart = new DepartureCycleSnapshotPart(cycle, times, NullLogger<DepartureCycleSnapshotPart>.Instance);
        cycle.MarkStarted();
        cycle.MarkComplete();
        // At the gate (no off-blocks stamp): only the turnaround flag may come back.
        var atGate = new DepartureCycleState();
        new DepartureCycleSnapshotPart(atGate, new FlightTimesStore(), NullLogger<DepartureCycleSnapshotPart>.Instance).Restore(cyclePart.Capture()!);
        Assert.False(atGate.Started);
        Assert.False(atGate.Complete);
        // Off the gate: the departure is over and its flags are safe to carry.
        times.Update(_ => FlightTimesSnapshot.Empty with { OffBlocksUtc = Now });
        var inFlight = new DepartureCycleState();
        new DepartureCycleSnapshotPart(inFlight, new FlightTimesStore(), NullLogger<DepartureCycleSnapshotPart>.Instance).Restore(cyclePart.Capture()!);
        Assert.True(inFlight.Started);
        Assert.True(inFlight.Complete);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition(), "condition never became true");
    }
}
