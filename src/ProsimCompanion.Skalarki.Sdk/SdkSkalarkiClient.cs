using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Hardware.Skalarki;
using SkalarkiIO.SDK;

namespace ProsimCompanion.Skalarki.Sdk;

/// <summary>
/// <see cref="ISkalarkiClient"/> over the SkalarkiIO SDK's <c>EventClient</c>, typed against
/// the SDK directly (no reflection) — the only class in the app that is. It is created by
/// <c>SdkLoader</c> through <c>Activator</c> inside the SDK load context, so this assembly may
/// reference the SDK while the app does not. Facts it leans on are from the 2026-10-27 spike
/// (docs/integrations/skalarki.md): the constructor takes an <see cref="IPAddress"/>, presence
/// arrives on <c>ConnectedDevicesChanged</c>, the licence call returns an int, and
/// <c>Outputs.All</c> must never be enumerated.
/// </summary>
public sealed class SdkSkalarkiClient : ISkalarkiClient
{
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private EventClient? _client;

    public SdkSkalarkiClient(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public event Action<bool>? LinkChanged;

    public event Action<SkalarkiDeviceId, bool>? DeviceChanged;

    public event Action<SkalarkiHardwareEvent>? HardwareEvent;

    public string SdkVersion { get; } = typeof(EventClient).Assembly.GetName().Version?.ToString(3) ?? "?";

    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var address = await ResolveAsync(host.Trim(), cancellationToken).ConfigureAwait(false);

        EventClient client;
        // The SDK's constructor installs a SynchronizationContext on the calling thread; keep
        // the thread-pool thread as it was.
        var previous = SynchronizationContext.Current;
        try
        {
            client = new EventClient(address, port, OnIoEvent, this);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        client.ConnectionStateChanged += (_, e) => LinkChanged?.Invoke(e.Connected);
        client.ConnectedDevicesChanged += (_, e) =>
        {
            if (TryMap(e.Device, out var device))
            {
                DeviceChanged?.Invoke(device, e.Connected);
            }
        };

        lock (_gate)
        {
            _client?.Dispose();
            _client = client;
        }

        await client.ConnectAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task DisconnectAsync()
    {
        lock (_gate)
        {
            _client?.Disconnect();
        }

        return Task.CompletedTask;
    }

    public void RegisterAll(IReadOnlyCollection<SkalarkiDeviceId> devices, SkalarkiCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var wanted = devices.Select(d => (DeviceType)(int)d).ToHashSet();
        var descriptors = InputCatalogue(catalogue)
            .Where(d => wanted.Contains(d.Device))
            .Distinct()
            .ToList();
        Current().RegisterEvents(descriptors, scanState: false);
        _logger.LogInformation("Skalarki: registered {Count} {Catalogue} input descriptors for {Devices} boards", descriptors.Count, catalogue, devices.Count);
    }

    public void SetDeviceOutputs(SkalarkiDeviceId device, bool lit)
    {
        var sdkDevice = (DeviceType)(int)device;
        var outputs = OutputCatalogue().Where(o => o.Device == sdkDevice).Distinct().ToList();
        if (outputs.Count == 0)
        {
            return;
        }

        Current().SetOutputs(outputs, lit);
    }

    public void HardwareReset(SkalarkiDeviceId device, bool hard)
    {
        var client = Current();
        if (device == SkalarkiDeviceId.Overhead)
        {
            // The only typed reset descriptor the SDK ships; the int is the SDK's own "hard" flag.
            client.HardwareReset(HardwareResets.A32XHARDWARE.A32X_OVERHEAD_RESET, hard ? 1 : 0);
            return;
        }

        // Every other board has a <BOARD>RESET output under Outputs.HARDWARE; pulsing it is the
        // Profiler's own reset action as far as the catalogue tells. Unverified on hardware.
        var name = device.ToString().ToUpperInvariant() + "RESET";
        var reset = Outputs.HARDWARE.All.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"The SDK has no reset output for {device}.");
        client.SetOutputs([reset], true);
        Thread.Sleep(250);
        client.SetOutputs([reset], false);
    }

    /// <summary>Never asks the SDK. In SDK 5.1.919 the licence reply is a request-reply packet
    /// that <c>Serializer.Deserialize</c> turns into a NULL entry of its event list; the dispatch
    /// block then throws NullReferenceException on it and that client's whole incoming pipeline
    /// (events AND presence) is dead for good (found 2026-10-27: "1 of 1 boards" lamp tests and
    /// the spike's "no events" both came from this). The licence is simply not shown.</summary>
    public Task<string?> GetLicenseAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public void Dispose()
    {
        EventClient? client;
        lock (_gate)
        {
            client = _client;
            _client = null;
        }

        if (client is null)
        {
            return;
        }

        try
        {
            client.Disconnect();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Skalarki SDK Disconnect threw during dispose");
        }

        client.Dispose();
    }

    private EventClient Current()
    {
        lock (_gate)
        {
            return _client ?? throw new InvalidOperationException("The Skalarki client is not connected.");
        }
    }

    private void OnIoEvent(IOEvent ioEvent, object state)
    {
        if (ioEvent is null || !TryMap(ioEvent.Device, out var device))
        {
            return;
        }

        string value;
        var source = ioEvent.Source switch
        {
            HardwareSource.Switch => SkalarkiSourceKind.Switch,
            HardwareSource.Encoder => SkalarkiSourceKind.Encoder,
            HardwareSource.Axis => SkalarkiSourceKind.Axis,
            _ => SkalarkiSourceKind.Unknown,
        };
        try
        {
            value = source == SkalarkiSourceKind.Switch
                ? (ioEvent.ValueAsBool() ? "ON" : "OFF")
                : ioEvent.ValueAsInt().ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or NullReferenceException)
        {
            value = "?";
        }

        HardwareEvent?.Invoke(new SkalarkiHardwareEvent(
            device,
            ioEvent.Group.ToString(),
            source,
            ioEvent.Event.ToString(),
            value,
            DateTimeOffset.UtcNow));
    }

    private static bool TryMap(DeviceType sdkDevice, out SkalarkiDeviceId device)
    {
        var value = (int)sdkDevice;
        if (value >= (int)SkalarkiDeviceId.Glare && value <= (int)SkalarkiDeviceId.Draims2)
        {
            device = (SkalarkiDeviceId)value;
            return true;
        }

        device = default;
        return false;
    }

    /// <summary>Switches and encoders of a catalogue (axes are left out: every potentiometer
    /// wobble would count as a hardware event). Each root's typed <c>All</c> is used; nothing
    /// under <c>Outputs</c> is touched here.</summary>
    private static IEnumerable<IEventDescriptor> InputCatalogue(SkalarkiCatalogue catalogue) => catalogue switch
    {
        SkalarkiCatalogue.A33X => A33XSwitches.All.Concat(A33XEncoders.All),
        SkalarkiCatalogue.B73X => B73XSwitches.All.Concat(Encoders.B73XMIP.All),
        SkalarkiCatalogue.B77X => B77XSwitches.All.Concat(B77XEncoders.All),
        _ => Switches.All.Concat(Encoders.All),
    };

    /// <summary>Every lamp/LED output the A32X catalogue knows, by panel list — never
    /// <c>Outputs.All</c> (it recurses forever through <c>Outputs.CBCTRL.All</c>) and never the
    /// HARDWARE list (those are resets).</summary>
    private static IEnumerable<IOutputDescriptor> OutputCatalogue()
        => Outputs.GLARE.All
            .Concat(Outputs.PEDESTAL.All)
            .Concat(Outputs.OVHD.All)
            .Concat(Outputs.MIP.All)
            .Concat(Outputs.BACKLIGHT.All)
            .Concat(Outputs.PWRCTRL.All);

    private static async Task<IPAddress> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return literal;
        }

        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        return addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault()
            ?? throw new InvalidOperationException($"'{host}' did not resolve to an address.");
    }
}
