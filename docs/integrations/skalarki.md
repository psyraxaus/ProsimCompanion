# Skalarki hardware (SkalarkiIO Profiler SDK)

The PANELS diagnostics page reads the SkalarkiIO Profiler's SDK server to show which cockpit
panels (boards) the Profiler sees, and to run a hardware check. Everything below was measured on
2026-10-27 against the owner's rig; nothing is taken from Skalarki documentation, because the SDK
ships none beyond a sample program that was not available to us.

## Spike findings (tools/SkalarkiSpike, 2026-10-27)

Run against SkalarkiIO Profiler **5.1.920.1000** on 10.0.1.21 with SDK assemblies **5.1.0.0**
(file version 5.1.919.1000), from a .NET 10.0.12 x64 console that references the SDK dlls
directly.

| Question | Answer |
|---|---|
| Do the .NET Framework SDK assemblies load and run on .NET 10? | **Yes, in-process.** `SkalarkiIO.SDK.dll` (image runtime v4.0.30319) plus `SkalarkiIO.SDK.Telemetry.dll`, `System.Threading.Tasks.Dataflow.dll` (8.0.0.0) and `Microsoft.Diagnostics.Tracing.EventSource.dll` (1.1.28) from the Profiler's `Tools` folder. Connect, events, licence call and dispose all worked. No .NET Framework sidecar is needed. |
| Real default port | **53000.** The SDK has no default of its own: `new EventClient(callback)` stores `server = null, port = -1` and `ConnectAsync()` then throws `InvalidOperationException("Either specify the server address in the constructor, or in the Connect overload accepting an IP address")`. 53000 is the Profiler's own setting (`skalarkiServerIP="127.0.0.1" skalarkiServerPORT="53000"` in `%APPDATA%\SkalarkiIO Profiler 5.1\Settings.config`) and the constant both Skalarki client tools carry. |
| Host | The PC running the Profiler (the USB boards' PC), which may not be the PC running this app. The server accepted a connection from another PC on the LAN without any setting change. |
| What carries per-device presence | **`EventClient.ConnectedDevicesChanged`**, args `EventClient.DeviceChangedEventArgs { DeviceType Device; bool Connected }`. Within 50 ms of `ConnectionStateChanged(Connected=true)` the Profiler sends one event for **every** `DeviceType` (26 of them), connected or not — a full presence snapshot, not just the present boards. `HardwareConnectionStateEvent` is the wire packet behind it; it never reaches the `OnEventCallback` stream (that delegate is typed `IOEvent`). |
| `GetLicenseTypeAsync()` | Returns `Task<int>`, not a string; the reply on the owner's licence was **200200**. The meaning of the number is undocumented; the page shows it as-is. |
| `RegisterEvents(events, scanState)` | Sends a `RegisterEventsCommand(events, scanState)` to the Profiler. With GLARE's 81 switches **and** with the whole A32X catalogue (1138 switches + encoders), with `scanState` false **and** true, **zero** IO events arrived in 12 s. Nobody touched a panel during the runs, so: (a) `scanState=true` did **not** replay the current switch positions on this Profiler build in ProSim mode, (b) live IO-event delivery is still unobserved — it needs a human pressing a switch while the app listens (manual step below). |
| Does registering `.All` upset the Profiler's ProSim profile? | Nothing visible: the Profiler stayed connected, its device list kept reporting, and the registration returned at once. It is a read-side subscription; the app never calls `SetOutputs` outside LAMP TEST, and `HardwareReset` only behind an explicit confirm. **To be verified on hardware by the owner** (see the manual test). |
| Dispose | `Disconnect()` raises `ConnectionStateChanged(false)` synchronously and `Dispose()` completes immediately. |
| SDK defect | **`Outputs.All` (through `Outputs.CBCTRL.All`) recurses into itself and kills the process with a stack overflow.** Never enumerate `Outputs.All`; the per-panel lists (`Outputs.GLARE.All` 51, `PEDESTAL` 210, `OVHD` 239, `MIP` 25, `BACKLIGHT` 24, `HARDWARE` 20) are fine. `Switches.All` (1124) and `Encoders.All` (14) are fine. |
| Owner's rig as reported | Connected: GLARE, OVERHEAD, MCDU1, TQ, MIP, CLOCK, ECAM, LOWERPED, ATC, RMP1, RMP3, ACP1, ACP3, DCDUCS. Not connected: PEDESTAL (the pedestal reports as its individual boards), MCDU2, MD80MCP, RMP2, ACP2, REFUEL, PWRCTRL, CBCTRL, DCDUFO, MCDU3, DRAIMS1, DRAIMS2. |

### Where the SDK metadata in the brief disagreed with the assemblies

- `EventClient(string server, int port, …)` does not exist: the constructor takes
  **`System.Net.IPAddress`** (`EventClient(IPAddress server, int port, OnEventCallback callback, object state)`),
  as does `ConnectAsync(IPAddress, int)`. Host names must be resolved by the caller.
- `HardwareReset(IHardwareResetDescriptor, int hardReset)` — an `int`, not a `bool`.
- `IEventDescriptor` exposes only `Device`, `ChipAddress`, `Index`; the **concrete**
  `SwitchEventDescriptor` adds `Source` and `Name` (`ARPT`, …). `IOEvent` has no `Value`
  property, only `ValueAsBool()` / `ValueAsInt()`.
- The `Event` enum (3739 members) is the control name of an `IOEvent` — `e.Event.ToString()`
  gives `FCU_SPD_PUSH`-style names without any lookup table.
- `SkalarkiIO.SDK.Diagnostics.Logic.VariableTable` / `EventMapper` / `IUpdateTarget` are all
  **internal** types: not reachable without reflection. The app uses the `Event` and `Group`
  enum names instead, which is what those mappers resolve to anyway.
- `DeviceType` also has a `Last = 27` sentinel after `DRAIMS2`.
- `GetLicenseTypeAsync()` returns `Task<int>`.
- The Diagnostics.Logic assembly additionally needs `System.Collections.Immutable` (in-box on
  .NET 10) and `System.Xml.Linq`.

### Re-running the spike

```
dotnet build tools/SkalarkiSpike/SkalarkiSpike.csproj
tools/SkalarkiSpike/bin/Debug/net10.0/SkalarkiSpike.exe 10.0.1.21 53000 --seconds 30
tools/SkalarkiSpike/bin/Debug/net10.0/SkalarkiSpike.exe 10.0.1.21 53000 --scan --all --seconds 30
```

Press any FCU (glareshield) button during the first run: the first 20 `IOEvent`s are printed
with device, group, event name, source and value. The spike needs the SDK assemblies in
`libs/skalarki/` (gitignored; copy them from `C:\Program Files (x86)\SkalarkiIO Profiler 5.1\Tools`).

## SDK posture (ADR-0017)

- **User-supplied, never shipped.** The SDK assemblies belong to the Profiler installation. The
  app never redistributes them and never references them at compile time. The user points the
  app at a folder on the Skalarki settings page (Settings → Hardware → Skalarki Panels →
  "Skalarki SDK folder"); the Profiler's own `C:\Program Files (x86)\SkalarkiIO Profiler 5.1\Tools`
  folder has every file needed and is the first place the Browse… dialog opens.
- **Four files are required** beside each other: `SkalarkiIO.SDK.dll`, `SkalarkiIO.SDK.Telemetry.dll`,
  `System.Threading.Tasks.Dataflow.dll`, `Microsoft.Diagnostics.Tracing.EventSource.dll`. The
  settings page says which one is missing. `SkalarkiIO.SDK.Diagnostics.Logic.dll` is optional
  (internal types only).
- **How it loads.** `ProsimCompanion.Skalarki` (referenced by the app) holds `ISkalarkiClient`,
  the connection service, the collector and `SdkLoader`. The one class that references the SDK,
  `SdkSkalarkiClient`, lives in `ProsimCompanion.Skalarki.Sdk`, which is built with
  `Reference Private=false` against `libs/skalarki/` and copied beside the exe without being
  referenced. `SdkLoader` creates one `AssemblyLoadContext` per SDK folder that resolves the
  SDK names from that folder and everything else from the default context, loads the bridge
  into it and instantiates the client. When `libs/skalarki/` is absent at build time the bridge
  compiles empty and the app reports "This build of the app has no Skalarki SDK bridge".
- **Dev folder.** `libs/skalarki/` is gitignored; copy the Profiler's `Tools` folder there to
  build the bridge and run the spike.
- **Host and port.** `skalarki.profilerHost` (default `127.0.0.1`) and `skalarki.profilerPort`
  (default 53000). The Profiler may run on another PC; the SDK takes an `IPAddress`, so the
  app resolves a host name first (IPv4 preferred).

## Board catalogue

`SkalarkiDeviceId` mirrors the SDK's `DeviceType` by value: GLARE 1, PEDESTAL 2, OVERHEAD 3,
MCDU1 4, MCDU2 5, MD80MCP 6, TQ 7, MIP 8, CLOCK 9, ECAM 10, LOWERPED 11, ATC 12, RMP1 13,
RMP2 14, RMP3 15, ACP1 16, ACP2 17, ACP3 18, REFUEL 19, PWRCTRL 20, CBCTRL 21, DCDUCS 22,
DCDUFO 23, MCDU3 24, DRAIMS1 25, DRAIMS2 26. The page's node ids are the lower-snake forms
(`mcdu1`, `lower_ped`, `dcdu_cs`); settings name boards by the SDK word (`LOWERPED`).

Registered inputs per catalogue (`skalarki.catalogue`): A32X = `Switches.All` + `Encoders.All`
(1124 + 14 descriptors across the boards); A33X = `A33XSwitches.All` + `A33XEncoders.All`;
B73X = `B73XSwitches.All` + `Encoders.B73XMIP.All`; B77X = `B77XSwitches.All` +
`B77XEncoders.All`. Axes are not registered (every potentiometer wobble would be an event).
The list is filtered to the watched boards; an empty `skalarki.enabledDevices` registers all.

The PANELS plan view and the hardware-check prompts come from
`wwwroot/data/skalarki-layout.json` (one section per catalogue; the A33X/B73X/B77X sections
are stubs from the `DeviceType` list). Another deck is a JSON edit there; the embedded copy
is the fallback.

## What the hardware check proves, and what it does not

- A board line turns **DONE** on the first input event the Profiler forwards from that board
  after the check started. That proves: the board is powered, its USB link is up, the
  Profiler sees it, the SDK server forwards it, and at least one control works.
- It does **not** prove that every switch on the board works, that outputs (lamps) work, or
  that the Profiler's ProSim profile maps the control correctly. Only LAMP TEST exercises
  outputs, and only the Profiler's own diagnostics tool covers mappings.
- A board that is not connected when the check starts is **SKIPPED** and joins as PENDING if
  it appears; a board that drops mid-check turns **LOST** and must be touched again when it
  returns.
- **LAMP TEST** drives every lamp/LED output of the chosen boards on for `skalarki.lampTestMs`
  (default 2 s) and off again, through `SetOutputs` on the per-panel output lists (never
  `Outputs.All`). **HW RESET** sends the SDK's reset for one board (the typed
  `A32X_OVERHEAD_RESET` for the overhead, the `<BOARD>RESET` output pulsed for 250 ms for the
  others) behind a two-click confirm; its effect on the Profiler's running profile is
  unverified.

## Open question: registering events alongside the Profiler's ProSim profile

The app registers the watched boards' switch and encoder descriptors with `scanState=false`.
In the spike this was a pure read-side subscription: the Profiler kept running its ProSim
profile, its device list kept reporting, nothing was written. Whether a second subscriber
changes anything in the Profiler's own event handling (double delivery, debouncing, the
`*_STATE` LVARs) could not be proven without a human at the hardware. The owner verifies on
the rig: with the PANELS page open and the check running, every normal cockpit action must
still reach ProSim exactly as before.
