# Cockpit switches and camera views (Stream Deck keys for things without hardware)

Two features, one owner story (2026-10-24, ADR-0015): the Stream Deck lives on a second PC, and
the pilot wants keys that press ProSim cockpit switches he has no hardware for (the three leak
measurement valves first) and that change ChasePlane views. Both go through the ProsimCompanion
plugin and the command API — no FSUIPC, no second plugin.

## Cockpit switches

### Settings (`cockpitSwitches`, `CockpitSwitchesOptions`, Settings → Cockpit Switches)

- `enabled` (true): off = keys still show state, every press answers `unavailable`.
- `rows[]`, one grid line each (rare fields in a per-row fold-out, owner pick 2026-10-24): `id` (slug a key sends, filled from the dataref on pick), `name`
  (key title), `dataRef` (picked from the catalogue; `system.switches.S_*` only — the picker
  hides `CB_*`), `look` (`korry2` | `korry1` | `toggle` | `knob` | `guarded`), `pressMode`
  (`toggle` | `momentary` | `cycle`), `upperLegend` / `lowerLegend`, `upperIndicator` /
  `lowerIndicator` (optional `system.indicators.I_*` datarefs that light the lamps; the lower
  lamp defaults to "switch value ≠ 0"), `upperColor` / `lowerColor` / `bezelColor` (hex; the
  page's swatches), `showUnlitLegend` (true = dim grey legend while the lamp is dark; false =
  extinguished, the legend appears only when lit). **Add the three leak measurement valves**
  sets (or resets) B / G / Y: blue / green / yellow bezels, lamp follows the switch (no
  indicator, so it works unpowered), legend ON, `showUnlitLegend` false — nothing while Normal,
  a lit ON while pushed (owner request 2026-10-24).

### Runtime (`src/ProsimCompanion.Prosim/Switches/CockpitSwitchService.cs`)

- Hosted; on start and on every options/catalogue change it (re)subscribes exactly the datarefs
  the rows need (`SubscribeDynamic`, Normal tier — the dynamic-subscribe guard test lists the
  file) and publishes `CockpitSwitchStateStore` (`id, value, position label, upperLit, lowerLit,
  stale`) on every push.
- Installs `ProsimWriteGate.ConfiguredSwitchAllowed`: a name passes only while it is a row AND
  catalogued AND the feature is on; the gate itself restricts the hook to `system.switches.S_`.
- `cockpit.press {id, long?}`: Toggle → `WriteAsync(other of the first two values)`; Momentary
  → `PressMomentaryAsync` (the serialised press worker, `prosim.momentaryPress*` timings); Cycle
  → next value, long press previous, wrapping. `cockpit.set {id, value}` writes a catalogue
  value. Outcomes: no row → `preconditionFailed` listing the ids; feature off → `unavailable`;
  no live value / ProSim away → `unavailable`; gate or backend refusal → `failed`. Session event
  `cockpit.press` (`id, dataRef, mode, from, to, source`).
- Pure rules in `Core/Aircraft/Switches/CockpitSwitchLogic.cs` (next value, face state, slug,
  hex check), unit-tested.

### Wire

- `GET /api/status` → `switches: [{id, value, position, upperLit, lowerLit, stale}]` (null
  without the ProSim pillar, empty without rows).
- `GET /api/controls` (same gate as the status feed) → `{ switchesEnabled, switches: [{id, name,
  dataRef, look, pressMode, upperLegend, lowerLegend, upperColor, lowerColor, bezelColor,
  positions: [{value, label}]}], viewsEnabled, views: [{name, button}] }` — configuration for
  the key faces and the Property Inspector dropdowns; the plugin re-reads it every 30 s and when
  a key's settings open.

### Plugin (`streamdeck/src/actions/cockpit-switch-action.ts`, `rendering/cockpit-tiles.ts`)

Own artwork from the A320 overhead, lights-out philosophy: a lit lamp is an outlined box in the
lamp colour with the legend in the same colour; unlit is a dark box with a dim legend; the bezel
takes the row's identity colour. Looks: Korry two-lamp / one-lamp, toggle (lever up/down, the
active word lit), knob (labels fanned over the knob, pointer at the current one; short press
next, hold ≥ 600 ms previous), guarded (red guard closed when off, lifted when on). Feedback on
the face edge: blue ring 250 ms when the app accepted, amber ring + reason 2 s when it refused.
Dim with a note while unpaired, offline, the feature off, ProSim away, or no value yet.

## Camera views (vJoy relay)

Settings → Setup → **vJoy relay** (`inputRelay`): the app presses a button on a vJoy virtual
joystick on the sim PC (`VJoyInputRelay` over `vJoyInterface.dll`, loaded lazily from the
driver's folder, never shipped); ChasePlane sees a real controller button. View rows map a name
to a button; the plugin's *Camera view* key (`camera-view-action.ts`) POSTs `input.pressView
{name}`; `input.vjoyPress {button, holdMs?}` is the raw form. States on the Setup page: OFF /
NO DRIVER / NO DEVICE / DEVICE BUSY / READY / FAILED; session event `input.vjoy`.

## Seen live (2026-10-24, owner's sim PC via the ProSim gateway)

Presses reach ProSim: `S_OH_HYD_LMV_GREEN` / `_YELLOW` read 1 after key presses. The lamps
(`I_OH_HYD_LMV_*_L`) stayed 0 and the valves stayed open because the aircraft was unpowered
(`S_OH_ELEC_BAT1` = 0, EXT PWR AVAIL lamp 0) — ProSim lights no indicator without power. The
lamp rule stays "non-zero = lit" (ProSim sends 1 or 2 by brightness; the de-icing PACK lamps use
the same rule, flown live).

## Verify live (still open)

1. Leak valve B: press the key → ProSim's overhead LMV B pushbutton changes, the key's lower
   lamp follows `I_OH_HYD_LMV_BLUE_L`, the log shows `Cockpit switch oh_hyd_lmv_blue … 0 → 1`.
2. A knob (ADIRS 1): short press steps OFF → NAV → ATT → OFF; a held press steps back.
3. With ProSim closed the key dims with "prosim away" and a press answers `unavailable`.
4. vJoy: Check → READY; a view row's Press moves ChasePlane when the view is bound to that
   vJoy button; the Camera view key does the same from the other PC.
