using ProsimCompanion.Core.Checklists;

namespace ProsimCompanion.Speech.Checklists;

/// <summary>
/// The spoken checklist's "already set" shortcut (owner request 2026-10-18): a verify line
/// whose dataref condition is already true when the FO reaches it is read through — the FO
/// says the line and its answer in one breath ("Flaps, retracted.") and moves on without
/// opening a listening window. Pure so the decision is testable without the engine.
/// </summary>
public static class SatisfiedLineReadThrough
{
    /// <summary>
    /// True when <paramref name="item"/> may be read through: a verify line with a plain
    /// spoken answer (a number or flap-config read-back always needs the pilot — the readback
    /// IS the check) whose every referenced dataref has a live value and whose condition is
    /// true on those values. <paramref name="read"/> returns null for a dataref that has no
    /// value yet or went stale on a ProSim drop; one null fails the whole line closed, so a
    /// not-yet-arrived subscription (catalog default 0) can never pass an "equals 0" line.
    /// </summary>
    public static bool IsSatisfied(ChecklistItemDefinition item, Func<string, double?> read)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(read);

        if (item.Verify is null
            || !item.Behavior.Equals("verify", StringComparison.OrdinalIgnoreCase)
            || !item.Expects.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var dataref in item.Verify.ReferencedDatarefs())
        {
            if (values.ContainsKey(dataref))
            {
                continue;
            }

            var value = read(dataref);
            if (value is null)
            {
                return false;
            }

            values[dataref] = value.Value;
        }

        return values.Count > 0
            && ConditionEvaluator.Evaluate(item.Verify, name => values.TryGetValue(name, out var v) ? v : 0.0);
    }

    /// <summary>
    /// What the FO says for a read-through line: the file's <c>confirmCallout</c> when it has
    /// one (the shipped lines already read "Flaps, retracted"), else the challenge plus the
    /// display response in lower case ("Strobe, auto."), else "…, checked.". Tokens in the
    /// callout are the caller's to expand.
    /// </summary>
    public static string Text(ChecklistItemDefinition item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!string.IsNullOrWhiteSpace(item.ConfirmCallout))
        {
            return item.ConfirmCallout;
        }

        var response = string.IsNullOrWhiteSpace(item.ExpectedResponse)
            ? "checked"
            : item.ExpectedResponse.Trim().ToLowerInvariant();
        return $"{item.Say}, {response}";
    }
}
