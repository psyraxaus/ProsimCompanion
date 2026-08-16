namespace ProsimCompanion.Core.Aircraft;

/// <summary>
/// ProsimCompanion's OWN tracking LVARs (issue #30): turnaround progress the sim cannot tell us
/// (quick services complete back to "available", refuel's LVAR is ambiguous, reposition has no
/// state at all) is written into the MSFS session so an app restart mid-turnaround can resync
/// instead of re-running ground services. LVARs were chosen over a progress file deliberately:
/// their lifetime matches GSX's own state — they survive an app restart but self-invalidate
/// when the sim restarts, exactly when GSX's ground state resets too, so a resync can never
/// consume stale carried-over state. Numeric only; leg identity stays live-derived from the
/// EFB/FMS datarefs. MSFS auto-creates unknown LVAR names as 0, so reader and writer MUST both
/// come through this class (a typo'd read silently means "not done").
/// </summary>
public static class CompanionLvarNames
{
    /// <summary>Every companion LVAR starts with this (also the sim write allow-list prefix).</summary>
    public const string Prefix = "L:PROSIMCOMPANION_";

    /// <summary>1 = this leg is a turnaround (an arrival was observed this sim session).</summary>
    public static readonly SimVarRef<double> Turnaround = new(Prefix + "TURNAROUND", "number", DataRefTier.Infrequent, 0.0);

    /// <summary>1 = the ground-preparation chain (reposition/equipment/jetway) completed.</summary>
    public static readonly SimVarRef<double> PrepDone = new(Prefix + "PREP_DONE", "number", DataRefTier.Infrequent, 0.0);

    /// <summary>Edition number of the preliminary loadsheet sent this cycle; 0 = none
    /// (int semantics on a numeric LVAR — read sites round).</summary>
    public static readonly SimVarRef<double> LoadsheetPrelimEdition = new(Prefix + "LOADSHEET_PRELIM_EDNO", "number", DataRefTier.Infrequent, 0.0);

    /// <summary>1 = the final loadsheet was sent this cycle.</summary>
    public static readonly SimVarRef<double> LoadsheetFinalSent = new(Prefix + "LOADSHEET_FINAL_SENT", "number", DataRefTier.Infrequent, 0.0);

    /// <summary>Per-service completion latch (1 = completed this cycle). Only meaningful for
    /// one-shot services — the jetway/stairs toggles are positional and never latch. A builder
    /// rather than fixed descriptors: the service-id set lives in GsxServiceIds; the guard test
    /// enumerates the ids so every producible name is still CSV-of-record checked.</summary>
    public static SimVarRef<double> ServiceDone(string serviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        return new SimVarRef<double>(Prefix + "SVC_DONE_" + serviceId.ToUpperInvariant(), "number", DataRefTier.Infrequent, 0.0);
    }
}
