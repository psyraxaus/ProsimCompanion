namespace ProsimCompanion.Web.Components;

/// <summary>
/// The settings pages' tally of fields whose value can never work. <see cref="SettingsLayout"/>
/// cascades one instance to the page and every <see cref="TextField"/> under it; a field
/// reports its blocking state on every render under a stable name (its label, or an explicit
/// key where one label repeats on a page), and the page base turns <see cref="BlockingCount"/>
/// into a disabled Save button with a "fix the highlighted field" message (owner decision
/// 2026-10-13, option 2). Advisory findings (a path that is not there yet) are shown under the
/// field but never counted here.
///
/// A field that leaves the screen keeps its entry (review 2026-10-13): the pages render one
/// section or fold at a time over drafts that outlive the components, so a red value in a
/// hidden section must still hold Save — the bar names the field instead. The page clears
/// the tally when it arrives and on Discard, so nothing stale survives a navigation or a
/// reload of the drafts.
/// </summary>
public sealed class FieldValidity
{
    private readonly SortedSet<string> _blocking = new(StringComparer.Ordinal);

    /// <summary>How many fields currently block a save.</summary>
    public int BlockingCount => _blocking.Count;

    /// <summary>The names of the fields that block a save, in name order — for the bar line,
    /// so a red field in a section the pilot is not looking at is still named.</summary>
    public IReadOnlyCollection<string> BlockingFields => _blocking;

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

    /// <summary>Forgets one field.</summary>
    public void Clear(string id) => Report(id, blocking: false);

    /// <summary>Forgets every field: a page arrived or its drafts were reloaded, so every
    /// value is about to be re-checked by the fields that render.</summary>
    public void Reset()
    {
        if (_blocking.Count == 0)
        {
            return;
        }

        _blocking.Clear();
        Changed?.Invoke();
    }
}
