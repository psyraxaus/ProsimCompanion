using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Speech.Crew;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>The ACP channel module (campaign #85): atomic transmit snapshot and the one
/// latch-or-grace wait — including the grace path the two hand-rolled copies never tested.</summary>
public sealed class AcpChannelTests
{
    private readonly Dictionary<string, Mock<IDataRefSubscription>> _subs = new(StringComparer.Ordinal);

    private AcpChannel CreateChannel()
    {
        var dataRefs = new Mock<IProsimDataRefs>();
        dataRefs
            .Setup(d => d.SubscribeDynamic(It.IsAny<string>(), It.IsAny<DataRefTier>()))
            .Returns((string name, DataRefTier _) =>
            {
                if (!_subs.TryGetValue(name, out var sub))
                {
                    sub = new Mock<IDataRefSubscription>();
                    // Absent by default: no value has ever arrived (RawValue null).
                    sub.SetupGet(s => s.RawValue).Returns((object?)null);
                    sub.Setup(s => s.GetValue(It.IsAny<int>())).Returns(0);
                    _subs[name] = sub;
                }

                return sub.Object;
            });
        return new AcpChannel(dataRefs.Object, NullLogger<AcpChannel>.Instance);
    }

    private void Set(string name, int value, bool stale = false)
    {
        var sub = _subs[name];
        sub.SetupGet(s => s.RawValue).Returns((double)value);
        sub.SetupGet(s => s.IsStale).Returns(stale);
        sub.Setup(s => s.GetValue(It.IsAny<int>())).Returns(value);
    }

    [Fact]
    public void Transmit_IsOneCoherentSnapshot()
    {
        using var channel = CreateChannel();
        Set(ProsimDataRefNames.AcpSendChannel.Name, 6);
        Set(ProsimDataRefNames.AcpIntSend.Name, 1);

        var transmit = channel.Transmit;

        Assert.Equal(AcpTransmitTarget.Intercom, transmit.Target);
        Assert.True(transmit.IntKeyPushed);
    }

    [Fact]
    public void Transmit_DegradesToUnknown_OnStaleOrAbsent()
    {
        using var channel = CreateChannel();
        Assert.Equal(AcpTransmitTarget.Unknown, channel.Transmit.Target);

        Set(ProsimDataRefNames.AcpSendChannel.Name, 7, stale: true);
        Assert.Equal(AcpTransmitTarget.Unknown, channel.Transmit.Target);
    }

    [Fact]
    public async Task AwaitReceive_ReturnsImmediately_WhenAlreadyLatched()
    {
        using var channel = CreateChannel();
        Set(ProsimDataRefNames.Acp2IntLatch.Name, 1);

        Assert.True(channel.IsReceiving(AcpChannelKind.Intercom));
        Assert.True(await channel.AwaitReceiveAsync(
            AcpChannelKind.Intercom, TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Fact]
    public async Task AwaitReceive_GracePath_ElapsesAndReportsUnlatched()
    {
        using var channel = CreateChannel();
        Set(ProsimDataRefNames.Acp1IntLatch.Name, 0);

        var latched = await channel.AwaitReceiveAsync(
            AcpChannelKind.Intercom, TimeSpan.FromMilliseconds(400), CancellationToken.None);

        Assert.False(latched);
    }

    [Fact]
    public async Task AwaitReceive_DegradesImmediately_WhenProsimIsStaleOrAbsent()
    {
        using var channel = CreateChannel();
        Set(ProsimDataRefNames.Acp1IntLatch.Name, 0, stale: true);

        // A dialogue must never hang on a signal nobody is producing — no grace burn.
        var start = Environment.TickCount64;
        var latched = await channel.AwaitReceiveAsync(
            AcpChannelKind.Intercom, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(latched);
        Assert.True(Environment.TickCount64 - start < 2000);
    }

    [Fact]
    public async Task CabinLatches_AreIndependentOfIntercom()
    {
        using var channel = CreateChannel();
        Set(ProsimDataRefNames.Acp3CabLatch.Name, 1);

        Assert.True(channel.IsReceiving(AcpChannelKind.Cabin));
        Assert.False(channel.IsReceiving(AcpChannelKind.Intercom));
        Assert.True(await channel.AwaitReceiveAsync(
            AcpChannelKind.Cabin, TimeSpan.FromSeconds(5), CancellationToken.None));
    }
}
