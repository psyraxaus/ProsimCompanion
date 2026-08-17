using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Crew;

/// <summary>
/// Voice trigger for the on-demand aircraft state re-check (issue #92): "check the aircraft
/// state" clears the once-per-session latch through the Core
/// <see cref="IAircraftStateCheckControl"/> seam and the fresh verdict comes back through the
/// FO's own advisory line (<see cref="AircraftStateAdvisoryService"/> — the re-check forces
/// Announce, so even a Pass is spoken). This feature only speaks itself when the assessment
/// could not run yet — the pilot must never ask and hear silence. The control is optional:
/// with the GSX pillar absent the feature is disabled and contributes no grammar.
/// </summary>
public sealed class AircraftStateVoiceService : IVoiceFeature
{
    private static readonly string[] CommandPhrases =
    [
        "check the aircraft state", "recheck the aircraft state",
        "check aircraft state", "recheck aircraft state",
        "check the state of the aircraft",
    ];

    private readonly IAircraftStateCheckControl? _control;
    private readonly ISpeechArbiter _arbiter;
    private readonly ILogger<AircraftStateVoiceService> _logger;

    public AircraftStateVoiceService(
        IAircraftStateCheckControl? control,
        ISpeechArbiter arbiter,
        ILogger<AircraftStateVoiceService> logger)
    {
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(logger);

        _control = control;
        _arbiter = arbiter;
        _logger = logger;
    }

    /// <summary>Disabled without the GSX pillar's check service — degrade, not fail.</summary>
    public bool Enabled => _control is not null;

    public IEnumerable<string> Phrases => CommandPhrases;

    public bool ValueParse => false;

    public bool TryHandle(string utterance)
    {
        var text = CommandMatcher.Normalize(utterance);
        if (_control is null || !CommandPhrases.Contains(text, StringComparer.Ordinal))
        {
            return false;
        }

        if (!_control.RequestRecheck("voice command"))
        {
            // The settle guards held (no sim session, datarefs not ready) — answer now
            // rather than leaving the request hanging; the advisory has nothing to speak.
            _logger.LogInformation("Aircraft state re-check by voice could not run yet");
            _ = _arbiter.EnqueueAsync(new SpeechRequest(
                "Standing by — I can't check the aircraft state right now.",
                SpeechPriority.Normal, Tag: "fo.aircraft-state"));
        }

        return true;
    }
}
