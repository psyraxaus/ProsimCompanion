using System.Diagnostics;
using System.Windows;

namespace ProsimCompanion.App;

/// <summary>
/// System tray icon (roadmap Phase 7): the app minimizes to the tray, and the tray owns quick
/// actions — open the web UI, show the status window, exit. WinForms NotifyIcon under WPF
/// (no built-in WPF equivalent); the icon comes from the exe so no separate asset is loaded.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;

    public TrayIcon(Window window, string webUrl)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(webUrl);

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Web UI", null, (_, _) => OpenBrowser(webUrl));
        menu.Items.Add("Show Window", null, (_, _) => Restore(window));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => window.Dispatcher.Invoke(() =>
            System.Windows.Application.Current.Shutdown()));

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "ProsimCompanion",
            Icon = LoadAppIcon(),
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notifyIcon.DoubleClick += (_, _) => Restore(window);

        // Minimize-to-tray: minimizing hides the window entirely; the tray brings it back.
        // Closing the window still exits the app (the tray is a parking spot, not a daemon).
        window.StateChanged += (_, _) =>
        {
            if (window.WindowState == WindowState.Minimized)
            {
                window.Hide();
            }
        };
    }

    /// <summary>Restores the window from the tray (also the second-launch activation path).</summary>
    public static void Restore(Window window)
        => window.Dispatcher.Invoke(() =>
        {
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
        });

    private static System.Drawing.Icon LoadAppIcon()
        => (Environment.ProcessPath is { } exe ? System.Drawing.Icon.ExtractAssociatedIcon(exe) : null)
            ?? System.Drawing.SystemIcons.Application;

    private static void OpenBrowser(string url)
        => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
