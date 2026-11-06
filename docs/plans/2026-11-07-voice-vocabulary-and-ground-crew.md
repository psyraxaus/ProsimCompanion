# Plan — voice vocabulary, real-life ground crew, and an intent tier (2026-11-07)

Owner request 2026-11-07: "commence boarding" / "start boarding" should work, the app should
sound more like real life, and our own speech engine should do every voice so the
SayIntentions ramp crew is no longer needed. Owner picked **both** options: the phrase grid
plus the pushback crew voice, AND an LLM intent tier for free wording. Investigation findings
are in the chat of 2026-11-07; this file is the package plan. Nothing here is built yet.

## What exists (so the packages build on it)

- `Speech/Gsx/GsxVoicePhrases.cs` — the one phrase → command table (exact wordings). The
  single-shot `GsxVoiceService` and the hail dialogues (`CrewHailService`) both read it; the
  VOICE drawer and the microphone grammar are generated from it.
- `Recognition/CommandMatcher.Snap` — spelling + sound-alike score onto the nearest phrase,
  pass mark `speech.snappingThreshold` (0.7); the gray band asks "did you mean …?"
  (`UtteranceInterpreter` → `InterpretKind.Confirm`). Free text reaches the LLM only as a
  question (`IFreeFormQuestionHandler`), never as a command.
- `SpeechRole.GroundCrew` — accents per airport, INT receive gating, MECH call, hail replies
  and upcalls (`GroundCrewUpcallService`, `InterphoneRing`).
- `Gsx/Sync/GsxPushbackSequenceService` — reads `L:FSDT_GSX_PUSHBACK_STATUS` (3/4 tug
  connected), `L:FSDT_GSX_VEHICLE_PUSHBACK_STATE` (8 pushing, 11 wait-shutdown, 12 awaiting
  engine start, 13 disconnecting, 14 clear) and `L:FSDT_GSX_BYPASS_PIN`; answers the state-12
  "confirm good engine start" menu itself; writes everything to the decision log only.
  **Nobody speaks on these edges** — that is the SayIntentions ramp crew's job today
  (comms channel INTERCOM3, which our code ignores).
- SayIntentions stays the ATC: `sayAs` carries our FO's read-backs (our voice speaks them
  locally); frequencies, hand-offs, the arrival gate and weather are data.

## Option 1 — the phrase grid and the ground crew's voice

### V1 — the phrase grid (Speech, one commit)

1. `GsxVoicePhrases` builds its table from a grid: verbs × services.
   Verbs: `request`, `start`, `commence`, `begin`, `call`, `send`, `let's start`, `can we start`,
   `please start`. Services and their nouns: boarding (`boarding`, `the boarding`), refuelling
   (`refueling`, `refuelling`, `fuel`, `the fuel truck`), catering (`catering`), pushback
   (`pushback`, `push back`, `the pushback`), de-icing (`de-icing`, `deicing`, `de-ice`),
   ground power (`ground power`, `the gpu`, `the ground power unit`, `external power`).
   Hand-written specials stay (`fuel confirmed`, `connect …`, `disconnect …`, `no de-icing`,
   `next service`). The success / crew-ack lines stay per service, not per phrase.
2. Filler stripping before the snap: a leading address (`ground`, `ground crew`, `cabin`,
   `cabin crew`, `cockpit to ground` already a hail) and politeness (`please`, `could you`,
   `can you`, `would you`) are removed in one pure helper used by the router's snap and by the
   hail listen. "Ground, please start boarding" → "start boarding".
3. Boarding is asked of the cabin, not ground (real life): the FO's single-shot line becomes a
   relay — FO "Cabin crew, commence boarding." then the purser's "Boarding underway." (the
   cabin-ack path that exists for the cabin wordings becomes the default for every boarding
   phrase). The ground hail keeps accepting boarding (GSX-wise it is the same trigger) but the
   ground crew answers "Copied — I'll tell the gate."
4. Tests: the grid contains every old phrase (no regression), "commence boarding" and
   "ground, please start boarding" resolve without a did-you-mean round trip against the full
   route vocabulary; the VOICE drawer lists the grid grouped per service, not 60 lines.
5. Probe: `gsx-voice-phrase-grid` (assert-absent: a `voice.unmatched` whose text contains a
   service noun with one of the grid verbs). No new UI text expected (the drawer reads the
   table); if a group title is added it ships in all seven packs.

### V2 — the pushback crew speaks (Core + Gsx + Speech, two or three commits)

1. Core: `GroundOpsSignals.PushbackProgress` — `event Action<PushbackStage>` with
   `PushbackStage { TugConnected, BrakesReleaseRequested, Pushing, PushComplete,
   EngineStartCleared, Disconnecting, Clear }`, raised by the GSX side; a
   `PushbackProgressStore` snapshot for the Flight Status card and the diagnostics.
2. Gsx: `GsxPushbackSequenceService` raises the stages from the LVAR edges it already reads
   (3/4 → TugConnected; 8 → Pushing; 11 → PushComplete; 12 → EngineStartCleared, when the
   service's own menu answer went in; 13 → Disconnecting; 14 or bypass pin 0 → Clear). The
   beacon sequence raises `DoorsClosed` and `BridgeRemoved` from its steps. One `gsx.pushback`
   session event per stage.
3. Speech: `PushbackCrewService` (GroundCrew role, the upcall shape — `InterphoneRing`, INT
   receive gating, accents): "Brakes released?" → listens on a tiny grammar
   (`released`, `brakes released`, `parking brake released`) → "Cleared to push, tail left."
   (direction from `PushbackChoiceStore`) → "Cleared to start engines." at EngineStartCleared
   (per-engine "Clear to start two" when the start sequence says which) → "Pushback complete,
   set parking brake." → "Disconnecting, bypass pin removed, hand signal on your left — have a
   good flight." A captain's "ready for pushback and start" / "ready for push" phrase joins the
   grid with the ack "Copied — stand by for the tug." Ground also says "All doors closed,
   bridge removed." when the beacon sequence finishes those steps.
4. Options (`groundCrew.pushbackCrew.*`: enabled, brakesCheck, engineStartClearance, the
   texts) with controls on the Ground Crew settings card and texts in all seven packs. Every
   line is Low/Normal priority through the arbiter; callouts still pre-empt.
5. Rig steps (owner, by hand): turn the ramp crew off in SayIntentions; turn GSX's own crew
   audio off in GSX. Check first whether the SayIntentions ramp crew also answers GSX's menus —
   if it does, only one of the two may stay on.
6. Tests: a pure `PushbackCrewCore` over the stage edges (one line per edge, never twice, the
   brakes question only once per push); the LVAR → stage mapping in the Gsx tick core.
   Probe `pushback-crew-dialogue`.

### V3 — the smaller real-life lines (Speech, one commit)

- Fueller: "Fuelling in progress, N tonnes requested." at the Refueling Active edge (the
  completion upcall exists).
- De-ice crew: "De-icing complete, Type two — holdover starts now." at `DeiceCompleted`
  (the holdover card already knows the fluid type).
- Purser: one report "Boarding complete, N on board, doors closed, cabin ready." when boarding
  completes and the doors close (today "cabin secure" comes at beacon-on; keep it).
- Texts configurable on the Ground Crew / Cabin cards, all seven packs; probe
  `crew-lines-2026-11` (assert-present per edge).

## Option 2 — the LLM intent tier (Speech, two commits)

1. `ICommandIntentResolver` (Recognition): given the raw utterance and the live vocabulary,
   returns ONE phrase from that vocabulary or none. Implementation over `OpenAiChatClient`
   (temperature 0, two-second budget, the vocabulary in the prompt, the answer must be an
   exact member of the list — anything else is "none"). Never a free action: the resolver can
   only pick a phrase the router could have fired anyway.
2. Where it sits: `UtteranceRouter`, idle utterances only (no item awaiting, no dialogue
   borrow), after the snap rejects and before the did-not-catch line, only when the LAN ASR
   is active (`FreeFormCapable`) and `LlmHealthStore` is healthy (the 401 circuit breaker of
   2026-08 stays the gate). Questions keep precedence: a sentence the question handler takes
   is not a command.
3. Trust levels — setting `speech.intentTier`: `off` | `confirm` (default) | `auto`.
   `confirm`: the pick goes through the existing "did you mean {phrase}?" round for any phrase
   that writes to the aircraft or calls GSX; read-only queries (loadsheet, fuel check, wind
   check, logbook) fire at once. `auto`: everything fires. Control on the Listening settings
   card, texts in all seven packs.
4. Record `voice.intent {utterance, phrase, outcome: fired|confirmed|declined|none, ms}`;
   the unmatched-candidates store keeps the raw text as today.
5. Tests with a fake resolver: a pick outside the vocabulary is refused; a GSX command under
   `confirm` asks first; a query under `confirm` fires; the tier never runs on the offline
   engine or while a dialogue holds the mic. Probe `voice-intent-tier`.
6. Cost and latency: one short completion per unmatched idle utterance; the prompt lists at
   most the route vocabulary (about 250 phrases). The budget keeps the did-not-catch line
   within three seconds when the model is slow.

## Order and branch

V1 → O2 (both are vocabulary; O2 is tested against the grid) → V2 → V3. Sequential commits on
the checked-out branch (branching rule 2026-10-24). Each package ships its probe, its settings
controls and its seven-pack texts in the same commit. Nothing merges; nothing is flown until
the owner flies it.
