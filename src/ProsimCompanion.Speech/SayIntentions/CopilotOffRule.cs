namespace ProsimCompanion.Speech.SayIntentions;

/// <summary>Why the FO-radios service tells the SayIntentions copilot "off".</summary>
internal enum CopilotOffReason
{
    /// <summary>Nothing to send.</summary>
    None,

    /// <summary>The first tick of an active flight (or of the feature being switched on).</summary>
    FlightStart,

    /// <summary>SayIntentions shows its copilot back in charge of comms.</summary>
    CopilotOn,
}

/// <summary>
/// When "SIAI_COPILOT = 0" goes to SayIntentions while the FO works the radios. Pure.
/// <para>
/// History: until 2026-11-14 it was re-sent every two minutes as a safety net. The
/// SayIntentions client plays its two-note copilot tone on EVERY such setVar, changed or not —
/// the owner heard it every two minutes for weeks, the client's own log showed audio on a
/// 120–135 s rhythm matching our 15 s tick, and switching the feature off stopped it. So: once
/// per flight (the copilot may be on when the flight begins — that one tone is correct), and
/// again only when <c>L:SIAI_COPILOT</c> reads on, never more often than <see cref="MinGap"/>
/// (a client that ignores the write must not be hammered, and the L:var lags the write).
/// With the L:var unreadable (no SimConnect) only the once-per-flight send remains.
/// </para>
/// </summary>
internal static class CopilotOffRule
{
    /// <summary>Shortest time between two "off" writes of one flight.</summary>
    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(30);

    public static CopilotOffReason Decide(bool sentThisFlight, double? copilotLvar, TimeSpan sinceLastSend)
    {
        if (!sentThisFlight)
        {
            return CopilotOffReason.FlightStart;
        }

        return copilotLvar is >= 0.5 && sinceLastSend >= MinGap
            ? CopilotOffReason.CopilotOn
            : CopilotOffReason.None;
    }
}
