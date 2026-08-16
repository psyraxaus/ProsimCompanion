using ProsimCompanion.Core.Checklists;

namespace ProsimCompanion.Core.AircraftState;

/// <summary>
/// A named expected-aircraft-state definition (issue #63): the cold-and-dark switch set the
/// session-start check verifies before phases and automation trust the aircraft. Deliberately
/// reuses the checklist <see cref="VerifyCondition"/> tree (same camelCase/tolerant serializer
/// contract via <see cref="ChecklistDefinition.JsonOptions"/>) so a user who already edits
/// checklist verify blocks can tune this file per airline SOP without learning a new shape.
/// </summary>
public sealed class AircraftStateDefinition
{
    /// <summary>Display name ("Cold and Dark"); a file without one is still usable.</summary>
    public string Name { get; set; } = "";

    public List<AircraftStateGroup> Groups { get; set; } = [];

    /// <summary>All expectations across groups, in file order.</summary>
    public IEnumerable<AircraftStateExpectation> AllItems()
        => Groups.SelectMany(group => group.Items);
}

/// <summary>One panel-oriented grouping ("Electrical", "Exterior lights") — display structure
/// only; evaluation flattens the groups.</summary>
public sealed class AircraftStateGroup
{
    public string Name { get; set; } = "";

    public List<AircraftStateExpectation> Items { get; set; } = [];
}

/// <summary>One expected switch position. An item without a <see cref="Verify"/> condition is
/// display furniture and never evaluated (degrades instead of failing the file).</summary>
public sealed class AircraftStateExpectation
{
    /// <summary>Row label, e.g. "Battery 1 OFF" — shown on the web Flight Status page.</summary>
    public string Label { get; set; } = "";

    /// <summary>Free-form documentation of the dataref semantics (value maps, quirks) — kept
    /// in the file so hand-editors see WHY the expected value is what it is.</summary>
    public string? Note { get; set; }

    /// <summary>What the FO says when this item fails ("battery 1 is on"). Absent falls back
    /// to a generic phrase built from <see cref="Label"/>.</summary>
    public string? MismatchPhrase { get; set; }

    /// <summary>The condition that must hold for the aircraft to match this state — the
    /// checklist condition tree, verbatim schema (leaf or compound).</summary>
    public VerifyCondition? Verify { get; set; }
}
