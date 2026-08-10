# MSFS session detection

Distilled from Prosim2GSX / CFIT.SimConnectLib (the predecessor's empirically-hardened session
ladder) and implemented in `ProsimCompanion.Sim` (`SimSessionEvaluator`, `SimSessionService`)
publishing `Core.State.SimSessionStore`. This is the gate that keeps GSX ground prep (the
reposition above all) from firing while MSFS is on the main menu or still loading.

## Why the SimConnect connection is not a session signal

The SimConnect handshake (`OnRecvOpen`) succeeds from the MSFS **main menu**. Meanwhile ProSim
keeps pushing plausible cold-and-dark datarefs and GSX's Couatl Remote API socket answers, so
every ProSim/GSX-side precondition for ground prep can pass with no pilot in a flight. The
predecessor solved this with a camera-state ladder; the raw connection only ever meant "MSFS
process is up".

## Signals used

| Signal | Transport | Meaning |
|---|---|---|
| `CAMERA STATE` (Enum) | SimVar subscription | Which camera/screen the user is on |
| `Sim` system event | `SubscribeToSystemEvent` | Flight sim state running (1) / stopped (0) |
| `Pause_EX1` system event | `SubscribeToSystemEvent` | Pause flag bitmask; 0 = unpaused |
| `IS AVATAR` (Bool) | SimVar subscription, **MSFS 2024 only** | Pilot is walking as an avatar |
| `SIMCONNECT_RECV_OPEN` version | connection handshake | MSFS 2020 (major 11) vs 2024 (major ≥ 12); gates the `IS AVATAR` subscription (2020 would error on it every session) |

Both system events transmit their current state immediately on subscribe, so a companion app
started mid-session still converges.

### Camera state values (empirical, from CFIT)

- `1–10` — in-flight views (cockpit, external/chase, drone, fixed…): **session cameras**.
- `0` — unset; `11` — load/wait screen; `12` — world map; `13–28` — hangar/menu RTCs
  (predecessor allowed `26`, and `16` only when unpaused — we treat all as non-session).
- `29–31` — MSFS 2024 walkaround/avatar family: session exists, pilot outside the aircraft.
- `32`, `35` — explicitly excluded by the predecessor (menu-adjacent).

## The phase rules (`SimSessionEvaluator`)

Phases: `Unknown` (no connection / no camera data — session-gated automation holds),
`NotInSession`, `Walkaround`, `InSession`.

- **Entry** to the session requires: session camera ∧ `Sim` running ∧ **unpaused**, sustained
  for 2 consecutive ticks (500 ms tick). Unpaused matters because MSFS parks the loaded flight
  *paused on a valid cockpit camera* during the **"Ready to Fly" hold** — the predecessor's
  README documented FlowPro's "Skip Ready to Fly" defeating this gate as a real failure mode
  (Prosim2GSX README §7.4). The debounce mirrors CFIT's two-tick `LastCameraValid` latch:
  the camera flickers through valid values while a flight loads.
- **Exit** happens immediately when the camera leaves the session values or the `Sim` state
  stops. A pause does **not** end the session (CFIT's `IsSessionStopped` likewise ignored
  plain pause).
- **Walkaround** = `IS AVATAR` == 1, or camera 29–31. Ambiguity (avatar and aircraft flags
  both set — a transitional state the predecessor waited out) maps to Walkaround: ground
  automation stays held, which is the safe direction.

## Consumers

- `GsxGroundPrepCoordinator` — holds the whole prep chain (reposition → gate anchor → GPU →
  jetway) until phase == `InSession` **and** the startup resync has assessed. `Unknown` holds
  too — a reposition teleports the aircraft, and a signal we cannot read is not a signal that
  passed (decided 2026-08-10: block, log why; no config bypass). Session end resets the chain.
- `GsxStartupResyncService` — the one-shot assessment (and its 90 s timeout window) waits for
  session entry: the tracking LVARs are created with the flight, so assessing from the menu
  would always latch "nothing detected". The departure sequencer already holds on the
  assessment, so departure automation is transitively session-gated.
- Web Flight Status page — Sim Running / Sim Session / Sim Paused / Walkaround / Camera State /
  Sim Version rows.

## Live-verification list (not yet sim-verified)

- MSFS 2024 camera values 29–31 and `IS AVATAR` behaviour during walkaround → boarding.
- The "Ready to Fly" hold arriving as a `Pause_EX1` flag on a valid camera in both 2020/2024.
- `Sim`/`Pause_EX1` initial-state delivery when the app starts mid-session (app-restart
  resync depends on it).
- MSFS 2024 `SIMCONNECT_RECV_OPEN` application version major (assumed ≥ 12).
