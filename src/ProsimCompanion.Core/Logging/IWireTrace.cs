namespace ProsimCompanion.Core.Logging;

/// <summary>
/// Raw protocol traffic capture (GraphQL bodies, GSX Remote API frames, …) into a dedicated
/// CMTrace-format wire log, gated by <c>logging.wireTrace</c>. Callers should check
/// <see cref="Enabled"/> before building expensive payload strings.
/// </summary>
public interface IWireTrace
{
    /// <summary>True while wire tracing is switched on in settings.</summary>
    bool Enabled { get; }

    /// <summary>Records one frame. <paramref name="channel"/> names the transport
    /// ("Gateway", "GsxRemoteApi"); <paramref name="direction"/> is "&gt;&gt;" (sent) or
    /// "&lt;&lt;" (received).</summary>
    void Trace(string channel, string direction, string payload);
}
