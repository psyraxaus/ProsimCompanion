using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Diagnostics.Ecam;

namespace ProsimCompanion.Core.Tests.Diagnostics.Ecam;

/// <summary>An in-memory dataref feed over the real subscription table: tests push values
/// and the collectors read them exactly as they would from ProSim.</summary>
internal sealed class TableDataRefs : IProsimDataRefs
{
    public DataRefSubscriptionTable Table { get; } = new();

    public IDataRefSubscription SubscribeDynamic(string name, DataRefTier tier) => Table.Subscribe(name, tier);

    public Task WriteAsync(string name, object? value, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PressMomentaryAsync(string name, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Push(string name, object value) => Table.UpdateValue(name, value, DateTimeOffset.UtcNow);
}

internal static class CollectorTestSupport
{
    /// <summary>Runs one collector through a fresh builder and sink, returning both.</summary>
    public static (DiagPage Page, DiagCautionSink Cautions, DiagEventLog Events) Run(IDiagnosticsCollector collector, DateTimeOffset? now = null)
    {
        var log = new DiagEventLog();
        var builder = new DiagPageBuilder(collector.Page, new DiagEventSink(log, now ?? EcamTestSupport.T0));
        var sink = new DiagCautionSink(now ?? EcamTestSupport.T0);
        collector.Contribute(builder, sink);
        return (builder.Build(), sink, log);
    }
}
