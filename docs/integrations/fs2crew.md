# FS2Crew (Fenix profile) bridge reference

From ProsimInterface (`SdkLvarBridgeService`, `Fs2CrewMappings` — port the maps verbatim; ~60
subscriptions ProSim→LVAR plus the reverse control direction). Lets FS2Crew's Fenix A320 profile
work with ProSim by mirroring state into Fenix-style LVARs (`L:S_*`, `L:A_*`, `L:I_*`, `L:N_*`).

## ProSim → LVAR (monitoring direction)

Signs, APU, engines, lights, control surfaces, flaps/gear/speedbrake, autobrake, AP indicators,
EFIS, packs, anti-ice, fuel pumps, WX radar, TCAS/XPDR, GPWS, ECAM indicators, baro.
Surface normalizations: elevator is sign-inverted.

## LVAR → ProSim (control direction)

- FS2Crew *increments* LVARs to signal presses: ECAM keys +2; FCU knobs ±1 where
  up = pull/selected → write 2, down = push/managed → write 1. Detect increments, then issue
  serialized momentary presses (`Channel<T>` worker: write 1 → hold `MomentaryPressMs` → write 0 →
  inter-press delay).
- Full MCDU2 keyboard mapped (`system.switches.S_CDU2_KEY_<suffix>`).
- FO axis inputs: LVAR −1000..1000 → ProSim analog `(v/1000×512)+512`; SimConnect axis
  −16383..16383 → `((v+16383)/32766)×1024` (ProSim analog range 0–1024).
- Baro sync ProSim→MSFS: SimConnect event `KOHLSMAN_SET`, value = hPa × 16, index 0 = captain.
- Sim events used: `AXIS_ELEVATOR_SET`, `KOHLSMAN_SET`.
