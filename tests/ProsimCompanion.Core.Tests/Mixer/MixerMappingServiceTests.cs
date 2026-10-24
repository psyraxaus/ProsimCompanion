using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Audio.Acp;
using ProsimCompanion.Audio.Mixer;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.Mixer;

/// <summary>#178 acceptance: a knob on the captain's ACP moves only PC1, the first officer's
/// only PC2; one agent down never holds the other; a row naming no agent says so.</summary>
public sealed class MixerMappingServiceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task CaptainKnob_GoesToPc1Only_FirstOfficerKnob_ToPc2Only()
    {
        await using var pc1 = new FakeBridgeServer { Token = "t1" };
        await using var pc2 = new FakeBridgeServer { Token = "t2" };
        pc1.Start();
        pc2.Start();
        await using var fixture = await Fixture.StartAsync(TwoAgentsRouted(pc1, pc2));
        var session1 = await pc1.NextSessionAsync(Wait);
        var session2 = await pc2.NextSessionAsync(Wait);
        await MixerClientTests.WaitUntilAsync(() => fixture.Host.All.Count == 2 && fixture.Host.All.All(c => c.State == MixerConnectionState.Connected));

        fixture.Prosim.Push(AcpDataRefCatalog.VolumeRef(AcpSide.Captain, AudioChannel.Vhf1).Name, 512.0);
        var set1 = await NextSetAsync(session1);
        Assert.Equal("Strip[0].Gain", set1["param"]?.GetValue<string>());
        Assert.Equal(-24, set1["value"]?.GetValue<double>()); // half travel over −60…+12

        fixture.Prosim.Push(AcpDataRefCatalog.VolumeRef(AcpSide.FirstOfficer, AudioChannel.Vhf1).Name, 1024.0);
        var set2 = await NextSetAsync(session2);
        Assert.Equal("Strip[0].Gain", set2["param"]?.GetValue<string>());
        Assert.Equal(12, set2["value"]?.GetValue<double>());

        await Task.Delay(150);
        Assert.Equal(1, SetsOn(session1));
        Assert.Equal(1, SetsOn(session2));

        await MixerClientTests.WaitUntilAsync(() => fixture.Store.Snapshot().Mappings.All(m => m.LastResult == "ok"));
        var rows = fixture.Store.Snapshot().Mappings;
        Assert.Equal(["PC1", "PC2"], rows.Select(r => r.Agent));
        Assert.Equal(-24, rows[0].GainDb);
        Assert.Equal(12, rows[1].GainDb);
    }

    [Fact]
    public async Task OneAgentDown_TheOtherKeepsReceiving_AndCatchesUpAfterReconnect()
    {
        await using var pc1 = new FakeBridgeServer { Token = "t1" };
        await using var pc2 = new FakeBridgeServer { Token = "t2" };
        pc1.Start();
        pc2.Start();
        await using var fixture = await Fixture.StartAsync(TwoAgentsRouted(pc1, pc2));
        var session1 = await pc1.NextSessionAsync(Wait);
        var session2 = await pc2.NextSessionAsync(Wait);
        await MixerClientTests.WaitUntilAsync(() => fixture.Host.All.Count == 2 && fixture.Host.All.All(c => c.State == MixerConnectionState.Connected));

        // PC2 restarts (no close handshake); while it is away the captain keeps turning.
        session2.Abort();
        await MixerClientTests.WaitUntilAsync(() => fixture.Host.Find("PC2")?.State != MixerConnectionState.Connected);

        fixture.Prosim.Push(AcpDataRefCatalog.VolumeRef(AcpSide.Captain, AudioChannel.Vhf1).Name, 0.0);
        var set1 = await NextSetAsync(session1);
        Assert.Equal(-60, set1["value"]?.GetValue<double>());

        // The FO knob moved meanwhile: nothing could go out, the value waits.
        fixture.Prosim.Push(AcpDataRefCatalog.VolumeRef(AcpSide.FirstOfficer, AudioChannel.Vhf1).Name, 1024.0);

        // PC2 is back: the pending FO value is sent on the new session.
        var session2b = await pc2.NextSessionAsync(Wait);
        var set2 = await NextSetAsync(session2b);
        Assert.Equal("Strip[0].Gain", set2["param"]?.GetValue<string>());
        Assert.Equal(12, set2["value"]?.GetValue<double>());
    }

    [Fact]
    public async Task RowNamingNoAgent_ShowsNoAgent_AndSendsNothing()
    {
        await using var pc1 = new FakeBridgeServer { Token = "t1" };
        pc1.Start();
        var options = new MixerOptions
        {
            Enabled = true,
            ReconnectDelayMs = 200,
            ReconnectMaxDelayMs = 200,
            Agents = [new MixerAgentOptions { Name = "PC1", Host = "127.0.0.1", Port = pc1.Port, Token = "t1" }, new MixerAgentOptions { Name = "PC2", Host = "127.0.0.1", Port = 1, Token = "x", Enabled = false }],
            Mappings =
            [
                new MixerMapping { Acp = AcpSide.Captain, Channel = AudioChannel.Vhf1, Agent = "PC9", StripIndex = 0 },
                new MixerMapping { Acp = AcpSide.Captain, Channel = AudioChannel.Vhf2, Agent = "PC2", StripIndex = 0 },
                new MixerMapping { Acp = AcpSide.Captain, Channel = AudioChannel.Vhf3, Agent = "", StripIndex = 1 },
            ],
        };
        await using var fixture = await Fixture.StartAsync(options);
        var session1 = await pc1.NextSessionAsync(Wait);
        await MixerClientTests.WaitUntilAsync(() => fixture.Host.All.Count == 1 && fixture.Host.All[0].State == MixerConnectionState.Connected);

        await MixerClientTests.WaitUntilAsync(() => fixture.Store.Snapshot().Mappings.Count == 3);
        var rows = fixture.Store.Snapshot().Mappings;
        Assert.Equal("no_agent", rows[0].LastResult);
        Assert.Equal("agent_off", rows[1].LastResult);
        Assert.Null(rows[2].LastResult);

        fixture.Prosim.Push(AcpDataRefCatalog.VolumeRef(AcpSide.Captain, AudioChannel.Vhf1).Name, 512.0);
        fixture.Prosim.Push(AcpDataRefCatalog.VolumeRef(AcpSide.Captain, AudioChannel.Vhf2).Name, 512.0);
        fixture.Prosim.Push(AcpDataRefCatalog.VolumeRef(AcpSide.Captain, AudioChannel.Vhf3).Name, 512.0);

        // Only the row on the first agent (empty name) reaches PC1 — Strip 2.
        var set = await NextSetAsync(session1);
        Assert.Equal("Strip[1].Gain", set["param"]?.GetValue<string>());
        await Task.Delay(150);
        Assert.Equal(1, SetsOn(session1));
        await MixerClientTests.WaitUntilAsync(() => fixture.Store.Snapshot().Mappings[2].LastResult == "ok");
        Assert.Equal(-24, fixture.Store.Snapshot().Mappings[0].GainDb); // the value is shown even though nothing is sent
    }

    private static MixerOptions TwoAgentsRouted(FakeBridgeServer pc1, FakeBridgeServer pc2) => new()
    {
        Enabled = true,
        ReconnectDelayMs = 200,
        ReconnectMaxDelayMs = 200,
        Agents =
        [
            new MixerAgentOptions { Name = "PC1", Host = "127.0.0.1", Port = pc1.Port, Token = pc1.Token },
            new MixerAgentOptions { Name = "PC2", Host = "127.0.0.1", Port = pc2.Port, Token = pc2.Token },
        ],
        Mappings =
        [
            new MixerMapping { Acp = AcpSide.Captain, Channel = AudioChannel.Vhf1, Agent = "PC1", StripIndex = 0, UseLatch = false },
            new MixerMapping { Acp = AcpSide.FirstOfficer, Channel = AudioChannel.Vhf1, Agent = "PC2", StripIndex = 0, UseLatch = false },
        ],
    };

    private static async Task<JsonObject> NextSetAsync(FakeBridgeSession session)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            var frame = await session.NextFrameAsync(Wait);
            if (frame["op"]?.GetValue<string>() == "set")
            {
                return frame;
            }
        }

        throw new TimeoutException("no set frame arrived in time");
    }

    private static int SetsOn(FakeBridgeSession session)
    {
        lock (session.Received)
        {
            return session.Received.Count(f => f["op"]?.GetValue<string>() == "set");
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required MixerClientHostTests.HostFixture HostFixture { get; init; }

        public required MixerMappingService Service { get; init; }

        public required FakeProsimDataRefs Prosim { get; init; }

        public MixerClientHost Host => HostFixture.Host;

        public MixerStatusStore Store => HostFixture.Store;

        public static async Task<Fixture> StartAsync(MixerOptions options)
        {
            var host = await MixerClientHostTests.HostFixture.StartAsync(options);
            var prosim = new FakeProsimDataRefs();
            var service = new MixerMappingService(host.Options, prosim, host.Host, host.Store, NullLogger<MixerMappingService>.Instance);
            await service.StartAsync(CancellationToken.None);

            // .NET 10 runs ALL of BackgroundService.ExecuteAsync on a background thread, so
            // StartAsync returns before the first Bind has subscribed the knobs (flaky under the
            // full suite's load, 2026-10-25: Push threw KeyNotFound for the captain's VHF1 ref).
            // Bind ends with PublishRows, so a row per mapping in the store proves the
            // subscriptions exist.
            var expected = options.Enabled ? options.Mappings.Count(m => m.Enabled && m.Channel.ExistsOn(m.Acp)) : 0;
            await MixerClientTests.WaitUntilAsync(() => host.Store.Snapshot().Mappings.Count == expected);
            return new Fixture { HostFixture = host, Service = service, Prosim = prosim };
        }

        public async ValueTask DisposeAsync()
        {
            using var cts = new CancellationTokenSource(Wait);
            await Service.StopAsync(cts.Token);
            Service.Dispose();
            await HostFixture.DisposeAsync();
        }
    }

    internal sealed class FakeProsimDataRefs : IProsimDataRefs
    {
        private readonly Dictionary<string, FakeSubscription> _subscriptions = new(StringComparer.Ordinal);

        public void Push(string name, object value)
        {
            FakeSubscription subscription;
            lock (_subscriptions)
            {
                subscription = _subscriptions[name];
            }

            subscription.RaisePush(value);
        }

        public IDataRefSubscription SubscribeDynamic(string name, DataRefTier tier)
        {
            lock (_subscriptions)
            {
                if (!_subscriptions.TryGetValue(name, out var subscription))
                {
                    subscription = new FakeSubscription(name);
                    _subscriptions[name] = subscription;
                }

                return subscription;
            }
        }

        public Task WriteAsync(string name, object? value, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PressMomentaryAsync(string name, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeSubscription(string name) : IDataRefSubscription
    {
        public string Name => name;

        public object? RawValue { get; private set; }

        public bool IsStale => false;

        public DateTimeOffset? LastUpdatedUtc { get; private set; }

        public event EventHandler? ValueChanged;

        public T GetValue<T>(T fallback) => DataRefCoercion.Coerce(RawValue, fallback);

        public void RaisePush(object value)
        {
            RawValue = value;
            LastUpdatedUtc = DateTimeOffset.UtcNow;
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
        }
    }
}
