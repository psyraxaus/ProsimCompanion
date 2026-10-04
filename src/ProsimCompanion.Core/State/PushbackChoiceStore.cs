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
/// <param name="Question">Set while the automation has asked the pilot to choose (GSX's
/// direction menu is open and no choice is known); the FO voices it.</param>
public sealed record PushbackChoiceSnapshot(
    PushbackChoice? Choice,
    PushbackSuggestion? Suggestion,
    IReadOnlyList<PushbackOption> Options,
    string? Stand,
    string? Runway,
    PushbackQuestion? Question)
{
    public static PushbackChoiceSnapshot Empty { get; } = new(null, null, [], null, null, null);

    /// <summary>The direction that will be applied when GSX asks: the pilot's choice, else the
    /// suggestion (when confident), else nothing.</summary>
    public PushbackChoice? Effective => Choice ?? (Suggestion is { Confidence: PushbackConfidence.High } s ? s.AsChoice("suggestion") : null);
}

/// <summary>An open "which way?" question, with the options the pilot can pick between.</summary>
public sealed record PushbackQuestion(DateTimeOffset AskedAtUtc, IReadOnlyList<PushbackOption> Options);

/// <summary>
/// Per-flight pushback direction (2026-10-04). The old model was one GLOBAL setting the pilot
/// had to remember to flip before every push; this store holds the decision for THIS departure
/// and resets with the flight cycle and whenever a new OFP arrives (a new plan may mean a new
/// runway). Written by the OFP page, the voice feature and the pushback advisor; read by the
/// GSX question catalogue when GSX raises "Select pushback direction".
/// </summary>
public sealed class PushbackChoiceStore : SnapshotStore<PushbackChoiceSnapshot>, IDisposable
{
    private readonly OfpStore _ofpStore;
    private readonly GroundOpsSignals _signals;
    private string? _lastOfpRequestId;

    public PushbackChoiceStore(OfpStore ofpStore, GroundOpsSignals signals)
        : base(PushbackChoiceSnapshot.Empty)
    {
        ArgumentNullException.ThrowIfNull(ofpStore);
        ArgumentNullException.ThrowIfNull(signals);
        _ofpStore = ofpStore;
        _signals = signals;
        _lastOfpRequestId = ofpStore.Current?.RequestId;
        _ofpStore.Changed += OnOfpChanged;
        _signals.FlightCycleReset += Reset;
    }

    /// <summary>Raised when the automation needs the pilot to choose (GSX's menu is open, no
    /// choice known, mode "ask"). The Speech pillar asks "Tail left or tail right?".</summary>
    public event EventHandler<PushbackQuestion>? QuestionRaised;

    public void Dispose()
    {
        _ofpStore.Changed -= OnOfpChanged;
        _signals.FlightCycleReset -= Reset;
    }

    /// <summary>The pilot decided (or re-decided). Clears any open question.</summary>
    public void Choose(PushbackChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        Update(s => s with { Choice = choice, Question = null });
    }

    /// <summary>Back to "undecided" for this flight (the suggestion stays).</summary>
    public void ClearChoice() => Update(s => s with { Choice = null });

    /// <summary>The advisor's view for the current stand/runway.</summary>
    public void SetSuggestion(PushbackSuggestion? suggestion, IReadOnlyList<PushbackOption> options, string? stand, string? runway)
        => Update(s => s with { Suggestion = suggestion, Options = options, Stand = stand, Runway = runway });

    /// <summary>The automation asks the pilot. Idempotent while a question is open.</summary>
    public void AskPilot(IReadOnlyList<PushbackOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        PushbackQuestion? raised = null;
        Update(s =>
        {
            if (s.Question is not null)
            {
                return s;
            }

            raised = new PushbackQuestion(DateTimeOffset.UtcNow, options);
            return s with { Question = raised, Options = options };
        });

        if (raised is not null)
        {
            QuestionRaised?.Invoke(this, raised);
        }
    }

    /// <summary>Withdraws an open question (menu gone, choice made elsewhere).</summary>
    public void ClearQuestion() => Update(s => s.Question is null ? s : s with { Question = null });

    public void Reset() => Update(_ => PushbackChoiceSnapshot.Empty);

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
