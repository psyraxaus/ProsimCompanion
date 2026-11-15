# Speech / AI stack reference (voice FO pillar, Phase 5)

From Prosim2FO. All endpoints are user-hosted or cloud; every provider is optional with fallback
chains — "local only" hard mode must exist.

## TTS (router order: Kokoro → ElevenLabs → Google → WinRT → SAPI5)

- **Kokoro** (kokoro-fastapi, local neural): OpenAI-compatible `POST {base}/v1/audio/speech` (WAV)
  and `GET {base}/v1/audio/voices`. Default `http://192.168.1.50:8880`, model `kokoro`, voice
  `bm_george`, per-voice disk cache. Two timeout phases (issue #113): `kokoroTimeoutMs`
  (1500 ms) covers connect/first byte only — the "is the host awake?" check; the streamed
  WAV body gets `kokoroBodyTimeoutMs` (15 s) + 100 ms per character, because kokoro-fastapi
  synthesises while it streams and a long briefing takes seconds. A timeout surfaces as one
  `TimeoutException` sentence naming the phase; the router still cools the provider down 60 s.
- **ElevenLabs** (paid cloud neural, design note `elevenlabs-tts-provider.md`):
  `POST {base}/v1/text-to-speech/{voice_id}?output_format=…`, key in the `xi-api-key` header,
  base `https://api.elevenlabs.io`. Default model `eleven_flash_v2_5` (~75 ms, half the credit
  cost; `eleven_v3` optional). ElevenLabs never emits WAV: default `mp3_44100_128` works on
  every plan (Free included) and is decoded locally via NAudio/Media Foundation; `pcm_24000`
  needs a paid plan and is wrapped in a 44-byte WAV header (no decode). Cache key voice
  segment is `{voice}@{model}@{format}` so a model/format change never serves stale audio;
  cache uncapped. Pre-call guards: `elevenLabsMaxCharsPerRequest` (2 500, the Free-plan
  per-generation cap) and the monthly budget `elevenLabsMonthlyCharBudget` (9 000 — plan-
  relative: Free 10k, Starter 30k, Creator 121k credits) counted in
  `cache/tts/usage.elevenlabs.json`. `apply_text_normalization=off` — the arbiter already
  emits spoken-form phraseology. 401 latches the provider until settings change; 429/422
  throw and the router cools it down. Voice picker: `GET {base}/v2/voices?category=premade`.
  Role voice ids (`voices.*`) must be ElevenLabs voice_ids while this provider serves.
- **Google Cloud TTS Chirp 3 HD**: service-account JSON key, LINEAR16; disk cache keyed by
  text+voice+format; prewarm checklist phrases; monthly usage counter (`cache/tts/usage.json`)
  against the 1M-char free tier.
- Playback: NAudio, device selection, intercom band-pass filter.

## Recognition (chain: LAN → WinRT → offline System.Speech)

- **LAN ASR** — two server flavours, selected by `speech.asrApi` (`AsrServerApi` holds the
  wire differences; the recognizer is flavour-agnostic):
  - `fasterWhisper` — on the page "Simple (/transcribe)" since 2026-11-16; the settings value
    keeps its name. The contract, complete: `GET /health` answers any 2xx; `POST /transcribe`
    takes a multipart form with `file` (WAV, 16 kHz mono 16-bit) and, when the open window
    has 1 to 50 phrases, `hotwords` (the phrases joined with ", "); the reply is a JSON
    object with `text` (string), `confidence` (0..1, optional) and `no_speech_prob` (0..1,
    optional). Nothing else is read. No authentication header is sent. The in-repo example
    server is `tools/asr-server` (faster-whisper; `confidence` = exp of the duration-weighted
    `avg_logprob`, silence = empty text, confidence 0, no_speech_prob 1).
  - `whisperCpp` — on the page "OpenAI-style (/v1/audio/transcriptions)". whisper.cpp
    `whisper-server` (e.g. native on a Mac mini since 2026-08-29), and any server with the
    same API: the owner's Qwen3-ASR server (a private script over mlx-qwen3-asr, not in the
    repo). Tested 2026-11-16 with the app's own form fields and one spoken WAV: `/health`
    200 `{"status": "ok", "model": "Qwen/Qwen3-ASR-1.7B"}`, the transcription answered
    `{"text": "Before start checklist."}` in 0.6 s, `text` only. That script also answers
    `POST /transcribe` with the same body, so it fits both types. Not tested through the
    app itself. A text-only reply carries no confidence and no no-speech score, so the
    interpreter's `confidenceFloor` / `noSpeechCeiling` checks have nothing to grade and the
    text-only ladder decides.
    OpenAI-style `POST /v1/audio/transcriptions`, fields `file`, `prompt` (grammar phrases go
    here — it is the initial prompt), `response_format=verbose_json`, `temperature=0`. Reply
    carries `text` plus `segments[].{start,end,avg_logprob,no_speech_prob}`; confidence is
    derived client-side with the wrapper's formula (exp of duration-weighted `avg_logprob`,
    `no_speech_prob` = max over segments) so the interpreter thresholds mean the same thing.
    `/health` may 404 on builds without the route — readiness accepts any HTTP answer.
  - **Trap (2026-08-29):** the wrong flavour answers 404 to every utterance while `/health`
    is green; the app used to drop those silently (a whole session with zero "ASR heard"
    lines). Now a Warning "LAN ASR server … answered 404 … check speech.asrApi" fires once
    per episode.
  `speech.asrBaseUrl` is empty by default (`http://192.168.1.50:8000` is only the field's
  example text); readiness polling + optional wake-on-LAN (MAC/broadcast/port 9).
  Seen on a whisper.cpp server 2026-11-16: `/health` 200, `POST /transcribe` answers 404
  with the body `File Not Found (/transcribe)`.
- **Utterance segmentation (ADR-0009)** — `UtteranceSegmenter` over a pluggable
  `ISpeechFrameClassifier` (512-sample / 32 ms frames at 16 kHz; WaveInEvent's 800-sample
  buffers are reframed with remainder carry). Engine per `speech.vadEngine`:
  - `silero` (default): Silero VAD v6, vendored ONNX model (THIRD_PARTY.md) on
    `Microsoft.ML.OnnxRuntime` CPU, one intra-op thread, recurrent state + 64-sample context
    reset per listening start. Hysteresis: speech opens at p ≥ `vadThreshold` (0.5), silence
    only counts below threshold − 0.15. Defaults: 500 ms end-silence, 300 ms min speech,
    300 ms pre-roll, 15 s hard cap (`vadEndSilenceMs` / `vadMinSpeechMs` / `vadPreRollMs` /
    `vadMaxUtteranceMs`).
  - `rms`: the legacy Prosim2FO energy gate (500/32767, its own 700 ms end-silence, ignores
    the Vad* tuning) — also the automatic fallback if Silero can't initialise (one Warning,
    sticky for the recognizer lifetime; recognition is never lost to the VAD).
  Per utterance an `asr.utterance` JSONL event carries `durationMs`, `peakSpeechProb`,
  `endReason` (`silence`|`hardCap`|`pttRelease`) and `vadEngine` — the tuning evidence for the
  thresholds; field names are stable for the verification workflow.
- Push-to-talk via keyboard hook or joystick; continuous mode optional; ATC-mute binding.
  PTT decides when the FO LISTENS; whether a crew HAIL is accepted is a separate, dataref-side
  gate — see "Interphone transmit gating" below.
- Phonetic snapping (Double Metaphone hybrid); optional context-aware utterance interpreter with a
  confidence ladder.

## LLM (briefings/debrief composition)

- Two API flavours (`briefing.llmApi`): `openAi` — any OpenAI-compatible endpoint, default
  `http://localhost:3000/api` (Open-WebUI) — or `ollama` — Ollama's native `POST {base}/api/chat`
  with base = server root (`http://host:11434`). Optional custom CA / allow-invalid-cert.
- **Thinking models (qwen3.6 etc.) — 2026-08-29:** the hidden reasoning phase adds 10 s+ before the
  first token. Ollama only honours thinking control through the NATIVE API's top-level
  `"think": <bool>`; the OpenAI-compatible `/v1/chat/completions` has no way to pass it, and the
  `/no_think` prompt prefix (a qwen3.5-era trick) does nothing on qwen3.6. `briefing.llmEnableThinking`
  (default false) is therefore sent explicitly in BOTH states on the Ollama flavour — such models
  default thinking ON, so omitting the field would silently re-enable it. Sampling limits move into
  Ollama's `options` object (`num_predict` ≙ `max_tokens`). Replies may carry a separate
  `message.thinking` field when enabled; only `message.content` ever reaches TTS. Ollama streams as
  NDJSON (one object per line, final line `"done": true`), not SSE — the parser accepts both the
  single-object and NDJSON shapes.
- **Every number in LLM output is verified against source facts** (`NumberVerifier`); deterministic
  template fallback when the LLM is unavailable or fails verification.
- **Streamed speech (issue #147, 2026-10-03; `briefing.streamLlm`, default on):**
  `OpenAiChatClient.StreamAsync` asks for `stream: true` and yields text deltas — SSE `data:`
  lines for the OpenAI shape (`choices[0].delta.content`; `data: [DONE]` ends; role-only, usage-only
  and finish chunks carry no text; `:` lines are keep-alives), NDJSON for Ollama
  (`message.content` per line; `done: true` ends; `message.thinking` is never read). Lines are
  read through a UTF-8 `StreamReader`, so a network chunk that splits a JSON object or a
  multi-byte character is harmless. ONE budget (`llmTimeoutSeconds`, 5 s floor) covers the first
  TEXT delta (a model that only thinks does not count) and then the gap between lines.
  `SentenceChunker` cuts the deltas into sentences (not inside "118.10", not after "e.g." or a
  single letter, not before a lower-case word, fragments join the next, 320-character safety
  cut). `StreamingNarrator` verifies each sentence — **spelled-out numbers included**:
  `SpokenNumberText` turns "one six three" / "five thousand" / "one one eight decimal one zero"
  back into digits for the check only (before this, a spelled number was never verified at all)
  — and hands it to the arbiter as the next segment of one `StreamedUtterance`
  (`SpeechRequest.Stream`). The first sentence that fails is dropped with everything after it
  and the template finishes with the sections the pilot has not heard yet (`NarrationCore`:
  a section counts as heard when its numbers, one of its key words and its runway appear in
  the spoken text — a miss repeats a short fact, never loses one); so does a stall (speech
  idle 4 s with no new sentence), a broken stream or an error. Nothing verified before the
  failure → the whole template as one ordinary utterance. No strict re-ask on this path.
  Arbiter rules: one queue item for the whole stream (nothing at its priority or below gets
  between sentences); a **High** callout is spoken between two sentences (owner decision D3);
  a **Critical** cuts the stream and the stream ENDS — `Superseded`, the LLM request cancelled,
  never resumed or restarted (D2; the pilot asks again for a fresh briefing). LLM sentences
  bypass the TTS disk cache (`TtsDiskCache.Bypass`, an async-local scope); template sentences
  use it. `StyledSpeechService` one-liners and `CompleteAsync` callers are unchanged. Session
  events: `llm.stream` (firstTokenMs, firstAudioMs, sentencesSpoken, templateTookOver,
  takeoverReason verify-failed|stalled|timeout|error|none, sectionsFromTemplate, preempted,
  model), `speech.segment` per sentence, `briefing.spoken` / `debrief.spoken` with
  `streamed: true` and the text actually spoken.

## Other data sources

- **Navigraph DFD**: user-supplied SQLite db (`navData.dfdPath`), read via Microsoft.Data.Sqlite for
  SID/STAR/approach/ILS/missed-approach legs. Pin SQLitePCLRaw.lib.e_sqlite3 ≥ 3.53.3 in every
  project (security pin GHSA-2m69-gcr7-jv3q); pin `RuntimeIdentifier win-x64` to avoid multi-RID
  native bloat. Both schema generations are probed (`tbl_p*_` with header `cycle`, legacy
  `tbl_*` with `current_airac`). Tables the briefings read, and what they carry (issue #180,
  2026-10-24 — from the DFD schema documentation; **unverified against a live database**):
  - `tbl_pa_airports` / `tbl_airports`: `transition_altitude` (may be a "FL110" string),
    `transition_level`, `elevation`, `airport_ref_latitude/longitude`, `airport_name`.
  - `tbl_pg_runways` / `tbl_runways`: `runway_true_bearing`, `runway_length` (ft),
    `landing_threshold_elevation`, `runway_latitude/longitude` — the DFD has no TORA/intersection
    data; the briefing's "runway length" is this length.
  - `tbl_pi_localizers_glideslopes`: `llz_identifier`, `llz_frequency`, `gs_angle`.
  - `tbl_pd_sids` / `tbl_sids`: ARINC 424 `route_type` — **`0` = engine-out SID (EOSID)**, 1/4 =
    runway transition, 2/5 = common route, 3/6 = enroute transition. `EngineOutProcedure` takes
    the rows with `route_type='0'` whose `transition_identifier` is `RW<rwy>`, `ALL` or blank,
    first `procedure_identifier` found, `seqno` order, and speaks them with the missed-approach
    leg wording (`course` new / `magnetic_course` legacy, `altitude1`). No EOSID = the
    straight-ahead default sentence.
  - `tbl_pf_iaps` / `tbl_iaps`: final-approach legs; the MAP is the leg with `M` at index 3 of
    `waypoint_description_code`; the missed approach is everything after it.
  - `tbl_ps_airport_msa` / `tbl_airport_msa`: one row per MSA centre (`msa_center`,
    `multiple_code`, `radius_limit`) with up to five `sector_bearing_N` / `sector_altitude_N`
    pairs. `AirportMsaFt` speaks the **highest** non-zero sector altitude across the airport's
    rows — conservative; picking the sector by departure bearing is a follow-up.
- **ActiveSky**: `http://{host}:19285/ActiveSky/API/GetMetarInfoAt?ICAO=` with file-snapshot
  fallback; composed with SayIntentions getWX behind a `IWxProvider` abstraction.

## Architecture notes to preserve

- **No figure reaches the TTS engine as digits** (2026-11-14, owner report: Kokoro read
  "8700 kilograms" as "eighty seven, zero" — every engine reads Arabic digits its own way).
  `Core/Speech/NumberWords` writes the words (`Cardinal`, `Number`, `Decimal`, `Clock`,
  `RadioAltitude`, `GroupForm`); `Phraseology.Normalize` — the one pass every line takes — is
  the net: after the named digit-by-digit rules (FL, heading, squawk, wind, QNH, frequency,
  runway, and since this change altimeter, V-speeds, `speed`/`gusting`/`limit is`/`FMS`/
  `performance` + figure, `### degrees`, `### at #`, `# knots`, `HH:mm`, three-digit stands,
  seats, doors, a bare `27R`) and the abbreviations, every figure still standing alone is a
  quantity and becomes plain words ("6.8" → "six point eight", "2,500", "-2" → "minus two";
  a leading zero or more than six digits reads digit by digit). Digits glued to a letter
  ("A320", "N1") are names and stay. So a composer may write `$"{kg} kilograms"`; write the
  words at the source only when the form is not the default (the radio altitude, a flight
  number). Rule order matters: the direction rule looks ahead for the digit the knots rule
  consumes, and the V-speed rule must run before the abbreviation pass turns "V1" into
  "Vee one". **Radio lines** (`RadioSpeech.Spoken(transmit, style, callsign)`) take the
  pilot's `sayIntentions.phraseology` — `icao` / `faa` ("point", group-form flight number) /
  `uk` ("flight level one hundred") — and only the FO-voice text changes, never the text
  sent to sayAs. Altitudes are "two thousand five hundred" / "one zero thousand" in all three
  (Annex 10 Vol II, AIM 4-2-9, CAP 413 agree). Cockpit talk has one style.
- **Stands and gates** (2026-11-14, #200: check-in "Gate Foxtrot one seven", read-back of the
  same gate "Foxtrot seventeen"). A stand / gate / bay designator **with a letter** ("F17",
  "F 17", "23A", already phonetic "Foxtrot 17" / "23 Alpha") is a name and is digit by digit
  in every path — `Phraseology.Letters` spells it with the caller's joiner, so the read-back
  text sent to SayIntentions is "Gate Foxtrot-one-seven" like the check-in's
  `RadioSpeech.Stand`. A **bare number**: three digits are digits everywhere; one or two
  digits are a number in cockpit talk ("gate twenty three", owner 2026-10-29) and digits on a
  radio line. The boundary is the function, not a tag: `RadioSpeech.Spoken` is the FO-voice
  text of every radio line (read-backs, check-ins, requests) and spells a bare stand number;
  a line that only passes `Phraseology.Normalize` is cockpit talk.
- **Taxiway letters from a language model** (2026-11-14, #200). Streamed sentences do pass
  `Phraseology.Normalize` (`SpeechArbiterService.SynthesizeAndPlayAsync`, the one synthesis
  path of segments and whole lines); `speech.segment.text` is the model's raw sentence and
  the event carries `spoken` since this date. The briefing facts were "taxiway LI closed" /
  "taxiway A, C and TW closed"; the model wrote "taxiway L I closed; taxiways A, C, and T W
  closed" and the list rule stopped at the space and at the Oxford comma. The net was
  repaired rather than the fact sheet: after "taxiway(s)" / "holding point" a spaced pair of
  capitals is one designator (three spaced capitals stay an abbreviation; a second letter
  "I" needs the list's end after it, so "taxiway A I think" keeps its pronoun), and
  ", and" continues a list. Handing the model "Lima India" in the fact sheet would not have
  removed the need — the model rewrites what it is given, the template path speaks the same
  lines, and the net is context-bound by the lead word. The routing words ("via", "on",
  "at") still take single letters only.
- **Abbreviations go through one table** (2026-11-14, owner request: "ECAM, not E C A M";
  SID and STAR were spelled by the engine). `Core/Speech/PronunciationTable`: `Words`
  (capital abbreviation → a respelling the engine reads as the word: "ECAM" → "ee-cam") and
  `Letters` ("APU" → "A P U"). `Phraseology.Normalize` applies it after the number rules and
  before the remaining expansions; the old `ILS`/`QNH`/`ATIS`/`ECAM`/`MCDU` … rules moved
  into its defaults. Matching is **case-sensitive on the capital form** (the old rules
  ignored case; "star" must stay a star), whole word, optional plural "s"; a word entry also
  matches its letter-spelled form ("A T I S", and the old "E CAM"), so hand-spelled files
  and model-written text say the word. The pilot's `pronunciations.json` (seeded) is laid
  over the defaults by `PronunciationTable.LoadUserFile` in `Program.cs` right after the
  seeding — before the host builds, because the TTS prewarm keys its cache on the spoken
  text; the table is a static on `Phraseology` (`UsePronunciations`) since `Normalize` is
  static and called from everywhere. No hot reload. **The respellings are guesses until
  heard**: kokoro, ElevenLabs, Google and the Windows voices each read "ee-cam" their own
  way — that is why the file exists. Never speak an abnormal's `title` (ECAM shorthand,
  "HYD B RSVR LO LVL"); use `FailureMonitor.SpokenFaultName` (the announce line).
- **Airport names have two tiers** (2026-11-14): `IAirportNames` is an `AirportNameChain` —
  `Core/Airports/AirportNameCatalog` over the seeded `airport-names.json` (OpenFlights, ODbL —
  THIRD_PARTY.md; name + city, the 23 crew short forms folded in) FIRST, then
  `DfdAirportNames` (its own curated overrides, then the DFD's `airport_name`). The file
  wins so the pilot's wording beats the DFD's formal name and names work with no DFD at
  all; `ISpokenText.Airport` still falls to the NATO-spelled code when neither knows the
  airport. Never hand a bare ICAO code to the voice — `NatoPhonetics.SpeakIdentifier("EGLL")`
  keeps a leading run of three or more letters AS WRITTEN (right for "VOLA3V", wrong for an
  airport): the FMGC cross-check did exactly that until this change. Use
  `ISpokenText.Airport` / `.Icao`. The cross-check spells both routes when two different
  airports would be said the same, so a mismatch is always audible.
- **Airline callsigns** (2026-11-14): `Core/Speech/AirlineCallsigns` over the seeded
  `airline-callsigns.json` (OpenFlights, ODbL — THIRD_PARTY.md), ICAO code first, then IATA,
  loaded on first use; an unlisted airline is spelled. With SayIntentions running the radio
  callsign is SayIntentions' own (`flight.json`) — the list serves lines that only have a
  flight number (the Duty Day "Next sector" message).

- **Transition reminders** (#198; `Speech/Callouts/TransitionReminderCore` + service). Climb:
  the origin's DFD `transition_altitude`. Descent threshold, in order
  (`ResolveDescentThreshold`, 2026-11-14 — "LOWW TL null" on the owner's flight, the normal
  case where ATC assigns the level): the destination's DFD `transition_level`; else the
  lowest whole-thousand flight level whose pressure altitude is at or above the
  destination's `transition_altitude` for the destination QNH (the Flight Status weather
  card's, the same figure the spoken line reads; ISA altimeter equation — TA 10,000 ft:
  QNH 1022 → FL100, QNH 1000 → FL110); else TA + 1,000 ft while no QNH is known; else no
  reminder. The destination TA is the same `DfdNavDataProvider.Lookup` row as the TL — no
  new source. ATC may assign one level higher (a guaranteed layer); the derived level is
  the lowest possible, so the reminder is late rather than early. `transition.reminder`
  carries `thresholdSource` (`databaseTa` / `databaseTl` / `derivedFromTaAndQnh` /
  `derivedFromTaPlusMargin`) and `thresholdBasis`; the log names the threshold once per
  value in the descent. Whether the DFD's `transition_level` is a flight level (the code
  multiplies by 100) is unverified against a live database, like the rest of the table list.
- **In-flight monitoring** (2026-10, #148): four modules in `Speech/Monitoring`, each a pure
  core plus a thin shell armed by `IFlightPhaseSource.IsLive`, all off by default under
  `sop.monitoring`. Fuel check (`fuel.check` event; navlog last-fix rule in `FuelCheckCore`),
  gross error check (`fo.gross-error-check`; the FMS INIT B ZFW/block datarefs are write-only,
  so live `aircraft.weight.zfw` / total fuel stand in; the Takeoff page fills
  `TakeoffPerfStore`), destination weather watch (`fo.destination-weather`; observes
  `HeroWeatherStore`, edges only, per-trigger rate limit), standalone read-backs
  (`fo.readback`; `StandaloneReadbacks` is registered BEFORE `RadioExecutor` / the FCU
  executor so "altimeter …" / "runway …" with a figure are checks, not settings, and a bare
  lead-in is declined so "minimums check" keeps its owner). `NumberExtractor.ExtractAll`
  reads every figure in order with hundred/thousand and splits an unbroken nine-digit run
  into three speeds.
- **FO questions** (2026-10, #149): `Speech/Questions`. The router offers the RAW
  transcription to `IFreeFormQuestionHandler` at BOTH idle-miss sites (interpreter reject and
  the end of `RouteText`), only when `IRecognitionWindow.FreeFormCapable` (= the LAN engine is
  active) — so a question is never considered on the offline closed-grammar engine, which
  cannot produce one anyway. `FoFactSheetBuilder` (Core, pure) prints "- Label: value" lines
  and the allowed-number set, including the spoken forms (tonnes to one decimal rounded on
  the DECIMAL value, clock as hhmm / hh / mm, durations as hours / minutes / total minutes,
  runway and callsign digit runs) — `NarrationCore` verifies each streamed sentence against
  it, so "six point two tonnes" verifies against 6200 kg. The service streams through
  `StreamingNarrator` with an EMPTY template (`NarrationPlan.Sections = []`; the narrator
  then speaks nothing on failure and the caller decides), cancels the "stand by" timer and
  the time budget on `NarrationPlan.OnFirstSpeech`, and does ONE strict `CompleteAsync`
  re-ask only when the stream failed verification before any speech. Sterile cockpit drops the
  question silently (recorded as outcome `sterile`). Nested list defaults are barred by the
  option-section guard, so `foQuestions.leadIns` is a comma-separated string.
- **SpeechArbiter**: single priority queue (Low/Normal/High/Critical) with pre-emption and pluggable
  suppression rules (sterile cockpit below 10,000 ft suppresses Low, defers Normal). All speech goes
  through one facade — no direct TTS calls from features.
- Humanized key timing for FCU/MCDU presses (randomized hold/gap, think pauses).
- MCDU actuation gates: arm switch + FO-is-PF + announced/verified/abortable.

## Speaker roles & accents (ADR-0006)

- Roles: FirstOfficer (default), Purser (CAB interphone), Company (ACARS), **GroundCrew**
  (INT interphone — hail replies + upcalls; `voices.ground`, intercom filter default on).
- Interphone receive gating: purser speech waits for a `S_ASP*_CAB_REC_LATCH`, ground speech
  for a `S_ASP*_INT_REC_LATCH` (any of the three ACPs), grace-then-play-anyway. Upcalls flash
  the MECH call first (`S_OH_CALLS_MECH` momentary press, allow-listed).
- **Interphone transmit gating** (issue #72, `speech.acpTransmitGating`, default on): pilot
  hails are latched to the captain ACP transmit selection `S_ASP_SEND_CHANNEL`
  [0 None, 1–3 VHF, 4–5 HF, 6 INT, 7 CAB, 8 PA] — "cockpit to ground" needs 6/INT,
  "cockpit to crew" needs 7/CAB. Wrong selector: one FO coaching line per session, then the
  hail simply goes unanswered. Selector leaves the channel mid-dialogue = hang up (crew stands
  by). Optional `speech.acpIntKeyRequired` also demands the momentary `S_ASP_INT_SEND`
  [0 normal, 1 pushed] at hail time for ground calls; the selector latch keeps the call open.
  Missing/stale datarefs → Unknown → gate stands down, hails accepted as before.
  **TRAP: never gate on `S_ASP_INTRAD`** — the INT/RAD rocker is repurposed as the GSX
  "force next service" smart button (GsxAutomationService); reading it here would fire ground
  services whenever the pilot keys the intercom. Only the pilot→crew direction is transmit-
  gated: upcalls and purser reports are the crew calling US, so they keep receive-side gating
  only.
- **Accent localization** (`accents.*`, GroundCrew only): ICAO prefix → Chirp 3 HD locale
  (`AirportAccentMap`, user-overridable), resolved per provider at render time — Google gets
  `{locale}-Chirp3-HD-{persona}`, Kokoro gets `bm_george` for en-GB/AU/IN, everything else
  falls back to `voices.ground`. `speech.localOnly` therefore degrades accents to US/UK.
- **FO roster voice** (issue #194, `persona.roster.*`): the drawn First Officer's
  `provider:id` voice from `fo-roster.json` rides the arbiter's voice-override slot for the
  FirstOfficer role only (`RoleVoiceResolver.Resolve(role, voices, firstOfficerVoice)`), NOT
  the accent selector: a provider-qualified id must promote its provider to the front of the
  TTS chain, which only the override slot does (the selector answers per provider and cannot
  reorder). Null voice = `speech.foVoice`, exactly as before; the purser / company / ground
  roles never read it. The same-FO rule across a restart is `fo-roster-state.json` (data root,
  beside `daystate.json`) — deliberately not `settings.json`, whose page saves write a whole
  section and would clobber a draw made after the page loaded.
- **Sim calendar day** (`ISimLocalTime.LocalDate`): `LOCAL YEAR` + `LOCAL DAY OF YEAR`
  SimVars (units `number`), valid only while the `LOCAL TIME` clock is fresh. Unverified live
  as of 2026-11-01 — if they never push, the roster logs "the sim's calendar day is not
  available" once and draws per UTC wall date. `ZULU TIME` (seconds) sits behind
  `ISimLocalTime.ZuluTimeOfDay` for the small talk's sun position (`SolarPosition`, NOAA
  low-precision; about a degree — never operational).
- **Cruise small talk** (issue #195, `smallTalk.*`): `CruiseSmallTalkService` is the
  `RegionFactService` shape — a 30 s tick, a pure core, two tiers. Three things to keep:
  (1) the Offer is NOT spoken by the service — it is raised on `SmallTalkOfferStore` and the
  shared `CaptainsQuestionDialogue` speaks it at the question dialogue's own (High) priority,
  so the core's cruise / altitude / nothing-open gates are the only thing keeping it out of a
  sterile moment (every other kind is Low `smalltalk.*` and the sterile rule drops it);
  (2) the quiet clock is reset by EVERY arbiter Spoken/Segment event (purser, company,
  checklist included), so a line only ever follows real silence; (3) the region facts win a
  tie — `RegionFactService.NextDueUtc` within three minutes holds the small talk. The purser's
  coffee line goes through `CabinCrewService.AnnounceAsync` with a `cabin.*` tag, so a descent
  begun while the kettle boils never blocks it.
- **FO walkaround** (issue #196, `groundCrew.walkaround.*`): `WalkaroundService` is the
  `GroundCrewUpcallService` shape — a 1 s tick, a pure edge-baselined core, the upcalls'
  delivery (MECH press on `S_OH_CALLS_MECH`, `IAcpChannel.AwaitReceiveAsync(Intercom, grace)`,
  then speak). The report's role is the new `SpeechRole.FirstOfficerOnInterphone`: the FO's
  voice (the roster FO's when drawn) with `voices.groundFilter` as the intercom decision —
  appended LAST in the enum so the earlier roles keep their values. "The FO is away" is ONE
  Core store, `FoAvailabilityStore`: the utterance router skips every feature whose
  `VoiceFeatureDeclaration.AvailableWhileFoAway` is false and speaks the away line at the idle
  miss (RTO, RoleManager, SmallTalkService's "quiet please" and GsxVoiceService declare true);
  the departure pump's `PreHold` holds the refuel row on it (reason "waiting for the First
  Officer's walkaround report"). The ready predicate is the flight engine's ground facts
  (parked, engines off) + the GSX diagnostics `GroundPrep.Stage == "Complete"` — the Core
  mirror of the prep coordinator, the only one the Speech pillar may read. A finding's icing
  outcome calls `DeiceRequestStore.Publish(Ask, …)` with the local METAR's OAT/precip; the GSX
  policy may re-judge it later and the captain's answer, once given, stands.
- SayIntentions requests (issue #52): immediate FO "Roger — calling {station}" ack, then the
  exact sayAs text spoken in the FO voice (`sayIntentions.foSpeaksTransmission`, default true
  — turn off if SI voices sayAs audibly on this setup).
