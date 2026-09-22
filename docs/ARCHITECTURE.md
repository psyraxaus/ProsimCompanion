# Architecture

## Shape

One process, one DI container, three surfaces:

```
┌─────────────────────────────────────────────────────────────┐
│ ProsimCompanion.App (WPF WinExe)                            │
│                                                             │
│  [STAThread] Main                                           │
│    ├─ builds WebApplication (Kestrel + Blazor Server)       │
│    │    └─ single IServiceProvider for the whole app        │
│    ├─ starts web host (background)                          │
│    └─ runs WPF shell (status window; later: tray icon)      │
│                                                             │
│  Browser (any LAN device) ── Blazor circuits ──┐            │
│  WPF shell ────────────────────────────────────┤            │
│                                                ▼            │
│                 Core state stores (observable, UI-agnostic) │
│                                                ▲            │
│   Prosim (SDK+gateway)   Sim (SimConnect)   Gsx (RemoteAPI) │
└─────────────────────────────────────────────────────────────┘
```

- The WPF window is deliberately dumb: connection indicators, the web UI URL + QR, open-browser
  button, and web-server settings (port/bind/token) so a bad web config can never lock the user out.
- Everything else renders in Blazor. State reaches the UI through observable state stores; Blazor
  components subscribe and re-render. The same stores can later feed REST/WebSocket/StreamDeck
  surfaces via the command registry.

## Project dependency rules

```
App ──► Web, Core, Prosim, Sim, Gsx     (composition root; the only project that sees everything)
Web ──► Core                            (UI binds to stores/options; no direct hardware access)
Prosim, Sim, Gsx ──► Core               (publish into stores; expose narrow interfaces)
Core ──► (nothing)
```

Feature projects never reference each other. When GSX automation needs a ProSim write (e.g. step the
fuel dataref during refuel), it calls a Core-defined interface (`IAircraftGroundOps` style) that
`ProsimCompanion.Prosim` implements. This is what dissolves the old Prosim2GSX↔ProsimInterface
circular dependency.

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
| **5320** | ProsimCompanion web UI (default; configurable) |
| 8744 | GSX Couatl Remote API (from `CouatlAddons.ini`, not configurable in-app by design) |

## Decisions

See `docs/decisions/` (ADRs). Summary: web-first UI (0001), Blazor (0002), Generic Host without CFIT
(0003), GSX pillar first (0004).
