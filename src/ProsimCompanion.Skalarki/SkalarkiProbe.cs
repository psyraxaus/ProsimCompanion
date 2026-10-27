using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Hardware.Skalarki;

namespace ProsimCompanion.Skalarki;

/// <summary>
/// The settings page's view of the SDK (<see cref="ISkalarkiSdkInspector"/>): folder checks
/// through <see cref="SdkLoader"/>, and a one-shot probe that runs the spike's sequence —
/// connect, collect the presence snapshot for a moment, ask the licence, disconnect — on a
/// client of its own, so it works while the background link is off or pointed elsewhere.
/// </summary>
public sealed class SkalarkiProbe : ISkalarkiSdkInspector
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan PresenceWindow = TimeSpan.FromMilliseconds(1500);

    private readonly ISkalarkiClientFactory _factory;
    private readonly ILogger<SkalarkiProbe> _logger;

    public SkalarkiProbe(ISkalarkiClientFactory factory, ILogger<SkalarkiProbe> logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(logger);
        _factory = factory;
        _logger = logger;
    }

    public string? DescribeSdk(string? sdkPath) => SdkLoader.DescribeSdk(sdkPath);

    public string? Validate(string? sdkPath) => _factory.Validate(sdkPath ?? "");

    public async Task<SkalarkiProbeResult> ProbeAsync(string host, int port, string? sdkPath, CancellationToken cancellationToken)
    {
        var refusal = _factory.Validate(sdkPath ?? "");
        if (refusal is not null)
        {
            return new SkalarkiProbeResult(false, refusal, [], [], null);
        }

        ISkalarkiClient client;
        try
        {
            client = _factory.Create(sdkPath!);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SkalarkiProbeResult(false, "The Skalarki SDK bridge failed to load: " + ex.Message, [], [], null);
        }

        var reported = new List<SkalarkiDeviceId>();
        var connected = new List<SkalarkiDeviceId>();
        var gate = new object();
        client.DeviceChanged += (device, isConnected) =>
        {
            lock (gate)
            {
                if (!reported.Contains(device))
                {
                    reported.Add(device);
                }

                if (isConnected && !connected.Contains(device))
                {
                    connected.Add(device);
                }
                else if (!isConnected)
                {
                    connected.Remove(device);
                }
            }
        };

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            await Task.Delay(PresenceWindow, cancellationToken).ConfigureAwait(false);
            string? licence = null;
            try
            {
                licence = await client.GetLicenseAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Skalarki probe: licence call failed");
            }

            List<SkalarkiDeviceId> seen, up;
            lock (gate)
            {
                seen = [.. reported.OrderBy(d => (int)d)];
                up = [.. connected.OrderBy(d => (int)d)];
            }

            return new SkalarkiProbeResult(true, $"{host}:{port}", seen, up, licence);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SkalarkiProbeResult(false, $"No answer from {host}:{port} within {ConnectTimeout.TotalSeconds:0} s.", [], [], null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SkalarkiProbeResult(false, ex.InnerException?.Message ?? ex.Message, [], [], null);
        }
        finally
        {
            var dispose = Task.Run(() =>
            {
                try
                {
                    client.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Skalarki probe: dispose threw");
                }
            });
            await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None)).ConfigureAwait(false);
        }
    }
}
