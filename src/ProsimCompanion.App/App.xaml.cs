using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.App;

/// <summary>
/// WPF application shell. Deliberately thin (ADR-0001): the container is owned by the web host in
/// <see cref="Program"/>; this class only opens the status window.
/// </summary>
public partial class App : Application
{
    private readonly IServiceProvider _services;
    private readonly string _webUrl;
    private readonly SingleInstanceGuard _singleInstance;
    private TrayIcon? _trayIcon;

    public App(IServiceProvider services, string webUrl, SingleInstanceGuard singleInstance)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(webUrl);
        ArgumentNullException.ThrowIfNull(singleInstance);

        _services = services;
        _webUrl = webUrl;
        _singleInstance = singleInstance;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var window = new MainWindow(
            _services.GetRequiredService<ConnectionStatusStore>(),
            _services.GetRequiredService<JsonSettingsFile>(),
            _services.GetRequiredService<IOptionsMonitor<WebUiOptions>>().CurrentValue,
            _webUrl);
        MainWindow = window;
        _trayIcon = new TrayIcon(window, _webUrl);
        // A second launch pokes the show-window event instead of starting twice.
        _singleInstance.OnShowRequested(() => TrayIcon.Restore(window));
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
