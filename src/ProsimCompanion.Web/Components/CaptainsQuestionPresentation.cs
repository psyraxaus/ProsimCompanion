using System.Globalization;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Web.Components;

/// <summary>What pressing a question button does on the store.</summary>
public enum QuestionButtonAction
{
    /// <summary>Yes.</summary>
    Accept,

    /// <summary>No.</summary>
    Decline,

    /// <summary>Pick the button's choice.</summary>
    Choose,
}

/// <summary>One button of an open captain's question.</summary>
/// <param name="Id">A stable name within the question ("accept", "decline", or the choice label).</param>
/// <param name="Caption">The button text: a pack id for yes/no words, the label (" · detail") for a choice.</param>
/// <param name="Tooltip">The hover title, or null for none.</param>
/// <param name="Primary">The yes button is primary; everything else secondary.</param>
/// <param name="Action">What the press does.</param>
/// <param name="Choice">The label <c>Choose</c> hands the store; null for yes/no.</param>
public sealed record QuestionButtonView(
    string Id,
    DisplayText Caption,
    DisplayText? Tooltip,
    bool Primary,
    QuestionButtonAction Action,
    string? Choice);

/// <summary>One open captain's question as the web card draws it.</summary>
/// <param name="Topic">The store's topic (what a press is routed by).</param>
/// <param name="Prompt">The raiser's line (the FO's words, or the web form of them).</param>
/// <param name="State">"open · counts as no at 10:45" / "re-asked · withdrawn at 10:45" /
/// "open · stands until answered" — the attempt and what the clock does.</param>
/// <param name="Buttons">The answers, in order: yes then no, or the choices in the raiser's order.</param>
public sealed record OpenQuestionView(string Topic, string Prompt, DisplayText State, IReadOnlyList<QuestionButtonView> Buttons);

/// <summary>
/// The open-question presentation module (architecture review C2): one captain's-question
/// state in, one <see cref="OpenQuestionView"/> out — for ANY question, so the GSX status panel
/// and the OFP page render every open question with the same card and a new question needs
/// no markup. Third adapter of the status-row seam <see cref="FlightStatusPresentation"/>
/// opened. The per-question button words are the only table here: the de-icing pair the
/// Status board always had, the turnaround pair, and the gate's variant tooltips.
/// </summary>
public static class CaptainsQuestionPresentation
{
    /// <summary>The yes/no words (yes/no questions) and the choice tooltips (pick-one) per topic.</summary>
    private sealed record TopicTexts(string? AcceptTextId, string? DeclineTextId, string? ChoiceDetailTextId, string? ChoiceNoDetailTextId);

    private static readonly TopicTexts Generic = new(
        "gsxSettings.captainsQuestions.yes", "gsxSettings.captainsQuestions.no", null, null);

    private static readonly IReadOnlyDictionary<string, TopicTexts> Texts = new Dictionary<string, TopicTexts>(StringComparer.Ordinal)
    {
        [DeiceRequestStore.QuestionDefinition.Topic] = new("gsxSettings.deiceStatus.request", "gsxSettings.deiceStatus.decline", null, null),
        [TurnaroundStartStore.QuestionDefinition.Topic] = new("gsxSettings.captainsQuestions.commence", "gsxSettings.captainsQuestions.standBy", null, null),
        [GateChoiceStore.QuestionDefinition.Topic] = new(null, null, "gsxSettings.arrivalGate.variantDetail", "gsxSettings.arrivalGate.variantNoDetail"),
        // Issue #180: a free-text question on the web has two honest buttons — the captain
        // briefed the extra threats themselves, or there are none.
        [ThreatsQuestionStore.QuestionDefinition.Topic] = new("gsxSettings.captainsQuestions.threatsBriefed", "gsxSettings.captainsQuestions.noFurtherThreats", null, null),
        // Issue #181: issue the revised final, or fly the LMC on the captain's authority.
        [LmcRevisionStore.QuestionDefinition.Topic] = new("gsxSettings.captainsQuestions.issueRevisedFinal", "gsxSettings.captainsQuestions.captainsAuthority", null, null),
    };

    /// <summary>Every open question of the given stores (optionally only the given topics), in
    /// registration order.</summary>
    public static IReadOnlyList<OpenQuestionView> Views(IEnumerable<ICaptainsQuestionStore> stores, DateTimeOffset nowUtc, IReadOnlySet<string>? topics = null)
    {
        ArgumentNullException.ThrowIfNull(stores);
        var views = new List<OpenQuestionView>();
        foreach (var store in stores)
        {
            if (topics is not null && !topics.Contains(store.Definition.Topic))
            {
                continue;
            }

            if (View(store.Definition, store.Asking, nowUtc) is { } view)
            {
                views.Add(view);
            }
        }

        return views;
    }

    /// <summary>The card for one question, or null when none stands or its TTL has passed
    /// at <paramref name="nowUtc"/> (the sweeper settles it within its cadence).</summary>
    public static OpenQuestionView? View(CaptainsQuestionDefinition definition, CaptainsQuestionState state, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(state);
        if (!state.QuestionOpen(nowUtc) || state.Question is not { } question)
        {
            return null;
        }

        var texts = Texts.GetValueOrDefault(definition.Topic) ?? Generic;
        var attempt = DisplayText.Id(question.Attempt > 1 ? "gsxSettings.captainsQuestions.reAsked" : "gsxSettings.captainsQuestions.open");
        var clock = question.ExpiresAtUtc is { } expires
            ? DisplayText.Id(
                definition.Expiry == CaptainsQuestionExpiry.CountsAsNo ? "gsxSettings.captainsQuestions.countsAsNoAt" : "gsxSettings.captainsQuestions.withdrawnAt",
                attempt,
                expires.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture))
            : DisplayText.Id("gsxSettings.captainsQuestions.standsUntilAnswered", attempt);

        IReadOnlyList<QuestionButtonView> buttons = definition.Shape == CaptainsQuestionShape.YesNo
            ?
            [
                new QuestionButtonView("accept", DisplayText.Id(texts.AcceptTextId ?? Generic.AcceptTextId!), null, true, QuestionButtonAction.Accept, null),
                new QuestionButtonView("decline", DisplayText.Id(texts.DeclineTextId ?? Generic.DeclineTextId!), null, false, QuestionButtonAction.Decline, null),
            ]
            : [.. question.Choices.Select(choice => new QuestionButtonView(
                choice.Label,
                DisplayText.Raw(choice.Detail is null ? choice.Label : $"{choice.Label} · {choice.Detail}"),
                Tooltip(texts, choice),
                false,
                QuestionButtonAction.Choose,
                choice.Label))];

        return new OpenQuestionView(definition.Topic, question.Prompt, clock, buttons);
    }

    private static DisplayText? Tooltip(TopicTexts texts, QuestionChoice choice)
    {
        if (choice.Detail is { } detail)
        {
            return texts.ChoiceDetailTextId is { } id ? DisplayText.Id(id, detail) : null;
        }

        return texts.ChoiceNoDetailTextId is { } none ? DisplayText.Id(none) : null;
    }
}
