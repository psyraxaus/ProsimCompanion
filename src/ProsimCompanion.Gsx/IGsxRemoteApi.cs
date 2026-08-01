using System.Text.Json.Nodes;
using ProsimCompanion.Gsx.Mirror;

namespace ProsimCompanion.Gsx;

/// <summary>
/// The Remote API surface consumed by the higher GSX layers (intents, gate selection,
/// automation). Implemented by <see cref="GsxRemoteApiClient"/>; faked in tests.
/// </summary>
public interface IGsxRemoteApi
{
    GsxReadiness Readiness { get; }

    /// <summary>Raised on readiness transitions, on the receive thread.</summary>
    event Action<GsxReadiness>? ReadinessChanged;

    GsxStateMirror Mirror { get; }

    /// <summary>True when the hello advertised the capability (case-insensitive).</summary>
    bool HasCapability(string token);

    /// <summary>Sends a command; synthetic failure results are returned, never thrown.</summary>
    Task<GsxCommandResult> SendCommandAsync(string verb, JsonObject? args, CancellationToken cancellationToken = default);
}
