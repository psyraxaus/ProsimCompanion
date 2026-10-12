using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ProsimCompanion.Core.Hosting;

namespace ProsimCompanion.App;

/// <summary>
/// The WPF side of <see cref="INativeFilePicker"/>: the stock Windows Open / Save / Folder
/// dialogs on the desktop thread. One dialog at a time (a second request gets a problem line
/// back, not a second window). The dialog is owned by a throw-away invisible top-most window
/// so it surfaces over the browser the pilot clicked in — an ownerless dialog can open behind
/// it, and the status window may be hidden in the tray.
/// </summary>
internal sealed class WpfFilePicker : INativeFilePicker
{
    private readonly SemaphoreSlim _oneDialog = new(1, 1);
    private readonly ILogger<WpfFilePicker> _logger;

    public WpfFilePicker(ILogger<WpfFilePicker> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public bool IsAvailable => Application.Current is not null;

    public async Task<NativePickResult> PickAsync(NativePickRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var app = Application.Current;
        if (app is null)
        {
            return NativePickResult.Failed("The desktop app is not running.");
        }

        if (!await _oneDialog.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return NativePickResult.Failed("A dialog is already open on the sim PC.");
        }

        try
        {
            return await app.Dispatcher.InvokeAsync(() => Show(request)).Task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Anything the dialog or a shutting-down Dispatcher throws (COM, Win32, a
            // cancelled dispatcher task) becomes a line under the field, never a faulted
            // circuit (degrade, not fail; review 2026-10-13).
            _logger.LogWarning(ex, "Native file dialog failed for {Title}", request.Title);
            return NativePickResult.Failed("The dialog could not be shown on the sim PC.");
        }
        finally
        {
            _oneDialog.Release();
        }
    }

    private static NativePickResult Show(NativePickRequest request)
    {
        var startIn = PickStart.DirectoryFor(request.InitialPath, request.StartIn ?? [], ExpectedFolders.SystemDrive);

        var owner = new Window
        {
            Width = 1,
            Height = 1,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            AllowsTransparency = true,
            Opacity = 0,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Top = -4000,
        };
        owner.Show();
        owner.Activate();
        try
        {
            switch (request.Kind)
            {
                case NativePickKind.Folder:
                {
                    var dialog = new OpenFolderDialog
                    {
                        Title = request.Title,
                        InitialDirectory = startIn,
                        Multiselect = false,
                    };
                    return dialog.ShowDialog(owner) == true
                        ? NativePickResult.Picked(dialog.FolderName)
                        : NativePickResult.Cancelled;
                }

                case NativePickKind.SaveFile:
                {
                    var dialog = new SaveFileDialog
                    {
                        Title = request.Title,
                        InitialDirectory = startIn,
                        Filter = FilterOrAll(request.Filter),
                        FileName = request.DefaultFileName ?? "",
                        OverwritePrompt = false,
                        AddExtension = true,
                    };
                    return dialog.ShowDialog(owner) == true
                        ? NativePickResult.Picked(dialog.FileName)
                        : NativePickResult.Cancelled;
                }

                default:
                {
                    var dialog = new OpenFileDialog
                    {
                        Title = request.Title,
                        InitialDirectory = startIn,
                        Filter = FilterOrAll(request.Filter),
                        CheckFileExists = true,
                        Multiselect = false,
                    };
                    return dialog.ShowDialog(owner) == true
                        ? NativePickResult.Picked(dialog.FileName)
                        : NativePickResult.Cancelled;
                }
            }
        }
        finally
        {
            owner.Close();
        }
    }

    private static string FilterOrAll(string? filter)
        => string.IsNullOrWhiteSpace(filter) ? "All files (*.*)|*.*" : filter;
}
