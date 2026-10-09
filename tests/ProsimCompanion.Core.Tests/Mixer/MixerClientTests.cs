using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Audio.Mixer;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Logging;
using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.Mixer;

/// <summary>MixerClient against the fake agent: hello first, watch replay after a drop,
/// set/result correlation out of order, timeouts, and the degrade-not-fail paths.</summary>
public sealed class MixerClientTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Hello_IsTheFirstFrame_WithTokenAndProtocol_ThenConnected()
    {
        await using var server = new FakeBridgeServer { Token = "tok-1" };
        server.Start();
        await using var fixture = await ClientFixture.StartAsync(server, "tok-1");

        var session = await server.NextSessionAsync(Wait);
        var hello = await session.NextFrameAsync(Wait);

        Assert.Equal("hello", hello["op"]?.GetValue<string>());
        Assert.Equal("tok-1", hello["token"]?.GetValue<string>());
        Assert.Equal(1, hello["protocol"]?.GetValue<int>());

        await WaitUntilAsync(() => fixture.Client.State == MixerConnectionState.Connected);
        Assert.True(fixture.Client.Voicemeeter.Connected);
        Assert.Equal("potato", fixture.Client.Voicemeeter.Kind);
        Assert.Equal(ConnectionState.Connected, StateOf(fixture.Status, Subsystems.Mixer));
    }

    [Fact]
    public async Task WatchList_IsReplayedOnTheNextSession_AfterTheAgentDrops()
    {
        await using var server = new FakeBridgeServer();
        server.Values["Strip[0].Gain"] = -6;
        server.Start();
        await using var fixture = await ClientFixture.StartAsync(server, server.Token, reconnectMs: 100);

        var first = await server.NextSessionAsync(Wait);
        _ = await first.NextFrameAsync(Wait); // hello
        _ = await first.NextFrameAsync(Wait); // the channel-label get every session starts with
        await WaitUntilAsync(() => fixture.Client.State == MixerConnectionState.Connected);

        fixture.Client.Watch(["Strip[0].Gain", "Bus[1].Mute"]);
        var watch = await first.NextFrameAsync(Wait);
        Assert.Equal("watch", watch["op"]?.GetValue<string>());
        await WaitUntilAsync(() => fixture.Client.TryGetValue("Strip[0].Gain", out var v) && v.Number == -6);

        // Agent restart: no close handshake.
        first.Abort();
        await WaitUntilAsync(() => fixture.Client.State != MixerConnectionState.Connected);

        var second = await server.NextSessionAsync(Wait);
        var hello = await second.NextFrameAsync(Wait);
        Assert.Equal("hello", hello["op"]?.GetValue<string>());
        var replay = await second.NextFrameAsync(Wait);
        Assert.Equal("watch", replay["op"]?.GetValue<string>());
        var names = replay["params"]!.AsArray().Select(n => n!.GetValue<string>()).OrderBy(n => n).ToArray();
        Assert.Equal(["Bus[1].Mute", "Strip[0].Gain"], names);

        // The cache survived the drop.
        Assert.True(fixture.Client.TryGetValue("Strip[0].Gain", out var cached));
        Assert.Equal(-6, cached.Number);
    }

    [Fact]
    public async Task SetAsync_CorrelatesResultsById_EvenOutOfOrder()
    {
        await using var server = new FakeBridgeServer { AutoReplySet = false };
        server.Start();
        await using var fixture = await ClientFixture.StartAsync(server, server.Token);

        var session = await server.NextSessionAsync(Wait);
        _ = await session.NextFrameAsync(Wait); // hello
        _ = await session.NextFrameAsync(Wait); // channel-label get
        await WaitUntilAsync(() => fixture.Client.State == MixerConnectionState.Connected);

        var setA = fixture.Client.SetAsync("Strip[0].Gain", -3);
        var frameA = await session.NextFrameAsync(Wait);
        var setB = fixture.Client.SetAsync("Strip[9].Gain", 0);
        var frameB = await session.NextFrameAsync(Wait);

        Assert.Equal("set", frameA["op"]?.GetValue<string>());
        Assert.Equal(-3, frameA["value"]?.GetValue<double>());
        var idA = frameA["id"]!.GetValue<string>();
        var idB = frameB["id"]!.GetValue<string>();
        Assert.NotEqual(idA, idB);

        // B answers first, with an error; A answers second, ok.
        await session.SendResultAsync(idB, ok: false, error: "unknown parameter");
        await session.SendResultAsync(idA, ok: true);

        var resultA = await setA.WaitAsync(Wait);
        var resultB = await setB.WaitAsync(Wait);
        Assert.True(resultA.Ok);
        Assert.False(resultB.Ok);
        Assert.Equal("unknown parameter", resultB.Code);
        Assert.Equal(1, fixture.Store.Snapshot().FailedSets);
    }

    [Fact]
    public async Task SetAsync_ReportsTimeout_WhenNoResultArrives()
    {
        await using var server = new FakeBridgeServer { AutoReplySet = false };
        server.Start();
        await using var fixture = await ClientFixture.StartAsync(server, server.Token, setTimeoutMs: 300);

        var session = await server.NextSessionAsync(Wait);
        _ = await session.NextFrameAsync(Wait); // hello
        await WaitUntilAsync(() => fixture.Client.State == MixerConnectionState.Connected);

        var result = await fixture.Client.SetAsync("Strip[0].Mute", 1).WaitAsync(Wait);

        Assert.False(result.Ok);
        Assert.Equal("timeout", result.Code);
    }

    [Fact]
    public async Task SetAsync_WhileUnreachable_ReturnsNotConnected_WithoutThrowing()
    {
        await using var server = new FakeBridgeServer();
        // Not started: nothing listens on the port.
        await using var fixture = await ClientFixture.StartAsync(server, server.Token, port: 1, reconnectMs: 60_000);

        var result = await fixture.Client.SetAsync("Strip[0].Gain", -10).WaitAsync(Wait);

        Assert.False(result.Ok);
        Assert.Equal("not_connected", result.Code);
        fixture.Client.Watch(["Strip[0].Gain"]); // queues for the next session, no throw
    }

    [Fact]
    public async Task WrongToken_IsRefused_AndReportedAsTheLastError()
    {
        await using var server = new FakeBridgeServer { Token = "right" };
        server.Start();
        await using var fixture = await ClientFixture.StartAsync(server, "wrong", reconnectMs: 60_000);

        await WaitUntilAsync(() => fixture.Store.Snapshot().LastError == "unauthorized");
        Assert.NotEqual(MixerConnectionState.Connected, fixture.Client.State);
    }

    [Fact]
    public async Task StatusFrame_UpdatesVoicemeeter_AndRaisesStateChanged()
    {
        await using var server = new FakeBridgeServer();
        server.Start();
        await using var fixture = await ClientFixture.StartAsync(server, server.Token);

        var session = await server.NextSessionAsync(Wait);
        _ = await session.NextFrameAsync(Wait);
        await WaitUntilAsync(() => fixture.Client.State == MixerConnectionState.Connected);

        var raised = 0;
        fixture.Client.StateChanged += (_, _) => Interlocked.Increment(ref raised);
        await session.SendStatusAsync(connected: false);

        await WaitUntilAsync(() => !fixture.Client.Voicemeeter.Connected);
        Assert.True(raised >= 1);
        Assert.Equal(MixerConnectionState.Connected, fixture.Client.State); // the agent link itself is fine
    }

    [Fact]
    public async Task Channels_AreReadAfterWelcome_FromTheLabelsTheEditionHas()
    {
        await using var server = new FakeBridgeServer();
        // A Banana: 5 strips, 5 buses; the rest answer "unknown parameter".
        for (var i = 0; i < 5; i++)
        {
            server.Texts[$"Strip[{i}].Label"] = i == 2 ? "Mic" : "";
            server.Texts[$"Bus[{i}].Label"] = i == 0 ? "Headset" : "";
        }

        server.Start();
        await using var fixture = await ClientFixture.StartAsync(server, server.Token);

        var session = await server.NextSessionAsync(Wait);
        _ = await session.NextFrameAsync(Wait); // hello
        var get = await session.NextFrameAsync(Wait);
        Assert.Equal("get", get["op"]?.GetValue<string>());
        Assert.Equal(16, get["params"]!.AsArray().Count);

        await WaitUntilAsync(() => fixture.Client.Channels.Count == 10);
        var strips = fixture.Client.Channels.Where(c => !c.IsBus).ToList();
        var buses = fixture.Client.Channels.Where(c => c.IsBus).ToList();
        Assert.Equal(5, strips.Count);
        Assert.Equal(5, buses.Count);
        Assert.Equal("Mic", strips[2].Label);
        Assert.Equal("Strip 3 — Mic", strips[2].DisplayName("banana"));
        Assert.Equal("Bus A1 — Headset", buses[0].DisplayName("banana"));
        Assert.Equal("Bus B2", buses[4].DisplayName("banana"));
        Assert.Equal("Strip[2].Gain", strips[2].GainParameter);

        // On demand too — and the label change is picked up.
        server.Texts["Strip[2].Label"] = "vPilot";
        var refreshed = await fixture.Client.RefreshChannelsAsync().WaitAsync(Wait);
        Assert.Equal("vPilot", refreshed.First(c => !c.IsBus && c.Index == 2).Label);
    }

    [Fact]
    public async Task Disabled_OpensNoSocket_UntilEnabledAtRuntime()
    {
        await using var server = new FakeBridgeServer();
        server.Start();
        await using var fixture = await ClientFixture.StartAsync(server, server.Token, enabled: false);

        await Task.Delay(200);
        Assert.Empty(server.Sessions);
        Assert.Equal(MixerConnectionState.Disabled, fixture.Client.State);

        fixture.Options.Update(o => o.Enabled = true);
        _ = await server.NextSessionAsync(Wait);
        await WaitUntilAsync(() => fixture.Client.State == MixerConnectionState.Connected);
    }

    private static ConnectionState StateOf(ConnectionStatusStore store, string subsystem) =>
        store.Snapshot().First(p => p.Key == subsystem).Value;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition not met in time");
            }

            await Task.Delay(10);
        }
    }

    private sealed class ClientFixture : IAsyncDisposable
    {
        public required MixerClient Client { get; init; }

        public required ConnectionStatusStore Status { get; init; }

        public required MixerStatusStore Store { get; init; }

        public required FakeOptionsMonitor Options { get; init; }

        public static async Task<ClientFixture> StartAsync(
            FakeBridgeServer server,
            string token,
            int? port = null,
            int reconnectMs = 200,
            int setTimeoutMs = 2000,
            bool enabled = true)
        {
            var options = new FakeOptionsMonitor(new MixerOptions
            {
                Enabled = enabled,
                Host = "127.0.0.1",
                Port = port ?? server.Port,
                Token = token,
                ReconnectDelayMs = reconnectMs,
                ReconnectMaxDelayMs = reconnectMs,
                SetTimeoutMs = setTimeoutMs,
            });
            var status = new ConnectionStatusStore();
            var store = new MixerStatusStore();
            var wire = new Mock<IWireTrace>();
            wire.SetupGet(w => w.Enabled).Returns(false);
            var client = new MixerClient(options, status, store, wire.Object, NullLogger<MixerClient>.Instance);
            await client.StartAsync(CancellationToken.None);
            return new ClientFixture { Client = client, Status = status, Store = store, Options = options };
        }

        public async ValueTask DisposeAsync()
        {
            using var cts = new CancellationTokenSource(Wait);
            await Client.StopAsync(cts.Token);
            Client.Dispose();
        }
    }

    private sealed class FakeOptionsMonitor(MixerOptions value) : IOptionsMonitor<MixerOptions>
    {
        private readonly List<Action<MixerOptions, string?>> _listeners = [];

        public MixerOptions CurrentValue { get; private set; } = value;

        public MixerOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<MixerOptions, string?> listener)
        {
            lock (_listeners)
            {
                _listeners.Add(listener);
            }

            return new Unsubscribe(() =>
            {
                lock (_listeners)
                {
                    _listeners.Remove(listener);
                }
            });
        }

        public void Update(Action<MixerOptions> mutate)
        {
            var next = new MixerOptions
            {
                Enabled = CurrentValue.Enabled,
                Host = CurrentValue.Host,
                Port = CurrentValue.Port,
                Token = CurrentValue.Token,
                ReconnectDelayMs = CurrentValue.ReconnectDelayMs,
                ReconnectMaxDelayMs = CurrentValue.ReconnectMaxDelayMs,
                SetTimeoutMs = CurrentValue.SetTimeoutMs,
            };
            mutate(next);
            CurrentValue = next;
            Action<MixerOptions, string?>[] listeners;
            lock (_listeners)
            {
                listeners = [.. _listeners];
            }

            foreach (var listener in listeners)
            {
                listener(next, null);
            }
        }

        private sealed class Unsubscribe(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}
