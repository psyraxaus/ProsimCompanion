namespace ProsimCompanion.Web.Components.Diagnostics;

/// <summary>One cell of the memo strip.</summary>
/// <param name="Label">The 11 px white caps label ("VER", "PHASE").</param>
/// <param name="Value">The 17 px value.</param>
/// <param name="Qualifier">A small qualifier after the value ("RC12", "H 14", "1 LOST").</param>
/// <param name="ValueClass">Extra class on the value (an amber caution count: "c-amber").</param>
/// <param name="QualifierClass">Extra class on the qualifier (cyan by default; "c-amber" for a lost count).</param>
/// <param name="Href">When set the value is a link (the bundle export).</param>
public sealed record EcamMemoCell(
    string Label,
    string Value,
    string? Qualifier = null,
    string? ValueClass = null,
    string? QualifierClass = null,
    string? Href = null);
