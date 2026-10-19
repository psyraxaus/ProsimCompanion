using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Airports.Parking;

namespace ProsimCompanion.Core.State;

/// <summary>The pushback-direction state of the current departure.</summary>
/// <param name="Choice">The pilot's decision (voice, OFP page Korry buttons, API), or the
/// suggestion once the automation adopted it; null while undecided.</param>
/// <param name="Suggestion">The advisor's recommendation for this stand and runway; null
/// while unknown (no stand, no runway, one option only is still a suggestion).</param>
/// <param name="Options">The directions on offer as last understood (profile slots or the
/// live GSX menu lines).</param>
/// <param name="Stand">The stand the suggestion was computed for ("Gate 40").</param>
/// <param name="Runway">The departure runway it was computed against ("22L").</param>
/// <param name="Asking">The captain's "which way?" question (the shared captain's-question
/// state): set while the automation has asked the pilot to choose (GSX's direction menu is
/// open and no choice is known); the FO voices it.</param>
public sealed record PushbackChoiceSnapshot(
    PushbackChoice? Choice,
    PushbackSuggestion? Suggestion,
    IReadOnlyList<PushbackOption> Options,
    string? Stand,
    string? Runway,
    CaptainsQuestionState Asking) : ICaptainsQuestionSnapshot<PushbackChoiceSnapshot>
{
    public static PushbackChoiceSnapshot Empty { get; } = new(null, null, [], null, null, CaptainsQuestionState.Empty);

    /// <summary>The direction that will be applied when GSX asks: the pilot's choice, else the
    /// suggestion (when confident), else nothing.</summary>
    public PushbackChoice? Effective => Choice ?? (Suggestion is { Confidence: PushbackConfidence.High } s ? s.AsChoice("suggestion") : null);

    /// <summary>The open "which way?" question, null when none stands.</summary>
    public CaptainsQuestion? Question => Asking.Question;

    /// <inheritdoc />
    public PushbackChoiceSnapshot WithAsking(CaptainsQuestionState asking)
    {
        ArgumentNullException.ThrowIfNull(asking);
        return this with { Asking = asking };
    }
}

/// <summary>
/// Per-flight pushback direction (2026-10-04). The old model was one GLOBAL setting the pilot
/// had to remember to flip before every push; this store holds the decision for THIS departure
/// and resets with the flight cycle and whenever a new OFP arrives (a new plan may mean a new
/// runway). Written by the OFP page, the voice feature and the pushback advisor; read by the
/// GSX question catalogue when GSX raises "Select pushback direction".
/// <para>Adapter over <see cref="CaptainsQuestionStore{TSnapshot}"/> (architecture review C2):
/// a pick-one question over the menu lines. The pushback question has no answer record of its
/// own — the flight's <see cref="PushbackChoiceSnapshot.Choice"/> is the decision (the Korry
/// buttons set it before any question exists), and a choice withdraws the standing question.
/// The #166 rules now hold here too: the FO's unanswered listens leave the question open
/// (before C2 they withdrew it), the standard 5-minute TTL withdraws it (GSX's menu stays open
/// for the pilot), and the menu closing withdraws it as before.</para>
/// </summary>
public sealed class PushbackChoiceStore : CaptainsQuestionStore<PushbackChoiceSnapshot>
{
    /// <summary>Pick one of the menu lines; a new menu may ask again; the TTL withdraws.</summary>
    public static CaptainsQuestionDefinition QuestionDefinition { get; } = new(
        "pushback",
        CaptainsQuestionShape.PickOne,
        CaptainsQuestionDefinition.StandardTtl,
        CaptainsQuestionExpiry.Withdraws,
        CaptainsQuestionDefinition.StandardMaxAsks,
        AnswerIsFinalForCycle: false);

    private readonly OfpStore _ofpStore;
    private string? _lastOfpRequestId;

    public PushbackChoiceStore(OfpStore ofpStore, GroundOpsSignals signals)
        : base(QuestionDefinition, PushbackChoiceSnapshot.Empty, signals)
    {
        ArgumentNullException.ThrowIfNull(ofpStore);
        _ofpStore = ofpStore;
        _lastOfpRequestId = ofpStore.Current?.RequestId;
        _ofpStore.Changed += OnOfpChanged;
    }

    public override void Dispose()
    {
        _ofpStore.Changed -= OnOfpChanged;
        base.Dispose();
    }

    /// <summary>"Pushback: tail left or tail right?" — or the stand's own labels when the
    /// profile names them ("Facing SW on Taxi AV, or Facing NE on Taxi AT?"). The question's
    /// prompt as the FO speaks it and the web prints it; pure, public for tests.</summary>
    public static string Prompt(IReadOnlyList<PushbackOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var turning = options.Where(o => o.Kind != PushbackOptionKind.Straight).ToList();
        var custom = turning.Where(o => !IsDefaultLabel(o.Label)).Select(o => o.Label).ToList();
        if (custom.Count >= 2)
        {
            return $"Pushback — which way? GSX offers {string.Join(", or ", custom.Take(3))}.";
        }

        var hasLeft = turning.Any(o => o.Kind == PushbackOptionKind.Left);
        var hasRight = turning.Any(o => o.Kind == PushbackOptionKind.Right);
        return hasLeft && hasRight
            ? "Pushback — tail left or tail right?"
            : "Pushback — which way?";
    }

    private static bool IsDefaultLabel(string label)
        => label.Contains("Tail Left", StringComparison.OrdinalIgnoreCase)
            || label.Contains("Tail Right", StringComparison.OrdinalIgnoreCase);

    /// <summary>The pilot decided (or re-decided). Withdraws any open question in the same
    /// update — the GSX menu catalogue acts on the one notification.</summary>
    public void Choose(PushbackChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        Commit(s => (s with { Choice = choice }).WithAsking(CaptainsQuestionRules.Withdraw(s.Asking)));
    }

    /// <summary>The web card's pick of one menu line (by label): the matching option becomes
    /// this flight's choice — the pushback's interpretation adapter for the shared pick-one
    /// surface. False when no option carries that label.</summary>
    public override bool Choose(string labelOrToken, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(labelOrToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var option = Snapshot().Options.FirstOrDefault(o => string.Equals(o.Label, labelOrToken, StringComparison.OrdinalIgnoreCase));
        if (option is null)
        {
            return false;
        }

        Choose(FromOption(option, source));
        return true;
    }

    /// <summary>The wish that selects a menu line (the advisor's own mapping).</summary>
    public static PushbackChoice FromOption(PushbackOption option, string source)
    {
        ArgumentNullException.ThrowIfNull(option);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var reason = $"picked '{option.Label}' ({source})";
        return option.Kind switch
        {
            PushbackOptionKind.Left => PushbackChoice.TailLeft(source, reason),
            PushbackOptionKind.Right => PushbackChoice.TailRight(source, reason),
            PushbackOptionKind.Straight => PushbackChoice.Straight(source, reason),
            _ => PushbackChoice.NamedSlot(option.Label, source, reason),
        };
    }

    /// <summary>Back to "undecided" for this flight (the suggestion stays).</summary>
    public void ClearChoice() => Update(s => s with { Choice = null });

    /// <summary>The advisor's view for the current stand/runway.</summary>
    public void SetSuggestion(PushbackSuggestion? suggestion, IReadOnlyList<PushbackOption> options, string? stand, string? runway)
        => Update(s => s with { Suggestion = suggestion, Options = options, Stand = stand, Runway = runway });

    /// <summary>The automation asks the pilot. Idempotent while a question is open; the menu
    /// lines become the question's choices and the options as last understood.</summary>
    public void AskPilot(IReadOnlyList<PushbackOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Commit(s =>
        {
            if (s.Asking.QuestionStands)
            {
                return s;
            }

            var choices = options.Select(o => new QuestionChoice(o.Label, o.Label, null)).ToList();
            return (s with { Options = options }).WithAsking(
                CaptainsQuestionRules.Ask(s.Asking, Definition, Prompt(options), choices, null, null, DateTimeOffset.UtcNow));
        });
    }

    private void OnOfpChanged(object? sender, EventArgs e)
    {
        var requestId = _ofpStore.Current?.RequestId;
        if (string.Equals(requestId, _lastOfpRequestId, StringComparison.Ordinal))
        {
            return;
        }

        _lastOfpRequestId = requestId;
        Reset();
    }
}
