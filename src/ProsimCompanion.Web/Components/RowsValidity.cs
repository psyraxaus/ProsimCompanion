using Microsoft.AspNetCore.Components;

namespace ProsimCompanion.Web.Components;

/// <summary>
/// Reports a hand-written row editor's verdict into the layout's <see cref="FieldValidity"/>
/// the way <see cref="TextField"/> reports its own (option 2, 2026-10-13). The table pages
/// (displays, cockpit switches, input-relay views, service order) paint a bad cell red with
/// the <c>invalid</c> class but, being raw inputs rather than typed fields, never told the
/// tally — so Save stayed enabled behind a blank name or a zero size (#234). One instance
/// sits beside each table, outside any "no rows yet" branch so it renders on every pass:
/// the page re-renders on every edit (<c>@bind</c>), the parameters arrive again, and the
/// entry under <see cref="Name"/> (the card's title, what the bar names) follows
/// <see cref="Blocking"/>. Renders nothing. Like a field, the entry is not cleared when the
/// table leaves the screen; the page base resets the tally on arrival and on Discard.
/// </summary>
public sealed class RowsValidity : ComponentBase
{
    [CascadingParameter]
    private FieldValidity? Validity { get; set; }

    /// <summary>The name the table blocks a save under — the card title, so the bar's
    /// "fix the highlighted field" line points at the right card.</summary>
    [Parameter, EditorRequired]
    public string Name { get; set; } = "";

    /// <summary>True while any row holds a value that can never work.</summary>
    [Parameter]
    public bool Blocking { get; set; }

    protected override void OnParametersSet() => Validity?.Report(Name, Blocking);
}
