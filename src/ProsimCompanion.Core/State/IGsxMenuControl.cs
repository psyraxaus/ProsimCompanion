namespace ProsimCompanion.Core.State;

/// <summary>Classification of one manual GSX menu action from a UI surface.</summary>
public enum GsxMenuActionStatus
{
    /// <summary>The action went out and GSX showed the expected effect (or the requested
    /// state already held — nothing to do).</summary>
    Done,

    /// <summary>Not possible right now: no menu open, the menu changed under the pilot, the
    /// entry is greyed, or the automation is answering this menu — the detail says which.
    /// The pilot re-reads the card and picks again; nothing was sent.</summary>
    NotAvailable,

    /// <summary>GSX is not connected / the Remote API is not ready (degraded mode).</summary>
    Unavailable,

    /// <summary>GSX refused the command or never showed the effect.</summary>
    Failed,
}

/// <summary>Status plus a human-readable detail — never a bare ack, so the card can say why
/// a click did nothing visible.</summary>
public sealed record GsxMenuActionOutcome(GsxMenuActionStatus Status, string Detail);

/// <summary>
/// Manual GSX menu driving from UI surfaces (issue #135, Nico 2026-09-27): the web card that
/// mirrors whatever menu GSX has open — pushback directions an addon airport names per
/// stand, "Customize", operator lists — and lets the pilot pick a line without opening the
/// GSX window. Implemented by the GSX layer on top of the same intent executor the
/// automation uses: a pick is re-matched against the live menu at send time (title, index,
/// entry text, greyed state), never a blind ordinal.
/// </summary>
public interface IGsxMenuControl
{
    /// <summary>True while the automation's own intent is answering the currently shown
    /// menu (a question handler, gate selection, reposition). The card shows the menu but
    /// refuses picks meanwhile — two writers on one menu is how a wrong line gets clicked.</summary>
    bool AutomationDriving { get; }

    /// <summary>Picks entry <paramref name="index"/> of the shown menu, provided that line
    /// still reads <paramref name="expectedEntry"/> when the pick is sent (the menu the pilot
    /// saw is the menu that gets clicked). Never throws for operational failures.</summary>
    Task<GsxMenuActionOutcome> PickAsync(int index, string expectedEntry, CancellationToken cancellationToken = default);

    /// <summary>Opens the GSX menu when none is shown (never toggles a shown one closed).</summary>
    Task<GsxMenuActionOutcome> OpenAsync(CancellationToken cancellationToken = default);

    /// <summary>Dismisses the shown menu — the pilot's "leave it" for a question the
    /// automation left for them.</summary>
    Task<GsxMenuActionOutcome> CloseAsync(CancellationToken cancellationToken = default);
}
