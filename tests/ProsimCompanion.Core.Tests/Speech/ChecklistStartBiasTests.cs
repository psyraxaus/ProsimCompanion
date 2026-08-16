using ProsimCompanion.Speech.Checklists;
using ProsimCompanion.Speech.Recognition;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>
/// Issue #38: "approach checklist" phonetically snapped to "activate approach phase" and the
/// checklist never ran. Saying "checklist" outside an item window must scope snapping to the
/// checklist phrases.
/// </summary>
public sealed class ChecklistStartBiasTests
{
    private static readonly List<string> IdleGrammar =
    [
        "restart checklist",
        "cancel checklist",
        "activate approach phase",
        "arm localizer",
        "brief the arrival",
        "Approach checklist",
        "After Takeoff checklist",
        "climb checklist",
    ];

    [Fact]
    public void UtteranceWithChecklist_ScopesToChecklistPhrases()
    {
        var scoped = UtteranceRouter.ScopeForChecklistStart(
            "approach checklist", IdleGrammar, itemAwaiting: false);

        Assert.All(scoped, p => Assert.Contains("checklist", p, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Approach checklist", scoped);
        Assert.DoesNotContain("activate approach phase", scoped);
        Assert.DoesNotContain("arm localizer", scoped);
    }

    [Fact]
    public void UtteranceWithoutChecklist_KeepsFullGrammar()
    {
        var scoped = UtteranceRouter.ScopeForChecklistStart(
            "activate approach phase", IdleGrammar, itemAwaiting: false);

        Assert.Same(IdleGrammar, scoped);
    }

    [Fact]
    public void ItemAwaiting_NeverScopes()
    {
        // A readback like "checklist complete" while a line is pending must stay routable
        // against the item's accepted phrases.
        var scoped = UtteranceRouter.ScopeForChecklistStart(
            "checklist", IdleGrammar, itemAwaiting: true);

        Assert.Same(IdleGrammar, scoped);
    }

    [Fact]
    public void NoChecklistPhrasesInGrammar_KeepsFullGrammar()
    {
        List<string> grammar = ["set heading three two zero"];

        Assert.Same(grammar, UtteranceRouter.ScopeForChecklistStart("checklist", grammar, false));
    }
}
