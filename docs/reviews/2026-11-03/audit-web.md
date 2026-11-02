# Audit: src/ProsimCompanion.Web (read-only, 2026-11-03)

Scope: `src/ProsimCompanion.Web` — 172 files on disk, 31,905 lines across .razor/.cs/.css/.js/.json
(app.css alone 2,228). Host wiring (Kestrel, middleware, minimal APIs) lives in
`src/ProsimCompanion.App/Program.cs` + `App/Hosting/*` and is mapped here because the Web project cannot
be understood without it. Governing docs read: CLAUDE.md, ADR-0001/0002/0010/0011/0013/0014,
docs/agents/localisation.md. Nothing was edited, built or committed.

---

## A. PATHWAY MAP

### A1. Request routing (App/Program.cs lines 329–419)

```
Kestrel  ── ListenLocalhost|ListenAnyIP(http 5320) [+ https 5321 when WebListenerPlan says so, ADR-0013]
  │
  ├─ LanTokenMiddleware            (App/Hosting/LanTokenMiddleware.cs)
  │     loopback → pass; GET of install assets (/manifest.webmanifest, /favicon.svg,
  │     /apple-touch-icon*.png, /icons/*.png) → pass; else cookie "prosimcompanion-token"
  │     | Authorization: Bearer | ?token= (mints cookie, 30 d, HttpOnly, Lax, redirect stripping
  │     only the token) → pass; else 401 (403 if no token configured). Fixed-time compares.
  ├─ ApiClientTrackingMiddleware   (Diagnostics REMOTE page; runs after the gate on purpose)
  ├─ UseStaticFiles()              (wwwroot incl. _framework/blazor.web.js copied at build)
  ├─ UseAntiforgery()
  ├─ minimal-API groups (all in App/Hosting, each `Map*Api()` extension):
  │     /api/commands (GET list), /api/command/{name} (POST)   CommandApiEndpoints  — opt-in (Enabled=false), 404 while off,
  │                                                              ApiTokenAuth with RequireTokenOnLoopback=true (default)
  │     /api/status (GET)                                        StatusApiEndpoints   — same commandApi gate
  │     /api/controls (GET)                                      ControlsApiEndpoints — same commandApi gate
  │     /api/telemetry/{summary,sessions,sessions/{name},logs,logs/{name}} TelemetryApiEndpoints — on by default,
  │                                                              RequireTokenOnLoopback=false (read-only)
  │     /api/debug/damage (GET)                                  SimDamageEndpoints   — rides telemetry gate
  │     /api/diagnostics/{bundle,snapshot} (GET)                 DiagnosticsEndpoints — rides telemetry gate; settings.json
  │                                                              passes SettingsRedactor before zipping
  │     /api/logbook/export (GET CSV)                            LogbookApiEndpoints  — ApiTokenAuth, loopback free
  │     /api/theme-logo/{slug}, /api/airline-logo/{code} (GET)   ThemeLogoEndpoints   — ApiTokenAuth, loopback free
  │     /api/app/boot (GET)                                      AppBootEndpoints     — no per-endpoint auth (middleware only)
  │     /api/gsxmenu/events (GET, side-effecting), /api/gsxmenu/flight-info (GET)
  │                                                              GsxHandlerEndpoints  — no per-endpoint auth; always on;
  │                                                              loopback-only consumer (gsx_handler.py); event names allow-listed
  └─ MapRazorComponents<Web.App>().AddInteractiveServerRenderMode()
        App.razor (static render: reads HttpContext once → IsLocalClient, Accept-Language; emits 13 <link> stylesheets +
        app.js/ecam.js with ?v=AssetVersion) → Routes.razor (Router, DefaultLayout=MainLayout, cascades ClientContext,
        seeds BrowserLanguageContext) → 36 `@page` routes. No catch-all route (memory rule honoured). No <NotFound>
        branch: an unknown URL renders an empty frame.
```

Layouts: `MainLayout` (StoreObserverLayout; tab strip, header pills, footer, sim clock timer),
`SettingsLayout` (nested; rail `SettingsNav`, search `SettingsSearch`, `FieldValidity` cascade, `SectionFoldStore`),
`MonitorLayout` (bare, for `/monitor`). `/efb` is one page: `EfbInit.razor` at `@page "/init"`.

### A2. Settings write path

```
<OptionField Draft="_gsx" Property="o => o.Doors.AutoOpen" />        (Components/OptionField.razor)
  → OptionFieldTexts.Resolve(description) picks {textId}.label/.hint/.more/.placeholder/.tally/.empty from the pack
  → BoolField/NumberField/RangeField/TextField/SelectField/VoicePickerField render; TextField/NumberField run
    FieldValidation and report to the cascaded FieldValidity (Validity.Report(name, blocking))
  → OptionField.WriteAsync → OptionsDraft<T>.Set(...)  (Core/Configuration/OptionsDraft.cs; the page's editable clone)
  → draft.Edited → SettingsPageBase.StateHasChanged (DirtyBar shows; Save grey while Validity.BlockingCount > 0)
SettingsPageBase.Save()   (Components/SettingsPageBase.cs:69)
  → draft.Normalize() per draft → page.OnBeforeSave() (e.g. SpeechPageRules.BeforeSave, AudioPageRules.BeforeSave)
  → SettingsWriter.Save(drafts)  (Core/Configuration/SettingsWriter.cs:52) — diff of dirty properties only
  → JsonSettingsFile.Update(mutate) (Core/Configuration/JsonSettingsFile.cs:41) — read → mutate JsonObject →
    write {path}.tmp → File.Move(overwrite) (atomic); SecretProtector handles DPAPI fields
  → config/settings.json is an AddJsonFile(reloadOnChange: true) source (Program.cs:318)
  → IOptionsMonitor<T>.CurrentValue refreshes; consumers either read CurrentValue per tick or OnChange()
  → page.OnAfterSave() (e.g. GsxSettings mirrors the gsx block into the active aircraft profile)
No-dirty-bar writes (Logs page level pickers, OFP Korry buttons) go through SettingsWriter.Set<T,V>(expr, value).
```

### A3. Live-update path (store → page)

```
Core SnapshotStore<T>.Set(...)  (writer thread)
  → store.Observe(callback)  registered by WatchSet.Watch (Components/WatchSet.cs)
  → callback: _ = InvokeAsync(() => { apply(snapshot); StateHasChanged(); })   (circuit marshalling)
  → unsubscribed by WatchSet.Dispose() in reverse order
Three entry points: StoreObserverComponent (pages/components), StoreObserverLayout (layouts), SettingsPageBase
(extends StoreObserverComponent). Non-SnapshotStore sources use WatchChanged(subscribe, unsubscribe, refresh)
in EventHandler / Action / EventHandler<T> flavours.
```

Does every page unsubscribe on dispose? **Yes for everything routed through WatchSet** (62 of 81 .razor files use
the base classes). Hand-rolled subscriptions outside WatchSet, all verified to be released in `Dispose()`:
`IOptionsMonitor.OnChange` in ThemeStyle:26, MainLayout:371, AppearanceSettings:350, Home:859, Profiles:121,
SpeechSettings:1382; `+=` in MixerStatusPanel:137, AudioSettings:792, SettingsLayout:171/175, SpeechSettings:1380.
One leak: **`Home.razor` `_tuneNoteTimer` is never disposed** (Dispose at 875–884 omits it) and a stray,
mis-indented `_tuneNoteTimer?.Dispose();` sits inside `ConfirmFuel()` at line 650 — the wrong timer in the wrong
method. Logs.razor (ComponentBase + IDisposable, 500 ms poll — by design: `LogBufferStore` has no fan-out) disposes
its timer. Fire-and-forget JS calls in `AppearanceSettings.Dispose` and `Diagnostics.Dispose` are try-guarded.

---

## B. REDUNDANCY

| # | What is copied | Evidence | Proposed shared piece | Lines removable |
|---|---|---|---|---|
| B1 | Airport/runway/METAR/ICAO-commit/reset-arm block | Landing.razor 377–640, 798–812 vs Takeoff.razor 394–619, 791–805: 21 shared private methods (`LoadAirportAsync`, `LoadRunwaysAsync`, `FetchWxAsync`, `RefreshWxAsync`, `ApplyWx`, `OnIcaoCommitAsync`, `ReseedIcaoAsync`, `SeedFromContext`, `ContextIcao`, `ValidIcao`, `ParseFeet`, `RunwayQdm`, `WindVerdict`, `SelectedRunway`, `OnResetClick`, `DoReset`, `OutFigure`, `Toggle`…); 137 identical non-trivial lines | `PerfPageBase : StoreObserverComponent` (or a scoped `PerfAirportContext` service) holding ICAO/runways/wx state + the shared handlers; `ParseFeet`/`RunwayQdm`/`WindVerdict` to a Core `PerfRunwayMath` with tests; `OutFigure` into `PerfRunwayCard` | ~280 |
| B2 | Row-list editor tables (`cell-input`/`cell-select`, `invalid` class, add/remove/move handlers) | AudioSettings 72–82, 156–180, 349–378 (3 tables, 8 handlers); CockpitSettings 56–130 (14 inputs); DisplaySettings 45–59; GsxSettings 152–170, 274, 297 (6 handlers); SetupSettings 213–215 (4 handlers); NotificationSettings 56–65. Matching header CSS block repeated 5× (`.audio-vm-header`, `.mixer-map-header`, `.vjoy-view-header`, `.cockpit-row-header`, `.display-row-header` — app.css 1590/1610/2159/2179/2215, byte-identical bodies) | `RowListEditor<TRow>` (header, add/remove/move, empty hint) + `CellText`/`CellNumber`/`CellSelect` that call `FieldValidity.Report` | ~200 + bug fix (see F/G) |
| B3 | Transient note / armed action timers | Home 540 (`_tuneNoteTimer`), 651 (`_confirmFuelNoteTimer`); EfbInit 404, 423, 478, 559, 582; SetupSettings 577; Logbook 327; Landing 340 & Takeoff 345 (`_resetArmTimer`); SettingsPageBase 99 — 11 copies of "dispose old timer, new Timer(InvokeAsync(clear; StateHasChanged))" | `TransientNote` / `ArmedAction` helper owned by WatchSet (auto-disposed) | ~120 |
| B4 | `IOptionsMonitor.OnChange(... InvokeAsync(StateHasChanged))` + repaint timers | 6 OnChange copies (A3 list); 1–2 s repaint timers in Fuel 226, FuelLog 234, WeightBalance 356, Monitor 557, MainLayout 365 | `WatchSet.WatchOptions<T>(IOptionsMonitor<T>)` and `WatchSet.RepaintEvery(TimeSpan)` | ~60 and six fewer `Dispose` overrides |
| B5 | Weather card rendered twice | Home.razor 665–711 (`WeatherCardMarkup(WeatherCardView)`) vs Monitor.razor 481–520 (`WeatherCardView(WeatherCard)` re-deriving from Core `WeatherCardFormat`, bypassing `FlightStatusPresentation.Weather`) | `<WeatherCardTile View=… Variant=Home|Monitor />` consuming the tested view record | ~60 |
| B6 | Mixer state label/tone switches | MixerStatusPanel.razor 230/238 (`StateLabel`/`StateTone`) vs AudioSettings.razor 810/822 (`MixerStateLabel`/`MixerStateTone`) | move both into existing `Components/AudioPresentation.cs` (44 lines, currently untested) | ~25 |
| B7 | `ConfirmFuel` action | Home 642–664 vs EfbInit 470–489 (same `ServiceControl.TryCallAsync(GsxServiceAction.ConfirmFuel)` + 8 s note) | one `EfbActions` method returning the note; page just binds | ~20 |
| B8 | Page-specific CSS living in app.css beside 12 page stylesheets | app.css families: `.vr-` 48 rules (Voice Reference drawer), `.korry-` 25, `.mixer-` 14, `.cockpit-` 11, `.display-` 10, `.audio-` 7, `.setup-` 7, OFP block 1871–2046 (176 lines) ≈ 544 lines | move to `css/pages/*.css` (or component-scoped) so app.css is tokens + chrome only | 0 net, −544 from app.css |
| B9 | Small-caps header utility | `{ font-size: 11px; text-transform: uppercase; letter-spacing: .06em; color: var(--text-muted); border-bottom: 1px solid var(--border) }` ×5 and `{ display:flex; flex-direction:column; gap:Npx; min-width:0 }` ×15 across files | `.caps-header`, `.stack-{2..6}` utilities | ~60 |

Not duplicated (checked): JS interop helpers (16 distinct `prosimCompanion.*` functions, each called from one site;
`wakeLock.describe` ×4 in one page); localisation helper pattern (one `UiText`, `T[...]` everywhere, 2,158 distinct
ids); Home vs Monitor code (3 shared method names, both on separate presentation classes); cross-file CSS selector
collisions (only `.side-item`, `.ecam-btn` — diagnostics.css overrides, intentional).

---

## C. BLOAT (files > 500 lines)

| File | Lines (markup / code) | Natural split | Pure logic to lift into a testable class |
|---|---|---|---|
| Pages/SpeechSettings.razor | 1406 (972 / 433) | Markup is 17 `else if (CurrentSection == …)` branches over 56 `<Section>`s → one child component per rail section (`Speech/PersonaSection.razor`, `ListeningSection`, `VoicesSection`, `GroundCrewSection`, `CabinSection`, `CompanySection`, `SayIntentionsSection`, `ProvidersSection` (its own sub-tab strip, 571–690), `BriefingSection`, `CalloutsSection`, `AbnormalsSection`, `ScenarioSection`) receiving the drafts as parameters; shell keeps route + drafts. @code has four unrelated concerns: roster pin/draw (200–255), provider voice listing (54–155), PTT binding + joystick scan (256–378), device option lists | `PttBindingEditor` component (binding text, scan, clear); `RosterPresentation` (RosterHint/AxisText/pinned messages); voice listing already in `SpeechPageRules` — keep going |
| Pages/AudioSettings.razor | 966 (442 / 523) | Three sub-domains in one file: CoreAudio mappings + blacklist (37–122), VoiceMeeter ACP target tables (123–230), Mixer agents + mappings (231–409) → `CoreAudioMappingsCard`, `VoiceMeeterTargetsCard`, `MixerAgentsCard`, `MixerMappingsTable` | `TargetKey`/`SetTarget`/`ChannelOptions`/`AddGenericTargets`/`MixerTargetLabel` (604–676, 910–949) → `AudioPageRules` (exists, 227 lines, tested); state label/tone → `AudioPresentation` |
| Pages/Home.razor | 885 (426 / 458), **29 `@inject`s** | `WeatherCardMarkup` (665), `GateStripMarkup` (712), `RowView` (760) RenderFragments → `WeatherCardTile`, `GateStripTile`, `StatusRowView` components; tile order/edit-layout/drag (471–519, JS tileDrag) → `FlightStatusTileLayout` code-behind; radio tune + confirm-fuel actions → `FlightStatusActions` scoped service | `MetricClass`, `Tooltip`, text resolution already thin; the 29-dependency fan-in itself is the smell — a `FlightStatusViewModelSource` aggregating the 17 stores into one snapshot would drop Home to ~5 injects |
| Pages/Landing.razor | 861 (242 / 618) | see B1; result card already `PerfRunwayCard` | `ParseFeet`, `RunwayQdm`, `WindVerdict`, displaced-threshold/LDA maths (680–760) → Core `PerfRunwayMath` |
| Pages/Takeoff.razor | 852 (262 / 589) | see B1; `ThsDisplay`, `ConfLabel`, flex/TOGA formatting → `TakeoffPresentation` | same |
| Components/FlightStatusPresentation.cs | 811 | Regions are already banners: Hero 224–301, Hero widgets 301–396, Sequence 396–434, Sim/App/GSX/Services rows 434–745, Radios 745–811 → four static partial files + `FlightStatusViews.cs` for the 11 records (13–140) | already pure and tested (3 test files); split is for navigability only |
| Pages/EfbInit.razor | 659 (163 / 495) | Inline-edit state machine (`FieldRange`, `StartEdit`/`CancelEdit`/`CommitEdit`/`OnEditKeyDown`/`FlashEditError`, 168–415) → `InlineNumberEdit` component; actions (`FetchOfp`/`SyncFms`/`ConfirmFuel`/`ClearOverrides`/`ResetFlight`/`UnloadOfp`, 416–590) share B3/B7 | `FormatKg`/`FormatPax`/`FormatZulu`/`Dash`/`Effective`/`Status` (591–659) → `EfbInitPresentation` with tests; `PendingBoxes` builds `MarkupString` from a count only (safe) |
| Pages/GsxSettings.razor | 603 (362 / 240) | 92 `<OptionField>` in 22 Sections — already the shape ADR-0010 wants; the service-sequence table (152–170) and two string-list editors (274, 297) are B2 instances | profile-mirroring in `OnAfterSave` → `GsxPageRules` (exists, 57 lines) |
| Pages/Monitor.razor | 595 (251 / 343) | B5 weather card; `Row`/`Door`/`Service`/`Lamp` fragments → components | `Tick`/sim-clock duplicates MainLayout.EvaluateSimClock (376) |
| Pages/SetupSettings.razor | 589 (299 / 289) | input-relay row editor is B2 | `OnBeforeSave` list pruning (455–525) → `SetupPageRules` (exists, 49 lines) |
| Pages/WeightBalance.razor | 505 | fine; 1 s repaint → B4 | — |
| wwwroot/css/app.css | 2228 | see B8 | — |

---

## D. LOCALISATION HEALTH

Measured over `src/ProsimCompanion.App/config/lang/{code}/*.json` (35 files per language) with a Node script
(`scratchpad/langaudit.js`, `unused2.js`).

| Check | Result |
|---|---|
| Ids per pack | **3,921 in every pack** (en, de, es, fr, ja, ko, nl); 35 files each |
| en ids missing in another pack / extra ids / missing files | **0 / 0 / 0** |
| Placeholder-set mismatches | **0** |
| Ids asked via `T["…"]` absent from en | **0** (2,158 distinct literal ids used) |
| Pack ids unused by any .razor/.cs | **0** after resolving OptionField convention suffixes (`.label/.hint/.more/.placeholder/.tally/.empty`) and dynamic prefixes. (A naive literal grep reports 1,097 — all convention-resolved; `OptionFieldCompletenessTests` is the guard.) |
| Hard-coded English in .razor markup (attribute/inline heuristic, outside `@code`) | **7 attribute hits, 0 inline** — all legitimate product/dataref names: AudioSettings 39 `Title="CoreAudio"`, 125 `Title="VoiceMeeter"`; CockpitSettings 106/109 `placeholder="system.indicators.I_…"`; Monitor 244/245 `title="ProSim"/"SimConnect"`; SetupSettings 213 `placeholder="wing"` |
| **Texts byte-identical to English in ALL six packs** (≥12 letters, not all-caps) | **94 ids** — speechSettings.json 66, voiceReference.json 6, shell.json 5, flightPhase.json 4, fields 2, monitor 2, setup 2, one each in day/diagnostics/gsxSettings/init/ofp/speechStatus/techLog |
| Latin-only sentences (≥4 words) in ja / ko | **66 / 67** |
| Per-pack identical-to-en counts | de 105, es 99, fr 103, ja 95, ko 95, nl 114 |

Top untranslated examples (same English string in de/es/fr/ja/ko/nl): `fields.section.collapse` "Collapse this
card", `fields.section.expand`, `settings.search.placeholder` "Search settings…", `settings.search.hint`,
`settings.search.noResults` "Nothing matches", `monitor.heading` "Flight Monitor", `ofp.heading` "Operational
Flight Plan", `day.foOnLeg`, `diagnostics.status.scenarioClearAll`, `flightPhase.pause.graceParked.*`, the whole
`speechSettings.smallTalk.*`, `speechSettings.groundCrew.walkaround.*`, `speechSettings.persona.roster.*`,
`speechSettings.callouts.transition.*`, `speechSettings.briefing.askForQuestions.*` blocks (the 2026-11-01
immersion batch and the #199 hub-usability slice). A handful are legitimately invariant (`setup.web.pfx.placeholder`
path, `setup.inputRelay.dll.label`, `speechSettings.providers.google.title`, `gsxSettings.simbrief.title`).

Conclusion: structural parity is perfect because `ShippedLanguagePackGuardTests` enforces ids/placeholders, but it
does **not** detect "copied English", so the owner rule *always translate GUI* (2026-10-13) has been silently
violated for ~90 recent texts. Proposed guard: fail when a non-en pack value equals en for an id not on a small
invariant allow-list.

---

## E. STATIC ASSETS

`wwwroot` = 925 KB: fonts 376 KB (JetBrainsMono ×2 187 KB, PublicSans ×4 134 KB, ShareTechMono 13.5 KB + three
OFL licence texts), img 236 KB (`img/wandb.png` alone 240 KB — single largest file, used by `CgEnvelope.razor:17`),
css 212 KB (app.css 76 KB, flightstatus.css 21.5 KB, perf.css 15.5 KB, diagnostics.css 14.8 KB, monitor 11.8 KB,
seven more 4–8 KB), js 36 KB (app.js 31 KB, ecam.js 2.5 KB), icons 28 KB, data 12 KB (`skalarki-layout.json`, also
embedded as a resource), manifest/favicon/touch icons.

- Unreferenced files: **none** (every CSS/JS/font/image is linked from App.razor, `@font-face`, a component or the
  manifest). The three `*-OFL.txt` files are licence texts — must ship, correctly so.
- Vendored libraries: **none** in wwwroot (no bootstrap/jquery/chart libs). `blazor.web.js` is copied at build from
  the framework (csproj note). Fonts: JetBrains Mono, Public Sans, Share Tech Mono (OFL) — versions not recorded in
  the folder; consider a `fonts/VERSIONS.txt`.
- Delivery: App.razor loads **13 stylesheets + 2 scripts on every page** (lines 37–72). Per-page CSS is global by
  design note ("kept out of app.css"), so the split buys nothing at runtime; either bundle at build or emit page
  CSS via `<HeadContent>`. `?v=AssetVersion` cache-busting is in place.
- Large/should-not-ship candidates: `wandb.png` (240 KB PNG with transparency) could be a ~20 KB SVG or WebP; the
  Core comment says the envelope was digitised from it, so the SVG could replace both uses.
- `skalarki-layout.json` is served from wwwroot *and* embedded — intentional fallback (ADR-0017), fine.

---

## F. SECURITY HYGIENE

- **Access token never logged**: no `Log*` call in `src/` mentions the token (hits for "token" are gate tokens /
  speech tokens / cancellation tokens). `AccessToken` is not referenced anywhere in the Web project — the token is
  shown only in the WPF QR (ADR-0001), and the diagnostics bundle passes settings.json through
  `Core/Diagnostics/SettingsRedactor` before zipping.
- **One gate for UI and API on LAN**: `LanTokenMiddleware` runs first for every request including the Blazor
  WebSocket; cookie + bearer + `?token=` are all fixed-time compared. Loopback always passes the middleware
  (lockout rule).
- **Write surfaces**: `/api/command/{name}` (the only endpoint that actuates the aircraft/GSX) is opt-in
  (`Enabled=false`), 404 while off, and `RequireTokenOnLoopback` defaults **true** — i.e. stricter than the UI.
  Status/controls share that gate. Telemetry/damage/diagnostics are read-only, on by default, token-free on
  loopback (`RequireTokenOnLoopback=false`).
- **Endpoints without a per-endpoint check**: `/api/app/boot` (harmless boot id) and `/api/gsxmenu/events`
  (**GET with side effects**: records an allow-listed handler event into `GsxDiagnosticsStore` + JSONL event log;
  consumer is the in-sim Python script, which cannot send headers). Both still sit behind the LAN middleware.
  Acceptable, but the event push should at least be documented as loopback-only and not become a template.
- **Blazor pages have no authorisation attribute** — none needed; the middleware is the gate, and `ClientContext.
  IsLocal` only decides whether the sim PC's folder-browse dialog is offered.
- **CORS**: nothing configured, no `AllowAnyOrigin`, no `http://` literals in Web — consistent with a same-origin
  LAN app. **HTTPS (ADR-0013)**: the plan is built once, HTTPS failure degrades to HTTP with a banner, the
  install-asset allow-list is exact-path and `..`-guarded; QR offers HTTPS when up. No `UseHttpsRedirection`
  (correct — would break the HTTP lockout path). Cookie is `HttpOnly, SameSite=Lax`, not `Secure` — expected since
  it must work on the HTTP origin; one cookie per scheme is already documented.
- **MarkupString from pack text**: none. The two builders (`EfbInit.PendingBoxes(int)`, `FuelLog.SvgText`) take
  numbers/CSS class names only.
- Minor: `Router` has no `<NotFound>`; an unknown route renders an empty shell rather than a 404 page (UX, not
  security).

---

## G. CONVENTION DRIFT vs CLAUDE.md

| Rule | Status | Evidence |
|---|---|---|
| no `async void` | clean | 0 hits in Web |
| never block on async in components | clean | `.Result` hits are `lmc.Result` (a record property, Loadsheet.razor 152–155); no `.Wait()`/`GetAwaiter().GetResult()` |
| structured logging, no interpolation | clean | 0 `Log*($"…")` in Web; `ILogger` barely used in components (correct — logs stay English) |
| `ConfigureAwait(false)` only in library code | clean | none in .razor |
| dispose/unsubscribe | **one leak** | `Home.razor` `_tuneNoteTimer` not in `Dispose()` (875–884); stray `_tuneNoteTimer?.Dispose();` mis-indented inside `ConfirmFuel()` at line 650 |
| no catch-all `@page` | clean | 36 routes, none `{*…}`; `/diagnostics/{Page?}`, `/settings/*/{Section?}` are optional segments only |
| **no "key" in identifiers** (owner rule 2026-10-13) | **drift** | introduced *after* the rule: `Layout/SettingsSearch.cs:34,196–211` `GroupKey`/`ItemKey` (2026-11-01); `Components/TextField.razor:76,142` `TallyKey` parameter (2026-10-13, same day); pre-rule survivors still in the public surface: `SettingsNav.cs:6,16` records `SettingsNavItem(string Key…)`/`SettingsNavGroup(string Key…)` (2026-09-19) and their `.Key` uses in SettingsSearch; `AudioSettings.razor:619,621,910` `TargetKey` ×3 (2026-08-06); `VoicePickerField.razor:60,102` `CustomKey`/`KeyOf` (2026-10-12); `Monitor.razor:274` `_tickKey` (2026-09-23); `FieldChoiceCatalog.cs:28` `"apiKeySource"` (that one is a settings property name — settings.json compatibility) |
| every option has a web control | clean | `OptionFieldCompletenessTests` + 0 unused pack ids |
| ADR-0010 option-2 validation (Save grey while a field is red) | **gap** | raw row editors in AudioSettings, CockpitSettings, DisplaySettings, GsxSettings, SetupSettings paint `invalid` but never call `FieldValidity.Report` (grep: 0 hits) → Save stays enabled with blank names / bad numbers; only `OnBeforeSave` pruning in Setup/Audio partly compensates |
| Blazor base-class consistency | minor | `Logs.razor` is `ComponentBase, IDisposable` with its own timer while every sibling is a `StoreObserverComponent`; `MonitorLayout` is a bare `LayoutComponentBase` (fine, it watches nothing) |
| XML docs on public API | good | presentation classes and bases documented; the 11 view records in FlightStatusPresentation.cs lack summaries |

---

## H. TEST SEAMS

Tests live only in `tests/ProsimCompanion.Core.Tests` (28 Web test files under `Web/` + 3 under `Localization/`).

Presentation/logic classes in Web with **no test mention**: `StatusPresentation` (113 lines, `DisplayText` resolve/
pill/mono), `AudioPresentation` (44), `FlightStatusChangeLog` (88, a startup module — behaviour of the #110 pill
log is probed in flight but not unit-tested), `SettingsSearchService` (the live index; the static `SettingsSearch`
table is covered by `SettingsHubTests`), `WatchSet` (102 — the one class every page depends on; no test of
initial-apply, marshalling, reverse-order dispose), `SettingsPageBase` (dirty/save/discard/timer; exercised only
indirectly by page render tests).

Pages with **no render test**: `Home` (presentation tested, page composition not), `EfbInit`, `OfpPage`,
`WeightBalance`. Components rendered only inside those pages are therefore also unexercised (GsxStatusPanel,
SpeechStatusPanel, MixerStatusPanel, VoiceReferenceDrawer, DiversionCard, OpenQuestionsCard, KorryPushbackButton,
CgEnvelope, A320Silhouette, PerfRunwayCard, WindLimitRow).

Logic sitting in `@code` blocks that has no test and should move out:
- Landing/Takeoff: `ParseFeet`, `RunwayQdm`, `WindVerdict`, displaced-threshold/LDA/TORA arithmetic (Landing
  680–760, Takeoff 690–720) — duplicated and untested; Core `PerfRunwayMath`.
- EfbInit: `FieldRange` clamping/integer rule (168, used by `CommitEdit` 362), `FormatKg/FormatPax/FormatZulu/Dash`,
  `Status` tuple (591) — `EfbInitPresentation`.
- AudioSettings: `TargetKey`/`SetTarget` parsing (`agent|bus:index` string protocol, 619–642, 910–922),
  `ChannelOptions`, `AddGenericTargets` — into the tested `AudioPageRules`.
- MainLayout: `EvaluateSimClock` freshness rule (376–395) and `NetworkVoiceDetail` (307) — small pure functions.
- Monitor: `Tick` / door / service lamp mapping (Row/Door/Service/Lamp fragments) — `FlightMonitorPresentation`
  already exists and is tested; finish moving.
- SpeechSettings: `BindingText`, `IsProviderConfigured`, `VoiceNote` (79–90, 276) — `SpeechPageRules`.

---

## TOP 15 BY PAYOFF

| # | Finding | Effort | Lines removable | Section |
|---|---|---|---|---|
| 1 | **94 texts shipped as English in all six packs** (66 in speechSettings.json: smallTalk, walkaround, roster, transition reminders; plus settings search, card fold, Monitor/OFP headings) — owner rule breach the guard cannot see. Translate, and add an "identical to en" guard with an invariant allow-list | M | 0 (content) | D |
| 2 | **Landing/Takeoff share 21 methods and 137 identical lines** → `PerfPageBase` + Core `PerfRunwayMath` (tested) | M | ~280 | B1, C, H |
| 3 | **Raw row editors bypass `FieldValidity`** (Audio ×3, Cockpit, Display, GSX ×3, Setup) → `RowListEditor<TRow>` + validity-reporting cells; also deletes 5 identical header CSS blocks | M | ~200 + fixes Save-while-red | B2, G |
| 4 | **SpeechSettings.razor 1406 lines** → one component per rail section (12), `PttBindingEditor`, roster presentation | L | 0 net; file → ~150 | C |
| 5 | **Home.razor 29 injects / 885 lines** → `WeatherCardTile`, `GateStripTile`, `StatusRowView`, tile-layout code-behind, aggregated snapshot source; fix `_tuneNoteTimer` leak + stray dispose at line 650 | M | ~250 moved, Home → ~400 | C, G |
| 6 | **11 copies of the transient-note/armed-action timer** → `TransientNote` on WatchSet (auto-disposed) | S | ~120 | B3 |
| 7 | **`WatchOptions<T>` + `RepaintEvery`** on WatchSet: removes 6 hand-rolled `OnChange` subscriptions and 5 repaint timers with their Dispose bookkeeping | S | ~60 | B4, A3 |
| 8 | **AudioSettings.razor 966** → three cards + `MixerMappingsTable`; move `TargetKey`/`SetTarget`/channel options into `AudioPageRules`; merge duplicate state label/tone with MixerStatusPanel into `AudioPresentation` | M | ~100 | C, B6, H |
| 9 | **`key` identifiers added after the 2026-10-13 rule**: `SettingsSearch` `GroupKey`/`ItemKey`, `TextField.TallyKey`; sweep the pre-rule `SettingsNav.Key`, `TargetKey`, `CustomKey`/`KeyOf`, `_tickKey` while there | S | ~0 (rename) | G |
| 10 | **Weather card rendered twice** (Home fragment vs Monitor inline re-derivation) → one `WeatherCardTile` over the tested `WeatherCardView` | S | ~60 | B5 |
| 11 | **EfbInit.razor 659** → `InlineNumberEdit` component + `EfbInitPresentation` (formatters, `FieldRange`) with tests; share `ConfirmFuel`/reset actions with Home | M | ~80 + 120 moved | C, B7, H |
| 12 | **app.css 2228 lines carries ~544 lines of page families** (`.vr-`, `.korry-`, `.mixer-`, `.cockpit-`, `.display-`, OFP block) next to 12 page stylesheets; 13 `<link>`s per page → move to `css/pages`, add `.caps-header`/`.stack-N` utilities, consider a build-time bundle | S–M | ~60 net, −544 from app.css | B8, B9, E |
| 13 | **Untested seams**: `WatchSet`, `SettingsPageBase`, `StatusPresentation`, `AudioPresentation`, `FlightStatusChangeLog`, `SettingsSearchService`; no render test for Home/EfbInit/OfpPage/WeightBalance | M | 0 | H |
| 14 | **FlightStatusPresentation.cs 811** → four partials by existing banner regions + `FlightStatusViews.cs`; add XML docs to the 11 view records | S | 0 | C |
| 15 | **API hygiene notes**: `/api/gsxmenu/events` is a side-effecting GET with no per-endpoint auth (loopback script by design — document, keep allow-list); `Router` lacks `<NotFound>`; cookie not `Secure` on HTTPS origin (acceptable, one cookie per scheme) | S | 0 | F |

---

## APPENDIX — remaining observations

- `wandb.png` 240 KB is the largest shipped asset; an SVG would serve both `CgEnvelope` and the Core provenance note.
- `Monitor.Tick` (1 s) and `MainLayout.EvaluateSimClock` both derive sim-clock text from the same dataref; one
  `SimClockStore` would feed both.
- `Logs.razor` polls `LogBufferStore.Snapshot()` every 500 ms by design (the store deliberately has no fan-out);
  fine, but it is the only page not on `StoreObserverComponent`.
- `Fuel`, `FuelLog`, `WeightBalance` repaint on 1–2 s timers rather than watching datarefs — documented choice
  ("cheaper than 30+ ValueChanged handlers"); B4's `RepaintEvery` keeps the choice and removes the boilerplate.
- `AppearanceSettings.Dispose` and `Diagnostics.Dispose` fire-and-forget JS/`DetachAsync`; both guarded, but a
  `DisposeAsync` would be the honest shape.
- `GsxSettings` `OnAfterSave` profile mirroring and `SetupSettings.OnBeforeSave` list pruning are page-resident
  rules while `GsxPageRules`/`SetupPageRules` exist — finish the move.
- `FieldChoiceCatalog` `"apiKeySource"` is a settings property name, so it stays (JSON compatibility), but it is
  the one place a "key" string is unavoidable — a comment saying so would stop future sweeps touching it.
- Localisation heuristics found no inline English in markup; the remaining Core-built English texts (status pills,
  event-log lines) are the ADR-0014 "hard 20 %" and out of this project's scope.
- Duplicate `.side-item` / `.ecam-btn` rules between app.css and diagnostics.css are intentional overrides; a
  comment at the override site would prevent a future "dedupe".
- Fonts have no version manifest; the OFL texts ship correctly.
- `Routes.razor` cascades `ClientContext` as `IsFixed=true` — correct; `BrowserLanguageContext` is scoped per
  circuit — correct per ADR-0014.
