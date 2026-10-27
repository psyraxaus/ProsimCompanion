# ADR-0017: Skalarki SDK isolation (PANELS diagnostics page)

Date: 2026-10-27
Status: accepted (built on `feature/ecam-diagnostics` in five packages; hardware verification
by the owner pending)
Extends: ADR-0016 (a sixth ECAM page), ADR-0003 (an in-repo integration layer over a vendor
SDK, no package feeds)

## Context

The owner's cockpit is Skalarki hardware driven by the SkalarkiIO Profiler. The Profiler
offers an SDK server (TCP, port 53000) and ships a .NET Framework SDK (`SkalarkiIO.SDK.dll`
5.1, `mscorlib 4.0`, `System.Threading.Tasks.Dataflow 8.0`) with no documentation beyond a
sample program. We want a PANELS page in the ECAM diagnostics family that shows which boards
the Profiler sees, lets the owner run a hardware check by touching each panel, and drives a
lamp test — without shipping Skalarki's assemblies, without binding the app to them at
compile time (a missing SDK must never stop the app), and without a second toolchain if we
can avoid one.

Three things were unknown: whether the .NET Framework assemblies run in-process on .NET 10,
what the SDK really sends (device presence, the `scanState` flag, the licence reply), and
whether we needed a .NET Framework 4.8 sidecar talking over a named pipe.

## Decision

1. **Spike first (package 0).** `tools/SkalarkiSpike` references the SDK directly from a .NET
   10 console and talks to the real Profiler. Finding: the assemblies load and run in-process
   on .NET 10 with four files beside each other (`SkalarkiIO.SDK`, `SkalarkiIO.SDK.Telemetry`,
   `System.Threading.Tasks.Dataflow`, `Microsoft.Diagnostics.Tracing.EventSource`). **No
   sidecar.** The rest of the findings (presence on `ConnectedDevicesChanged`, `Task<int>`
   licence, `scanState=true` replays nothing, `Outputs.All` recurses forever) are recorded at
   the top of `docs/integrations/skalarki.md`.
2. **The SDK sits behind our own seam.** `ProsimCompanion.Skalarki` references Core only and
   declares `ISkalarkiClient` (connect, register, outputs, reset, licence, three events) over
   our own `SkalarkiDeviceId` enum, which mirrors the SDK's `DeviceType` by value. Core and
   Web never see an SDK type; the connection service, the state core and the collector are
   testable with a fake client.
3. **One conditionally built bridge assembly.** `ProsimCompanion.Skalarki.Sdk` is the only
   project that references the SDK (`Reference Private=false` against the gitignored
   `libs/skalarki/`). When the folder is absent it compiles no sources and produces an empty
   assembly, so CI and fresh clones build; the app copies the bridge beside the exe through a
   `ProjectReference` with `ReferenceOutputAssembly=false` and never binds to it.
4. **Runtime loading into a dedicated `AssemblyLoadContext`.** `SdkLoader` validates the user's
   SDK folder (the four files plus the bridge), creates one load context per folder that
   resolves the SDK assembly names from that folder and defers everything else to the default
   context (so `ISkalarkiClient` is one type, not two), loads the bridge into it, finds the
   client type and instantiates it. The ProSim SDK's `AssemblyLoadContext.Default.Resolving`
   hook was not copied: that pattern keeps the dll for the life of the process and binds the
   main assembly to the SDK at compile time, which is exactly what we are avoiding here.
5. **Degrade, never fail.** Off by default; silent while off (no page, no caution, no log
   line). With the feature on and no usable folder, the link is `Disabled` with a reason the
   settings page and the PANELS page print. Every SDK call is wrapped; a failure sets the link
   state and a caution. The client is disposed off-thread with a 3 s bound so a wedged
   Dataflow block cannot hold up shutdown. Outputs are driven only by LAMP TEST (bounded by
   the configured hold) and HW RESET (two clicks).
6. **Hardware check is pure Core.** `HardwareCheckSession` holds the per-board walk; the
   service feeds it presence and events; the page reads it from the Skalarki snapshot.
7. **PANELS is a page only while the link is on.** The collector draws nothing while
   disabled; `DiagnosticsSnapshot.Panels` is null then and the selector and route hide it.

## Consequences

- The app ships no Skalarki code it does not own; the user points it at the Profiler's
  `Tools` folder (first Browse… candidate).
- A developer without `libs/skalarki/` builds and tests everything except the bridge's own
  code; the PANELS feature then reports "built without the SDK" at runtime.
- The .NET Framework compatibility finding holds for SDK 5.1.919 on .NET 10.0.12. A future
  SDK that needs a Framework-only API would bring the sidecar fallback back
  (`tools/SkalarkiBridge/`, JSON lines over a named pipe behind the same `ISkalarkiClient`);
  nothing above the seam would change.
- The `Event` enum name is the control name; the SDK's `EventMapper` is internal and is not
  used.
- Still to verify on hardware (owner): live input events reach the app; a second subscriber
  does not disturb the Profiler's ProSim profile; LAMP TEST lights the right boards; HW
  RESET re-enumerates a board.
