using System.Diagnostics;
using System.Windows;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.App;

/// <summary>
/// Status window: shows the web UI address and live subsystem states. Intentionally the only
/// WPF surface besides (later) the web-server settings card — see ADR-0001.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ConnectionStatusStore _status;
    private readonly string _webUrl;

    public MainWindow(ConnectionStatusStore status, string webUrl)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentException.ThrowIfNullOrWhiteSpace(webUrl);

        _status = status;
        _webUrl = webUrl;

        InitializeComponent();
        WebUrlText.Text = webUrl;

        _status.Changed += OnStatusChanged;
        Closed += (_, _) => _status.Changed -= OnStatusChanged;
        RefreshStatuses();
    }

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        // Store events fire on the writer's thread; marshal to the dispatcher.
        Dispatcher.BeginInvoke(RefreshStatuses);
    }

    private void RefreshStatuses()
    {
        StatusList.ItemsSource = _status
            .Snapshot()
            .Select(pair => $"{pair.Key}: {pair.Value}")
            .ToList();
    }

    private void OpenBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(_webUrl) { UseShellExecute = true });
    }
}
