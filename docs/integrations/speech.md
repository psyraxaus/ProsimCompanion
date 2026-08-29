# Speech / AI stack reference (voice FO pillar, Phase 5)

From Prosim2FO. All endpoints are user-hosted or cloud; every provider is optional with fallback
chains — "local only" hard mode must exist.

## TTS (router order: Kokoro → Google → WinRT → SAPI5)

- **Kokoro** (kokoro-fastapi, local neural): OpenAI-compatible `POST {base}/v1/audio/speech` (WAV)
  and `GET {base}/v1/audio/voices`. Default `http://192.168.1.50:8880`, model `kokoro`, voice
  `bm_george`, per-voice disk cache. Two timeout phases (issue #113): `kokoroTimeoutMs`
  (1500 ms) covers connect/first byte only — the "is the host awake?" check; the streamed
  WAV body gets `kokoroBodyTimeoutMs` (15 s) + 100 ms per character, because kokoro-fastapi
  synthesises while it streams and a long briefing takes seconds. A timeout surfaces as one
  `TimeoutException` sentence naming the phase; the router still cools the provider down 60 s.
- **Google Cloud TTS Chirp 3 HD**: service-account JSON key, LINEAR16; disk cache keyed by
  text+voice+format; prewarm checklist phrases; monthly usage counter (`cache/tts/usage.json`)
  against the 1M-char free tier.
- Playback: NAudio, device selection, intercom band-pass filter + listening tone.

## Recognition (chain: LAN → WinRT → offline System.Speech)

- **LAN ASR** — two server flavours, selected by `speech.asrApi` (`AsrServerApi` holds the
  wire differences; the recognizer is flavour-agnostic):
  - `fasterWhisper` (Prosim2FO `deploy/faster-whisper` wrapper): `GET /health`,
    `POST /transcribe` (multipart WAV 16 kHz) → `text`, `duration`, `confidence`, `avg_logprob`,
    `no_speech_prob`, word timestamps; biasing fields `initial_prompt`/`hotwords`/`vad_filter`.
  - `whisperCpp` (whisper.cpp `whisper-server`, e.g. native on a Mac mini since 2026-08-29):
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
  Default `http://192.168.1.50:8000`; readiness polling + optional wake-on-LAN (MAC/broadcast/port 9).
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

## Other data sources

- **Navigraph DFD**: user-supplied SQLite db (`navData.dfdPath`), read via Microsoft.Data.Sqlite for
  SID/STAR/approach/ILS/missed-approach legs. Pin SQLitePCLRaw.lib.e_sqlite3 ≥ 3.53.3 in every
  project (security pin GHSA-2m69-gcr7-jv3q); pin `RuntimeIdentifier win-x64` to avoid multi-RID
  native bloat.
- **ActiveSky**: `http://{host}:19285/ActiveSky/API/GetMetarInfoAt?ICAO=` with file-snapshot
  fallback; composed with SayIntentions getWX behind a `IWxProvider` abstraction.

## Architecture notes to preserve

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
- SayIntentions requests (issue #52): immediate FO "Roger — calling {station}" ack, then the
  exact sayAs text spoken in the FO voice (`sayIntentions.foSpeaksTransmission`, default true
  — turn off if SI voices sayAs audibly on this setup).
