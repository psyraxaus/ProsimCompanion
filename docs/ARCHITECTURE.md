# Architecture

## Shape

One process, one DI container, three surfaces:

```
┌──────────────────────────────────────────────────────────────────────────┐
│ ProsimCompanion.App (WPF WinExe — the composition root)                  │
│                                                                          │
│  [STAThread] Main                                                        │
│    ├─ builds WebApplication (Kestrel HTTP 5320 + optional HTTPS 5321,    │
│    │    Blazor Server) — single IServiceProvider for the whole app       │
│    ├─ starts the web host + 26 hosted services (background)              │
│    └─ runs the WPF shell (status window, tray icon, web-server settings) │
│                                                                          │
│  Browser (any LAN device) ── Blazor circuits ──┐                         │
│  Stream Deck / scripts ── /api/* ──────────────┤                         │
│  WPF shell ────────────────────────────────────┤                         │
│                                                ▼                         │
│              Core state stores (observable, UI-agnostic) + options       │
│                                                ▲                         │
│  Prosim (SDK+gateway) · Sim (SimConnect, vJoy) · Gsx (Remote API)        │
│  Speech (voice FO) · Audio (CoreAudio, VoiceMeeter, mixer agents)        │
│  Skalarki (+ Skalarki.Sdk bridge) · Analyzers (build-time only)          │
└──────────────────────────────────────────────────────────────────────────┘
```

### Projects

| Project | Role | Size (2026-11-03) |
|---|---|---|
| `ProsimCompanion.Core` | stores, options, flight-state engine, event log, pure cores; depends on nothing | 411 files, 54 k lines |
| `ProsimCompanion.Speech` | the voice First Officer: recognition, routing, checklists, callouts, briefings, abnormals, scenarios, cabin crew, persona, SayIntentions radios, duty day, arbiter + TTS | 256 files, 55 k lines |
| `ProsimCompanion.Web` | Blazor interactive-server UI, language packs, static assets | 124 files, 25 k lines |
| `ProsimCompanion.Gsx` | GSX Pro ground automation over the Couatl Remote API and LVARs | 89 files, 20 k lines |
| `ProsimCompanion.Prosim` | ProSim SDK (runtime-loaded) + EFB gateway, dataref subscriptions, loadsheet/ACARS, failure injection | 28 files, 6 k lines |
| `ProsimCompanion.Audio` | CoreAudio session control, local VoiceMeeter, remote mixer agents (VoicemeeterBridge) | 25 files, 4.5 k lines |
| `ProsimCompanion.App` | WPF shell, composition root, minimal-API endpoints, Serilog sink, tray | 29 files, 3.6 k lines |
| `ProsimCompanion.Sim` | SimConnect wrapper, LVAR read/write gate, vJoy relay, FOV nudge | 13 files, 2 k lines |
| `ProsimCompanion.Skalarki` | Skalarki PANELS page backend (ADR-0017), SDK server client on port 53000 | 7 files, 1.3 k lines |
| `ProsimCompanion.Skalarki.Sdk` | the one project that references the Skalarki SDK assembly, loaded in its own context (ADR-0017) | 1 file |
| `ProsimCompanion.Analyzers` | Roslyn analyzer PCGSX001: `service.trigger` may only be built by the trigger slot | 1 file |
| `tools/ProsimCompanion.Reduce` | reduces a diagnostics bundle / session recording against the probe catalog | 9 files, 1.9 k lines |
| `tests/ProsimCompanion.Core.Tests` | xunit + Moq tests for every project above | 433 files, 75 k lines |

A full read of the codebase — pathway maps per pillar, redundancy families, the state-store
table and the improvement plan — is `docs/reviews/2026-11-03/README.md`.

- The WPF window is deliberately dumb: connection indicators, the web UI URL + QR, open-browser
  button, and web-server settings (port/bind/token) so a bad web config can never lock the user out.
- Everything else renders in Blazor. State reaches the UI through observable state stores; Blazor
  components subscribe and re-render. The same stores can later feed REST/WebSocket/StreamDeck
  surfaces via the command registry.

### Diagnostics pipeline (Core/Diagnostics/Ecam, ADR-0016)

The ECAM-style diagnostics pages are the one place every pillar reports its health in the
same vocabulary. Each feature project registers `IDiagnosticsCollector`s (Speech → VOICE;
Prosim, Sim and the GSX source box → SIM; Audio and Web → REMOTE; Gsx → GSX). Core's
`DiagnosticsSnapshotService` ticks at 1 Hz, runs every collector in its own try/catch (a
failure costs that collector's nodes, which draw as amber `XX`, never the page), merges the
pages and folds the cautions into one immutable `DiagnosticsSnapshot` in `DiagnosticsStore`.
Collectors read the existing stores plus two Core registries the hot paths write into:
`LatencyRegistry` (ASR, LLM first token / total, TTS, end-to-end) and `RateRegistry`
(datarefs, simvars, API). The Web project draws the snapshot and never computes health; the
same snapshot is `GET /api/diagnostics/snapshot` and travels in the support bundle.

## Project dependency rules

```
App ──► Web, Core, Prosim, Sim, Gsx, Speech, Audio, Skalarki, Skalarki.Sdk
                                        (composition root; the only project that sees everything)
Web ──► Core                            (UI binds to stores/options; no direct hardware access)
Prosim, Sim, Gsx, Speech, Audio, Skalarki ──► Core
                                        (publish into stores; expose narrow interfaces)
Core ──► (nothing)
```

Feature projects never reference each other. When GSX automation needs a ProSim write (e.g. step the
fuel dataref during refuel), it calls a Core-defined interface (`IAircraftGroundOps` style) that
`ProsimCompanion.Prosim` implements. This is what dissolves the old Prosim2GSX↔ProsimInterface
circular dependency.

Two deliberate exceptions, both one-directional and both documented where they live:

- `Skalarki.Sdk ──► Skalarki` (ADR-0017): the SDK-facing assembly is loaded into its own
  `AssemblyLoadContext` by the Skalarki project and implements that project's bridge interface;
  nothing references `Skalarki.Sdk` at compile time except App (copy-only).
- `Gsx ──► Analyzers` as an analyzer reference (build-time only, `PCGSX001`): the trigger-slot
  rule is enforced where `service.trigger` could be built. Nothing from Analyzers ships.

A test (`ServiceCompositionTests`) builds the real container with `ValidateOnBuild` so a missing
registration fails in CI, not at the user's first click.

## Threading model

- **UI thread (STA)**: WPF only. Nothing else may touch it; no dispatcher calls from teardown paths.
- **Blazor circuits**: per-circuit sync context managed by ASP.NET Core; components marshal store
  events via `InvokeAsync(StateHasChanged)`.
- **Pollers**: `System.Threading.Timer`-driven ticks (flight state 250 ms, automation tick 500 ms,
  callout-critical 100 ms) with per-service try/catch and overlap guards (`Interlocked` reentrancy
  flags). Extract pure `ProcessTick(state) -> decisions` cores so logic is testable without timers.
- **Callbacks**: ProSim SDK and SimConnect callbacks arrive on their own threads. They write into
  concurrent caches / raise events only; consumers marshal as needed. Events documented as firing on
  arbitrary threads.
- **Presses/actuations**: single-reader `Channel<T>` workers serialize momentary presses (write 1 →
  hold → write 0 → gap) so concurrent features can never interleave switch writes.
- **Shutdown**: bounded per-component teardown with watchdog; neutralize flight controls before
  ProSim disconnect; the ProSim SDK owns an unjoinable foreground thread, so the process ends with
  `Environment.Exit` after orderly teardown.

## Configuration

- `config/settings.json` beside the exe; camelCase; per-feature options classes registered via
  `IOptionsMonitor<T>`; every property has a safe default so partial files work.
- Secrets (`prosim.apiKey`, `sayIntentions.manualApiKey`, `briefing.llmApiKey`,
  `webUi.accessToken`) are stored DPAPI-protected (CurrentUser scope) as `dpapi:<base64>`.
  `SecretProtector` (Core) owns the path list and the protect/unprotect primitives;
  `JsonSettingsFile.Update` protects on every write and a startup pass upgrades older files;
  `ProtectedJsonConfigurationProvider` (App) decrypts on load so options bind plain values. A
  value that fails to decrypt (file copied to another PC/user) binds as empty, is logged once,
  and is listed in `SecretProtector.Unreadable` for the settings pages' re-enter hint. DPAPI
  output is non-deterministic: never compare stored strings, only decrypted option values.
- `configVersion` stamp + stepwise `SettingsMigrator` (runs at startup, before binding). Additive
  settings never need migration; the ladder exists for breaking changes only (renames,
  restructures) — the model that served Prosim2GSX through 33 config versions.
- External component locations (ProSimSDK.dll, Virtuali directory, VoiceMeeter) are captured by
  the installer or the web Settings page and stored in config. The app never assumes install
  paths; an unset path degrades that subsystem with guidance in the log/UI.
- Hot reload: file watcher with ~300 ms debounce; saves are debounced (~750 ms) and flushed on exit.
- **Every setting gets a web-UI control** (owner decision 2026-08-14): a new options property
  ships WITH its field on the matching settings page in the same change — settings.json-only
  knobs are not acceptable (exception: power-user dictionary/list structures may stay
  JSON-edited, but must be mentioned in a visible hint on the page).
- Aircraft profiles carry per-aircraft feature settings, matched on aircraft title/airline.
- Content packs (checklists, SOPs, abnormals, themes) are separate user-editable JSON directories,
  hot-reloaded.

## Resilience rules (non-negotiable)

1. **Degraded mode**: missing ProSim SDK/MSFS/GSX/network never blocks startup. Each subsystem starts
   in a named try/caught step; failure disables that feature only.
2. **Read model**: ProSim datarefs are push-subscribed once and read from cache. `ReadDataRef` in a
   loop is forbidden (synchronous network round-trip — stalls the app).
3. **Write gating**: aircraft writes go through explicit allow-lists; dataref-first over switch
   actuation; risky actuations (MCDU mutations) require armed + announced + verified + abortable.
4. **Safe-fail menus**: GSX menu interaction must verify outcomes and prefer "leave the menu open for
   the user" over a wrong click.
5. **Never redistribute**: `ProSimSDK.dll` (runtime-loaded from the user's install) and
   `VoicemeeterRemote64.dll` (runtime-loaded) never ship in output or installer.

## Ports & neighbours

| Port | Owner |
|---|---|
| 5000 | ProSim EFB gateway (theirs — why we don't use it) |
| 5001 | legacy Prosim2GSX web EFB (avoid clashes while both run) |
| 8730 | legacy Prosim2FO dashboard |
| **5320** | ProsimCompanion web UI, HTTP (default; configurable) |
| **5321** | ProsimCompanion web UI, optional HTTPS listener (ADR-0013; off by default) |
| 8744 | GSX Couatl Remote API (from `CouatlAddons.ini`, not configurable in-app by design) |
| 53000 | SkalarkiIO Profiler SDK server (theirs; the PANELS page connects to it, ADR-0017) |
| 5088 | VoicemeeterBridge mixer agent on a remote PC (theirs; `mixer.agents[].port`) |

## Decisions

See `docs/decisions/` (ADRs, this repo's convention — not `docs/adr/`):

| ADR | Decision |
|---|---|
| 0001 | Web-first UI with a minimal WPF shell |
| 0002 | Blazor (interactive server) for the web UI |
| 0003 | .NET Generic Host + Microsoft.Extensions DI; no CFIT packages |
| 0004 | GSX ground automation is the first feature pillar |
| 0005 | Drop the FS2Crew bridge — superseded by the built-in voice FO |
| 0006 | Voice-gated ground services ride the command path; crew speech rides the arbiter roles |
| 0007 | User-editable config lives in `%LOCALAPPDATA%\ProsimCompanion\config` |
| 0008 | Flight phase is an ordered rule table, tuned by replaying recorded flights |
| 0009 | Silero VAD replaces the RMS energy gate |
| 0010 | One settings hub with progressive disclosure |
| 0011 | Superdesign "KLM navy instrument panel" restyle |
| 0012 | Departure is a real flight phase, entered on boarding evidence |
| 0013 | Optional HTTPS listener beside HTTP |
| 0014 | UI localisation through JSON language packs (en, de, es, fr, ja, ko, nl) |
| 0015 | Cockpit switches from the Stream Deck through a configured-row write gate |
| 0016 | ECAM-style diagnostics pages |
| 0017 | Skalarki SDK isolation (PANELS diagnostics page) |
| 0018 | Scheduled non-normals through the ProSim SDK failure API behind its own gate |
| 0019 | The duty day is a sequence of legs; a leg boundary is three pilot-visible steps |
