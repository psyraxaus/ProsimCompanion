using ProsimCompanion.Core.Aircraft;

namespace ProsimCompanion.Speech.SayIntentions;

/// <summary>
/// SayIntentions' own L:vars (docs/integrations/sayintentions.md), read through
/// <see cref="ISimVars"/> for the radio-clear gate before a sayAs transmission (2026-10-08 —
/// the Phase 5 leftover). The sim auto-creates an unknown L:var as 0, so with SayIntentions
/// absent both read "clear" and the gate passes at once — the degrade the predecessor's
/// no-SimVars fallback gave. Typed catalog, as the DataRef guard requires.
/// </summary>
public static class SayIntentionsLvarNames
{
    /// <summary>1 while ATC (or another station) is transmitting on COM1.</summary>
    public static readonly SimVarRef<double> Com1Receiving = new("L:SIAI_COM1_RECEIVING", "number", DataRefTier.Frequent, 0.0);

    /// <summary>1 while the pilot's own push-to-talk is keyed in SayIntentions.</summary>
    public static readonly SimVarRef<double> RadioPtt = new("L:SIAI_RADIO_PTT", "number", DataRefTier.Frequent, 0.0);

    /// <summary>The settable PTT control LVARs the SayIntentions client lists under Controls →
    /// "Map PTT inside the sim with an LVAR or dataref" (read off the client 2026-10-10; not in
    /// the published LVAR reference): write 1 to talk, 0 to stop. Each is on the sim write
    /// allow-list by exact name.</summary>
    public static readonly IReadOnlyList<(string Name, string Label)> ControlPttLvars =
    [
        ("L:SIAI_CONTROL_PTT_COM", "COM radio PTT (the selected radio)"),
        ("L:SIAI_CONTROL_PTT_COM1", "COM1 PTT (forced)"),
        ("L:SIAI_CONTROL_PTT_COM2", "COM2 PTT (forced)"),
        ("L:SIAI_CONTROL_PTT_INTERCOM1", "Intercom PTT — channel 1"),
        ("L:SIAI_CONTROL_PTT_INTERCOM2", "Intercom PTT — channel 2"),
        ("L:SIAI_CONTROL_PTT_INTERCOM3", "Intercom PTT — channel 3"),
        ("L:SIAI_CONTROL_PTT_GROUP", "Group flight PTT"),
    ];
}
