# Using the app

## Desktop shell & tray

The desktop window shows subsystem connection states and the web-server card (port, LAN
binding, access token, QR). Minimizing hides the window to the **system tray**; double-click
the tray icon (or *Show Window*) to bring it back, *Open Web UI* to jump to the browser,
*Exit* to quit. Closing the window exits the app. Starting the app twice just brings the
running instance's window to the front.

## Web UI tour

| Tab | What it does |
|---|---|
| **Flight Status** | Phase bar, Sim/App/GSX cards, per-service state pills, boarding counters, last handler event |
| **INIT** | MCDU-style OFP display: fetch the SimBrief OFP, per-field overrides (ZFW, fuel, pax, cargo), SYNC TO FMS, flight reset |
| **OFP** | Flight-plan summary, pushback-direction Korry buttons, arrival-gate assignment, weather (METAR/TAF/ATIS), de-ice holdover card |
| **Loadsheet** | Prelim/final loadsheets with per-weight MAC envelope brackets, manual STD, resend/reset |
| **W&B** | Live weights/CG on the envelope chart, valid MAC ranges, pax/cargo state, aircraft silhouette with doors, passenger manifest, SIMULATE |
| **Fuel** | Fuel panel |
| **Performance** | Takeoff and Landing sub-tabs — runway/intersection/condition pick, calculated speeds, FMS PERF uplink |
| **Checklists** | ECAM-style interactive checklists (visual runner; the voice FO runs beside it) |
| **GSX** | Status board (departure services with hold/skip reasons), decision log, gate control, and all GSX settings |
| **Aircraft Profiles** | Per-aircraft settings profiles with automatic matching |
| **Audio** | Audio pillar status + settings (CoreAudio mappings, VoiceMeeter, device filters) |
| **App Settings** | ProSim connection/SDK path, theme, units, logging levels |
| **First Officer** | Speech pillar status + settings (voices, recognition, PTT, minima card) |
| **Tech Log / Duty Day / Logs** | MEL & tech log, multi-leg company day mode, live log viewer |

## A typical flight

1. **Cold & dark at the gate.** Ground prep runs automatically: reposition → GPU + chocks
   (+PCA per settings) → jetway or stairs. Nothing else happens until a flight plan exists.
2. **Load the plan.** Enter the route in the MCDU (or press FETCH OFP on INIT). The SimBrief
   import writes the booked seat map, planned fuel and cargo into the ProSim EFB.
3. **Departure services** run in your configured order (default: cleaning/lavatory on
   turnarounds, refuel + catering in parallel, water, then boarding last). The INT/RAD
   switch or the GSX page's button force-calls the next service. Refuel steps the ProSim
   fuel while the GSX hose is connected; boarding fills seats as GSX counts pax aboard.
4. **Loadsheets.** The preliminary loadsheet arrives when refuel starts; the final one after
   boarding completes (dispatcher delay). Doors close and the jetway retracts on final —
   or, with the beacon sequence enabled, everything runs off the beacon: APU up → doors →
   jetway → ground equipment → pushback, with crew-realistic random delays.
5. **Pushback.** GSX's questions (direction, tug, de-ice) are answered per your settings —
   every answer, and every "left for you", is in the decision log.
6. **Flight.** The arrival gate you confirmed on the OFP page is sent to GSX (and
   SayIntentions ATC) at cruise. The voice FO runs callouts, monitors flows, and briefs on
   request.
7. **Arrival.** Once stably parked (engines off, brake set, beacon off): fuel is remembered
   for next session, chocks go in after a crew-realistic delay, jetway/stairs connect,
   deboarding is called and the cabin empties front-first. At shutdown the FO debriefs, the
   logbook folds the flight and the tech log carries its sectors.

## Themes & units

Airline themes (dark/light and airline palettes) switch live from App Settings; drop your own
JSON themes in `config/themes`. Weight displays follow the app unit setting or the aircraft's
own unit selection (kg/lb); settings entry fields stay in kg.
