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

    [Inject]
    protected SettingsWriter Writer { get; set; } = default!;

    protected string? BarMessage { get; private set; }

    protected DirtyBar.MessageTone BarTone { get; private set; } = DirtyBar.MessageTone.Unsaved;

    protected bool IsDirty => _drafts.Any(d => d.IsDirty);

    protected bool BarVisible => IsDirty || BarMessage is not null;

    /// <summary>Creates and registers this page's editable clone of one option section.
    /// Call once per section from <c>OnInitialized</c>.</summary>
    protected OptionsDraft<TOptions> Draft<TOptions>(IOptionsMonitor<TOptions> monitor)
        where TOptions : class, IOptionSection
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var draft = new OptionsDraft<TOptions>(() => monitor.CurrentValue);
        _drafts.Add(draft);
        return draft;
    }

    /// <summary>Persists every dirty draft in one atomic settings-file write.</summary>
    protected void Save()
    {
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
        _savedMessageTimer?.Dispose();
        base.Dispose();
    }
}
