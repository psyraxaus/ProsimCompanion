namespace ProsimCompanion.Speech.SayIntentions;

/// <summary>
/// The falling edge of <c>L:SIAI_COM1_RECEIVING</c> — "ATC has just finished a line on COM1".
/// SayIntentions has no push feed for its comms history (REST only, 2026-11-07), so the
/// monitor polls on this edge instead of on a fixed clock: the reply's text is in the record
/// before its audio has finished playing, so an edge-triggered poll never lands in the gap
/// between the pilot's record being created and ATC's text landing in it, and the FO answers
/// as ATC stops talking rather than up to a poll interval later. Pure for tests; the first
/// sample is never an edge (a restart mid-sentence must not fire a poll for nothing).
/// </summary>
internal sealed class Com1ReceivingEdge
{
    private bool? _lastBusy;

    /// <summary>Feeds one sample; true exactly when it goes from busy (≥ 0.5) to clear.</summary>
    public bool Step(double value)
    {
        var busy = value >= 0.5;
        var falling = _lastBusy == true && !busy;
        _lastBusy = busy;
        return falling;
    }

    /// <summary>Forgets the last sample — after the subscription went stale (MSFS gone) the
    /// next value is a first sample again.</summary>
    public void Reset() => _lastBusy = null;
}
