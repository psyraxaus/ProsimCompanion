# GSX airport profiles and MSFS parking data — what a stand is called, and where it is

Distilled 2026-10-04 from the shipped GSX Pro manual (`C:\Program Files (x86)\Addon
Manager\couatl\GSX\GSX_manual_MSFS.pdf`, 2026-07-09, 291 pp — it also contains the complete
**Couatl Remote API v2 Developer Guide**, pp. ~272–291), from 42 real `.ini` / 23 real `.py`
profiles on the owner's sim PC, and from the EGLL→EFHK flights of 2026-10-03/04. Implemented as
the **airport parking catalogue** (`Core/Airports/Parking`): GSX-profile tier
(`Gsx/Profiles`) + scenery facility-data tier (`Sim/Facilities`), merged per stand.

## 1. Why this exists — the W40 refusals

`gate.select "W40"` at EFHK was refused `not_found` four times (2026-10-04) although the stand
exists. GSX's own name for it is **`Apron 1W (Gates W34-W48) | Gate 40`** (Couatl.log
"Requested parking services to"). The profile `EFHK-MKStudios.py` names the `GATE_W` group with
the template `"Apron 1W (Gates W34-W48) | Gate #§"`, and (manual p.127) **`#` expands the number
only and `§` the suffix only — the GATE letter is never expanded.** So uiGateName = "Gate 40";
"W40" matches none of selectGate's rungs (exact bglName → exact uiGateName → exact uiName →
suffix on uiGateName; manual p.181). The identity that always works is the parking **number**
as an integer (`{"gate": 40}`), disambiguated by `bglName` when several groups share a number.

Default (unprofiled) airports print "Gate W40" (letter + number, manual examples "Gate A1",
"Parking 12"), so a bare token works there — only custom-template airports break.

## 2. Where profiles live (manual p.68, priority low → high)

1. FSDT built-in `couatl\GSX\airports\*.pye` — encrypted, unreadable. Not scanned.
2. An `.ini` the scenery developer ships **inside the package**, next to the BGLs — e.g.
   `Community\inibuilds-airport-egll-heathrow\Official GSX Profile\egll-24-iniBuilds.ini`.
   EGLL has NO user ini; only this one. Scanned under every MSFS package root
   (`Community`, `Official\OneStore`, `Official\Steam`); packages named for the ICAO deeply,
   others two levels.
3. `%APPDATA%\Virtuali\GSX\MSFS\<icao>-*.ini|.py` — the user's own (highest). File name = ICAO
   followed by `-`, `_`, `.`, space or the extension (`LGAV.ini`, `EFHK-MKStudios.py`,
   `egll-24-iniBuilds.ini`). Several inis per airport are possible (one per BGL seen); the
   user tier wins, then newest.

MSFS package roots: Store/Xbox `%LOCALAPPDATA%\Packages\Microsoft.FlightSimulator_8wekyb3d8bbwe\
LocalCache\Packages` (2020) / `Microsoft.Limitless_8wekyb3d8bbwe` (2024), Steam
`%APPDATA%\Microsoft Flight Simulator[ 2024]\Packages`, plus `UserCfg.opt`'s
`InstalledPackagesPath "…"`. Overridable: `gsx.sceneryPackageFolders`, `gsx.gsxProfileFolder`.

## 3. The `.ini` (stand data)

Plain INI. `[general]` (creator, scenario/bgl, version), `[jetway_*]` tables, de-ice areas (free
text, skipped), and one section per customised stand named by **scenery identity, lower-case**:
`[gate 17]`, `[gate w 40]`, `[gate s 45a]`, `[parking 401]`, `[n parking 1w]`, `[dock 0]`,
`[none 101]`, MARS sub-positions `[gate a 252_mars_252a]` (skipped). Keys used:

| key | meaning |
|---|---|
| `this_parking_pos` | `lat lon heading` of the stand (GSX's working position) |
| `radiusleft` / `radiusright` | wing-tip clearance radii, m |
| `maxwingspan` | m |
| `hasjetway` | 0/1 |
| `type` | SDK parking type (9 GATE_MEDIUM, 10 GATE_HEAVY, 5 RAMP_CARGO, 1–4 GA…) |
| `airlinecodes` | comma list — **empty at EGLL and EFHK**; "airline rules" are weak in profiles |
| `handlingtexture` | ground-handling operator list (AIRPRO,AVIATOR,AY) |
| `parkingsystem` | SafeDockTS24 / SafeDockTS42 / Marshaller / … |
| `pushback` | 0 none, 1 left, 2 right, 3 both |
| `pushbacklabels` | `left label\|right label`; empty side ⇒ GSX default text |
| `pushbackleftpos` / `pushbackrightpos` | `lat lon heading` at RELEASE for the custom route |
| `pushbackaddpos` | Python list of extra slots `{'label': u'Facing South (V2)', 'pos': (lat, lon, hdg), …}` |

Headings are written −180..180 (normalised). GSX's default labels (manual p.23/24):
`Nose Right/Tail Left (LEFT)` (nose ends ~90° RIGHT of the stand heading) and
`Nose Left/Tail Right (RIGHT)` (~90° LEFT). Auto-labels look like `On Taxiway A, facing S`.

## 4. The `.py` (names)

Only the static shape is read: `X = CustomizedName("Terminal 1 | Gate #§", 1)` assignments and
`parkings = { GROUP : { key : (X, stopFn), … } }` where GROUP ∈ `GATE`, `GATE_A..GATE_Z`,
`PARKING`, `N_PARKING`…`NW_PARKING`, `DOCK`, `0` (the NONE enum); key `None` = group default,
`40` / `'34B'` / `"218R"` = number + suffix; the tuple's first element is the name (an
identifier or an inline `CustomizedName(...)`). uiName = the expanded template verbatim
(`Terminal 3 | Gate  313` — EGLL keeps TWO spaces); uiGateName = the part after the last `|`.
Six owner profiles (PHNL, YBBN, YMML, YPPH, YSSY, EGPF) compute names in Python
(`PresetNames(...)`) — those stands stay known by identity but unnamed; the integer send
still works for them.

Validated 2026-10-04 against all 42 inis / 23 pys (parsers in `Gsx/Profiles`, tests
`GsxProfileParserTests`); EFHK W40 → "Apron 1W (Gates W34-W48) | Gate 40", EGLL 313 →
"Terminal 3 | Gate  313" — both equal to GSX's own log lines.

## 5. The simulator's parking list (facility data)

MSFS 2024 streams default airports — there is no local BGL to parse. GSX itself reads parkings
through SimConnect facility data (`requestFacilityData EFHK` in Couatl.log) and so do we:
`OPEN AIRPORT` (LATITUDE, LONGITUDE, ALTITUDE, N_TAXI_PARKINGS, N_JETWAYS) → `OPEN
TAXI_PARKING` (TYPE, TAXI_POINT_TYPE, NAME, SUFFIX, NUMBER, ORIENTATION, HEADING, RADIUS,
BIAS_X, BIAS_Z, N_AIRLINES) → `OPEN JETWAY` (PARKING_GATE, PARKING_SUFFIX, PARKING_SPOT).
NAME is the SDK enum (0 NONE, 1 PARKING, 2–9 compass parkings, 10 GATE, 11 DOCK, 12–37
GATE_A..Z — the same numbers as GSX's `FSDT_GSX_SetGate_Name` readback), SUFFIX 0/1..26.
BIAS_X/Z are metres from the airport reference point (east/north assumed — **unverified live**;
`AirportParkingCatalog` logs the median offset between ini and facility positions of the same
stands: a median in the hundreds of metres means an axis is flipped). TAXI_PARKING_AIRLINE
rows are not requested yet (field type unconfirmed); airline codes come from the ini only.

A rejected definition field surfaces as a SimConnect exception; the tier then marks itself
failed for the session instead of timing out every lookup.

## 6. Resolution rules (pure, `ParkingTokenResolver`)

1. Exact normalised match on GSX's full name / gate name / default name (the pilot typed what
   GSX shows).
2. Designator shape `[word] [letter] number [suffix]` ("W40", "Stand 313", "545R", "gate d-27"):
   letter = GATE letter group, trailing letters = suffix. Exact (letter+number+suffix) →
   Likely (unsuffixed token, one suffixed stand; or bare number of a lettered gate) → Ambiguous
   (several groups share the number: plain GATE preferred, alternatives listed).
3. Tokens to send, in order: **number (int)** → GSX gate name → GSX full name → default name →
   the typed text (last). `ambiguous` picks the candidate whose `bglName` / `uiName`
   normalises to the resolved stand.

## 7. Pushback direction from this data (`PushbackAdvisor`)

Options come from the ini slots (label + release heading), else the live menu lines (kind from
`(LEFT)`/`(RIGHT)`/"Tail Left"/"Tail Right"/"Straight"; heading from a compass word in the
label; else ±90° from the stand heading). The suggestion is the option whose release heading
is closest to the bearing stand → departure-runway threshold (gateway runway list, then the
Navigraph DFD `runway_latitude/longitude`); < 30° separation between the two best = Low
confidence (lean, FO asks). A pilot's compass wish ("facing north") accepts an option within
45°. Per-flight choice store: `PushbackChoiceStore`; mode: `gsx.pushbackPreference`
(`auto` default since this change, `ask`, legacy fixed values).

## 8. Unused Remote API surface worth knowing

`menu.search { "text": "40" }` filters GSX's own "Select Position" page live (`state.search
{active, session}`; manual §8.11/§23.3, reference client `couatl\GSX\remoteClient\js\menu.js`).
Not used yet — a possible second rung for the arrival-gate position menu.
