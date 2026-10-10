using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.EventLog;
using Xunit;

namespace ProsimCompanion.Core.Tests.Ofp;

/// <summary>Issue #164: the planned route goes on the session record whether or not a
/// briefing is ever spoken, once per distinct plan.</summary>
public sealed class OfpEventRecorderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pc-ofp-rec-").FullName;

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

    [Fact]
    public async Task RecordsOncePerDistinctPlan_AndOnStoreChange()
    {
        var store = new OfpStore();
        var eventLog = new JsonlEventLog(_dir, NullLogger<JsonlEventLog>.Instance);
        var path = eventLog.Path;
        var recorder = new OfpEventRecorder(store, eventLog, NullLogger<OfpEventRecorder>.Instance);

        store.Set(new OfpData { RequestId = "1", OriginIcao = "EDDM", DestinationIcao = "LHDC", FlightNumber = "DLH1687" });
        await recorder.StartAsync(CancellationToken.None); // an OFP already loaded at start is recorded
        store.Set(new OfpData { RequestId = "1", OriginIcao = "EDDM", DestinationIcao = "LHDC", FlightNumber = "DLH1687", PaxCount = 120 }); // same plan re-published: no repeat
        store.Set(new OfpData { RequestId = "2", OriginIcao = "LHDC", DestinationIcao = "EDDM", FlightNumber = "DLH1688" });
        store.Clear();                                                                                     // nothing to record
        await recorder.StopAsync(CancellationToken.None);
        store.Set(new OfpData { RequestId = "3", OriginIcao = "EGLL", DestinationIcao = "EGCC" });        // unsubscribed: ignored
        await eventLog.DisposeAsync();

        var lines = File.ReadAllLines(path).Where(l => l.Contains("\"ofp.loaded\"", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Contains("\"origin\":\"EDDM\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"destination\":\"LHDC\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"flightNumber\":\"DLH1687\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"origin\":\"LHDC\"", lines[1], StringComparison.Ordinal);
    }
}
