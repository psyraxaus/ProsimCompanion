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
}
