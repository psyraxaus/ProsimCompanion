using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Radios;

/// <summary>
/// "Shall I tune it?" — the FO's offer to put COM1 on the frequency ATC expects (owner flight
/// 2026-11-16, EGLL stand 313: the pilot sat on a wrong COM1 frequency at the gate and the FO
/// said nothing; the old report was a statement with no offer). One question at a time; the
/// subject is the assignment and the COM1 frequency it was asked about. No answer within the
/// minute counts as no: the FO never changes the radio the captain did not hand over.
/// </summary>
public sealed class Com1TuneOfferStore : CaptainsQuestionStore<CaptainsQuestionState>
{
    public static CaptainsQuestionDefinition QuestionDefinition { get; } = new(
        "com1TuneOffer",
        CaptainsQuestionShape.YesNo,
        TimeSpan.FromSeconds(60),
        CaptainsQuestionExpiry.CountsAsNo,
        MaxAsks: 1,
        AnswerIsFinalForCycle: false);

    public Com1TuneOfferStore(GroundOpsSignals signals)
        : base(QuestionDefinition, CaptainsQuestionState.Empty, signals)
    {
    }

    /// <summary>Raises the offer; false while another offer stands.</summary>
    /// <param name="prompt">The question as the web card prints it.</param>
    /// <param name="subject">What it is about (<see cref="Com1TuneOffer.Subject"/>).</param>
    public bool Ask(string prompt, string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        if (Asking.QuestionStands)
        {
            return false;
        }

        return Raise(prompt, subject: subject) is not null;
    }
}

/// <summary>
/// What one tune offer is about, carried in the question's subject so the phrase table and the
/// web card render it without a second store: the frequency ATC expects, its station, and what
/// COM1 was on (0 = not read) and — when a source names it — the station that owns that
/// frequency ("Scottish Control"; empty = not known).
/// </summary>
public sealed record Com1TuneOffer(int AssignedKhz, int Com1Khz, string Station, string Com1Station = "")
{
    /// <summary>"121705|119000|Ground|Scottish Control" (the last part empty when unknown).</summary>
    public string Subject => FormattableString.Invariant($"{AssignedKhz}|{Com1Khz}|{Station}|{Com1Station}");

    /// <summary>Reads a subject back; null when it is not one of ours.</summary>
    public static Com1TuneOffer? Parse(string? subject)
    {
        var parts = subject?.Split('|', 4);
        if (parts is not { Length: 4 }
            || !int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var assigned)
            || !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var com1))
        {
            return null;
        }

        return new Com1TuneOffer(assigned, com1, parts[2], parts[3]);
    }
}
