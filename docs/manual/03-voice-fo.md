# Voice First Officer

## Talking to the FO

- **Push-to-talk** (default): bind a key or joystick button on the First Officer settings
  page; hold, speak, release. A listening tone confirms the mic opened. Continuous mode is
  available for boom-mic setups.
- **Recognition** tries your LAN faster-whisper box first (if configured) and falls back to
  Windows offline recognition. Misheard phrases snap to the nearest expected phrase; a
  genuinely unclear response gets a "say again?".
- A separate **ATC-mute** binding silences the FO while you transmit to online ATC.

## Spoken checklists

Say the checklist's start phrase (e.g. *"before start checklist"*). The FO reads each
challenge and listens indefinitely for your response; wrong responses get an "are you sure?"
and the FO never gives up — say **"skip"** to move past an item, **"say again"** to repeat,
**"hold the checklist" / "resume"**, **"cancel checklist"** or **"restart checklist"**
anytime. Items with a dataref condition verify against the aircraft; the visual runner on the
Checklists page stays in sync. The flight-control check is fully monitored (your sweep is
watched at 30 Hz, then the FO answers with the FO-side controls).

## Callouts & monitoring

SOP callouts (thrust set, one hundred, V1, rotate, positive climb, transition, one-thousand-
to-go, approach 1000/500, minimums, rollout calls) fire from live data; minimums arm only
from the minima you enter on the First Officer page. Stabilized-approach gates call
*"stabilized"* or *"unstable, go around"* at the 1000 ft gate. Flow monitoring speaks
advisories (landing lights, flaps/gear ceilings, parking brake with thrust, seatbelts, icing
conditions, anti-ice left on, ISA deviation) once per condition with a cooldown. Sterile
cockpit suppresses chatter below 10,000 ft in climb/descent phases.

## Briefings, radios, FCU

- *"Brief the departure"* / *"brief the arrival"* — composed from the FMS plan, Navigraph
  DFD procedures, live weather and your minima. With an LLM configured the wording is
  natural; every number is verified against the facts and the deterministic template is the
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
settings): Kokoro local neural first, then Google Chirp HD (cached, budget-enforced), then
Windows voices. Cabin reports arrive on the CAB channel with the interphone chime; company
messages with the ACARS beep.
