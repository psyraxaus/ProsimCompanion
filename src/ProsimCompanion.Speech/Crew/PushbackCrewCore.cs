using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Speech.Crew;

/// <summary>
/// The pushback crew's script, pure (owner request 2026-11-07, plan V2): which line a
/// <see cref="PushbackStage"/> or a captain's phrase earns, given what was already said this
/// push. Every line comes once; the brakes question is asked once and answered once; the
/// clearance to push is given either on the captain's "brakes released" or, when that never
/// came, as the push itself begins — never both. The shell (<see cref="PushbackCrewService"/>)
/// owns the signal subscriptions, the interphone ring and the arbiter.
/// </summary>
internal static class PushbackCrewCore
{
    /// <summary>What was said this push (reset with the flight cycle and at the Clear stage).</summary>
    internal readonly record struct CrewState(
        bool BridgeSpoken,
        bool TugSpoken,
        bool BrakesAsked,
        bool PushClearanceSpoken,
        bool EngineStartSpoken,
        bool PushCompleteSpoken,
        bool ConfirmedSpoken,
        bool ClearSpoken)
    {
        internal static readonly CrewState Initial = default;

        /// <summary>The captain's "brakes released" is an answer only while the question stands.</summary>
        internal bool BrakesQuestionOpen => BrakesAsked && !PushClearanceSpoken;
    }

    /// <summary>One line to speak: the session-event tag suffix, the text, and whether the crew
    /// opens the call (MECH press) or continues one already running.</summary>
    internal sealed record CrewLine(string Tag, string Text, bool OpensCall);

    /// <summary>The captain's phrases the crew answers — public for the drawer and the tests.</summary>
    internal static readonly string[] BrakesReleasedPhrases =
    [
        "brakes released",
        "brake released",
        "parking brake released",
        "parking brake is released",
        "brakes are released",
        "released",
    ];

    internal static readonly string[] ReadyPhrases =
    [
        "ready for pushback",
        "ready for push",
        "ready for pushback and start",
        "ready for push and start",
        "we are ready for pushback",
        "we are ready for push",
    ];

    /// <summary>The line a stage earns, or null when it was said already or the option is off.</summary>
    internal static (CrewState State, CrewLine? Line) OnStage(CrewState state, PushbackStage stage, PushbackCrewOptions options, string? direction)
    {
        ArgumentNullException.ThrowIfNull(options);
        switch (stage)
        {
            case PushbackStage.BridgeRemoved when !state.BridgeSpoken:
                return (state with { BridgeSpoken = true }, Line("bridge", options.BridgeRemovedText, opensCall: true));

            case PushbackStage.TugConnected when !state.TugSpoken:
                {
                    var text = options.TugConnectedText;
                    var asks = options.BrakesCheck && !string.IsNullOrWhiteSpace(options.BrakesRequestText);
                    if (asks)
                    {
                        text = Join(text, options.BrakesRequestText);
                    }

                    return (state with { TugSpoken = true, BrakesAsked = asks }, Line("tug", text, opensCall: true));
                }

            case PushbackStage.Pushing when !state.PushClearanceSpoken || (options.EngineStartClearance && !state.EngineStartSpoken):
                {
                    // The clearance that was never given by voice comes with the push; the
                    // engine-start clearance rides on the same breath (the A320 starts during
                    // the push). Either half may already be spoken.
                    var parts = new List<string>(2);
                    if (!state.PushClearanceSpoken)
                    {
                        parts.Add(Fill(options.PushingText, direction));
                    }

                    if (options.EngineStartClearance && !state.EngineStartSpoken)
                    {
                        parts.Add(options.EngineStartText);
                    }

                    var next = state with { PushClearanceSpoken = true, EngineStartSpoken = state.EngineStartSpoken || options.EngineStartClearance };
                    return (next, Line("pushing", Join(parts), opensCall: false));
                }

            case PushbackStage.PushComplete when !state.PushCompleteSpoken:
                return (state with { PushCompleteSpoken = true }, Line("complete", options.PushCompleteText, opensCall: false));

            case PushbackStage.EngineStartConfirmed when !state.ConfirmedSpoken:
                return (state with { ConfirmedSpoken = true }, Line("confirmed", options.EngineStartConfirmedText, opensCall: false));

            case PushbackStage.Clear when !state.ClearSpoken:
                return (state with { ClearSpoken = true }, Line("clear", options.ClearText, opensCall: true));

            default:
                return (state, null);
        }
    }

    /// <summary>The captain's "brakes released": the push clearance (with the engine-start
    /// clearance when that option is on) while the question stands; nothing otherwise — the
    /// phrase then belongs to nobody and the router says so.</summary>
    internal static (CrewState State, CrewLine? Line) OnBrakesReleased(CrewState state, PushbackCrewOptions options, string? direction)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!state.BrakesQuestionOpen)
        {
            return (state, null);
        }

        var text = Fill(options.ClearedToPushText, direction);
        if (options.EngineStartClearance && !state.EngineStartSpoken)
        {
            text = Join(text, options.EngineStartText);
        }

        return (
            state with { PushClearanceSpoken = true, EngineStartSpoken = state.EngineStartSpoken || options.EngineStartClearance },
            Line("cleared", text, opensCall: false));
    }

    /// <summary>The captain's "ready for pushback": acknowledged, nothing latched — the tug
    /// comes when GSX sends it.</summary>
    internal static CrewLine? OnReady(PushbackCrewOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Line("ready", options.ReadyAckText, opensCall: false);
    }

    /// <summary><c>{direction}</c> → "tail left" and friends; unknown → the placeholder goes,
    /// and the comma or "to the" before it with it ("Pushing back, {direction}." → "Pushing back.").</summary>
    internal static string Fill(string template, string? direction)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (!template.Contains("{direction}", StringComparison.Ordinal))
        {
            return template;
        }

        if (!string.IsNullOrWhiteSpace(direction))
        {
            return template.Replace("{direction}", direction.Trim(), StringComparison.Ordinal);
        }

        var text = template.Replace(", {direction}", "", StringComparison.Ordinal)
            .Replace(" {direction}", "", StringComparison.Ordinal)
            .Replace("{direction}", "", StringComparison.Ordinal);
        return text.Replace(" .", ".", StringComparison.Ordinal).Replace(" ,", ",", StringComparison.Ordinal).Trim();
    }

    private static CrewLine? Line(string tag, string text, bool opensCall)
        => string.IsNullOrWhiteSpace(text) ? null : new CrewLine(tag, text.Trim(), opensCall);

    private static string Join(params IEnumerable<string> parts)
        => string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()));
}
