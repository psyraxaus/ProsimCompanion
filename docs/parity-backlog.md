# Prosim2GSX parity backlog

What remains unported after the three parity phases (2026-08-08; commits "parity phase A/B/C").
Each entry records what Prosim2GSX does, what ProsimCompanion needs to match it, and the risk.
Reassess priorities after the 2026-08-08/09 flight test — its results (pushback menu entry
texts, profile matching, SayIntentions assignGate, seatOccupation format) may reorder items.

**Status 2026-08-08 (second pass)**: items 1–10 are implemented (GitHub issues #1–#10, one
commit per issue, all relabelled ready-for-human) and await the flight-test verification.
Only items 11 (cabin-call — write-safety review first, #11) and 12 (skip walkaround, #12)
remain unimplemented, plus the walkaround-stairs sliver of item 10 (needs Sim-pillar
CAMERA STATE / IS AVATAR reads, shared with item 12).

## Agreed differences — NOT backlog

Decided with the project owner during the parity planning pass; do not "fix" without a new decision:

- Flight Status has no inline log tail (the Logs tab owns log viewing).
- Web App Settings never shows the access token (ADR-0001 lockout prevention; desktop-only).
- Settings inputs stay in kg with fixed suffixes (Prosim2GSX also stored kg; converted status
  displays only). The kg/lb system converts displays and perf inputs, not settings fields.
- Departure-services status board lives on the GSX Status page only.
- Destructive actions use the two-click CONFIRM arm, not `window.confirm` (no JS interop).
- GSX process-restart behaviours (RestartGsxOnTaxiIn, ResetGsxStateVarsFlight, …) are presumed
  obsolete under the Remote API architecture — revisit only if a real failure mode appears.

## Backlog (suggested order)

### Quick wins first

1. **OFP/INIT data fields** — RWY OUT/IN, CRZ FL, CI, CPNY RTE (OFP + INIT), OEW / FUEL MIN /
   FUEL EXTRA and the SIMBRIEF/MCDU/MANUAL source chip (INIT) all render "—": `OfpData` does not
   capture them. Extend the SimBrief importer parsing + the model. Mechanical, low risk, best
   visible payoff per effort.
2. **Flight Status placeholder rows** — Pax Target / Pax Total (B|D) / Cargo (B|D) / Last
   Handler Event: surface the GSX boarding counters + handler events from the sync services into
   `GsxDiagnosticsStore`. Small.
3. **Per-service minimum flight time** — field on `DepartureServiceStep`, sequencer check
   against OFP enroute time, settings-editor column, tests. Small.
4. **Refuel extras** — dynamic rate for a fixed time target, skip-on-tankering (FOB > planned),
   finish-on-hose-disconnect (FUELHOSE_CONNECTED LVAR already tracked). Refuel-sync module only.
5. **Audio troubleshooting** — Device-Filter DataFlow/State selects (device enumerator scope)
   + "Write Debug Info" dump to `log\AudioDebug.txt` via the audio control seam. Small.
6. **Update-available banner** — GitHub-releases version check + dismissible header banner
   (Prosim2GSX's "New Stable Version available"). Wanted for the initial public release.
7. **Tug question + pushback-when-tug-attached** — one more `GsxQuestionCatalog` handler (port
   the exact menu title from Prosim2GSX's GsxConstants/dispatcher) + a timing rule in the
   pushback sequencer (call push after services complete / after final LS).

### Contained, needs care

8. **Real MAC envelope limits** — replace the fixed 21–38 %MAC window (Loadsheet brackets, W&B
   VALID RANGE) with weight-interpolated min/max from the trim-envelope polygon (encode the CG
   chart corner points as data). Self-contained math; unit-test heavily.

### Automation-heavy — one sim-verified pass each

9. **Door + jetway/stairs granularity** (largest item) — per-door-class rules in
   `GsxDoorService` (pax doors follow stairs, service door follows catering, cargo doors with
   keep-open choices, close-on-final via ground-ops signals) and the jetway/stairs lifecycle
   (connect at session start / departure start / arrival; remove-stairs-after-departure
   Never/Always/OnlyJetway; remove-on-final). Touches live automation state machines.
10. **Ground equipment extras** — randomized arrival chock delay min/max, PCA tri-state
    (Never/Always/OnlyJetway via mirrored gate capabilities) + override, GPU-with-APU-running,
    gradual removal during pushback. The walkaround-stairs option needs the Sim pillar to track
    MSFS 2024 walkaround/camera state (also unblocks Flight Status "Walkaround"/"Camera State").
11. **Cabin-call auto-answer** — taxi-out and approach answering with configurable delays.
    Writes ACP cabin-channel datarefs ⇒ requires a write-safety allow-list review first.
12. **Skip walkaround** — SimConnect input-event injection (Sim pillar currently read-only);
    sim-version and keybinding fragile ("requires default binding"). Lowest priority.

## Also worth remembering

- Saved-FOB list viewer (`gsx.fuelFobSaved`) has no UI; the dict is app-maintained.
- Prosim2GSX's `/debug` page equivalent: our Logs + GSX Status pages cover most of it; a raw
  snapshot page could reuse `GsxDiagnosticsStore` + `FlightStateEngine.LastSnapshot`.
- Perf pages approximate `FitToViewport` (scale-to-fit) with responsive reflow.
