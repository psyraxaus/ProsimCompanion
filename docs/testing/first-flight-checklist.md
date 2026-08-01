# First flight checklist — GSX pillar smoke test

Goal: validate the GSX Remote API integration and sync modules against live GSX for the first
time, and capture enough telemetry that anything wrong is diagnosable afterwards.

## Setup (5 minutes)

1. Copy the build output (`src\ProsimCompanion.App\bin\Debug\net10.0-windows\`) to the sim PC
   (or build there: `dotnet build ProsimCompanion.slnx`).
2. In `config\settings.json` (or the web Settings page):
   - `prosim.sdkPath` → the ProSim install folder
   - `logging.wireTrace` → **true** (full GSX + gateway frames for this session)
   - optionally `logging.defaultLevel` → `"Debug"` for maximum detail
3. Start MSFS, ProSim, GSX; spawn at a gate. Start ProsimCompanion.
4. Open `http://localhost:5320` — Status page should show ProSim **and** SimConnect Connected,
   a real aircraft title, and a sensible flight phase (ColdAndDark/Preflight on the ground).

## Test sequence (watch the /gsx page throughout)

1. **First contact**: GSX readiness should reach **Ready** with capabilities listed; airport
   ICAO + gate context populate; the services table fills with wire states.
   *Red flags in Logs*: "protocol … only 1 is supported", "does not consume key", "undocumented
   semantic state" — none are fatal, all are wanted data.
2. **Native guard**: the decision log should show "disabled 6 ProSim efb.gsx.* auto flags".
3. **Ground equipment**: on Preflight, GPU + chocks appear in ProSim (decision-logged).
4. **Questions**: trigger any GSX question (e.g. call a service that asks about crew) — the
   decision log records the answer or "left for the user".
5. **Departure sequence**: import a SimBrief OFP in ProSim, then press *Arm gate* aside — start
   services by setting `gsx.autoStartDepartureServices` true, or watch holds: the decision log
   should show "hold Refueling — waiting for SimBrief OFP import" beforehand (proves OFP gating).
6. **Refuel**: when GSX connects the hose, ProSim fuel should climb ~25 kg/s toward the EFB
   target; disconnecting pauses it (decision-logged).
7. **Boarding**: pax spread evenly across all four zones (same load factor front-to-back —
   watch the ProSim W&B CG stay sensible with a partial load); cargo loads with GSX's percent.
8. **Deboarding is observe-only** this flight: its counters are logged, nothing written.
9. **Beacon**: park brake set, beacon on → PCA/GPU/chocks removed (chocks stay if brake off).
10. **Arrival gate** (optional): type a destination-airport gate into the /gsx *Arm gate* box
    mid-flight; watch armed → assigned → confirmed via the SetGate readback.

## Afterwards, collect

- `%LOCALAPPDATA%\ProsimCompanion\logs\ProsimCompanion-<date>.log` (CMTrace)
- `ProsimCompanion-wire-<date>.log` (raw frames)
- `%LOCALAPPDATA%\ProsimCompanion\sessions\session-*.jsonl` (event log)
- Anything odd on the /gsx page (screenshot is fine)

Known-unverified items this test settles: real hello/snapshot frame shapes, semantic state
strings in the wild, menu titles vs the question catalogue's prefixes, `gate.select` matching
behaviour, deboard counter semantics, and the boarding-total source for pax sync.
