using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Core.State;

/// <summary>Send-now surface for Wake-on-LAN (2026-10-08): the Setup page's per-row button and
/// the Voice FO status panel's "Wake PCs". Each call returns the human-readable outcome
/// line the UI shows; nothing is awaited — WoL has no acknowledgement.</summary>
public interface IWakeOnLanControl
{
    /// <summary>One packet to one target (the page's row button; works unsaved).</summary>
    string Send(WakeTarget target);

    /// <summary>One packet to every enabled target (plus the legacy single-PC block).</summary>
    string SendAll();
}
