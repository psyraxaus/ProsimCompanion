# Voice First Officer

## Talking to the FO

- **Push-to-talk** (default): bind a key or joystick button on the First Officer settings
  page; hold, speak, release. A listening tone confirms the mic opened. Continuous mode is
  available for boom-mic setups.
- **Recognition** tries your LAN faster-whisper box first (if configured) and falls back to
  Windows offline recognition. Misheard phrases snap to the nearest expected phrase; a
  genuinely unclear response gets a "say again?".
- A separate **ATC-mute** binding silences the FO while you transmit to online ATC.

## What can I say?

Press the **mic pill** in the header of any page (it reads READY, LISTENING or PAUSED — the
FO's ear). The Voice Reference drawer lists every phrase
that is live right now — FO, ground crew, cabin crew, ATC and checklists on their own tabs,
with what each phrase does, what comes back, and the badges that matter (pilot-flying only,
INT/CAB channel, phase, value, dialogue). Type a word to filter every tab. Phrases from your
own `commands.json` appear under "From commands.json". Open it directly with
`http://localhost:5320/?voice=1`.

### Ask the First Officer

Everything above is an exact phrase. With **Ask the First Officer** switched on (Settings →
Voice First Officer → Briefings & LLM) you can also just ask: *"what is our fuel on board"*,
*"how long to top of descent"*, *"tell me the destination weather"*, *"are we above the
minimum takeoff fuel"*, *"what time do we land"*. The rule: the utterance starts with one of
the question lead-ins (what, how, when, tell me, are we, question…), is at least four words
long, and matched no exact phrase — known phrases always win, so nothing you could say before
changes. The FO answers in one or two sentences from what the app knows right now (phase,
fuel and plan figures, weights, distance and ETA, block and flight times, the weather cards,
the briefed minima, the OFP basics, open tech-log items) and from nothing else: *"I don't have
that."* when the facts do not cover it. Every number in the answer is checked against those
facts; an answer that fails the check is asked for once more, strictly, and after that you
hear *"I don't have a verified answer for that."* After two seconds of silence the FO says
*"Stand by."*; after six seconds without an answer, the fixed line. The answer is only ever
spoken — it is never read as a command, so an answer that happens to contain "set heading"
does nothing. Below ten thousand feet in the climb, descent and approach (the sterile
cockpit) a question is heard but not answered. Needs the LLM (same card) and the **LAN speech
server** — the offline Windows engine only hears exact phrases.

**Small talk and fun facts** (a second switch on the same card, off by default) lets you ask
anything: *"who is better, Chelsea or Arsenal"*, *"tell me a fun fact"*, *"what is the
capital of Peru"*. No lead-in word is needed — any sentence of four words or more that matched
nothing else goes to the FO. A question that mentions the flight (fuel, weights, speeds,
altitude, runway, weather, times, passengers…) still takes the strict path above. On the
small-talk path the FO answers from general knowledge, in character, one or two light
sentences; numbers there are trivia and are not verified, but a sentence about *this* flight
with a figure in it is thrown away and you hear the fixed line instead. If the FO says *"Let
me check."* the question turned out to be about the flight and the strict answer follows.

**"What are we flying over?"** (a third switch on the card, off by default) answers the
question every passenger asks: *"what are we flying over"*, *"where are we"*, *"what is that
city on the left"*, *"what country is this"*. The FO answers at once from an **atlas built
into the app** — no internet needed — naming the country (and which part of it), the
mountains, desert or sea below, and the nearest notable towns with distance and which side
they are on: *"We're over the Alps in northern Italy, about 30 miles north of Turin. Nearest
town is Aosta, 12 miles out on the left."* Then the language model adds a fact or two about
those places. These phrases need no lead-in word and no minimum length, and they work with
small talk off. The position line never comes from the model, so it is always the same for the
same spot; "left" and "right" are relative to the track, so in a strong crosswind a town
"ahead" may sit a few degrees off the nose.

**Wikipedia facts** (a fourth switch, only with the one above) makes the FO take those facts
from a short Wikipedia summary of the nearest town instead of the model's memory: one small
request to `en.wikipedia.org` per place, a three-second limit, cached after the first time,
and nothing but the place name is sent. If the fetch fails or finds nothing, you still get the
position and the model's own facts.

The atlas: country outlines from [Natural Earth](https://www.naturalearthdata.com/) 1:110m,
seas and natural regions from Natural Earth 1:50m (both public domain), towns from
[GeoNames](https://www.geonames.org/) (`cities15000`, towns of 25 000 and up plus every
capital and first-order seat; licensed [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/)).

## Spoken checklists

Say the checklist's start phrase (e.g. *"before start checklist"*). The FO reads each
challenge and listens indefinitely for your response; wrong responses get an "are you sure?"
and the FO never gives up — say **"skip"** to move past an item, **"say again"** to repeat,
**"hold the checklist" / "resume"**, **"cancel checklist"** or **"restart checklist"**
anytime. Items with a dataref condition verify against the aircraft; the visual runner on the
Checklists page stays in sync. The flight-control check is fully monitored (your sweep is
watched at 30 Hz, then the FO answers with the FO-side controls).

A `confirmCallout` in your checklist file may carry a live value: `{fuel}` (or
`{fuelQuantity}`), `{altimeter}`, `{qnh}`, `{v1}`, `{vr}`, `{v2}`, `{flex}`, `{runway}`,
`{flightLevel}`, `{takeoffConfig}`. A brace word the app does not know is left out, never read
aloud (a warning names it in the log once).

## Callouts & monitoring

SOP callouts (thrust set, one hundred, V1, rotate, positive climb, transition, one-thousand-
to-go, approach 1000/500, minimums, rollout calls) fire from live data; minimums arm only
from the minima you enter on the First Officer page. Stabilized-approach gates call
*"stabilized"* or *"unstable, go around"* at the 1000 ft gate. The gates are yours to set —
airlines differ — on **Settings → Voice First Officer → Callouts & Placards → Approach
Gates**: per gate the height, whether the gear must be down, the minimum flap lever, the
speed band around VLS, the maximum sink rate, and whether a pass is spoken. Add a gate (a
1500 ft gate, say) or remove one. The same page holds the flap and gear placard speeds and
the height of the *"gear still down"* reminder in the climb. Flow monitoring speaks
advisories (landing lights, flaps/gear ceilings, parking brake with thrust, seatbelts, icing
conditions, anti-ice left on, ISA deviation) once per condition with a cooldown. Sterile
cockpit suppresses chatter below 10,000 ft in climb/descent phases.

### In-flight monitoring

Four extra monitors live on the same page under **In-flight monitoring**. Each has its own
switch and all are **off** until you turn them on; each needs the flight to be live (ProSim
connected, real flight data).

- **Fuel check.** In the cruise, every 30 minutes (your interval) and whenever you say
  *"fuel check"*, the FO reads the last flight-plan fix you passed, the fuel on board, how far
  you are from the SimBrief plan at that point, and the estimated landing fuel against the
  planned figure: *"Fuel check. Past KONAN, fuel on board 6.2 tonnes, 200 kilos below plan.
  Estimated landing fuel 2.9 tonnes, planned 3.1."* A shortfall beyond your margin is spoken
  with priority. The check needs the SimBrief OFP (the navlog comes with it) and the aircraft
  position; without them it falls back to burn rate × time to the ETA.
- **Gross error check.** Once the final loadsheet has been sent and the FMS PERF TO page has
  V-speeds, the FO compares the aircraft's zero fuel weight and fuel with the loadsheet, and
  the FMS flaps, flex and V1/VR/V2 with the result on the **Takeoff** performance page —
  *"Gross error check: checked."* or each mismatch by name. Once per loadsheet; again if the
  final is revised. Say *"gross error check"* to run it any time. If you never used the
  Takeoff page the FO says so ("takeoff performance not compared").
- **Destination weather watch.** From the cruise, when the destination METAR changes in a way
  that matters — visibility or ceiling crossing your thresholds (down or back up), a new ATIS
  letter, a tailwind on the planned runway — the FO tells you, and adds the alternate's weather
  when the destination has dropped below a limit. Each kind of change is spoken at most once
  per 15 minutes (your setting). Steady weather is never repeated.
- **Read-backs.** State a figure and the FO checks it against the aircraft: *"altimeter one
  zero one three"* (EFIS baro), *"V speeds one four one, one four four, one four seven"* (FMS
  PERF TO), *"runway two seven right"* (the briefed departure or arrival runway), *"minimums
  four one zero"* (the minima you entered). The answer is *"QNH one zero one three, checked."*
  or *"Negative. I read QNH one zero one seven."* A lead-in with no figure (*"minimums check"*)
  is still the checklist's.

## Briefings, radios, FCU

- *"Brief the departure"* / *"brief the arrival"* — composed from the FMS plan, Navigraph
  DFD procedures, live weather and your minima. With an LLM configured the wording is
  natural and the FO starts speaking after the model's first sentence, not after the whole
  reply (Settings → Voice First Officer → LLM Styling → **Speak while the model writes**, on by
  default; the same switch covers the debrief). Every sentence is checked before it is spoken
  — numbers written as words included — and if one is wrong, or the model stops, the plain
  template finishes the briefing without starting again. A safety callout (minimums, V1) cuts
  a briefing short for good; ask again for a fresh one. Every number is verified against the
  facts and the deterministic template is the
  floor.
- Radio management: *"set one two one decimal nine"*, box selection, standby-then-swap only.
- FCU: hand the FO pilot-flying (*"you have controls"*) and instruct — headings, altitudes,
  speeds, V/S, managed/selected modes. Announce → 3-second cancel window ("negative") →
  action with verification. Take back controls instantly with *"I have controls"*.
- ECAM abnormals are detected and read (memory drills auto-fire); tech-log raise/rectify
  and the spoken debrief run as guided dialogues.

## Persona

Off by default (`persona` settings section). When enabled, the FO gets a personality:

- **Name, experience** (junior/standard/senior), **formality** (casual/standard/formal),
  **chattiness** (0–3) colour the LLM-styled speech — briefings, the debrief and flow
  advisories, each with its own toggle. Only wording and tone change; numbers and
  operational content are verified and locked, and without an LLM the deterministic texts
  speak unchanged.
- **Acknowledgement variation** picks randomly from the pools in
  `%LOCALAPPDATA%\ProsimCompanion\config\phrases.json`
  (edit the file to add your own "are you sure" / "say again" variants — hot-reloaded, and
  used by both the persona and the plain round-robin).
- Say **"quiet please"** to silence chatter for the rest of the session (checklists and
  callouts are never chatter).

## Voices

The FO, the purser and company ACARS each have their own voice (`speech` and `voices`
settings). Voices are tried in order: Kokoro local neural first, then ElevenLabs (your own
key, cached, budget-enforced), then Google Chirp HD (cached, budget-enforced), then Windows
voices. Cabin reports arrive on the CAB channel with the interphone chime; company messages
with the ACARS beep.

To set up ElevenLabs — key, voice, plan, crew voices, cost — follow the
[ElevenLabs setup guide](06-elevenlabs.md).
