namespace ProsimCompanion.Core.State;

/// <summary>
/// One-click component tests for the /speech page (Prosim2FO status-window parity): exercise
/// a single TTS provider, the LLM endpoint, or the microphone without flying a sector. Kept
/// in Core so the Web project (which references only Core) can inject it; implemented by the
/// speech pillar. Every method returns a human-readable result line and never throws (except
/// for cancellation) — a failed dependency is the answer, not an error.
/// </summary>
public interface ISpeechDiagnostics
{
    /// <summary>Synthesizes <paramref name="sampleText"/> with exactly the named provider —
    /// no fallback chain — and plays it, so providers and voices can be compared and a
    /// misconfigured one is caught directly.</summary>
    Task<string> TestTtsProviderAsync(string providerName, string sampleText, CancellationToken cancellationToken);

    /// <summary>One tiny chat completion against the configured LLM endpoint (briefing.llm*
    /// settings, shared by briefing and debrief styling).</summary>
    Task<string> TestLlmAsync(CancellationToken cancellationToken);

    /// <summary>Captures a couple of seconds from the configured input device and reports the
    /// peak level heard — the pre-flight "can the FO hear me at all" check.</summary>
    Task<string> TestMicrophoneAsync(CancellationToken cancellationToken);

    /// <summary>Verifies the Navigraph DFD is present and readable (AIRAC header) — the DFD
    /// nulls out silently on a wrong path or schema, so this is the cheap pre-flight check
    /// that briefings will actually have nav facts.</summary>
    Task<string> TestNavDataAsync(CancellationToken cancellationToken);

    /// <summary>Sends the Wake-on-LAN magic packet to the configured LLM host (manual wake
    /// mid-session; the startup send happens automatically when enabled). Fire-and-forget —
    /// confirm readiness with the LLM connection test afterwards.</summary>
    Task<string> WakeLlmServerAsync(CancellationToken cancellationToken);
}
