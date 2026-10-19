namespace ProsimCompanion.Core.State;

/// <summary>Arms/cancels arrival-gate requests from UI surfaces (implemented by the GSX layer;
/// status is visible through <see cref="GsxDiagnosticsStore"/>).</summary>
public interface IGsxGateControl
{
    void RequestGate(string gate);

    void Cancel();
}
