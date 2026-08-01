using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.App;

/// <summary>
/// Status window plus the one settings surface that deliberately lives in WPF (ADR-0001): the
/// web-server card (port, LAN binding, access token, QR onboarding) — so a broken web
/// configuration can always be repaired from here.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ConnectionStatusStore _status;
    private readonly JsonSettingsFile _settings;
    private readonly string _webUrl;
    private int _port;
    private bool _bindAll;
    private string _token;

    public MainWindow(ConnectionStatusStore status, JsonSettingsFile settings, WebUiOptions webUi, string webUrl)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(webUi);
        ArgumentException.ThrowIfNullOrWhiteSpace(webUrl);

        _status = status;
        _settings = settings;
        _webUrl = webUrl;
        _port = webUi.Port;
        _bindAll = webUi.BindToAllInterfaces;
        _token = webUi.AccessToken ?? "";

        InitializeComponent();
        WebUrlText.Text = webUrl;
        LanCheckBox.IsChecked = _bindAll;
        PortBox.Text = _port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        TokenBox.Text = _token;
        UpdateQr();

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

    private void LanCheckBox_Toggled(object sender, RoutedEventArgs e)
        => SaveHintText.Text = "Unsaved changes — click Save.";

    private void SaveWebButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text, out var port) || port is < 1024 or > 65535)
        {
            SaveHintText.Text = "Port must be a number between 1024 and 65535.";
            return;
        }

        _port = port;
        _bindAll = LanCheckBox.IsChecked == true;
        _settings.Update(root =>
        {
            var webUi = JsonSettingsFile.GetOrCreateSection(root, WebUiOptions.SectionName);
            webUi["port"] = _port;
            webUi["bindToAllInterfaces"] = _bindAll;
        });

        SaveHintText.Text = "Saved. Restart ProsimCompanion to apply port/LAN binding changes.";
        UpdateQr();
    }

    private void RegenerateTokenButton_Click(object sender, RoutedEventArgs e)
    {
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _settings.Update(root =>
            JsonSettingsFile.GetOrCreateSection(root, WebUiOptions.SectionName)["accessToken"] = _token);

        TokenBox.Text = _token;
        SaveHintText.Text = "New token saved — previously connected LAN devices must scan again.";
        UpdateQr();
    }

    private void UpdateQr()
    {
        if (!_bindAll || string.IsNullOrEmpty(_token))
        {
            QrImage.Source = null;
            QrHintText.Text = _bindAll
                ? "No access token available."
                : "Enable LAN access (and restart) to connect phones/tablets on your network.";
            return;
        }

        var lanAddress = FindLanAddress();
        if (lanAddress is null)
        {
            QrImage.Source = null;
            QrHintText.Text = "No LAN network address found on this machine.";
            return;
        }

        var url = $"http://{lanAddress}:{_port}/?token={_token}";
        QrImage.Source = RenderQr(url);
        QrHintText.Text = $"Scan from a device on your network, or open: {url}";
    }

    private static IPAddress? FindLanAddress()
        => NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address)
            .FirstOrDefault(address =>
                address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address));

    private static BitmapImage RenderQr(string content)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(6);

        using var stream = new MemoryStream(png);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
