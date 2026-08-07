using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Sessions;
using ProsimCompanion.Core.Tests.Speech;
using Xunit;

namespace ProsimCompanion.Core.Tests.Sessions;

public sealed class SessionFinalizerTests
{
    private sealed class RecordingStep : ISessionFinalizationStep
    {
        private readonly List<string> _sink;
        private readonly bool _throws;

        public RecordingStep(string name, int order, List<string> sink, bool throws = false)
        {
            Name = name;
            Order = order;
            _sink = sink;
            _throws = throws;
        }

        public string Name { get; }

        public int Order { get; }

        public SessionFinalizationContext? SeenContext { get; private set; }

        public Task RunAsync(SessionFinalizationContext context, CancellationToken cancellationToken)
        {
            SeenContext = context;
            lock (_sink)
            {
                _sink.Add(Name);
            }

            return _throws ? throw new InvalidOperationException("step boom") : Task.CompletedTask;
        }
    }

    private static SessionFinalizer Create(
        FakePhaseSource phases, List<string> sink, out RecordingStep debrief, params ISessionFinalizationStep[] extra)
    {
        debrief = new RecordingStep("debrief", 10, sink);
        // Registered deliberately out of order — the finalizer must sort by Order.
        var steps = new List<ISessionFinalizationStep>
        {
            new RecordingStep("techlog", 30, sink),
            debrief,
            new RecordingStep("logbook", 20, sink),
        };
        steps.AddRange(extra);

        var finalizer = new SessionFinalizer(
            phases,
            SpeechTestSupport.TempEventLog(),
            steps,
            NullLogger<SessionFinalizer>.Instance,
            flushDelay: TimeSpan.Zero);
        finalizer.Start();
        return finalizer;
    }

    [Fact]
    public async Task Shutdown_RunsStepsInExplicitOrder_OncePerFlight()
    {
        var phases = new FakePhaseSource();
        var sink = new List<string>();
        using var finalizer = Create(phases, sink, out var debrief);

        phases.SetPhase(FlightPhase.Shutdown);
        await finalizer.LastRun!;

        Assert.Equal(["debrief", "logbook", "techlog"], sink);
        Assert.NotNull(debrief.SeenContext);
        Assert.StartsWith("session-", debrief.SeenContext.SessionId, StringComparison.Ordinal);
        Assert.EndsWith(".jsonl", debrief.SeenContext.SessionPath, StringComparison.Ordinal);

        // A second Shutdown edge without a new flight in between stays silent.
        phases.SetPhase(FlightPhase.TaxiIn);
        phases.SetPhase(FlightPhase.Shutdown);
        await finalizer.LastRun!;
        Assert.Equal(3, sink.Count);
    }

    [Fact]
    public async Task NewFlight_RearmsFinalization()
    {
        var phases = new FakePhaseSource();
        var sink = new List<string>();
        using var finalizer = Create(phases, sink, out _);

        phases.SetPhase(FlightPhase.Shutdown);
        await finalizer.LastRun!;

        phases.SetPhase(FlightPhase.TakeoffRoll); // re-arm
        phases.SetPhase(FlightPhase.Shutdown);
        await finalizer.LastRun!;

        Assert.Equal(6, sink.Count);
    }

    [Fact]
    public async Task ThrowingStep_DoesNotStopLaterSteps()
    {
        var phases = new FakePhaseSource();
        var sink = new List<string>();
        var boom = new RecordingStep("boom", 5, sink, throws: true);
        using var finalizer = Create(phases, sink, out _, boom);

        phases.SetPhase(FlightPhase.Shutdown);
        await finalizer.LastRun!;

        Assert.Equal(["boom", "debrief", "logbook", "techlog"], sink);
    }
}
