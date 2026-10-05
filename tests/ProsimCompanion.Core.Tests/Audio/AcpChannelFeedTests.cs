using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Audio.Acp;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using Xunit;

namespace ProsimCompanion.Core.Tests.Audio;

/// <summary>
/// The loudspeaker dial on the shared ACP feed (2026-10-05): no latch — the dial fully down is
/// its mute — its own power rule, and no observer dial. The ACP knob/latch path is covered
/// through the same fake so a regression in the shared plumbing shows here too.
/// </summary>
public sealed class AcpChannelFeedTests : IDisposable
{
    private const string CaptLoudspeaker = "system.analog.A_MIP_LOUDSPEAKER_CAPT";
    private const string AcEss = "system.gates.B_ELEC_BUS_POWER_AC_ESS";
    private const string AudioSwitching = "system.switches.S_AUDIO_SWITCHING";

    private readonly PushDataRefs _prosim = new();
    private readonly RecordingSink _sink = new();
    private readonly AcpChannelFeed _feed;

    public AcpChannelFeedTests()
    {
        _feed = new AcpChannelFeed(_prosim, NullLogger<AcpChannelFeed>.Instance);
    }

    public void Dispose() => _feed.Dispose();

    [Fact]
    public void LoudspeakerDial_EmitsVolume_AndSubscribesNoLatch()
    {
        _prosim.Push(AcEss, true);
        _feed.Bind([(AcpSide.Captain, AudioChannel.Loudspeaker)], _sink);

        _prosim.Push(CaptLoudspeaker, 512);

        Assert.Equal(0.5f, _sink.Volumes[^1].Normalized, precision: 3);
        Assert.DoesNotContain(_prosim.Subscribed, name => name.Contains("REC_LATCH", StringComparison.Ordinal));
    }

    [Fact]
    public void LoudspeakerDial_FullyDown_Mutes_AndUnmutesOnceRaised()
    {
        _prosim.Push(AcEss, true);
        _prosim.Push(CaptLoudspeaker, 600);
        _feed.Bind([(AcpSide.Captain, AudioChannel.Loudspeaker)], _sink);
        Assert.Equal([false], _sink.Mutes.Select(m => m.Muted)); // seeded on bind

        _prosim.Push(CaptLoudspeaker, 0);
        _prosim.Push(CaptLoudspeaker, 4);   // pot noise inside the zero band — no second write
        _prosim.Push(CaptLoudspeaker, 300);
        _prosim.Push(CaptLoudspeaker, 1020); // the owner's dial tops out here

        Assert.Equal([false, true, false], _sink.Mutes.Select(m => m.Muted));
        Assert.All(_sink.Mutes, m => Assert.Equal(AudioChannel.Loudspeaker, m.Channel));
    }

    [Fact]
    public void LoudspeakerDial_NeverSeen_EmitsNothing()
    {
        _prosim.Push(AcEss, true);

        _feed.Bind([(AcpSide.Captain, AudioChannel.Loudspeaker)], _sink);

        // A spurious zero at startup would mute the speakers before the dial was ever read.
        Assert.Empty(_sink.Volumes);
        Assert.Empty(_sink.Mutes);
    }

    [Fact]
    public void LoudspeakerDial_IgnoresTheAudioSwitchingSwap_ButNotABusLoss()
    {
        _prosim.Push(AcEss, true);
        _prosim.Push(AudioSwitching, 0); // CAPT 3: the captain's ACP is out of the loop
        _prosim.Push(CaptLoudspeaker, 512);
        _feed.Bind(
            [(AcpSide.Captain, AudioChannel.Loudspeaker), (AcpSide.Captain, AudioChannel.Vhf1)], _sink);
        _prosim.Push("system.analog.A_ASP_VHF_1_VOLUME", 512);

        Assert.Contains(_sink.Volumes, v => v.Channel == AudioChannel.Loudspeaker);
        Assert.DoesNotContain(_sink.Volumes, v => v.Channel == AudioChannel.Vhf1);

        _sink.Volumes.Clear();
        _prosim.Push(AcEss, false);
        _prosim.Push(CaptLoudspeaker, 900);

        Assert.Empty(_sink.Volumes); // unpowered: the target holds
    }

    [Fact]
    public void LoudspeakerDial_PowerRestore_ReEmitsVolumeAndMute()
    {
        _prosim.Push(CaptLoudspeaker, 0);
        _feed.Bind([(AcpSide.Captain, AudioChannel.Loudspeaker)], _sink);
        Assert.Empty(_sink.Mutes);

        _prosim.Push(AcEss, true);

        Assert.Single(_sink.Volumes);
        Assert.Equal([true], _sink.Mutes.Select(m => m.Muted));
    }

    [Fact]
    public void ObserverLoudspeaker_IsSkipped_NotThrown()
    {
        _prosim.Push(AcEss, true);

        _feed.Bind(
            [(AcpSide.Observer, AudioChannel.Loudspeaker), (AcpSide.Captain, AudioChannel.Loudspeaker)], _sink);

        Assert.Single(_prosim.Subscribed, name => name.Contains("LOUDSPEAKER", StringComparison.Ordinal));
    }

    [Fact]
    public void AcpKnob_StillReadsItsRecLatch()
    {
        _prosim.Push(AcEss, true);
        _prosim.Push("system.analog.A_ASP_VHF_1_VOLUME", 1024);
        _feed.Bind([(AcpSide.Captain, AudioChannel.Vhf1)], _sink);

        _prosim.Push("system.switches.S_ASP_VHF_1_REC_LATCH", 0);
        _prosim.Push("system.analog.A_ASP_VHF_1_VOLUME", 0); // a knob at zero is NOT a mute

        Assert.Equal([true], _sink.Mutes.Select(m => m.Muted));
    }

    private sealed class RecordingSink : IAcpVolumeSink
    {
        public List<(AcpSide Acp, AudioChannel Channel, float Normalized)> Volumes { get; } = [];

        public List<(AcpSide Acp, AudioChannel Channel, bool Muted)> Mutes { get; } = [];

        public void OnVolume(AcpSide acp, AudioChannel channel, float normalized) =>
            Volumes.Add((acp, channel, normalized));

        public void OnMute(AcpSide acp, AudioChannel channel, bool muted) =>
            Mutes.Add((acp, channel, muted));
    }

    /// <summary>Dataref fake that pushes: <see cref="Push"/> stores the value and raises
    /// ValueChanged on every live subscription of that name, like the SDK dispatcher.</summary>
    private sealed class PushDataRefs : IProsimDataRefs
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
        private readonly List<Sub> _subs = [];

        public IEnumerable<string> Subscribed => _subs.Where(s => !s.Disposed).Select(s => s.Name);

        public void Push(string name, object? value)
        {
            _values[name] = value;
            foreach (var sub in _subs.Where(s => !s.Disposed && s.Name == name).ToList())
            {
                sub.Raise();
            }
        }

        public IDataRefSubscription SubscribeDynamic(string name, DataRefTier tier)
        {
            var sub = new Sub(this, name);
            _subs.Add(sub);
            return sub;
        }

        public Task WriteAsync(string name, object? value, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The audio feed never writes to the aircraft.");

        public Task PressMomentaryAsync(string name, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The audio feed never writes to the aircraft.");

        private sealed class Sub(PushDataRefs owner, string name) : IDataRefSubscription
        {
            public string Name => name;

            public bool Disposed { get; private set; }

            public object? RawValue => owner._values.GetValueOrDefault(name);

            public bool IsStale => false;

            public DateTimeOffset? LastUpdatedUtc => null;

            public event EventHandler? ValueChanged;

            public void Raise() => ValueChanged?.Invoke(this, EventArgs.Empty);

            public T GetValue<T>(T fallback)
            {
                var raw = RawValue;
                return raw is null
                    ? fallback
                    : (T)Convert.ChangeType(raw, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
            }

            public void Dispose() => Disposed = true;
        }
    }
}
