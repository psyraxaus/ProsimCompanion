using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Tests.Localization;
using ProsimCompanion.Web.Components;
using Xunit;

namespace ProsimCompanion.Core.Tests.Web;

/// <summary>
/// The open-question presentation module (architecture review C2): one card for ANY captain's
/// question — the yes/no words per topic, one button per choice with the gate's tooltips, the
/// state line per expiry rule, and nothing once the TTL passed. The gate suffix-variant
/// assertions from <c>GsxPresentationTests</c> live here now.
/// </summary>
public sealed class CaptainsQuestionPresentationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 10, 45, 0, TimeSpan.Zero);

    private static readonly IStringLocalizer T = new ServiceCollection().AddTestLocalizer()
        .BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IStringLocalizer>();

    private static CaptainsQuestionState Standing(CaptainsQuestion question) => CaptainsQuestionState.Empty with { Question = question };

    [Fact]
    public void NoQuestion_IsNoCard()
    {
        Assert.Null(CaptainsQuestionPresentation.View(DeiceRequestStore.QuestionDefinition, CaptainsQuestionState.Empty, T0));

        var answered = CaptainsQuestionRules.Accept(CaptainsQuestionState.Empty, "web", T0);
        Assert.Null(CaptainsQuestionPresentation.View(DeiceRequestStore.QuestionDefinition, answered, T0));
    }

    [Fact]
    public void GateVariants_OneButtonPerCandidate_WithTheDetailOnTheCaption()
    {
        var question = new CaptainsQuestion("gateChoice", T0, "Which 40?", null, 1, [new("40", "40", "Gate W 40"), new("40A", "40A", null)], "40");

        var view = CaptainsQuestionPresentation.View(GateChoiceStore.QuestionDefinition, Standing(question), T0)!;

        Assert.Equal("gateChoice", view.Topic);
        Assert.Equal("Which 40?", view.Prompt);
        Assert.Equal(2, view.Buttons.Count);
        Assert.Equal("40", view.Buttons[0].Id);
        Assert.Equal("40 · Gate W 40", view.Buttons[0].Caption.Literal);
        Assert.Equal("gsxSettings.arrivalGate.variantDetail", view.Buttons[0].Tooltip!.TextId);
        Assert.Equal("Gate W 40", view.Buttons[0].Tooltip!.Arguments[0]);
        Assert.Equal(QuestionButtonAction.Choose, view.Buttons[0].Action);
        Assert.Equal("40", view.Buttons[0].Choice);
        Assert.False(view.Buttons[0].Primary);
        Assert.Equal("40A", view.Buttons[1].Caption.Literal);
        Assert.Equal("gsxSettings.arrivalGate.variantNoDetail", view.Buttons[1].Tooltip!.TextId);

        // No clock: the first variant goes at taxi-in (probe gsx-gate-suffix).
        Assert.Equal("gsxSettings.captainsQuestions.standsUntilAnswered", view.State.TextId);
        Assert.Equal("open · stands until answered", view.State.Resolve(T));
        Assert.NotNull(CaptainsQuestionPresentation.View(GateChoiceStore.QuestionDefinition, Standing(question), T0.AddHours(5)));
    }

    [Fact]
    public void Deice_TheStatusBoardsTwoButtons_AndTheCountsAsNoClock()
    {
        var question = new CaptainsQuestion("deice", T0, "Request de-icing?", TimeSpan.FromMinutes(5), 1, [], null);

        var open = CaptainsQuestionPresentation.View(DeiceRequestStore.QuestionDefinition, Standing(question), T0.AddMinutes(1))!;
        Assert.Equal("Request de-icing?", open.Prompt);
        Assert.Equal(["accept", "decline"], open.Buttons.Select(b => b.Id));
        Assert.Equal("gsxSettings.deiceStatus.request", open.Buttons[0].Caption.TextId);
        Assert.True(open.Buttons[0].Primary);
        Assert.Equal(QuestionButtonAction.Accept, open.Buttons[0].Action);
        Assert.Equal("gsxSettings.deiceStatus.decline", open.Buttons[1].Caption.TextId);
        Assert.Equal(QuestionButtonAction.Decline, open.Buttons[1].Action);
        Assert.Equal("gsxSettings.captainsQuestions.countsAsNoAt", open.State.TextId);
        Assert.Equal("gsxSettings.captainsQuestions.open", ((DisplayText)open.State.Arguments[0]).TextId);
        Assert.Matches(@"^\d\d:\d\d$", (string)open.State.Arguments[1]);
        Assert.StartsWith("open · counts as no at ", open.State.Resolve(T), StringComparison.Ordinal);

        var reAsked = CaptainsQuestionPresentation.View(DeiceRequestStore.QuestionDefinition, Standing(question with { Attempt = 2 }), T0.AddMinutes(1))!;
        Assert.Equal("gsxSettings.captainsQuestions.reAsked", ((DisplayText)reAsked.State.Arguments[0]).TextId);

        Assert.Null(CaptainsQuestionPresentation.View(DeiceRequestStore.QuestionDefinition, Standing(question), T0.AddMinutes(6)));
    }

    [Fact]
    public void Turnaround_UsesItsOwnWords()
    {
        var question = new CaptainsQuestion("turnaroundStart", T0, "Next plan loaded — commence ground services?", TimeSpan.FromMinutes(5), 1, [], null);

        var view = CaptainsQuestionPresentation.View(TurnaroundStartStore.QuestionDefinition, Standing(question), T0)!;

        Assert.Equal("gsxSettings.captainsQuestions.commence", view.Buttons[0].Caption.TextId);
        Assert.Equal("Commence ground services", view.Buttons[0].Caption.Resolve(T));
        Assert.Equal("gsxSettings.captainsQuestions.standBy", view.Buttons[1].Caption.TextId);
        Assert.Equal("gsxSettings.captainsQuestions.countsAsNoAt", view.State.TextId);
    }

    [Fact]
    public void Pushback_OneButtonPerMenuLine_AndTheWithdrawnClock()
    {
        var question = new CaptainsQuestion("pushback", T0, "Pushback — which way? GSX offers Facing SW on Taxi AV, or Facing NE on Taxi AT.", TimeSpan.FromMinutes(5), 1,
            [new("Facing SW on Taxi AV", "Facing SW on Taxi AV", null), new("Facing NE on Taxi AT", "Facing NE on Taxi AT", null)], null);

        var view = CaptainsQuestionPresentation.View(PushbackChoiceStore.QuestionDefinition, Standing(question), T0)!;

        Assert.Equal(["Facing SW on Taxi AV", "Facing NE on Taxi AT"], view.Buttons.Select(b => b.Caption.Literal));
        Assert.All(view.Buttons, b => Assert.Null(b.Tooltip));
        Assert.All(view.Buttons, b => Assert.Equal(QuestionButtonAction.Choose, b.Action));
        Assert.Equal("gsxSettings.captainsQuestions.withdrawnAt", view.State.TextId);
        Assert.StartsWith("open · withdrawn at ", view.State.Resolve(T), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownYesNoTopic_FallsBackToYesAndNo()
    {
        var definition = new CaptainsQuestionDefinition("other", CaptainsQuestionShape.YesNo, null, CaptainsQuestionExpiry.Never, 2, true);
        var question = new CaptainsQuestion("other", T0, "Really?", null, 1, [], null);

        var view = CaptainsQuestionPresentation.View(definition, Standing(question), T0)!;

        Assert.Equal("Yes", view.Buttons[0].Caption.Resolve(T));
        Assert.Equal("No", view.Buttons[1].Caption.Resolve(T));
    }

    [Fact]
    public void Views_ListsEveryOpenQuestion_InStoreOrder_OrOnlyTheGivenTopics()
    {
        var signals = new GroundOpsSignals();
        using var turnaround = new TurnaroundStartStore(signals);
        using var gate = new GateChoiceStore(signals);
        using var deice = new DeiceRequestStore(signals);
        turnaround.Ask("Next plan loaded — commence ground services?");
        gate.Ask("W36", [new("36A", "Gate W 36A", null), new("36B", "Gate W 36B", "Safedock")], "W36 is 36A or 36B in this scenery — which?");
        var stores = new ICaptainsQuestionStore[] { deice, turnaround, gate };

        var all = CaptainsQuestionPresentation.Views(stores, DateTimeOffset.UtcNow);
        Assert.Equal(["turnaroundStart", "gateChoice"], all.Select(v => v.Topic));

        var gateOnly = CaptainsQuestionPresentation.Views(stores, DateTimeOffset.UtcNow, new HashSet<string>(StringComparer.Ordinal) { "gateChoice" });
        Assert.Equal(["gateChoice"], gateOnly.Select(v => v.Topic));
        Assert.Equal(["36A", "36B · Safedock"], gateOnly[0].Buttons.Select(b => b.Caption.Literal));
    }
}
