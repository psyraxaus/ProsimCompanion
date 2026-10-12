namespace ProsimCompanion.Web.Components;

/// <summary>
/// The settings pages' tally of fields whose value can never work. <see cref="SettingsLayout"/>
/// cascades one instance to the page and every <see cref="TextField"/> under it; a field
/// reports its blocking state on every render and clears it on dispose, and the page base
/// turns <see cref="BlockingCount"/> into a disabled Save button with a "fix the highlighted
/// field" message (owner decision 2026-10-13, option 2). Advisory findings (a path that is
/// not there yet) are shown under the field but never counted here.
/// </summary>
public sealed class FieldValidity
{
    private readonly HashSet<string> _blocking = new(StringComparer.Ordinal);

    /// <summary>How many fields currently block a save.</summary>
    public int BlockingCount => _blocking.Count;

    /// <summary>Raised inside the circuit whenever <see cref="BlockingCount"/> changes.</summary>
    public event Action? Changed;

    /// <summary>Records whether the field <paramref name="id"/> blocks a save. Fires
    /// <see cref="Changed"/> only on a real change, so a field re-rendering with the same
    /// state never loops the page.</summary>
    public void Report(string id, bool blocking)
    {
        ArgumentNullException.ThrowIfNull(id);
        var changed = blocking ? _blocking.Add(id) : _blocking.Remove(id);
        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Forgets a field (its component is going away).</summary>
    public void Clear(string id) => Report(id, blocking: false);
}
