namespace ProsimCompanion.Core.Radios;

/// <summary>How a tile-driven COM1 tune ended.</summary>
public enum RadioTuneOutcome
{
    /// <summary>COM1 active reads the requested frequency.</summary>
    Tuned,

    /// <summary>ProSim is not pushing the COM1 datarefs — nothing was written.</summary>
    NotConnected,

    /// <summary>The frequency is outside the VHF COM band — nothing was written.</summary>
    OutOfBand,

    /// <summary>The standby write did not take (twice) — the active frequency was left alone.</summary>
    Failed,

    /// <summary>The swap was written but COM1 active did not read back the frequency in time.</summary>
    Unverified,
}

/// <summary>Tunes COM1 from the web surfaces (the Radios tile). Dataref-first and
/// standby-then-swap, like the FO's own radio executor — never a cockpit switch, never the
/// active frequency written without the standby having taken first.</summary>
public interface IRadioTuner
{
    /// <summary>Sets <paramref name="khz"/> on COM1 standby, verifies it, then swaps it to
    /// active. <paramref name="reason"/> names the button that asked ("assigned", "station")
    /// and goes to the session event.</summary>
    Task<RadioTuneOutcome> TuneCom1Async(int khz, string reason, CancellationToken cancellationToken = default);
}
