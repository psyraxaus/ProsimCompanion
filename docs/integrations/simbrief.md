# SimBrief integration reference

- Endpoint: `https://www.simbrief.com/api/xml.fetcher.php?json=1&userid={id}` or `&username={name}`.
  The predecessors had **two** clients (typed `json=1` + legacy `json=v2` used only for origin ICAO)
  — build exactly **one** typed client in ProsimCompanion.
- Identity: the SimBrief user id lives in ProSim dataref `efb.simbrief.id` — not in our config.
- Fetch triggers: MCDU flight-plan entry detection (FMS origin change) or manual.
- `params.request_id` becomes the flight-plan id (EDNO), matched against
  `prosimTimes.prelimEdno` in `efb.flightTimestampJSON`.
- `params.units` may be `"lbs"` — convert to kg on import.
- The `alternate` node is polymorphic: object, array, or empty string — parse defensively.
- Import writes: seat map / `efb.passengers.booked`, zone statistics, `efb.plannedfuel`,
  `efb.plannedCargoKg`, `efb.flightTimestampJSON`, `efb.simbriefPlanImported`.
- If OFP pax > aircraft seats: capacity-clamp with a plausible load factor.
- Add fetch retry (the predecessors had none) — transient SimBrief failures were a known annoyance.
- **Navlog** (2026-10, #148): `navlog.fix` is an array of fix objects — or a single object when
  the route has one fix (the JSON is converted from XML). Fields read: `ident`, `pos_lat`,
  `pos_long`, `fuel_plan_onboard` (plan units, converted like every other weight),
  `time_total` (seconds from takeoff), `altitude_feet`, `is_sid_star`. A fix without a usable
  position is skipped. The destination row closes the plan at `fuel.plan_landing`. TOC / TOD
  are ordinary fixes with those idents. `OfpData.Navlog` is empty for an OFP without one —
  the fuel check then falls back to burn × time-to-ETA.

## NOTAMs (issue #189)

The JSON fetch (`xml.fetcher.php?json=1`) DOES carry NOTAMs when the pilot's SimBrief options
include them — settled 2026-10-24 against the owner's live plan (EFHK→EKCH: 95 NOTAMs). The
shipped test fixture `simbrief-ofp-sample.json` has none, which is why the item was nearly
dropped. Three containers, two shapes:

- `origin.notam` / `destination.notam` — the station's own NOTAMs. **Polymorphic like the
  alternate node**: an object when there is one, an array when several, `""` when none. Fields
  `notam_id`, `location_icao`, `notam_qcode` (ICAO Q-code, e.g. `QMRLC`), `notam_qcode_category`
  / `_subject` / `_status` (English words), `notam_text` (the E line), `notam_raw`, `notam_html`,
  `date_effective` / `date_expire` as ISO-8601 UTC (`"PERM"` for permanent), `date_expire_is_estimated`
  (`"1"` or an empty object), `notam_schedule`.
- `notams.notamdrec[]` — the FIR records for every station on the plan (count in
  `notams.rec-count`). Fields `notam_id`, `icao_id`, `notam_qcode`, `notam_text`, `notam_report`,
  `notam_effective_dtg` / `notam_expire_dtg` / `notam_expire_dtg_estimated` as `yyyyMMddHHmm`
  (no field at all for PERM). No category/subject/status words here.

`SimbriefImportService.ParseNotams` keeps the origin and destination containers plus the FIR
records whose `icao_id` is the origin or destination, each `notam_id` once (the station copy
wins), on `OfpData.Notams`. `NotamHighlighter` (Core) classifies by Q-code — subject letters 2–3
(`MR` runway, `MX` taxiway, `IC/IL/IG` ILS/LOC/GP, `L*` lighting, `LP` PAPI, `MT/MD` threshold /
declared distances) and status letters 4–5 (`LC` closed, `AS` unserviceable) — with the E-line
keywords (`RWY … CLSD`, `ILS … U/S`, `PAPI`, `THR DISPLACED`, `LGT U/S`) as the fallback for a
NOTAM without a Q-code. Nothing else is read from a NOTAM.
