# ProSim integration reference

Hard-won knowledge from ProsimInterface and Prosim2FO. Verify against the live system before relying
on it, but treat every "quirk" entry as empirically discovered — do not "simplify" them away.
The full ~4,470-row dataref catalog is `ProsimDataref.csv` at this repo's root (also in the
ProsimInterface and Prosim2FO repos); ProsimInterface's `ProsimConstants.cs` (~400 curated names
with polling tiers) is the canonical curated list to port verbatim.

## 1. ProSim SDK (ProSimSDK.dll)

**Loading** — compiled against with `Private=false` (typed access; build-time path via the
`ProSimSdkDir` MSBuild property), never copied to output or redistributed. At runtime the dll is
loaded from the **user-configured** directory (`prosim.sdkPath` in config — written by the
installer or web Settings; no assumed install path). Requires `SetDllDirectory` (native deps), WCF
shim packages (`System.ServiceModel.Primitives/Http/NetTcp` 10.0.652802) and
`System.Security.Cryptography.Xml` 10.0.10 on modern .NET. The SDK's XML API doc ships beside the
dll (`ProSimSDK.xml` in the ProSim System install image).

**Connecting** — two constructor shapes exist and they are **disjoint**; pick by reflection
(`SdkConstructorSelector`), never with a direct `new ProSimConnect(...)`:
- older (ProSim 1.74-beta.8, dll product version `1.0.0+2f0d88c188`, 2025-11): `ProSimConnect()` only.
- newer (dll `1.0.0+626faa2df9` of 2026-07; ProSim 1.75.1, `1.0.0+1374251c6f`):
  `ProSimConnect(string apiKey = "")` only; throws `AuthenticationException` on a bad API key.

Compiled against the newer dll, `new ProSimConnect()` becomes a `.ctor(String)` member reference
too, so a direct call binds one shape and dies with `MissingMethodException` on the other
(issue #158, ticket t-20261005-1918). `Connect()` and `Connect(string host, bool synchronous)` are
on **both** shapes — there is no one-parameter `Connect(host)` (an earlier version of this section
said so; verified by reflection on all three dlls, 2026-10-06). The dll **file version is
`1.1.1.0` on every build** and the assembly version `1.1.0.0` on the first two; only the product
version (it carries the ProSim commit hash) tells builds apart — the app logs it at start. The
`ActiveSchematics` subfolder of a ProSim install holds a different `ProSimSDK.dll`
(`1.42.0+…`) — not the one to load.

Non-blocking `Connect(host, false)` makes the SDK retry internally (measured on the 1.74-beta.8
dll with ProSim off: `onFailedToConnect` every ~2.6 s from one call) — do **not** stack watchdog
`Connect` calls on top. Default host `localhost`. The SDK owns an unjoinable foreground thread →
process must end with `Environment.Exit` after orderly teardown.

**Read model (critical)** — `ReadDataRef(name)` is a synchronous network round-trip; polling it
stalls the app. Correct pattern: create `DataRef(name, interval, connection, autoRegister: false)`,
attach `onDataChange` **before** calling `dr.Register()`, then read the cached `.value` locally.
Note: `Register()` is an instance method on `DataRef` — the SDK's XML doc claims
`ProSimConnect.Register(DataRef)` but no such method exists in the binary (verified v1.1.1.0).
The 3-arg `DataRef` ctor auto-registers (leak hazard if allocated per write). Cadence tiers used
historically: Critical 100 ms / Frequent 250 ms / Normal 500 ms / Infrequent 2000 ms.
On reconnect, re-register every subscription (fixes delayed-ProSim-start bugs). On disconnect, flag
cached values stale rather than clearing them ("valid or hold previous decision").
`DataRefNotFoundException` is expected for lazily-created refs (e.g. `efb.prelimLoadsheet` exists
only after first write). On ProSim 1.74-beta.8 an unknown name does **not** throw at `Register()`:
the ref goes to `DataRefState.Error` and reading `.value` throws `DataRefNotReady` (seen
2026-10-06 with a made-up name; not compared with a newer ProSim).

**Writes** — go through cached write-DataRefs gated by code-level allow-lists. Momentary presses
(write 1 → hold ~configurable ms → write 0 → inter-press gap) must be serialized through a
single-reader `Channel<T>` worker.

## 2. EFB gateway (HTTP, port 5000)

Base `http://{prosimHost}:5000`. PascalCase requests / camelCase responses. 3 retry attempts,
30 s timeout, no proxy. This port is why our own web UI must not default to 5000/5001.

- `POST /graphql` — dataref mutations `writeBool/writeInt/writeFloat/writeString` and
  `dataRef(name:){value}` queries. JSON-escape message bodies properly.
- `POST /efb/tasks/cancelBoarding`
- `POST|DELETE /efb/loadsheet`; `POST /efb/loadsheet/generate?type=Preliminary|Final` —
  **the Preliminary endpoint is broken for non-A320 variants; deliberately unused** (in-house
  pipeline instead, §4).
- `POST /efb/calculate/vspeeds` (V1/VR/V2/FLEX/THS), `POST /efb/calculate/ldr` — note the request
  fields misspelled `Break*` are required as-is.
- `GET /efb/airport/{icao}/runways?includeIntersections=`, `GET /efb/airport/{icao}/metar`
  (204 = no data; don't retry), `GET /efb/failures`.
- **Airport coordinates come from the runway list** (issue #145): the gateway has no airport
  reference point, so `GatewayAirportCoordinates` takes the centre of the runway coordinates
  and the mean runway elevation. **The coordinate wire shape is unverified live.** The DTO
  (carried from ProsimInterface) types BOTH `lat` and `lng` as a `{latitude, longitude}`
  pair. Two readings are handled: each is a full point (two per runway), or `lat` carries
  only the latitude and `lng` only the longitude (one point per runway). Points more than
  15 nm from their own centre are distrusted and the Navigraph DFD tier
  (`airport_ref_latitude` / `airport_ref_longitude` / `elevation`) answers instead. The log
  line `Gateway airport <ICAO>: N runway(s), M coordinate point(s) -> …` and the
  `airport-coordinates` session event (`source: gateway|dfd`) settle it on the first flight.

Two write paths exist (SDK vs GraphQL). The predecessors chose ad-hoc per feature; ProsimCompanion
should consolidate on SDK push/write where possible and use the gateway only for gateway-exclusive
functionality (calculations, EFB tasks, loadsheet slots).

**The write verdict is in the body, not the status (2026-10-06, ProSim 1.75.1 at 10.0.1.22).**
Every well-formed mutation returns HTTP 200; `{"data":{"dataRef":{"writeBool":true}}}` means
written, `writeBool:false` means "no such dataref / not writable" (`efb.gsx.autoCatering`
answered false; `system.config.Config.DOORS` true). A query for an unknown name answers
`value: null` — a real off is `false`, never null. `ProsimGatewayClient.WriteDataRefAsync`
reads the verdict (`GraphQlMessages.WriteAccepted`); until then a dead write logged as done.
Introspection (`__schema`, `__type`) is refused with 400.

## 3. Key datarefs (canonical spellings)

**Fuel/refuel**: `aircraft.fuel.total.amount.kg`, `aircraft.refuel.fuelTarget[.kg]`,
`aircraft.refuel.refuelingActive/refuelingPower/refuelingRate`, per-tank
`aircraft.systems.fuel.{left|right|center...}.{inner|outer}.amount.kg`, refuel LVAR
`L:S_THIRD_PARTY_REFUELG`.

**Passengers/cargo**: `aircraft.passengers.zone{1-4}.{amount|capacity}`,
`aircraft.passengers.seatOccupation[.string]`, `efb.passengers.booked[.string]`,
`efb.passengerStatistics`, `aircraft.cargo.{forward|aft|bulk}.amount|capacity`
(**bulk amount is not settable** — fold bulk into aft), `efb.plannedCargoKg`, `efb.plannedfuel`.

**Weights/CG**: `aircraft.weight.{gross|zfw}[.max]`, `aircraft.{cg|zfwcg}`,
`aircraft.fms.init.{block|zfw|zfwcg}`, `aircraft.fms.perf.takeOff.*` (v1/vr/v2/flexTemp),
`aircraft.fms.{origin|destination}`.

**EFB/flight**: `efb.simbrief.id` (SimBrief identity lives in ProSim, not our config),
`efb.simbriefPlanImported`, `efb.flightTimestampJSON` (nested JSON incl.
`prosimTimes.prelimEdno` = flight-plan id), `efb.efb.boardingStatus`
(`notstarted`/`inProg`/`completed`), `efb.{prelim|final}Loadsheet`.

**ACARS**: `efb.aoc.message.uplink` (+`.copy`, `.result` — "Message processed" on success). Envelope
keys are **lowercase**: `{type, header, id, accept, content}`.

**Doors/ground**: `doors.entry.*`, `doors.cargo.*`, `groundservice.groundpower`, `efb.chocks`,
`groundservice.preconditionedAir`, `efb.fwdStairs|aftStairs`, `groundservice.pushback`.

**`groundservice.pushback` enum (settled empirically, 2026-08-23 instrumented flight, issue
#104)**: **3 = idle/no pushback** — observed parked cold-and-dark, during taxi, airborne, and at
shutdown; **0 = pushback service session in progress** — the only other value ever observed,
present for the whole GSX push incl. engine start. Values 1/2 have never been observed (presumed
direction variants; we treat any non-3 as active). The obvious ">0 = pushback active" reading is
WRONG and latched the flag true for entire flights. A missing dataref must fall back to 3.

**Disable ProSim's native integrations when we own them**: `efb.gsx.*` flags, `efb.autoJetway`,
`efb.autoDoor`, plus native audio channel control. **On ProSim 1.75.1 none of the `efb.gsx.*`
or `efb.autoDoor` names exist** (gateway read null, write `false`; the A322 catalogue has no
such rows) — the native guard's "disabled 6 flags" was a no-op there until 2026-10-06. Which
ProSim build last had them is unknown.

**ProSim IOS options are datarefs: `system.config.*` (2026-10-06).** The Config / Datalink /
Units / Audio / Dynamics pages of ProSim System are `system.config.<Page>.<Key>` rows in the
A322 catalogue (lines ~1184–1241): tick boxes are bool, drop-downs are the exact choice text
(`"Quick,Realistic"`, `"Disabled,Enabled"` …). Read and written through the gateway, they
**take effect at once, no restart** (Door logic flipped true → read true → false → read false,
live). `system.version` gives the ProSim version (`"1.75.1"`). The ones ProsimCompanion checks
(`ProsimSetupRecommendations`; the recommended values are the owner's working config):

| IOS option | Dataref | Wanted |
|---|---|---|
| Door logic | `system.config.Config.DOORS` | `false` (our door automation drives the doors) |
| Automatic ground power | `system.config.Config.GROUNDPOWER` | `false` (our GPU latch) |
| Datalink → Load Cargo/PAX | `system.config.Datalink.loadCargo` | `false` (our loadsheet boards) |
| Datalink → Load Fuel | `system.config.Datalink.loadFuel` | `false` (our refuel sets the fuel) |
| Refuelling rate | `system.config.Config.refuelRate` | `Realistic` |

Other rows seen, not acted on: `Config.autoMute` (`Disabled`/`Enabled` — ProSim's own mute on
GSX position freeze, see audio notes), `Config.RepositionMode`, `Config.ACT`, `Config.EPR`,
`Units.Weight`, `Datalink.Atis`, `Datalink.Metar`, `cockpitSetup.current` (and `.load`/
`.store`/`.delete` — write-only loaders, never to be written by us). The write gate lists the
five names exactly; never the `system.config.` prefix.

**Flight dynamics (FO pillar)**: `aircraft.speed.ias`, `aircraft.altitude[.aboveGround|.radio]`,
`aircraft.verticalspeed`, `aircraft.gearDown`, `aircraft.flap.positionHandle`
(**scale 0=Up,1=F1,2=F1+F,3=F2,4=F3,5=F4 — NOT the S_FC_FLAPS switch scale**),
`aircraft.engines.limits.toga/flex`, `debug.groundSpoilersDeployd` (sic — misspelled in ProSim),
`aircraft.ground.nose`, `environment.ambientInCloud/Visibility`.

**Position (issue #145)**: `aircraft.latitude`, `aircraft.longitude` (decimal degrees, the
true position in the world — NOT the `aircraft.adiru.N.latitude/longitude` IRS outputs, which
are blank until the IRS aligns) and `aircraft.track.true`, all at the 500 ms tier (the slowest
that still feeds a 1 Hz consumer). Fallback NaN; **stale, NaN or exactly (0, 0) is "no
position"** (`GeoPoint.FromRaw`) — (0, 0) is what an unpopulated ref and a ProSim with no sim
attached read as. Position is never phase evidence and never holds the flight-live gate.
**Unverified live**: that the two refs push with MSFS attached and read sim-true values.

**Distance / top-of-descent references — none usable yet.** The A322 catalog has no FMS
distance-to-destination and no T/D dataref. The two near misses are recorded raw on the
`flight-progress` and `tod-approaching` session events and acted on by nothing:
`debug.vnav.distance.remaining` ("VNAV distance remaining" — unit and reference point
undocumented: destination? T/D? the next constraint?) and `aircraft.fms.TimeToDest` (seconds).
Compare them with `toGoNm` / `minutesToTod` on the same events after a flight; if the VNAV ref
proves to be an along-route distance to the destination it should replace the 3:1 estimate's
direct distance. Until then the top of descent is `(cruise altitude − destination elevation)
/ 1000 × 3` nm before the destination on the great-circle direct distance — an estimate that
errs early, because the route is never shorter than the direct line.
**Touchdown recorder (issue #146)**: `aircraft.verticalspeed`, `aircraft.pitch`,
`aircraft.bank` and `aircraft.acceleration.Y` at the 100 ms tier, with the raw
`system.gates.B_GROUND` flag used only to find the instant of first wheel contact (the
committed ground contact trails it by the agreement filter). Fallback NaN on pitch / bank /
acceleration, so a dead ref is a blank figure. Two things are **unverified live**:

- **`aircraft.pitch` sign.** Expected nose-up positive; recorded and shown as ProSim sends it.
  If a normal landing shows a negative pitch on the Logbook page, the sign is inverted.
- **`aircraft.acceleration.Y` unit.** The catalog says "Aircraft vertical acceleration" and
  nothing else. The `touchdown` session event carries three RAW values —
  `accelerationYRawBeforeContact` (mean over the second before contact),
  `accelerationYRawMax` and `accelerationYRawMin` (contact to contact + 3 s). The baseline
  tells the unit apart: about 1 = G including gravity, about 0 = G with gravity removed,
  about 9.8 = m/s², about 32 = ft/s². Nothing converts it to G and no page shows it until
  the owner confirms the unit from a flight.

**Cockpit systems**: FCU `system.analog.A_FCU_{HEADING|ALTITUDE|SPEED|VS}`,
`system.switches.S_FCU_*`, `system.indicators.I_FCU_*`; MCDU2 keys
`system.switches.S_CDU2_KEY_<suffix>`; COM `system.analog.R_COM{1|2}_{ACTIVE|STANDBY}`; baro
`system.numerical.N_FCU_EFIS2_BARO_HPA`; F/O controls `system.analog.A_FC_FO_{ROLL|PITCH|RUDDER}`;
ACP audio `system.analog.A_ASP{1|2|3}_*_VOLUME` (0–1024) and
`system.switches.S_ASP*_*_REC_LATCH`.

**ACP cabin call (issue #11, 2026-10-09).** The CAB family per panel (`S_ASP_` captain,
`S_ASP2_` FO, `S_ASP3_` observer): `S_ASP*_CAB_REC` is the momentary reception-knob push
(`[0:Off, 1:Pushed]`), `S_ASP*_CAB_REC_LATCH` the knob's state (`[0:Off, 1:On]` — CAB
reception selected), `S_ASP*_CAB_SEND` the momentary transmission key, `S_ASP*_RESET` the
RESET key; `I_ASP*_CAB_CALL` is the CALL light and is **read-only** (the catalog says so and
the SDK refuses it), so the app's own purser call shows as the web "CABIN CALLING" banner
instead. ProsimInterface answered ProSim's own call (it watched `L:I_ASP_CAB_CALL`) with a
three-press chain through MobiFlight LVAR toggles — CAB SEND, delay, VHF1 SEND, RESET — which
flips the transmit channel twice. ProsimCompanion's auto-answer answers its OWN cabin call
with **one idempotent latch write**: `S_ASP2_CAB_REC_LATCH = 1` (the captain's `S_ASP_…`
when `speech.pilotSeat = right` — the FO's panel is seat-relative), because "CAB reception
selected on any panel" is exactly what the purser report already waits for, and written back
to 0 after the report. Not a momentary press: `S_ASP*_CAB_REC` would *toggle* (a second push
deselects), the latch sets a known state. Only those two latches are on the write allow-list.
**Unverified live**: that a gateway/SDK write of the latch is echoed back on the subscription
(a hardware ACP that drives the switch each scan may override it — the `cabin.auto-answer`
verdict `written-no-echo` flags that case), and whether ProSim's audio routing or the CALL
light care about the latch at all.

**Baro sync ProSim→MSFS** (initial EFIS sync at connect): SimConnect event `KOHLSMAN_SET`,
value = hPa × 16, index 0 = captain.

**Quirks**:

- `S_FCU_EFIS2_BARO_STD` (F/O baro knob) is momentary and the catalog's value legend is
  inverted: settled live 2026-08-29 16:42 (A322, cruise, gateway `writeInt`) — writing **2**
  pushes the knob (STD gate goes true), writing **1** pulls it (QNH), the switch reads 0 again
  immediately either way, and a 300 ms hold is plenty. The catalog says `1:Pushed, 2:Pulled`.
  Presume the captain's `S_FCU_EFIS1_BARO_STD` matches, unverified (issue #109).
- Park brake: read `system.switches.S_MIP_PARKING_BRAKE`; the hydraulic gate
  `B_HYD_PARKING_BRAKE_SET` reads **inverted on the A322** (found 2026-05-02).
- ACP knob range is 0–1024 (`VolumeMax = 1024`).
- Hardcoded constants with no dataref: A322 `MaxLaw = 64 500 kg`, `WaterWaste = 1 080 kg`,
  pax-zone capacity fallback `{24,30,36,42}` — make per-variant config in the new app.

## 4. In-house loadsheet / W&B pipeline (bit-exact reverse engineering)

Built because ProSim's Preliminary loadsheet endpoint is broken for non-A320 variants.

- Pax mass **88 kg** (ProSim's value, not MSFS's 77).
- Loaded Index formula, bit-exact to ProSim: `LI = 710.4 + (mac − 29.6) × (3.9 / 2.6)` — preserve
  the IEEE-noise literals exactly.
- Fuel-CG correction: ProSim's `ZfwcgAdjArray` table, indexed by `fuel_kg / 100`.
- Loadsheet JSON envelope: `type`/`unit` are enum **ints** (Preliminary=1, Final=2; Kg=0), camelCase
  names, `time` format `MM/dd/yyyy HH:mm:ss`; written to `efb.prelimLoadsheet` /
  `efb.finalLoadsheet` (renders in the EFB W&B page); fixed slot ids `"01"`/`"02"`; ~3 s settle
  delay after write.
- ACARS text goes to the MCDU RCVD MSGS with an ACCEPT prompt via the lowercase-keyed envelope
  (verified 2026-05-10/16). EDNO management: prelim EDNO from `prosimTimes.prelimEdno`
  (= SimBrief `params.request_id`); FINAL prints "REVISIONS" comparison vs the cached prelim; limit
  values carry `L` markers.
- CG plausibility validation before emitting (refuse to send a loadsheet from an unpopulated CG).
- Randomized dispatcher name pool for flavour.

## 5. File/where things live

| Thing | Path |
|---|---|
| ProSim SDK default | `C:\prosim\prosim-system\ProSimSDK.dll` |
| SayIntentions flight context | `%LOCALAPPDATA%\SayIntentionsAI\flight.json` (`flight_details.api_key`, callsign, gate, runway) |
| Legacy configs (for importers) | Prosim2GSX `AppConfig.json` (beside exe, version-migrated, v33); Prosim2FO `config/settings.json` |
