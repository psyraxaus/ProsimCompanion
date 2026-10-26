# ECAM-style diagnostics page — design

## Why

Users (and JC, at 2 am) need one glance to see what the app is doing and which hop is
unhappy. The app already has the shape of an aircraft system: a handful of subsystems, links
between them, a few live values each, and a healthy / degraded / failed state. Airbus ECAM
system-display grammar maps onto that almost one-to-one, and every ProSim user already reads
it fluently.

## Where it lives

A pinned nav item **below Settings** in `MainLayout` (second hard-coded `NavLink` after the
Settings one, not in the `Tabs` list), route `/diagnostics` with page aliases
`/diagnostics/{Page?}` (`voice|sim|remote|gsx|status`). The nav icon carries a small
caution-count badge (amber) when `Cautions.Count > 0`, red if any warning, so people see
there is something to look at without opening the page.

## ECAM conventions (the rules the Razor side applies, nothing else)

| Colour | Hex | Meaning |
|---|---|---|
| White | `#f2f2f2` | Titles, static labels, dividers, "- - -" for not-applicable |
| Green | `#33ff33` | Normal operation; values; bus bars; links |
| Amber | `#ffbf00` | Caution — degraded, reconnecting, queued, stale, lost |
| Red | `#ff3b30` | Warning — hard failure that blocks the feature; red gauge arc |
| Cyan | `#4fd1ff` | Units and small qualifiers (`GB`, `MS`, `HZ`, `T`, `%`); action lines on STATUS |
| Grey | `#8a8a8a` | Inactive page-selector entries |

- Background pure black. Font: a condensed monospace (`Share Tech Mono` stand-in in the
  mockups); pick a self-hosted open font, do not copy ProSim's bitmap font.
- A node whose data source is missing shows `XX` (amber) for the value, never a stale number.
- A node that is intentionally not in use shows `- - -` in white, no colour.
- Links are dashed lines with a solid arrowhead; a link inherits the worse state of its
  endpoints.
- Page title is white, underlined, centred-ish; every page has the same memo strip along the
  bottom (green rule, then four cells separated by white verticals) and the same ECP-style
  page selector under the strip.
- The page is a 720×640 fixed drawing area scaled to fit its container (`transform: scale`
  via a `ResizeObserver` or CSS `aspect-ratio` + `container` units); it is not responsive
  text layout.

## Pages

Each page is a bespoke Razor component laid out from absolute-positioned elements (boxes,
dashed-line divs, small inline SVG only for gauge arcs, arrowheads and the aircraft outline).
Colour and dash/solid come from `DiagState`; text from `Value` / `Detail`. The component
looks nodes up **by id** from the snapshot; it never computes health itself.

### VOICE (ENGINE + FUEL grammar)

- Upper-left gauges: `e2e` (end-to-end ms, utterance end → first audio out), `gpu` (LLM host
  GPU %), each with a red arc from `WarningAt`. Readouts beside: `vram`, `tok_s`, `rtf`,
  `queue`.
- Upper-right "LAST UTTERANCE": text, confidence, duration, language; `intent`, `action`,
  `gate` (classifier in use), `bleed` (rejects in last minute).
- Pipeline row, nodes `mic → vad → stt → llm → tts → out`, host label above each
  (`SIM PC`, `MAC MINI`, `RTX 3090`, from options), value box below (level dB, SPEECH/SILENCE,
  last STT ms, last LLM first-token ms, last TTS ms, IDLE/PLAYING), detail under that
  (sample rate, classifier, model, backend, voice, output device).
- Endpoint bus row: STT / LLM / TTS `host:port` coloured by state.
- Caution line (centred amber) from the first voice caution, if any.

### SIM (ELEC grammar)

- Three source boxes: `prosim` (SDK version, aircraft, CONN/RECONN), `simconnect`
  (sim name, CONN/RECONN), `gsx` (COUATL state). Feed lines to bus bars with a `rate`
  readout (Hz) beside each.
- Bus bars `DATAREF BUS` and `SIMVAR BUS` with `subs` / `stale` counts; amber tie between
  them when either side is reconnecting.
- GLN-style grid: `phase` (+ gate), `ground` (ON GND/AIRBORNE + park brake), `power`
  (EXT PWR / APU / GEN), `doors` (first open door + cargo).
- Rows: `fpln` (orig → dest, runway, SID), `load` (pax, ZFW, block), `wx` (provider, age,
  wind).

### REMOTE (F/CTL grammar)

*The mockup says "SIGNALR HUB" — in the app this is the Blazor Server circuit host plus the
REST API, so label it `WEB HOST`.*

- Centre box `webhost`: circuit count, API req/s, listen endpoint, TLS on/off.
- Left "DISPLAYS": one box per connected Blazor circuit (user-agent class, remote IP, current
  route, round-trip ms from a 5 s ping).
- Right "CONTROLLERS": `streamdeck` (last `/api/status` poll age, client version header if
  present), `mixer` agents as numbered ELAC/SEC boxes (one per configured mixer endpoint /
  agent on the current branch; amber when lost, with `LOST nn S` detail).
- Audio routing row: one column per logical channel (FO, PA, INT/RAD, GSX, AMB — take the
  real channel set from `MixerStatusStore.Mappings`), ON/OFF/PTT block, level dB, device.

### GSX (DOOR/OXY grammar)

- Aircraft outline with door markers driven by ProSim door datarefs (amber filled = open).
- Service boxes either side with dashed connectors to the hull: left `jetway`, `gpu`,
  `stairs`, `pushback`; right `catering`, `refuel`, `boarding`, `deice`. Value = GSX stage
  (`- - -` / AVAIL / REQ / CONN / PROG / DONE), detail = stage-specific (door, wait time,
  litres, pax).
- Progress row: `pax` (boarded/total), `cargo` (loaded/total ULD or kg), `fuel`
  (current/target t) as fuel-quantity bars.
- Caution line: queued/stalled service and the next smart-button service.
- Memo strip here is GSX-specific: GSX state, smart-button trigger, turnaround elapsed, phase.

### STATUS (STATUS page grammar)

- Left: cautions grouped by system — amber title, cyan dotted action lines
  (`- WOL MAGIC PKT ........ SENT 00:42`). Green entries for informational notes (e.g. an
  active model trial).
- Right: `INOP SYS` (features blocked by a caution), `DEGRADED`, then `RECENT` — last 8
  diagnostic events with HH:mm, amber for state transitions to caution.
- Memo strip: caution count (amber if > 0), warning count, bundle export readiness (this is
  where the existing "Export diagnostics" action gets a button), phase.

## Snapshot shape

```csharp
namespace ProsimCompanion.Core.Diagnostics.Ecam;

public enum DiagState { Normal, Caution, Warning, NotApplicable, NoData }

/// One box on a page. Value is what sits in the box, Detail the line under it.
public sealed record DiagNode(
    string Id, DiagState State, string Value,
    string? Detail = null, string? Host = null);

/// A dashed link between two node ids; state is the worse of the two unless overridden.
public sealed record DiagLink(string From, string To, DiagState State);

/// A dial. Red arc begins at WarningAt; amber text from CautionAt.
public sealed record DiagGauge(
    string Id, double? Value, double Min, double Max,
    double CautionAt, double WarningAt, string Unit);

public sealed record DiagAction(string Label, string Value);

public sealed record DiagCaution(
    string System, string Title, DiagState Severity,
    IReadOnlyList<DiagAction> Actions, DateTimeOffset RaisedUtc);

public sealed record DiagEvent(DateTimeOffset AtUtc, string Text, DiagState State);

/// The four-cell strip at the bottom of every page.
public sealed record DiagMemo(
    string Version, string Model, TimeSpan Uptime, string Phase,
    int CautionCount, int WarningCount, bool BundleReady);

public sealed record DiagPage(
    string Id,
    IReadOnlyDictionary<string, DiagNode> Nodes,
    IReadOnlyList<DiagLink> Links,
    IReadOnlyDictionary<string, DiagGauge> Gauges,
    IReadOnlyDictionary<string, string> Readouts,   // free-form green text keyed by id
    string? CautionLine);

public sealed record DiagnosticsSnapshot(
    DateTimeOffset AtUtc,
    DiagMemo Memo,
    DiagPage Voice, DiagPage Sim, DiagPage Remote, DiagPage Gsx,
    IReadOnlyList<DiagCaution> Cautions,
    IReadOnlyList<string> InopSystems,
    IReadOnlyList<string> Degraded,
    IReadOnlyList<DiagEvent> Recent)
{
    public static DiagnosticsSnapshot Empty { get; } = …; // every node NoData
}
```

Node ids are the lower-snake names used in the page sections above. The Razor pages render
`Nodes.GetValueOrDefault(id) ?? DiagNode.NoData(id)`, so a collector that is not yet wired
produces amber `XX` rather than a missing element — the page itself tells you what is not
instrumented yet.

## Collection

- `IDiagnosticsCollector { string Page { get; } void Contribute(DiagPageBuilder page, DiagCautionSink cautions); }`
  — one per feature project (`Speech`, `Prosim`, `Sim`, `Gsx`, `Audio`, `Web` for circuits),
  registered as `IEnumerable<IDiagnosticsCollector>`. Collectors read the existing stores
  (`ConnectionStatusStore`, `SpeechStatusStore`, `LlmHealthStore`, `GsxDiagnosticsStore`,
  `MixerStatusStore`, `WeatherStore`, `IFlightPhaseSource`, …) plus the new metrics below.
  They are the only code that knows what "degraded" means for their hop.
- `DiagnosticsSnapshotService : BackgroundService` ticks at 1 Hz (configurable), calls every
  collector inside try/catch (a throwing collector yields `NoData` for its page and a log
  line, never a dead page), builds the snapshot, publishes to
  `DiagnosticsStore : SnapshotStore<DiagnosticsSnapshot>`. Pure `ProcessTick(...)` core so
  it is testable without a timer.
- `DiagEventLog`: ring of the last 50 events; collectors push on state transitions only
  (compare with previous tick), so RECENT is a change log, not a heartbeat.

## Metrics that do not exist yet

- `LatencyTracker` (Core/Diagnostics): ring buffer of the last N samples with `Last`, `P50`,
  `P95`; one each for `asr`, `llm_first_token`, `llm_total`, `tts`, `e2e`. Fed from
  `LanAsrRecognizer`, `OpenAiChatClient` (`StreamAsync` first token / completion),
  `TtsRouter` (per provider call), and the arbiter/narrator for end-to-end.
- `RateCounter`: events per second over a sliding 5 s window; one for dataref dispatches,
  one for simvar dispatches (hook `DataRefSubscriptionTable` dispatch), one for API requests.
- `UptimeTracker`: process start time; trivial.
- `CircuitTracker` (Web): a `CircuitHandler` that records open circuits with remote IP,
  user-agent class, current route (from `NavigationManager.LocationChanged`), and a 5 s
  JS `ping` round-trip.
- `ApiClientTracker` (App): middleware on `/api/*` recording last-seen per remote address and
  `User-Agent`, so the Stream Deck plugin's 1 Hz `/api/status` poll becomes a presence.
- `LlmHostProbe`: alongside `LlmHealthProbeService`, a TCP/ICMP reachability check of the LLM
  host so the VOICE page can distinguish "host asleep, WoL sent" (amber, with an action line)
  from "host up, server down" (warning). Reuse `IWakeOnLanControl` for the action.
- GPU %, VRAM, tok/s: tok/s from `StreamAsync` token count ÷ elapsed; GPU/VRAM only if
  llama-server exposes `/metrics` (it does when started with `--metrics`) — probe once,
  `NoData` otherwise. Do not add an SSH or nvidia-smi dependency.

## API and bundle

- `GET /api/diagnostics/snapshot` returns the current snapshot as JSON (same gating as the
  other telemetry endpoints) — this is what a future iPad display or the Stream Deck can
  render from.
- `DiagnosticsBundleBuilder` adds `diagnostics-snapshot.json` (the latest snapshot) and
  `diagnostics-events.jsonl` (the ring) to the bundle; `ProsimCompanion.Reduce` reads them
  into the report's health section.
