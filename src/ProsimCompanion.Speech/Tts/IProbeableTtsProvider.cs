namespace ProsimCompanion.Speech.Tts;

/// <summary>
/// A TTS provider with a cheap liveness check separate from synthesis — network providers
/// with a health endpoint. The router's "reconnect" probe uses it to clear a cooldown early
/// without spending a synthesis on a box that may still be down.
/// </summary>
public interface IProbeableTtsProvider
{
    /// <summary>True when the provider answers its health check now. Never throws.</summary>
    Task<bool> ProbeAsync(CancellationToken cancellationToken);
}
