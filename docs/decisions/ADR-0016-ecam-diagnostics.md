# ADR-0016: ECAM-style diagnostics pages

Date: 2026-10-27
Status: accepted (owner design, `docs/design/diagnostics/design.md` + five mockups; built on
`feature/ecam-diagnostics` in four packages)
Amends: ADR-0011 (one page family deliberately outside the airline theme), ADR-0014 (ECAM
tokens are not localised)

## Context

Users (and the owner at 2 am) need one glance to see what the app is doing and which hop is
unhappy: is the LLM box asleep or is the server down, is SimConnect reconnecting, which mixer
agent dropped, which GSX service is queued. The app already has the shape of an aircraft
system — a handful of subsystems, links between them, a few live values each, a healthy /
degraded / failed state — and every ProSim user already reads Airbus ECAM system displays
fluently. Until now that information was spread over the Status cards, the footer dots, the
Logs page and the CMTrace log.

## Decision

1. **ECAM grammar, not another dashboard.** Five fixed 720 × 640 pages (VOICE, SIM, REMOTE,
   GSX, STATUS) drawn with the ECAM vocabulary: boxes with a value box under them, dashed
   links with arrowheads, bus bars, 270° dials, a four-cell memo strip and an ECP-style page
   row. A link shows the worse state of its two endpoints. A node whose source is missing
   shows amber `XX`, never a stale number; a node intentionally not in use shows white
   `- - -`. The page itself tells you what is not instrumented yet.
2. **Collectors feed a snapshot; the pages only draw.** Each feature project owns one
   `IDiagnosticsCollector` per page it contributes to (`Speech` → VOICE; `Prosim`, `Sim` and
   the GSX source box → SIM; `Audio` and `Web` → REMOTE; `Gsx` → GSX) and is the only code
   that knows what "degraded" means for its hop. A 1 Hz `DiagnosticsSnapshotService` runs
   every collector inside its own try/catch (a throwing collector costs its nodes, never the
   page, and logs once per failure streak), merges the pages, folds the cautions (raise time
   carried across ticks, RECENT lines on the edges) and publishes one immutable
   `DiagnosticsSnapshot` to a `SnapshotStore`. The Razor components look nodes up by id and
   never compute health. The same snapshot is `GET /api/diagnostics/snapshot` and goes into
   the support bundle with the RECENT ring, so a bug report arrives with the amber box in it.
3. **Metrics that did not exist are recorded where the work happens.** `LatencyRegistry`
   (ring of 64, last / p50 / p95) and `RateRegistry` (5 s window) are Core singletons the
   feature projects write into by name: the ASR client, the LLM stream, the TTS router, the
   dataref and simvar dispatch, the `/api/*` middleware. End-to-end latency is correlated by
   time (utterance end stamped by the recognizer, closed by the arbiter's first audio within
   20 s) because no utterance id crosses the pipeline and adding one would touch every hop for
   one figure. Host reachability for the LLM is a TCP connect beside the health probe — no
   ICMP privilege, no SSH, no `nvidia-smi`. GPU utilisation is therefore not available and the
   GPU dial shows `XX` by design; Ollama's resident model size and llama-server's KV-cache fill
   are read because they are offered.
4. **The theme exception.** The drawing area is pure black with the six ECAM hexes, whatever
   airline theme the rest of the app wears (ADR-0011 themes everything else). A system display
   that changed colour with the livery would stop being readable at a glance, and the colour
   IS the information. No colour literal lives in a Razor file: `EcamPresentation.StateClass`
   maps a `DiagState` to one `st-*` class and `diagnostics.css` holds the hexes.
5. **No global key handler.** The ECP page row is a row of real links. Keys 1–5 are not bound:
   the tablet has no keyboard, a global handler fights the settings pages' inputs, and the
   app has never had one (ADR-0010/0011 keep interaction to visible controls).
6. **Tokens stay English; labels are the user's.** `XX`, `- - -`, `CONN`, `RECONN`, `STT`,
   `LLM`, the page names and the memo labels are avionics tokens in `EcamTokens`, not pack
   texts (ADR-0014 exception). The host label above a pipeline node is the host part of the
   configured URL until the user names it on Settings → Advanced → Diagnostics; nothing is
   assumed about which machine runs what.
7. **The font.** Share Tech Mono (SIL OFL, self-hosted, latin subset) is the display face: a
   condensed monospace that reads like a system display without reproducing ProSim's own
   bitmap font. Recorded in THIRD_PARTY.md like the other two fonts.
8. **Scaling.** The stage is scaled as one piece to its 9:8 frame by a `ResizeObserver`
   (`ecam.js`) writing `--ecam-scale`; nothing reflows, so the layout is the same on a phone
   and an ultrawide. Same rule as the pop-out Flight Monitor board.

## Consequences

- Two new options (`diagnostics.ecamTickMs`, `diagnostics.ecamHostLabels`) with controls on a
  new Settings → Advanced → Diagnostics page; a second hard-coded nav item under Settings with
  the caution count as a badge.
- The support bundle gains `diagnostics-snapshot.json` and `diagnostics-events.jsonl`; the
  reducer's report gains a `health` section. Older bundles reduce exactly as before.
- The Stream Deck plugin sends `User-Agent: ProsimCompanion-StreamDeck/<version>` so the
  REMOTE page can tell it from any other API client.
- Public constructors of `LanAsrRecognizer`, `OpenAiChatClient`, `TtsRouter`,
  `SpeechArbiterService`, `UtteranceRouter`, `ProsimDataRefService` and `SimVarService` grew
  optional trailing parameters; hand-built instances (tests, tools) measure nothing and keep
  working.
- What the app genuinely does not know stays `XX`: GPU %, the recognised language, the TTS
  output device actually opened (only the configured name), cargo in ULDs (the board gives a
  percentage), the SIM page's SID (the OFP has no SID field).
