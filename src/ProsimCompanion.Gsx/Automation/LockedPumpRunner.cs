namespace ProsimCompanion.Gsx.Automation;

/// <summary>
/// The host's pump lock: runs one planning function under a try-acquire lock and applies its
/// effects in order — the ones flagged <see cref="DeparturePumpEffect.RunsAfterLockRelease"/>
/// only once the lock is released. A second caller while a pump is running is turned away,
/// not queued: state changes re-trigger the pump anyway. Separate from the host so a test can
/// prove the deferral with a fake applier that records <see cref="IsHeld"/>.
/// </summary>
internal sealed class LockedPumpRunner : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>True while a pump holds the lock (what a deferred effect must never see).</summary>
    public bool IsHeld => _lock.CurrentCount == 0;

    /// <summary>Runs one pump. Returns false when another pump already held the lock.</summary>
    /// <param name="plan">Gathers, evaluates and plans — runs under the lock.</param>
    /// <param name="applier">Performs each effect.</param>
    /// <param name="onError">Receives any exception from planning or applying (the pump is
    /// a timer callback — nothing may escape it).</param>
    public bool TryRun(
        Func<IReadOnlyList<DeparturePumpEffect>> plan,
        IDeparturePumpEffectApplier applier,
        Action<Exception> onError)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(applier);
        ArgumentNullException.ThrowIfNull(onError);

        if (!_lock.Wait(0))
        {
            return false; // a pump is already running; state changes re-trigger us anyway
        }

        // Deferred effects are collected in list order as the walk reaches them, so an
        // effect that failed to apply never lets a later deferred one run — exactly the
        // shell's old `markDeiceNext` flag, which was set in place and read after the lock.
        var deferred = new List<DeparturePumpEffect>();
        try
        {
            foreach (var effect in plan())
            {
                if (effect.RunsAfterLockRelease)
                {
                    deferred.Add(effect);
                    continue;
                }

                applier.Apply(effect);
            }
        }
        catch (Exception ex)
        {
            onError(ex);
        }
        finally
        {
            _lock.Release();
        }

        foreach (var effect in deferred)
        {
            try
            {
                applier.Apply(effect);
            }
            catch (Exception ex)
            {
                onError(ex);
            }
        }

        return true;
    }

    public void Dispose() => _lock.Dispose();
}
