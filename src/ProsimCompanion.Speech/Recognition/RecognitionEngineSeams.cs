namespace ProsimCompanion.Speech.Recognition;

/// <summary>
/// Test seams for <see cref="RecognitionController"/>'s LAN/offline engine chain. Production
/// DI passes none: the controller then builds the real recognizers and probes the LAN
/// engine's <c>/health</c> over HTTP with its production timings.
/// </summary>
/// <param name="HealthProbe">Replaces the HTTP health check; true = the LAN engine is ready.</param>
/// <param name="LanFactory">Builds the LAN engine (used at start-up when configured and on
/// every swap back).</param>
/// <param name="OfflineFactory">Builds the offline engine (used at start-up without a LAN
/// configuration and on every fallback).</param>
/// <param name="ReadinessBudget">How long the start-up probe waits before falling back.</param>
/// <param name="ReadinessCadence">Delay between start-up probes.</param>
/// <param name="ReprobeInterval">Delay between re-probes after a fallback.</param>
public sealed record RecognitionEngineSeams(
    Func<CancellationToken, Task<bool>>? HealthProbe = null,
    Func<IVoiceRecognizer>? LanFactory = null,
    Func<IVoiceRecognizer>? OfflineFactory = null,
    TimeSpan? ReadinessBudget = null,
    TimeSpan? ReadinessCadence = null,
    TimeSpan? ReprobeInterval = null);
