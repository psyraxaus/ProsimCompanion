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
