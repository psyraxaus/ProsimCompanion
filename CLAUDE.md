# ProsimCompanion

A single Windows companion application for the ProSim A320 (A322) in MSFS 2020/2024, consolidating three
predecessor projects into one codebase:

- **Prosim2GSX** — GSX Pro ground-services automation, loadsheets, audio control, web EFB
- **ProsimInterface** — ProSim connectivity library (SDK + gateway), W&B/ACARS pipeline
- **Prosim2FO** — voice First Officer (checklists, callouts, briefings, speech, immersion)

The predecessors live as siblings at `C:\Users\johncarlo\git\{Prosim2GSX,ProsimInterface,Prosim2FO}`.
**Consult them for behaviour and hard-won integration knowledge, but do not copy code** — this is a clean
rewrite. Distilled integration knowledge lives in `docs/integrations/` and is the first place to look
before re-deriving any protocol, dataref, or LVAR detail.

## Locked architectural decisions (see docs/decisions/)

1. **Web-first UI** (ADR-0001): the WPF app is a minimal shell (status + link/QR to the web UI).
   All configuration and feature UI is browser-based, served by embedded Kestrel. Never build a
   settings screen in WPF; the only WPF-resident settings are the web server bind/port/token
   (lockout prevention).
2. **Blazor** (ADR-0002): the web UI is Blazor (interactive server), C# end-to-end. No Node toolchain.
3. **.NET Generic Host, no CFIT** (ADR-0003): standard `Microsoft.Extensions.*` DI/Options/Hosting.
   The SimConnect/LVAR layer is written in-repo (`ProsimCompanion.Sim`) against the raw SimConnect SDK —
   no CFIT.* packages, no local package feeds.
4. **GSX ground automation is the first feature pillar** (ADR-0004): see `docs/ROADMAP.md` for phases.

## Solution layout

```
src/ProsimCompanion.App      WPF shell + composition root; hosts Kestrel + Blazor
src/ProsimCompanion.Core     Domain: config, flight phases, state stores. No UI, no I/O frameworks.
src/ProsimCompanion.Prosim   ProSim access: SDK (push datarefs) + EFB gateway (GraphQL/REST :5000)
src/ProsimCompanion.Sim      SimConnect + LVAR access (clean-room, raw SimConnect SDK)
src/ProsimCompanion.Gsx      GSX Couatl Remote API v2 client + ground-ops automation
src/ProsimCompanion.Web      Blazor UI (Razor class library) — pages, layouts, components
tests/ProsimCompanion.Core.Tests   xunit + Moq
docs/                        Roadmap, architecture, ADRs, integration references
```

Dependencies point inward: App → everything; Web → Core; Prosim/Sim/Gsx → Core; Core → nothing.
Feature projects never reference each other — cross-feature communication goes through Core
abstractions/state stores.

## Build & test

```
dotnet build ProsimCompanion.slnx
dotnet test ProsimCompanion.slnx
dotnet run --project src/ProsimCompanion.App
```

Requires .NET 10 SDK on Windows. `ProSimSDK.dll` is **never** referenced at build time and never
redistributed — it is loaded at runtime from the user's ProSim install (default
`C:\prosim\prosim-system`), resolved via `SetDllDirectory` + assembly resolver. The app must start and
remain usable (degraded mode) when the SDK, ProSim, MSFS, or GSX are absent.

## Conventions

- **Logging**: inject `Microsoft.Extensions.Logging.ILogger<T>`; Serilog is the sink, configured only
  in the composition root. Structured placeholders, never interpolation.
- **DI**: constructor injection, `ArgumentNullException.ThrowIfNull` for required collaborators,
  narrow `I`-prefixed interfaces. Per-project `IServiceCollection` extension methods
  (`AddProsimServices()` etc.) keep the composition root thin — no god-object composition root.
- **Async**: no `async void` outside event handlers (catch inside those); never block on async on a
  UI/dispatcher thread; `ConfigureAwait(false)` in library code; pass `CancellationToken` down.
- **Options**: per-feature options classes bound from `config/settings.json` — no single mega-settings
  class. Every property has a safe default so partial config files work. camelCase JSON.
- **Degrade, not fail**: every external dependency (ProSim, SimConnect, GSX, network services) can be
  absent. A failed subsystem logs and disables itself; it never blocks startup or other features.
- **Write safety**: anything that writes to the aircraft goes through explicit code-level allow-lists.
  Dataref-first; never actuate cockpit switches when a dataref write achieves the same result.
- **ProSim read model**: register a `DataRef` subscription once and read the cached value. Never poll
  `ReadDataRef` in a loop — it is a synchronous network round-trip and stalls the app.
- Tests: xunit + Moq only (no MSTest/NUnit/FluentAssertions). Favour extracting pure
  `ProcessTick`-style cores from timer-driven services so they are testable without timers.
- XML doc comments on public API; explain *why*, not what the signature says. Preserve dated
  "archaeology" comments for empirically-discovered quirks (see integration docs).

## Key external ports/paths (full details in docs/integrations/)

| Thing | Where |
|---|---|
| ProSim EFB gateway (GraphQL/REST) | `http://{prosimHost}:5000` |
| GSX Couatl Remote API v2 | `ws://127.0.0.1:{port}`, port from `%APPDATA%\Virtuali\CouatlAddons.ini` (default 8744) |
| ProsimCompanion web UI | `http://localhost:5320` (default; avoids 5000/5001/8730 used by neighbours) |
| User config | `config/settings.json` beside the exe |
| Logs | `%LOCALAPPDATA%\ProsimCompanion\logs\` |
