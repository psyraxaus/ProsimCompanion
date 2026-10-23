namespace ProsimCompanion.Core.State;

/// <summary>The delay-code question's state and the code on record for this departure (issue #185).</summary>
/// <param name="Asking">The shared question part.</param>
/// <param name="Code">The IATA code on record; null until answered or timed out.</param>
/// <param name="Title">Its title.</param>
/// <param name="DelayMinutes">The off-blocks delay the question was about.</param>
/// <param name="Source">Who settled it ("voice", "web", "timeout").</param>
public sealed record DelayCodeSnapshot(CaptainsQuestionState Asking, string? Code, string? Title, int? DelayMinutes, string? Source)
    : ICaptainsQuestionSnapshot<DelayCodeSnapshot>
{
    public static DelayCodeSnapshot Empty { get; } = new(CaptainsQuestionState.Empty, null, null, null, null);

    DelayCodeSnapshot ICaptainsQuestionSnapshot<DelayCodeSnapshot>.WithAsking(CaptainsQuestionState asking) => this with { Asking = asking };
}

/// <summary>
/// "We're twenty-two minutes late off blocks — delay code?" (issue #185): a pick-one captain's
/// question over the shipped IATA list, raised once per departure cycle at the OUT edge when
/// the off-blocks time is past the schedule plus the tolerance. The pick settles it; the TTL
/// withdraws it and the movement service records code 99 "other" with a note. Adapter over
/// <see cref="CaptainsQuestionStore{TSnapshot}"/>; the choices' tokens are the codes.
/// </summary>
public sealed class DelayCodeStore : CaptainsQuestionStore<DelayCodeSnapshot>
{
    public static CaptainsQuestionDefinition QuestionDefinition { get; } = new(
        "delayCode",
        CaptainsQuestionShape.PickOne,
        CaptainsQuestionDefinition.StandardTtl,
        CaptainsQuestionExpiry.Withdraws,
        CaptainsQuestionDefinition.StandardMaxAsks,
        AnswerIsFinalForCycle: true);

    public DelayCodeStore(GroundOpsSignals signals)
        : base(QuestionDefinition, DelayCodeSnapshot.Empty, signals)
    {
    }

    /// <summary>The code on record this cycle, null while none.</summary>
    public string? Code => Snapshot().Code;

    /// <summary>Raises the question with the list as choices (label = "15 Boarding", token = "15").</summary>
    public bool Ask(string prompt, IReadOnlyList<QuestionChoice> choices, int delayMinutes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentNullException.ThrowIfNull(choices);
        return Commit(s => s with
        {
            DelayMinutes = delayMinutes,
            Asking = CaptainsQuestionRules.Ask(s.Asking, Definition, prompt, choices, null, null, DateTimeOffset.UtcNow),
        }).Raised is not null;
    }

    /// <summary>Records the code (from the pick, or 99 on the timeout) — once per cycle.</summary>
    public bool Record(string code, string title, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var recorded = false;
        Update(s =>
        {
            if (s.Code is not null)
            {
                return s;
            }

            recorded = true;
            return s with { Code = code, Title = title, Source = source };
        });
        return recorded;
    }
}
