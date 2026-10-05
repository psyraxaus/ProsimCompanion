using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Audio.Backends.VoiceMeeter;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.Audio;

/// <summary>
/// The mute rule on the VoiceMeeter path, read back from the status store. The remote is never
/// logged in here (no DLL in a test run), so every write is a no-op — exactly the degraded
/// mode — while the binder's own decision stays observable.
/// </summary>
public sealed class VoiceMeeterBinderTests : IDisposable
{
    private readonly VoiceMeeterRemote _remote = new(NullLogger<VoiceMeeterRemote>.Instance);
    private readonly AudioStatusStore _status = new();
    private readonly VoiceMeeterBinder _binder;

    public VoiceMeeterBinderTests()
    {
        _binder = new VoiceMeeterBinder(_remote, _status, NullLogger<VoiceMeeterBinder>.Instance);
    }

    public void Dispose() => _remote.Dispose();

    [Fact]
    public void LoudspeakerDial_MutesItsTarget_WithoutTheLatchTick()
    {
        // Bus 8 (B3 on a Potato) with the Latch box clear — the owner's wiring, 2026-10-05.
        _binder.Bind([(AcpSide.Captain,
            [new VoiceMeeterTargetMapping(AudioChannel.Loudspeaker, 7, isBus: true)])]);

        _binder.OnMute(AcpSide.Captain, AudioChannel.Loudspeaker, muted: true);

        Assert.True(Assert.Single(_status.Snapshot().VoiceMeeterBindings).Muted);
    }

    [Fact]
    public void AcpChannel_WithoutTheLatchTick_NeverWritesMute()
    {
        _binder.Bind([(AcpSide.Captain,
            [new VoiceMeeterTargetMapping(AudioChannel.Vhf1, 0, isBus: false)])]);

        _binder.OnMute(AcpSide.Captain, AudioChannel.Vhf1, muted: true);

        Assert.Null(Assert.Single(_status.Snapshot().VoiceMeeterBindings).Muted);
    }
}
