using System.Globalization;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Diagnostics.Ecam;
using ProsimCompanion.Core.Hardware.Skalarki;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Skalarki;

/// <summary>
/// The PANELS page: the Profiler box, one node per board (every board the SDK knows, so the
/// layout file may name any of them), the hardware-check readouts, the memo cells, and the
/// cautions — Profiler link lost, a watched board lost (with the cockpit function it takes
/// with it as INOP). Draws nothing at all while the feature is off, which is how the page
/// disappears from the selector.
/// </summary>
public sealed class SkalarkiDiagnosticsCollector : IDiagnosticsCollector
{
    private const string System = "SKALARKI";

    private readonly SkalarkiStatusStore _store;
    private readonly IOptionsMonitor<SkalarkiOptions> _options;
    private readonly StateTransitions<string> _edges = new();

    public SkalarkiDiagnosticsCollector(SkalarkiStatusStore store, IOptionsMonitor<SkalarkiOptions> options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        _options = options;
    }

    public string Page => DiagPageIds.Panels;

    public void Contribute(DiagPageBuilder page, DiagCautionSink cautions)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(cautions);

        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            return;
        }

        var now = page.Events.Now;
        var snap = _store.Snapshot();
        var endpoint = $"{options.ProfilerHost}:{options.ProfilerPort}".ToUpperInvariant();
        var linked = snap.LinkState == ConnectionState.Connected;

        // The Profiler box.
        var (linkState, linkWord) = snap.LinkState switch
        {
            ConnectionState.Connected => (DiagState.Normal, EcamTokens.Conn),
            ConnectionState.Connecting => (DiagState.Caution, EcamTokens.Reconn),
            ConnectionState.Disconnected => (DiagState.Caution, EcamTokens.Reconn),
            _ => (DiagState.NoData, EcamTokens.NoData),
        };
        page.Node("profiler", linkState, linkWord, endpoint, snap.SdkVersion is null ? null : "SDK " + snap.SdkVersion);
        page.Readout("host", endpoint);
        page.Readout("license", snap.License);
        page.Readout("sdk_version", snap.SdkVersion);
        page.Readout("evt_s", linked ? snap.EventsPerSecond.ToString("0.0", CultureInfo.InvariantCulture) : null);
        page.Readout("link_since", snap.LinkSinceUtc is { } since ? EcamPresentationClock(since) : null);
        page.Readout("link_reason", snap.Reason?.ToUpperInvariant());

        // One node per board the SDK knows: watched boards carry the live state, the rest are
        // "- - -" so a layout that names them draws them white instead of amber.
        var states = new List<KeyValuePair<string, DiagState>>();
        var lostLabels = new List<string>();
        foreach (var device in SkalarkiDevices.All)
        {
            var id = SkalarkiDevices.NodeId(device);
            var label = options.LabelFor(device).ToUpperInvariant();
            page.Readout("label_" + id, label);
            if (!snap.Devices.TryGetValue(device, out var view) || !view.Enabled)
            {
                page.NotApplicable(id, EcamTokens.Off);
                continue;
            }

            if (!linked)
            {
                page.NoData(id, EcamTokens.LinkDown);
                continue;
            }

            if (view.Connected)
            {
                var detail = view.LastEventUtc is { } at
                    ? $"{EcamTokens.Last} {EcamPresentationClock(at)} {view.LastGroup}".TrimEnd()
                    : view.EventCount > 0 ? $"{view.EventCount} {EcamTokens.Evt}" : null;
                page.Node(id, DiagState.Normal, EcamTokens.Conn, detail);
                states.Add(new(label, DiagState.Normal));
                page.Readout("evt_" + id, view.EventCount.ToString(CultureInfo.InvariantCulture));
            }
            else if (view.LastChangeUtc is { } lost)
            {
                page.Node(id, DiagState.Caution, EcamTokens.Lost, Age(now - lost));
                states.Add(new(label, DiagState.Caution));
                lostLabels.Add(label);
                cautions.Raise(System, $"{label} {EcamTokens.Lost}", DiagState.Caution, new DiagAction(EcamTokens.HwReset, EcamTokens.Avail));
                cautions.Inop(InopFor(device));
            }
            else
            {
                // Named in the settings but never reported by the Profiler.
                page.NoData(id, "NOT REPORTED");
                states.Add(new(label, DiagState.NoData));
            }
        }

        // Hardware check readouts.
        if (snap.Check is { } check)
        {
            page.Readout("check_progress", $"{check.Done}/{check.Total}");
            page.Readout("check_started", EcamPresentationClock(check.StartedUtc));
            page.Readout("check_complete", check.Complete ? "1" : null);
            foreach (var item in check.Items)
            {
                var id = SkalarkiDevices.NodeId(item.Device);
                page.Readout("check_" + id, item.State switch
                {
                    SkalarkiCheckState.Done => EcamTokens.Done,
                    SkalarkiCheckState.Lost => EcamTokens.Lost,
                    SkalarkiCheckState.Skipped => EcamTokens.Skipped,
                    _ => item.Prompt,
                });
                if (item.DoneAtUtc is { } doneAt)
                {
                    page.Readout("check_" + id + "_at", EcamPresentationClock(doneAt));
                }
            }
        }

        // Caution line and the link cautions.
        if (snap.LinkState == ConnectionState.Disabled)
        {
            page.CautionLine(snap.Reason is null ? "SKALARKI LINK OFF" : "SKALARKI SDK NOT LOADED");
            cautions.Raise(System, "SKALARKI SDK NOT LOADED", DiagState.Caution, new DiagAction("FOLDER", options.SdkPath.Length == 0 ? "NOT SET" : "CHECK"));
            cautions.Inop("SKALARKI PANELS");
        }
        else if (!linked)
        {
            page.CautionLine("SKALARKI PROFILER LINK LOST · RECONNECT AUTO");
            cautions.Raise(System, "SKALARKI PROFILER LINK LOST", DiagState.Caution,
                new DiagAction("RECONNECT", EcamTokens.Auto), new DiagAction("HOST", endpoint));
            cautions.Inop("SKALARKI PANELS");
        }
        else if (lostLabels.Count > 0)
        {
            page.CautionLine($"{string.Join(" · ", lostLabels.Take(2))} {EcamTokens.Lost} · {EcamTokens.HwReset} {EcamTokens.Avail}");
        }

        // Memo cells.
        page.Readout("memo_devices", $"{snap.ConnectedCount}/{snap.EnabledCount}");
        page.Readout("memo_profiler", snap.SdkVersion);
        page.Readout("memo_license", snap.License);
        page.Readout("memo_link", snap.LinkState switch
        {
            ConnectionState.Connected => EcamTokens.LinkUp,
            ConnectionState.Disabled => EcamTokens.Off,
            _ => EcamTokens.LinkDown,
        });

        states.Add(new("PROFILER", linked ? DiagState.Normal : DiagState.Caution));
        foreach (var (id, _, to) in _edges.Observe(states))
        {
            page.Events.Push($"SKALARKI {id} {(to == DiagState.Normal ? "OK" : EcamTokens.Lost)}", to);
        }
    }

    /// <summary>The cockpit function a lost board takes with it (the STATUS page's INOP SYS).</summary>
    public static string InopFor(SkalarkiDeviceId device) => device switch
    {
        SkalarkiDeviceId.Glare => "FCU",
        SkalarkiDeviceId.Overhead => "OVERHEAD",
        SkalarkiDeviceId.Pedestal => "PEDESTAL",
        SkalarkiDeviceId.Mcdu1 => "MCDU 1",
        SkalarkiDeviceId.Mcdu2 => "MCDU 2",
        SkalarkiDeviceId.Mcdu3 => "MCDU 3",
        SkalarkiDeviceId.Md80Mcp => "MCP",
        SkalarkiDeviceId.Tq => "THR LEVERS",
        SkalarkiDeviceId.Mip => "MIP",
        SkalarkiDeviceId.Clock => "CLOCK",
        SkalarkiDeviceId.Ecam => "ECAM CP",
        SkalarkiDeviceId.LowerPed => "LOWER PED",
        SkalarkiDeviceId.Atc => "TRANSPONDER",
        SkalarkiDeviceId.Rmp1 => "RADIO 1",
        SkalarkiDeviceId.Rmp2 => "RADIO 2",
        SkalarkiDeviceId.Rmp3 => "RADIO 3",
        SkalarkiDeviceId.Acp1 => "AUDIO 1",
        SkalarkiDeviceId.Acp2 => "AUDIO 2",
        SkalarkiDeviceId.Acp3 => "AUDIO 3",
        SkalarkiDeviceId.Refuel => "REFUEL PANEL",
        SkalarkiDeviceId.PwrCtrl => "PWR CTRL",
        SkalarkiDeviceId.CbCtrl => "CB PANEL",
        SkalarkiDeviceId.DcduCs => "DCDU CS",
        SkalarkiDeviceId.DcduFo => "DCDU FO",
        SkalarkiDeviceId.Draims1 => "DRAIMS 1",
        SkalarkiDeviceId.Draims2 => "DRAIMS 2",
        _ => device.ToString().ToUpperInvariant(),
    };

    /// <summary>"42 S" under 100 s, then "3 M", then "2 H": the LOST age as ECAM prints it.</summary>
    public static string Age(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        return age.TotalSeconds < 100 ? $"{(int)age.TotalSeconds} {EcamTokens.Units.Seconds}"
            : age.TotalMinutes < 100 ? $"{(int)age.TotalMinutes} M"
            : $"{(int)age.TotalHours} H";
    }

    private static string EcamPresentationClock(DateTimeOffset at)
        => at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
}
