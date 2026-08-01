namespace ProsimCompanion.Gsx;

/// <summary>
/// GSX LVAR names used alongside the Remote API (docs/integrations/gsx-remote-api.md §8 —
/// "the API drives the answer; LVARs drive the timing"). Names are wire identifiers; extend
/// deliberately as features land. Subscribe with unit "number".
/// </summary>
public static class GsxLvarNames
{
    // Gate-assignment readback (confirmation of gate.select; letter map in GsxGateResolver).
    public const string SetGateName = "L:FSDT_GSX_SetGate_Name";
    public const string SetGateNumber = "L:FSDT_GSX_SetGate_Number";
    public const string SetGateSuffix = "L:FSDT_GSX_SetGate_Suffix";
}
