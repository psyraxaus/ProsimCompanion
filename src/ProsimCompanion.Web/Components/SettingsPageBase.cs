using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Web.Components;

/// <summary>
/// Base for settings pages (campaign #84): a page declares one draft per option section it
/// edits and binds its inputs straight to the clones. Dirty tracking, the atomic diff save,
/// the DirtyBar messaging and the saved-message timer live here once — the per-page
/// LoadDraft/Snapshot/Save triples and hand-written JSON keys are gone. Extends the store
/// observer base (#86) so a settings page that also renders live state declares watches the
/// same way every other page does.
/// </summary>
public abstract class SettingsPageBase : StoreObserverComponent
{
    private readonly List<OptionsDraft> _drafts = [];
    private Timer? _savedMessageTimer;
    private bool _validitySubscribed;

    [Inject]
    protected SettingsWriter Writer { get; set; } = default!;

    /// <summary>The layout's tally of fields whose value can never work (option 2,
    /// 2026-10-13). Null outside <see cref="SettingsLayout"/>, where nothing blocks.</summary>
    [CascadingParameter]
    protected FieldValidity? Validity { get; set; }

    protected string? BarMessage { get; private set; }

    protected DirtyBar.MessageTone BarTone { get; private set; } = DirtyBar.MessageTone.Unsaved;

    protected bool IsDirty => _drafts.Any(d => d.IsDirty);

    protected bool BarVisible => IsDirty || BarMessage is not null;

    /// <summary>How many fields currently block a save.</summary>
    protected int BlockingFieldCount => Validity?.BlockingCount ?? 0;

    /// <summary>False while any field on the page is red: the DirtyBar's Save goes grey.</summary>
    protected bool CanSave => BlockingFieldCount == 0;

    /// <summary>The DirtyBar line: a status message when one is showing, otherwise the
    /// "fix the highlighted field" prompt or the plain unsaved-changes line. The prompt names
    /// the fields, because a red one may sit in a section or fold that is not on screen.</summary>
    protected string BarText => BarMessage ?? (CanSave
        ? "You have unsaved changes"
        : BlockingFieldCount == 1
            ? $"Fix \"{Validity!.BlockingFields.First()}\" before saving"
            : $"Fix these fields before saving: {string.Join(", ", Validity!.BlockingFields)}");

    /// <summary>Subscribes to the cascaded validity tally once it arrives, so a field turning
    /// red (or back) re-renders the DirtyBar. Done here rather than in OnInitialized because
    /// pages override that without calling base. The tally is reset on arrival: it lives in
    /// the layout, which outlives the page, and the fields of this page are about to report
    /// afresh.</summary>
    public override async Task SetParametersAsync(ParameterView parameters)
    {
        await base.SetParametersAsync(parameters).ConfigureAwait(true);
        if (!_validitySubscribed && Validity is not null)
        {
            Validity.Reset();
            Validity.Changed += OnValidityChanged;
            _validitySubscribed = true;
        }
    }

    private void OnValidityChanged() => _ = InvokeAsync(StateHasChanged);

    /// <summary>Creates and registers this page's editable clone of one option section.
    /// Call once per section from <c>OnInitialized</c>.</summary>
    protected OptionsDraft<TOptions> Draft<TOptions>(IOptionsMonitor<TOptions> monitor)
        where TOptions : class, IOptionSection, new()
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var draft = new OptionsDraft<TOptions>(() => monitor.CurrentValue);
        _drafts.Add(draft);
        return draft;
    }

    /// <summary>Persists every dirty draft in one atomic settings-file write.</summary>
    protected void Save()
    {
        // The button is grey while a field is red; this guard covers a stale click.
        if (!CanSave)
        {
            return;
        }

        try
        {
            OnBeforeSave();
            Writer.Save(_drafts);
            OnAfterSave();
            BarMessage = "Saved.";
            BarTone = DirtyBar.MessageTone.Info;
            _savedMessageTimer?.Dispose();
            _savedMessageTimer = new Timer(_ => _ = InvokeAsync(() =>
            {
                BarMessage = null;
                BarTone = DirtyBar.MessageTone.Unsaved;
                StateHasChanged();
            }), null, TimeSpan.FromSeconds(1.5), Timeout.InfiniteTimeSpan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            BarMessage = $"Save failed: {ex.Message}";
            BarTone = DirtyBar.MessageTone.Error;
        }
    }

    /// <summary>Discards edits on every draft and clears the bar.</summary>
    protected void Discard()
    {
        foreach (var draft in _drafts)
        {
            draft.Reload();
        }

        BarMessage = null;
        BarTone = DirtyBar.MessageTone.Unsaved;
        // The reloaded values are the saved ones, which never block; fields off screen would
        // otherwise keep their red entry.
        Validity?.Reset();
        OnDiscarded();
    }

    /// <summary>Input normalization (trims, empty-to-null) applied to the drafts before the
    /// diff decides what changed.</summary>
    protected virtual void OnBeforeSave()
    {
    }

    /// <summary>Page-specific follow-up after a successful save (e.g. mirroring the gsx block
    /// into the active aircraft profile).</summary>
    protected virtual void OnAfterSave()
    {
    }

    /// <summary>Re-derive page-local view state after a discard reload.</summary>
    protected virtual void OnDiscarded()
    {
    }

    public override void Dispose()
    {
        if (_validitySubscribed && Validity is not null)
        {
            Validity.Changed -= OnValidityChanged;
            _validitySubscribed = false;
        }

        _savedMessageTimer?.Dispose();
        base.Dispose();
    }
}
