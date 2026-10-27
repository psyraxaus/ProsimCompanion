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
