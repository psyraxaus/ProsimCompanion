using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Web.Components;

/// <summary>The pack text ids one field renders with, resolved from its description (C3).</summary>
/// <param name="Label">Always present: <c>{root}.label</c>, else <c>{root}</c>, else the explicit id.</param>
/// <param name="Hint">The one-sentence hint, or null when the pack has none.</param>
/// <param name="More">The "More" fold, or null when the pack has none.</param>
/// <param name="Placeholder">A translated input placeholder, or null.</param>
/// <param name="Tally">The DirtyBar tally name where one label repeats on a page, or null (= the label).</param>
/// <param name="EmptyListHint">The line under an empty voice list, or null.</param>
/// <param name="BlankLabel">The voice picker's blank choice, or null.</param>
public sealed record OptionFieldTextIds(
    string Label,
    string? Hint,
    string? More,
    string? Placeholder,
    string? Tally,
    string? EmptyListHint,
    string? BlankLabel);

/// <summary>
/// Resolves a field description's text-id root into the ids the field components take
/// (ADR-0014, candidate C3). The convention is in <see cref="OptionFieldAttribute"/>: a derived
/// id (<c>{root}.hint</c>) is used only when the English pack — the source of truth — defines
/// it, so a field with no hint simply has no <c>.hint</c> text, and an explicit id on the
/// attribute wins over the convention. Pure over a "does the pack have this id" predicate, so
/// the shipped pack and a test can both drive it.
/// </summary>
public static class OptionFieldTexts
{
    /// <summary>Resolves the ids for <paramref name="description"/> against the English pack.</summary>
    /// <param name="description">The field.</param>
    /// <param name="hasText">True when the English pack defines the id.</param>
    public static OptionFieldTextIds Resolve(OptionFieldDescription description, Func<string, bool> hasText)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(hasText);
        var spec = description.Spec;
        var root = spec.TextId;

        var label = spec.Label ?? (hasText(root + ".label") ? root + ".label" : root);
        return new OptionFieldTextIds(
            label,
            spec.Hint ?? Derived(".hint"),
            spec.More ?? Derived(".more"),
            Derived(".placeholder"),
            Derived(".tally"),
            spec.EmptyListHint ?? Derived(".empty"),
            spec.BlankLabel);

        string? Derived(string suffix)
            => root.Length > 0 && hasText(root + suffix) ? root + suffix : null;
    }
}
