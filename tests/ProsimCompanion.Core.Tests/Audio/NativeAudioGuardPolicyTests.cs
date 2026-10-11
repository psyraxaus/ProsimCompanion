using ProsimCompanion.Audio;
using Xunit;

namespace ProsimCompanion.Core.Tests.Audio;

/// <summary>Issue #175: the native-audio guard's retry verdict. ProSim 1.75.1 refuses all seven
/// window datarefs on a reachable gateway — that is "not present", said once, not a retry.</summary>
public sealed class NativeAudioGuardPolicyTests
{
    [Fact]
    public void AllCleared_IsCleared()
        => Assert.Equal(NativeAudioGuardVerdict.Cleared, NativeAudioGuardPolicy.Decide(0, 7, gatewayReachable: true, attempt: 1));

    [Fact]
    public void AllRefused_OnAReachableGateway_IsNotPresent_OnTheFirstAttempt()
        => Assert.Equal(NativeAudioGuardVerdict.NotPresent, NativeAudioGuardPolicy.Decide(7, 7, gatewayReachable: true, attempt: 1));

    [Fact]
    public void AllFailed_WithTheGatewayDown_IsATransient()
    {
        Assert.Equal(NativeAudioGuardVerdict.Retry, NativeAudioGuardPolicy.Decide(7, 7, gatewayReachable: false, attempt: 1));
        Assert.Equal(NativeAudioGuardVerdict.Retry, NativeAudioGuardPolicy.Decide(7, 7, gatewayReachable: false, attempt: 2));
        Assert.Equal(NativeAudioGuardVerdict.GaveUp, NativeAudioGuardPolicy.Decide(7, 7, gatewayReachable: false, attempt: NativeAudioGuardPolicy.MaxAttempts));
    }

    [Fact]
    public void PartialFailure_RetriesThenGivesUp()
    {
        Assert.Equal(NativeAudioGuardVerdict.Retry, NativeAudioGuardPolicy.Decide(2, 7, gatewayReachable: true, attempt: 1));
        Assert.Equal(NativeAudioGuardVerdict.GaveUp, NativeAudioGuardPolicy.Decide(2, 7, gatewayReachable: true, attempt: 3));
    }

    [Fact]
    public void Guards()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeAudioGuardPolicy.Decide(-1, 7, true, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeAudioGuardPolicy.Decide(0, 0, true, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeAudioGuardPolicy.Decide(0, 7, true, 0));
    }
}
