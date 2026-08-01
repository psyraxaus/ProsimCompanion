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

**Connecting** — two API shapes exist; probe by reflection:
- legacy: parameterless ctor + `Connect(host)`
- beta: `ctor(apiKey)` + `Connect(host, bool synchronous)`; throws `AuthenticationException` on a bad
  API key.
Non-blocking `Connect(host, false)` makes the SDK retry internally — do **not** stack watchdog
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
only after first write).

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

Two write paths exist (SDK vs GraphQL). The predecessors chose ad-hoc per feature; ProsimCompanion
should consolidate on SDK push/write where possible and use the gateway only for gateway-exclusive
functionality (calculations, EFB tasks, loadsheet slots).

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

**Disable ProSim's native integrations when we own them**: 8 `efb.gsx.*` flags, `efb.autoJetway`,
`efb.autoDoor`, plus native audio channel control.

**Flight dynamics (FO pillar)**: `aircraft.speed.ias`, `aircraft.altitude[.aboveGround|.radio]`,
`aircraft.verticalspeed`, `aircraft.gearDown`, `aircraft.flap.positionHandle`
(**scale 0=Up,1=F1,2=F1+F,3=F2,4=F3,5=F4 — NOT the S_FC_FLAPS switch scale**),
`aircraft.engines.limits.toga/flex`, `debug.groundSpoilersDeployd` (sic — misspelled in ProSim),
`aircraft.ground.nose`, `environment.ambientInCloud/Visibility`.

**Cockpit systems**: FCU `system.analog.A_FCU_{HEADING|ALTITUDE|SPEED|VS}`,
`system.switches.S_FCU_*`, `system.indicators.I_FCU_*`; MCDU2 keys
`system.switches.S_CDU2_KEY_<suffix>`; COM `system.analog.R_COM{1|2}_{ACTIVE|STANDBY}`; baro
`system.numerical.N_FCU_EFIS2_BARO_HPA`; F/O controls `system.analog.A_FC_FO_{ROLL|PITCH|RUDDER}`;
ACP audio `system.analog.A_ASP{1|2|3}_*_VOLUME` (0–1024) and
`system.switches.S_ASP*_*_REC_LATCH`.

**Baro sync ProSim→MSFS** (initial EFIS sync at connect): SimConnect event `KOHLSMAN_SET`,
value = hPa × 16, index 0 = captain.

**Quirks**:
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
