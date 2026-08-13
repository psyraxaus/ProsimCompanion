# Voice-gated ground operations & crew immersion — design

Status: agreed 2026-08-14 (owner discussion) · ADR-0006 records the architectural decision.
Work packages tracked as GitHub issues (WP1–WP4).

## Motivation

Ground operations today are either fully automatic (prep chain, departure sequencer) or
single-shot voice phrases with no feedback ceremony. The owner wants flight-deck realism:
services gated behind spoken crew calls ("Cockpit to ground" → "Go ahead, captain" →
"Request refueling"), audible crew responses in localized accents, and positive FO
acknowledgement of ATC requests (today a SayIntentions request transmits silently and the
clearance "just appears").

## Locked decisions (from the 2026-08-14 discussion)

1. **Both dialogue forms**: a hail ("cockpit to ground" / "flight deck to ground",
   "…to crew"/"…to cabin") opens a crew-answered listening window; the existing single-shot
   phrases ("request refueling") keep working unchanged.
2. **Voice is an additional trigger, never the only one.** Web UI, HTTP API, Stream Deck and
   the INT/RAD smart button bypass voice gates. All dispatch stays on the single-writer
   command-registry path — no second write path.
3. **The prep gate holds the entire chain** — reposition, gate anchor, ground equipment,
   jetway — until ground services are commenced (voice or any other trigger).
4. **Channels like real life**: purser speaks on CAB (existing), ground crew on INT. Hearing
   them requires the matching ACP receive latch (any of the three panels), with the same
   grace-then-play-anyway fallback the purser already uses.
5. **SayIntentions**: the FO acknowledges an ATC request immediately and (optionally) voices
   the transmission locally before `sayAs`. No fabricated ATC replies — readbacks remain the
   SI copilot's job (`flight.json` carries no clearance transcript to fake from).
6. **Accents are automatic**: ground-crew voice locale follows the airport country
   (ICAO-prefix map); graceful degradation to the configured voice when the provider can't
   serve the locale (Kokoro = US/UK English only; Google Chirp 3 HD = 51 locales).
7. **"Cockpit to ground" is repurposed** from "start departure services" to the hail. The new
   phrase for starting is **"commence ground services"** (plus the surviving
   "start ground services").

## WP1 — Voice gating of GSX automation

### Ground-prep gate

- New option `gsx.groundPrepActivation`: `"auto"` (default, today's behaviour) or `"voice"`.
- In `voice` mode `GsxGroundPrepCoordinator` holds **before the Reposition stage** with a
  visible hold reason until departure services have been started
  (`IGsxDepartureControl.Started`). No new command: "commence ground services" maps to the
  existing `gsx.startDepartureServices`, so the web button / API / Stream Deck release the
  same gate (decision 2).
- Degraded mode: mic-less users set `auto` or press the web button; nothing blocks startup.

### Per-service voice activation

- New `GsxServiceActivation.Voice` value for departure-queue steps
  (`Core/Configuration/DepartureServiceStep.cs`). Sequencer semantics = `Manual` (the
  sequencer never auto-calls it; an on-demand request or force-next advances it), but with a
  distinct hold reason ("awaiting your call — say 'request …'") so the status board shows
  what the sequencer is waiting for.
- Recommended immersion queue (documented, not default): Refueling/Catering/Boarding =
  `voice`, rest unchanged.

### On-demand gate parity (closes an existing hole)

`GsxServiceControl.TryCallCoreAsync` gains two guards that today only exist on the automatic
sequencer:

- **Flight-plan gate**: `Refueling`, `Catering`, `Boarding` refuse while
  `gsx.requireOfpBeforeDeparture` is true, the flight phase is `ColdAndDark`/`Preflight`,
  and no plan is present (same rule as the sequencer: `efb.simbriefPlanImported` OR valid
  `aircraft.fms.origin`+`destination`). Spoken/reported reason: "no flight plan in the
  MCDU or EFB yet". Extracted into a shared helper so sequencer and on-demand path use one
  implementation.
- **Session gate**: on-demand triggers refuse while the sim session is definitively
  `NotInSession`/`Walkaround`. `Unknown` (SimConnect absent) does **not** block —
  degrade-not-fail.

## WP2 — Crew hail dialogues, ground-crew persona, upcalls

### GroundCrew speaker role

- `SpeechRole.GroundCrew` + `voices.ground` (Kokoro default `am_michael`) +
  `voices.groundIntercomFilter` (default true) resolved in `RoleVoiceResolver`.

### Hail dialogues (new `CrewHailService`, Speech pillar)

- Phrases: "cockpit to ground", "flight deck to ground" → ground crew; "cockpit to crew",
  "flight deck to crew", "cockpit to cabin", "flight deck to cabin" → purser.
- Flow: borrow the mic (`IMicOwnership`), speak the crew reply in-role and awaited
  ("Go ahead, captain" variants), `ListenAsync` with a narrow grammar
  (ground: GSX request phrases + "commence ground services" + cancel words;
  cabin: boarding phrases + cancel words), timeout ≈ 8 s → "Standing by." in-role.
- Recognized requests dispatch through the same phrase→command catalog as
  `GsxVoiceService` (extracted so there is exactly one phrase table), but the outcome is
  spoken by the hailed crew role ("Copied — fuel truck on the way") instead of the FO.
- Ground replies respect the INT receive latch (decision 4): reply is held until an INT
  latch (`S_ASP{,2,3}_INT_REC_LATCH`) is up or the grace elapses — mirroring
  `CabinCrewService.RingAndWaitAsync`.

### Ground-crew upcalls (new `GroundCrewUpcallService`)

Crew-initiated calls, once per flight cycle each (reset on `FlightCycleReset`):

| Event | Source | Line |
|---|---|---|
| Ground power connected | groundPower dataref edge | "Cockpit, ground — ground power connected." |
| Chocks in place | chocks dataref edge | "Chocks in place, cockpit." |
| Refueling complete | diagnostics stage → Completed | "Refueling complete — {fuel} on board." |
| Catering complete | diagnostics stage → Completed | "Catering finished, all doors closed." |

Delivery mirrors the purser interphone flow on INT: optional MECH call (momentary press of
the overhead calls dataref → ACP lamp), wait for an INT receive latch or the grace period,
then speak in the GroundCrew role. Options section `groundCrew`: `enabled`,
`requireIntChannel` (true), `intChannelGraceSeconds` (25), `mechCall` (true).

## WP3 — SayIntentions positive feedback

In `SayIntentionsService`:

1. **Immediate FO acknowledgement** on phrase recognition, before any network call:
   randomized "Roger — calling {station}." (arbiter, Normal). Always on (it is the fix for
   the silent-clearance defect).
2. **Locally voiced transmission**: new option `sayIntentions.foSpeaksTransmission`
   (default **true**): after auto-tune + settle, the FO speaks the exact `sayAs` message
   text (awaited), then the request transmits. If SI turns out to also voice `sayAs`
   audibly on the user's setup (live-verify), the toggle turns local voicing off.
3. Readback stays with the SI copilot (`SIAI_COPILOT=1`) — unchanged.

## WP4 — Accent localization

- New options section `accents`: `enabled` (true), `googlePersona` ("Charon"),
  `overrides` (map ICAO-prefix → locale, wins over the built-in table).
- Built-in `AirportAccentMap`: longest-prefix ICAO → Chirp 3 HD locale (`Y`→en-AU,
  `EG`→en-GB, `K`→en-US, `LF`→fr-FR, `ED`→de-DE, `LI`→it-IT, `LE`→es-ES, V-India→en-IN,
  `RJ/RO`→ja-JP, …). Unmapped → no override (configured voice).
- Applies to the **GroundCrew** role only (purser/company stay as configured; airline-based
  purser accents are future work). Airport source: `aircraft.fms.origin` while on the
  ground pre-departure, `aircraft.fms.destination` from TaxiIn/Shutdown.
- Per-provider resolution: the Google provider gets `{locale}-Chirp3-HD-{persona}`; local
  providers (Kokoro) get a mapped US/UK voice for English locales (`en-GB`/`en-AU`/`en-IN`
  → British set, `en-US` → American set) and the configured `voices.ground` otherwise.
  `speech.localOnly` therefore degrades accents to US/UK automatically.

## Live-verification list (flight test)

- [ ] Does SayIntentions voice `sayAs` audibly? (decides `foSpeaksTransmission` default docs)
- [ ] Chirp 3 HD non-English voice speaking English → usable accent? (undocumented by Google)
- [ ] Kokoro cross-language voices speaking English → expected garbled; confirm and document
- [ ] INT latch gating with the audio pillar's Couatl volume mapping active (no fight)
- [ ] Hail dialogue under LAN-ASR loss (System.Speech closed-grammar fallback)
- [ ] Prep gate in `voice` mode across a full cold-and-dark start

## Out of scope (future candidates)

- Pushback ground-crew dialogue replacing the silent beacon choreography
- Fabricated ATC voice when no ATC provider is active
- Airline-based purser accents; walkaround crew greeting
- Voice phrases for water/lavatory/cleaning/jetway/stairs/deboarding (add to the catalog
  as wanted — the extraction in WP2 makes this one-line-per-phrase)
