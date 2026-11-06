using Microsoft.Extensions.Logging;

namespace ProsimCompanion.Speech.Arbiter;

/// <summary>
/// The house-safe fire-and-forget (review 2026-11-05): a task nobody awaits still gets its
/// fault read and logged, instead of dying as an unobserved exception. For the speech lines a
/// handler sends on its way out — a cancelled checklist's "cancelled", the away line, a
/// transition reminder — where the handler's own token is already gone.
/// </summary>
public static class FireAndForget
{
    /// <summary>Observes <paramref name="task"/>'s fault at Warning under <paramref name="what"/>.
    /// A cancelled task is not a fault and logs nothing.</summary>
    public static void Observe(this Task task, ILogger logger, string what)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(logger);
        task.ContinueWith(
            t => logger.LogWarning(t.Exception, "{What} faulted", what),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
