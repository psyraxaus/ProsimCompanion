using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Callouts;

/// <summary>
/// The "gear up" / "gear down" PM action (issue #43, first non-FCU PM duty): the pilot's
/// call moves the lever — <c>S_MIP_GEAR</c> [0:Down, 1:Up], a latching state switch written
/// directly (dataref-first per the write-safety rule) — and the FO reads the command back.
/// Called twice and rejected seconds after liftoff on the 2026-08-22 flight. Safety gate:
/// "gear up" needs airborne evidence from the flight state engine — with no engine or no
/// data the feature answers verbally and never writes. Sibling of
/// <see cref="EngineFlapCallFeature"/> (which stays deliberately write-free); this feature
/// exists because gear is the one call whose whole point is the action.
/// </summary>
public sealed class GearCallFeature : IVoiceFeature
{
    private const int LeverUp = 1;
    private const int LeverDown = 0;

    private static readonly string[] UpPhrases = ["gear up"];
    private static readonly string[] DownPhrases = ["gear down"];

    private readonly IProsimDataRefs _dataRefs;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<GearCallFeature> _logger;
    private readonly IFlightPhaseSource? _flight;

    public GearCallFeature(
        IProsimDataRefs dataRefs,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<GearCallFeature> logger,
        IFlightPhaseSource? flight = null)
    {
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _dataRefs = dataRefs;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
        _flight = flight;
    }

    public bool Enabled => true;

    public IEnumerable<string> Phrases => [.. UpPhrases, .. DownPhrases];

    public bool ValueParse => false;

    public bool TryHandle(string utterance)
    {
        var text = CommandMatcher.Normalize(utterance);
        var up = UpPhrases.Contains(text, StringComparer.Ordinal);
        if (!up && !DownPhrases.Contains(text, StringComparer.Ordinal))
        {
            return false;
        }

        var data = _flight?.Snapshot().Data;
        if (up && data is not { IsValid: true, OnGround: false })
        {
            // Retraction demands positive evidence of being airborne — "no data" and "on the
            // ground" both refuse. The refusal wording is the real-world PM's.
            Speak("Negative — we are on the ground.");
            return true;
        }

        if (!up && data is { IsValid: true, OnGround: true, GearDown: true })
        {
            Speak("The gear is down.");
            return true;
        }

        _ = MoveGearAsync(up);
        return true;
    }

    private async Task MoveGearAsync(bool up)
    {
        try
        {
            await _dataRefs.WriteAsync(ProsimDataRefNames.MipGear, up ? LeverUp : LeverDown)
                .ConfigureAwait(false);
            _eventLog.Record("voicecommand.gear", new { position = up ? "up" : "down" });
            Speak(up ? "Gear up." : "Gear down.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gear lever write failed");
            Speak("Unable — the gear lever did not respond.");
        }
    }

    private void Speak(string text)
        => _ = _arbiter.EnqueueAsync(new SpeechRequest(
            text, SpeechPriority.Normal, Ttl: TimeSpan.FromSeconds(20), Tag: "fo.gear"));
}
