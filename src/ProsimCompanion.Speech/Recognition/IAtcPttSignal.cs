namespace ProsimCompanion.Speech.Recognition;

/// <summary>
/// "The pilot keyed ATC" as one edge, whatever keys it: the ATC push-to-talk binding (a key, a
/// joystick button or a ProSim switch — <see cref="PushToTalkService"/>) and the ProSim switch
/// relayed to SayIntentions (<c>sayIntentions.pttSource</c>). Consumers take every registered
/// signal; the wrong-frequency question asks at once on it (owner request 2026-11-16). Reading
/// this adds no input polling of its own.
/// </summary>
public interface IAtcPttSignal
{
    /// <summary>Raised once per press, on the input's own thread — handlers must not block.</summary>
    event Action? AtcPttPressed;
}
