// SkalarkiIO SDK 5.1 on .NET 10 — compatibility spike (Package 0, 2026-10-27).
// Usage: SkalarkiSpike <host> <port> [--scan] [--all] [--seconds N]
//   --scan     register with scanState = true (default false) and count the burst that follows
//   --all      register every switch, encoder and axis of the A32X catalogue, not just GLARE switches
//   --seconds  how long to listen for IO events after registering (default 20)
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using SkalarkiIO.SDK;

var host = args.Length > 0 ? args[0] : "127.0.0.1";
var port = args.Length > 1 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 53000;
var scan = args.Contains("--scan", StringComparer.OrdinalIgnoreCase);
var all = args.Contains("--all", StringComparer.OrdinalIgnoreCase);
var seconds = 20;
var si = Array.FindIndex(args, a => a.Equals("--seconds", StringComparison.OrdinalIgnoreCase));
if (si >= 0 && si + 1 < args.Length)
{
    _ = int.TryParse(args[si + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds);
}

var clock = Stopwatch.StartNew();
void Say(string text) => Console.WriteLine($"[{clock.Elapsed.TotalSeconds,7:0.000}] {text}");

Say($"runtime {Environment.Version} ({(Environment.Is64BitProcess ? "x64" : "x86")})");
var sdk = typeof(EventClient).Assembly;
Say($"SDK assembly {sdk.GetName().Version} file {FileVersionInfo.GetVersionInfo(sdk.Location).FileVersion} image runtime {sdk.ImageRuntimeVersion}");
Say($"target {host}:{port} scanState={scan} all={all} listen={seconds}s");

// Descriptor catalogue facts the app will need: what type the lists carry and how big they are.
var glare = Switches.GLARE.All.Cast<object>().ToList();
Say($"Switches.GLARE.All: {glare.Count} items of {glare.FirstOrDefault()?.GetType().FullName ?? "?"}");
if (glare.FirstOrDefault() is { } first)
{
    var props = first.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Select(pr => $"{pr.Name}={Describe(pr.GetValue(first))}");
    Say("  first descriptor: " + string.Join(", ", props));
}

// Outputs.All is NOT enumerated: in SDK 5.1.919 Outputs.CBCTRL.All calls itself and the
// process dies with a stack overflow (found by this spike, 2026-10-27). Per-panel lists only.
Say($"Switches.All {Count(Switches.All)}  Encoders.All {Count(Encoders.All)}  Outputs.HARDWARE.All {Count(Outputs.HARDWARE.All)}  Outputs.BACKLIGHT.All {Count(Outputs.BACKLIGHT.All)}  Outputs.GLARE.All {Count(Outputs.GLARE.All)}  Outputs.PEDESTAL.All {Count(Outputs.PEDESTAL.All)}  Outputs.OVHD.All {Count(Outputs.OVHD.All)}  Outputs.MIP.All {Count(Outputs.MIP.All)}");
foreach (var (name, list) in new (string, IEnumerable)[]
{
    ("Switches.GLARE", Switches.GLARE.All), ("Switches.PEDESTAL", Switches.PEDESTAL.All), ("Switches.OVHD", Switches.OVHD.All),
    ("Switches.MIP", Switches.MIP.All), ("Switches.CBCTRL", Switches.CBCTRL.All),
    ("Encoders.GLARE", Encoders.GLARE.All), ("Encoders.MIP", Encoders.MIP.All), ("Encoders.CLOCK", Encoders.CLOCK.All),
    ("Encoders.PEDESTAL", Encoders.PEDESTAL.All), ("Encoders.OVHD", Encoders.OVHD.All),
})
{
    var devices = list.Cast<IEventDescriptor>().Select(d => d.Device).Distinct().OrderBy(d => (int)d).ToList();
    Say($"  {name}: {Count(list)} descriptors, devices {string.Join('/', devices)}");
}

var ioEvents = 0;
var burst = 0;
var printed = 0;
DateTime? registeredAt = null;
var deviceEvents = new List<string>();

void OnEvent(IOEvent e, object state)
{
    Interlocked.Increment(ref ioEvents);
    if (registeredAt is { } at && DateTime.UtcNow - at < TimeSpan.FromSeconds(3))
    {
        Interlocked.Increment(ref burst);
    }

    if (Interlocked.Increment(ref printed) <= 20)
    {
        string value;
        try
        {
            value = e.Source == HardwareSource.Switch ? e.ValueAsBool().ToString() : e.ValueAsInt().ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            value = "?" + ex.GetType().Name;
        }

        Say($"  IOEvent device={e.Device} group={e.Group} event={e.Event} source={e.Source} value={value}");
    }
}

EventClient client;
try
{
    client = new EventClient(IPAddress.Parse(host), port, OnEvent, null!);
}
catch (Exception ex)
{
    Say($"FAIL constructing EventClient: {ex.GetType().FullName}: {ex.Message}");
    return 2;
}

Say("EventClient constructed (SynchronizationContext now " + (SynchronizationContext.Current?.GetType().Name ?? "null") + ")");
client.ConnectionStateChanged += (_, e) => Say($"  ConnectionStateChanged Connected={e.Connected}");
client.ConnectedDevicesChanged += (_, e) =>
{
    var line = $"ConnectedDevicesChanged Device={e.Device} Connected={e.Connected}";
    lock (deviceEvents)
    {
        deviceEvents.Add(line);
    }

    Say("  " + line);
};

try
{
    Say("ConnectAsync()...");
    await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(10));
    Say("ConnectAsync returned");
}
catch (Exception ex)
{
    Say($"FAIL ConnectAsync: {ex.GetType().FullName}: {ex.Message}");
    client.Dispose();
    return 3;
}

await Task.Delay(TimeSpan.FromSeconds(4));
Say($"device events in the first 4 s: {deviceEvents.Count}, IO events before registering: {ioEvents}");

// The licence call is opt-in since 2026-10-27 (second run): the reply packet makes the SDK's
// Serializer.Deserialize add a NULL to its event list, the dispatch block dereferences it, and
// that client's whole incoming pipeline is dead from then on (no events, no presence). The
// unobserved NullReferenceException below is the proof.
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Say($"  UNOBSERVED TASK EXCEPTION: {e.Exception.InnerException?.GetType().Name}: {e.Exception.InnerException?.Message} @ {e.Exception.InnerException?.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
    e.SetObserved();
};
if (args.Contains("--license", StringComparer.OrdinalIgnoreCase))
{
    try
    {
        Say($"GetLicenseTypeAsync()...");
        var licence = await client.GetLicenseTypeAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Say($"  licence reply = {licence} (Task<int>)");
        await Task.Delay(1000);
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
    catch (Exception ex)
    {
        Say($"  GetLicenseTypeAsync failed: {ex.GetType().FullName}: {ex.Message}");
    }
}
else
{
    Say("licence call skipped (--license to run it; it poisons the SDK's event pipeline)");
}

try
{
    IEnumerable<IEventDescriptor> toRegister = all
        ? Switches.All.Cast<IEventDescriptor>().Concat(Encoders.All.Cast<IEventDescriptor>())
        : Switches.GLARE.All.Cast<IEventDescriptor>();
    var list = toRegister.ToList();
    Say($"RegisterEvents({list.Count} descriptors, scanState={scan})...");
    registeredAt = DateTime.UtcNow;
    client.RegisterEvents(list, scan);
    Say("RegisterEvents returned");
}
catch (Exception ex)
{
    Say($"FAIL RegisterEvents: {ex.GetType().FullName}: {ex.Message}");
}

var until = DateTime.UtcNow.AddSeconds(seconds);
var lastReport = 0;
while (DateTime.UtcNow < until && printed < 20)
{
    await Task.Delay(500);
    if (ioEvents != lastReport && DateTime.UtcNow - registeredAt > TimeSpan.FromSeconds(3))
    {
        lastReport = ioEvents;
    }
}

Say($"IO events total {ioEvents}; within 3 s of registering: {burst} (scanState={scan})");
Say($"device presence lines: {deviceEvents.Count}");

try
{
    Say("UnregisterEvents + Disconnect...");
    client.UnregisterEvents(Switches.GLARE.All.Cast<IEventDescriptor>().ToList());
    client.Disconnect();
    var dispose = Task.Run(client.Dispose);
    var finished = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(5))) == dispose;
    Say(finished ? "Dispose completed" : "Dispose did NOT complete within 5 s");
}
catch (Exception ex)
{
    Say($"Disconnect/Dispose threw: {ex.GetType().FullName}: {ex.Message}");
}

// Flush any unobserved task exception the SDK's Dataflow blocks left behind before exiting.
GC.Collect();
GC.WaitForPendingFinalizers();
await Task.Delay(200);
Say("done");
return 0;

static int Count(IEnumerable list) => list.Cast<object>().Count();

static string Describe(object? value) => value switch
{
    null => "null",
    int[] ints => "[" + string.Join(',', ints) + "]",
    IEnumerable e and not string => "[" + string.Join(',', e.Cast<object>().Take(4)) + "]",
    _ => value.ToString() ?? "",
};
