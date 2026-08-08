using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Arbiter;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Settable phase source so engine tests can drive phases without the state engine.</summary>
internal sealed class FakePhaseSource : IFlightPhaseSource
{
    public FlightPhase CurrentPhase { get; private set; } = FlightPhase.Unknown;

    public event EventHandler<FlightPhaseChangedEventArgs>? PhaseChanged;

    public void SetPhase(FlightPhase phase)
    {
        var previous = CurrentPhase;
        CurrentPhase = phase;
        PhaseChanged?.Invoke(this, new FlightPhaseChangedEventArgs(previous, phase));
    }
}

/// <summary>Captures enqueued requests; every submission resolves Spoken.</summary>
internal sealed class FakeArbiter : ISpeechArbiter
{
    public List<SpeechRequest> Requests { get; } = [];

    public event Action<SpeechArbiterEvent>? Observed
    {
        add { }
        remove { }
    }

    public Task<SpeechOutcome> EnqueueAsync(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        return Task.FromResult(SpeechOutcome.Spoken);
    }

    public Task<SpeechOutcome> SpeakAsync(
        string text, SpeechPriority priority = SpeechPriority.Normal, CancellationToken cancellationToken = default)
        => EnqueueAsync(new SpeechRequest(text, priority), cancellationToken);

    public IReadOnlyList<string> Tags
    {
        get
        {
            lock (Requests)
            {
                return [.. Requests.Select(r => r.Tag ?? "")];
            }
        }
    }
}

/// <summary>Settable flight-data source (also serves placard validity re-sampling).</summary>
internal sealed class FakeFlightSource : IFlightDataSource
{
    public FlightDataSnapshot Snapshot { get; set; } = new() { IsValid = false };

    public FlightDataSnapshot Sample() => Snapshot;
}

internal static class SpeechTestSupport
{
    public static IOptionsMonitor<SopOptions> SopMonitor(SopOptions options)
    {
        var monitor = new Mock<IOptionsMonitor<SopOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(() => options);
        return monitor.Object;
    }

    public static IOptionsMonitor<SpeechOptions> SpeechMonitor(SpeechOptions options)
    {
        var monitor = new Mock<IOptionsMonitor<SpeechOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(() => options);
        return monitor.Object;
    }

    public static IOptionsMonitor<ChecklistOptions> ChecklistMonitor(ChecklistOptions options)
    {
        var monitor = new Mock<IOptionsMonitor<ChecklistOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(() => options);
        return monitor.Object;
    }

    public static IOptionsMonitor<BriefingOptions> BriefingMonitor(BriefingOptions options)
    {
        var monitor = new Mock<IOptionsMonitor<BriefingOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(() => options);
        return monitor.Object;
    }

    /// <summary>A real event log writing to a throwaway temp directory.</summary>
    public static JsonlEventLog TempEventLog()
        => new(Directory.CreateTempSubdirectory("pc-tests-").FullName, NullLogger<JsonlEventLog>.Instance);
}
