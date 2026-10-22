using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Audio.Mixer;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Logging;
using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.Mixer;

/// <summary>#178: the host runs one client per enabled agent, follows the options, and
/// folds the clients into the one footer dot. Two fake agents stand in for two pilot PCs.</summary>
public sealed class MixerClientHostTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task TwoAgents_RunTwoClients_InSettingsOrder_AndFindResolvesNames()
    {
        await using var pc1 = new FakeBridgeServer { Token = "t1" };
        await using var pc2 = new FakeBridgeServer { Token = "t2" };
        pc1.Start();
        pc2.Start();
        await using var fixture = await HostFixture.StartAsync(TwoAgents(pc1, pc2));

        _ = await pc1.NextSessionAsync(Wait);
        _ = await pc2.NextSessionAsync(Wait);
        await MixerClientTests.WaitUntilAsync(() => fixture.Host.All.Count == 2 && fixture.Host.All.All(c => c.State == MixerConnectionState.Connected));

        Assert.Equal(["PC1", "PC2"], fixture.Host.All.Select(c => c.Name));
        Assert.Same(fixture.Host.All[0], fixture.Host.Find(""));
        Assert.Same(fixture.Host.All[1], fixture.Host.Find(" pc2 "));
        Assert.Null(fixture.Host.Find("PC3"));
        Assert.Equal(ConnectionState.Connected, fixture.StateOf());
        Assert.Equal("2 of 2 agents connected", fixture.ReasonOf());
        var roster = fixture.Store.Snapshot();
        Assert.True(roster.Enabled);
        Assert.Equal(["PC1", "PC2"], roster.Agents.Select(a => a.Name));
        Assert.Equal(2, roster.ConnectedAgents);
    }

    [Fact]
    public async Task OneAgentDown_LeavesTheOtherConnected_AndTheDotSaysSo()
    {
        await using var pc1 = new FakeBridgeServer { Token = "t1" };
        var pc2 = new FakeBridgeServer { Token = "t2" };
        pc1.Start();
        pc2.Start();
        await using var fixture = await HostFixture.StartAsync(TwoAgents(pc1, pc2));
        try
        {
            _ = await pc1.NextSessionAsync(Wait);
            _ = await pc2.NextSessionAsync(Wait);
            await MixerClientTests.WaitUntilAsync(() => fixture.Host.All.Count == 2 && fixture.Host.All.All(c => c.State == MixerConnectionState.Connected));

            // PC2 goes away for good (listener stopped): its client falls to Disconnected and
            // keeps retrying; PC1's session is untouched.
            await pc2.DisposeAsync();
            await MixerClientTests.WaitUntilAsync(() => fixture.Host.Find("PC2")?.State == MixerConnectionState.Disconnected);

            Assert.Equal(MixerConnectionState.Connected, fixture.Host.Find("PC1")!.State);
            Assert.Single(pc1.Sessions);
            Assert.Equal(ConnectionState.Disconnected, fixture.StateOf());
            Assert.Equal("1 of 2 agents connected", fixture.ReasonOf());

            // A set on the dead agent fails at once; the live one still answers.
            var dead = await fixture.Host.Find("PC2")!.SetAsync("Strip[0].Gain", -6).WaitAsync(Wait);
            Assert.Equal("not_connected", dead.Code);
            var live = await fixture.Host.Find("PC1")!.SetAsync("Strip[0].Gain", -6).WaitAsync(Wait);
            Assert.True(live.Ok);
        }
        finally
        {
            // Already disposed above when the test got that far; a second dispose would throw.
        }
    }

    [Fact]
    public async Task DisablingOneBlock_RetiresOnlyThatClient()
    {
        await using var pc1 = new FakeBridgeServer { Token = "t1" };
        await using var pc2 = new FakeBridgeServer { Token = "t2" };
        pc1.Start();
        pc2.Start();
        await using var fixture = await HostFixture.StartAsync(TwoAgents(pc1, pc2));
        _ = await pc1.NextSessionAsync(Wait);
        var session2 = await pc2.NextSessionAsync(Wait);
        await MixerClientTests.WaitUntilAsync(() => fixture.Host.All.Count == 2 && fixture.Host.All.All(c => c.State == MixerConnectionState.Connected));

        var changes = 0;
        fixture.Host.ClientsChanged += (_, _) => Interlocked.Increment(ref changes);
        fixture.Options.Update(o => o.Agents[1].Enabled = false);

        await MixerClientTests.WaitUntilAsync(() => fixture.Host.All.Count == 1);
        await MixerClientTests.WaitUntilAsync(() => session2.Closed);
        Assert.Equal("PC1", fixture.Host.All[0].Name);
        Assert.Single(pc1.Sessions);
        Assert.Null(fixture.Host.Find("PC2"));
        Assert.True(changes >= 1);
        var pc2Entry = fixture.Store.Snapshot().Agent("PC2");
        Assert.NotNull(pc2Entry);
        Assert.Equal(MixerConnectionState.Disabled, pc2Entry.State);
        Assert.Equal("agent off", pc2Entry.LastError);
        Assert.Equal(ConnectionState.Connected, fixture.StateOf());
        Assert.Null(fixture.ReasonOf()); // one client: no "n of m"
    }

    [Fact]
    public async Task ChangedToken_RestartsOnlyThatClient()
    {
        await using var pc1 = new FakeBridgeServer { Token = "t1" };
        await using var pc2 = new FakeBridgeServer { Token = "t2" };
        pc1.Start();
        pc2.Start();
        await using var fixture = await HostFixture.StartAsync(TwoAgents(pc1, pc2));
        _ = await pc1.NextSessionAsync(Wait);
        _ = await pc2.NextSessionAsync(Wait);
        await MixerClientTests.WaitUntilAsync(() => fixture.Host.All.Count == 2 && fixture.Host.All.All(c => c.State == MixerConnectionState.Connected));
        var before = fixture.Host.Find("PC2");

        pc2.Token = "t2-new";
        fixture.Options.Update(o => o.Agents[1].Token = "t2-new");

        var second = await pc2.NextSessionAsync(Wait);
        var hello = await second.NextFrameAsync(Wait);
        Assert.Equal("t2-new", hello["token"]?.GetValue<string>());
        await MixerClientTests.WaitUntilAsync(() => fixture.Host.Find("PC2") is { State: MixerConnectionState.Connected } now && !ReferenceEquals(now, before));
        Assert.Single(pc1.Sessions);
        Assert.Equal(2, pc2.Sessions.Count);
    }

    [Fact]
    public async Task LegacyFlatFields_RunOneClient_NamedMixerPc()
    {
        await using var pc = new FakeBridgeServer { Token = "legacy" };
        pc.Start();
        await using var fixture = await HostFixture.StartAsync(new MixerOptions
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = pc.Port,
            Token = "legacy",
            ReconnectDelayMs = 200,
            ReconnectMaxDelayMs = 200,
        });

        var session = await pc.NextSessionAsync(Wait);
        var hello = await session.NextFrameAsync(Wait);
        Assert.Equal("legacy", hello["token"]?.GetValue<string>());
        await MixerClientTests.WaitUntilAsync(() => fixture.Host.All.Count == 1 && fixture.Host.All[0].State == MixerConnectionState.Connected);

        Assert.Equal(MixerAgentOptions.LegacyName, fixture.Host.All[0].Name);
        Assert.Same(fixture.Host.All[0], fixture.Host.Find(""));
        Assert.Same(fixture.Host.All[0], fixture.Host.Find(MixerAgentOptions.LegacyName));
        Assert.Equal(ConnectionState.Connected, fixture.StateOf());
    }

    [Fact]
    public async Task FeatureOff_OpensNoSocket_UntilEnabledAtRuntime()
    {
        await using var pc = new FakeBridgeServer();
        pc.Start();
        var options = TwoAgents(pc, pc);
        options.Enabled = false;
        await using var fixture = await HostFixture.StartAsync(options);

        await Task.Delay(200);
        Assert.Empty(pc.Sessions);
        Assert.Empty(fixture.Host.All);
        Assert.Equal(ConnectionState.Disabled, fixture.StateOf());
        Assert.False(fixture.Store.Snapshot().Enabled);
        Assert.Equal(2, fixture.Store.Snapshot().Agents.Count); // listed, both Off

        fixture.Options.Update(o => o.Enabled = true);
        _ = await pc.NextSessionAsync(Wait);
        await MixerClientTests.WaitUntilAsync(() => fixture.Host.All.Count == 2);
    }

    private static MixerOptions TwoAgents(FakeBridgeServer pc1, FakeBridgeServer pc2) => new()
    {
        Enabled = true,
        ReconnectDelayMs = 200,
        ReconnectMaxDelayMs = 200,
        Agents =
        [
            new MixerAgentOptions { Name = "PC1", Host = "127.0.0.1", Port = pc1.Port, Token = pc1.Token },
            new MixerAgentOptions { Name = "PC2", Host = "127.0.0.1", Port = pc2.Port, Token = pc2.Token },
        ],
    };

    internal sealed class HostFixture : IAsyncDisposable
    {
        public required MixerClientHost Host { get; init; }

        public required ConnectionStatusStore Status { get; init; }

        public required MixerStatusStore Store { get; init; }

        public required FakeMixerOptionsMonitor Options { get; init; }

        public ConnectionState StateOf() => Status.Snapshot().First(p => p.Key == Subsystems.Mixer).Value;

        public string? ReasonOf() => Status.ReasonOf(Subsystems.Mixer);

        public static async Task<HostFixture> StartAsync(MixerOptions options)
        {
            var monitor = new FakeMixerOptionsMonitor(options);
            var status = new ConnectionStatusStore();
            var store = new MixerStatusStore();
            var wire = new Mock<IWireTrace>();
            wire.SetupGet(w => w.Enabled).Returns(false);
            var host = new MixerClientHost(monitor, status, store, wire.Object, NullLogger<MixerClient>.Instance, NullLogger<MixerClientHost>.Instance);
            await host.StartAsync(CancellationToken.None);
            return new HostFixture { Host = host, Status = status, Store = store, Options = monitor };
        }

        public async ValueTask DisposeAsync()
        {
            using var cts = new CancellationTokenSource(Wait);
            await Host.StopAsync(cts.Token);
            Host.Dispose();
        }
    }
}
