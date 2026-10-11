namespace ProsimCompanion.Audio;

/// <summary>What one pass of the native-audio guard concluded.</summary>
public enum NativeAudioGuardVerdict
{
    /// <summary>Every binding cleared.</summary>
    Cleared,

    /// <summary>The gateway answered and refused every window: the
    /// <c>aircraft.communication.windows.*</c> datarefs are not on this ProSim build (ProSim
    /// 1.75.1 answers <c>writeBool:false</c> for all seven, 2026-10-10). Nothing to clear;
    /// said once per connection, never retried.</summary>
    NotPresent,

    /// <summary>Some windows failed, or the gateway was not answering: try again on the next
    /// status change.</summary>
    Retry,

    /// <summary>Retried as often as the policy allows; give up for this connection.</summary>
    GaveUp,
}

/// <summary>
/// Pure decision for <see cref="ProsimNativeAudioGuard"/> (issue #175): the 2026-10-10 log
/// carried "Clearing ProSim native audio bindings failed … will retry" 41 times in a day
/// because every connection-status change re-ran a write set that ProSim 1.75.1 refuses by
/// design. The gateway's "false" verdict on a reachable gateway means "no such dataref",
/// the same reading the ProSim Setup Check uses — so all-failed-while-reachable is
/// <see cref="NativeAudioGuardVerdict.NotPresent"/>, not a transient.
/// </summary>
public static class NativeAudioGuardPolicy
{
    /// <summary>Retries allowed per connection for a partial or transport failure.</summary>
    public const int MaxAttempts = 3;

    /// <param name="failed">Windows the gateway refused on this pass.</param>
    /// <param name="total">Windows written.</param>
    /// <param name="gatewayReachable">Whether the gateway answered HTTP after the pass.</param>
    /// <param name="attempt">1-based attempt number within this connection.</param>
    public static NativeAudioGuardVerdict Decide(int failed, int total, bool gatewayReachable, int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(failed);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(total);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);

        if (failed == 0)
        {
            return NativeAudioGuardVerdict.Cleared;
        }

        if (failed >= total && gatewayReachable)
        {
            return NativeAudioGuardVerdict.NotPresent;
        }

        return attempt >= MaxAttempts ? NativeAudioGuardVerdict.GaveUp : NativeAudioGuardVerdict.Retry;
    }
}
