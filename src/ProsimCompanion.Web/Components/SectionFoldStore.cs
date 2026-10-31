using ProsimCompanion.Core.State;

namespace ProsimCompanion.Web.Components;

/// <summary>
/// Which settings cards are folded on THIS browser (hub-usability pass, issue #199). One per
/// circuit: the cards on the open page register here, the chevron and the page's
/// "collapse all / expand all" write here, and the layout mirrors every change into the
/// browser's localStorage through <see cref="Persist"/> — a view preference, never a server
/// setting. A card's id is the page path plus its title, so "General" on two pages are two
/// cards; a card with no saved state shows its <c>DefaultFolded</c>. The snapshot is a change
/// counter: observers re-read <see cref="IsFolded"/>.
/// </summary>
public sealed class SectionFoldStore : SnapshotStore<int>
{
    private readonly object _lock = new();
    private readonly Dictionary<string, bool> _saved = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _onPage = new(StringComparer.Ordinal);

    public SectionFoldStore()
        : base(0)
    {
    }

    /// <summary>The page the registering cards belong to (set by the layout before the body renders).</summary>
    public string Page { get; set; } = "";

    /// <summary>Raised with the entries to write to the browser, on the caller's thread.</summary>
    public event Action<IReadOnlyDictionary<string, bool>>? Persist;

    /// <summary>The card id for a title on the current page.</summary>
    public string IdFor(string title) => Page + "|" + title;

    /// <summary>True when the card is folded: its saved state, else its default.</summary>
    public bool IsFolded(string id, bool defaultFolded)
    {
        lock (_lock)
        {
            return _saved.TryGetValue(id, out var folded) ? folded : defaultFolded;
        }
    }

    /// <summary>A card on the open page, with the state it shows without a saved one.</summary>
    public void Register(string id, bool defaultFolded)
    {
        lock (_lock)
        {
            _onPage[id] = defaultFolded;
        }
    }

    public void Unregister(string id)
    {
        lock (_lock)
        {
            _onPage.Remove(id);
        }
    }

    /// <summary>True when at least one card on the page is folded / unfolded (for the two buttons).</summary>
    public bool AnyFolded => Any(folded: true);

    public bool AnyUnfolded => Any(folded: false);

    /// <summary>The browser's saved states, read once per circuit by the layout.</summary>
    public void Load(IReadOnlyDictionary<string, bool>? saved)
    {
        lock (_lock)
        {
            _saved.Clear();
            if (saved is not null)
            {
                foreach (var (id, folded) in saved)
                {
                    _saved[id] = folded;
                }
            }
        }

        Update(v => v + 1);
    }

    /// <summary>One chevron click.</summary>
    public void Set(string id, bool folded)
    {
        lock (_lock)
        {
            _saved[id] = folded;
        }

        Update(v => v + 1);
        Persist?.Invoke(new Dictionary<string, bool>(StringComparer.Ordinal) { [id] = folded });
    }

    /// <summary>Every card on the open page.</summary>
    public void SetAll(bool folded)
    {
        Dictionary<string, bool> changed;
        lock (_lock)
        {
            changed = _onPage.Keys.ToDictionary(id => id, _ => folded, StringComparer.Ordinal);
            foreach (var id in changed.Keys)
            {
                _saved[id] = folded;
            }
        }

        Update(v => v + 1);
        if (changed.Count > 0)
        {
            Persist?.Invoke(changed);
        }
    }

    private bool Any(bool folded)
    {
        lock (_lock)
        {
            return _onPage.Any(card => (_saved.TryGetValue(card.Key, out var state) ? state : card.Value) == folded);
        }
    }
}
