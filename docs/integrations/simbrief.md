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
