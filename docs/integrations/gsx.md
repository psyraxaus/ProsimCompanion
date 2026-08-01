# GSX Pro integration reference

From Prosim2GSX (branch `GsxRemoteApiImplementation`). Primary upstream references:
`Prosim2GSX\docs\00-shared-context.md` (locked Remote API decisions — carry over verbatim) and
`Prosim2GSX\GSX\GsxConstants.cs` (full LVAR catalog). Requires GSX Pro ≥ 4.0.0.

## 1. Couatl Remote API v2 (primary control path)

- Transport: WebSocket JSON at `ws://127.0.0.1:{port}`. Port read from
  `%APPDATA%\Virtuali\CouatlAddons.ini`, `[gsx] remote_server_port`, default **8744**.
  **Do not make the ini key configurable in-app** — a configurable key once caused a
  silent-degradation bug.
- Handshake: hello frame carrying `protocol: 1` + capabilities exchange.
- Verbs: `state.get`, `gate.select`, `service.trigger`, `menu.pick`, `menu.open`, `menu.close`,
  `handler.set` (incl. `autoSelectOperator`).
- Keep a client-side state cache updated by patches; mirror per-service state
  (`GsxServiceStateMirror` pattern) backing each service's `State`.
- Gate selection retry ladder: plain `gate.select` → disambiguation → service-revoke → force.
  Confirm via `SetGate_*` LVAR readback within a ~60 s window.
- Menu interaction is an **intent framework**: match live menu text (string/regex), verify the
  outcome by polling, and on any uncertainty *leave the menu open for the user* — never a wrong
  click.

## 2. LVARs that deliberately remain (timing-critical / no API equivalent)

State values for per-service `_STATE` LVARs: `1` Callable, `4` Requested, `5` Active, `6` Completed —
**except refuel, which completes back at `1`**.

| LVAR | Notes |
|---|---|
| `L:FSDT_GSX_COUATL_STARTED[_{5,6,7}_PROGRESS]` | Couatl engine alive/progress |
| `L:FSDT_GSX_MENU_OPEN` / `MENU_CHOICE` | legacy menu; choice is 0-based |
| `L:FSDT_GSX_..._STATE` per service | see state values above |
| `L:FSDT_GSX_FUELHOSE_CONNECTED` | drives refuel sync start/stop |
| `L:FSDT_GSX_NUMPASSENGERS[_BOARDING_TOTAL\|_DEBOARDING_TOTAL]` | live pax counters → ProSim sync |
| `L:FSDT_GSX_BOARDING_CARGO_PERCENT` / `DEBOARDING_CARGO_PERCENT` | cargo progress |
| `L:FSDT_GSX_PUSHBACK_STATUS` | 3/4 = tug connected |
| `L:FSDT_GSX_VEHICLE_PUSHBACK_STATE` | 8 pushing, 11 wait-shutdown, **12 awaiting engine start**, 13 disconnecting, 14 clear |
| `L:FSDT_GSX_BYPASS_PIN` | pin inserted/removed |
| `L:FSDT_GSX_DEICING_TYPE` | 1–4 (fluid type) |
| `L:FSDT_GSX_OPERATEJETWAYS_STATE` / `OPERATESTAIRS_STATE` | **unreliable/sticky — apply a 30 s grace window** |
| `L:FSDT_GSX_SetGate_Name/Number/Suffix` | gate readback; letter map 12=A…37=Z, 10=GATE |
| `L:FSDT_GSX_DISABLE_DOORS_MSG` | suppress GSX door prompts while we drive doors |
| `L:FSDT_GSX_SET_REMOTECONTROL` | headless/remote-control mode |
| crew/pilot/cabin question suppression flags, door toggles | see GsxConstants |

GSX binaries to detect: `Couatl64_MSFS` (2020) / `Couatl64_MSFS2024`. Menu title anchors:
`"Activate Services at"`, `"Additional Services"`.

## 3. Ground automation state machine

Phases: SessionStart → Preparation → Departure → PushBack → TaxiOut → Flight → TaxiIn → Arrival →
TurnAround, evaluated on a tick loop. Per-service `GsxServiceState`
(Callable/Requested/Active/Completed/…) with activation policies and constraints (company-hub vs
non-hub, turnaround-only services). Departure service order is user-configurable
(refuel/catering/water/lavatory/cleaning/boarding).

Behaviours that must survive the port:
- **OFP gating**: refuel/boarding hard-rejected until the SimBrief OFP is imported (30 s wait loop).
- **Beacon-orchestrated pushback**: doors → jetway → ground equipment removal with randomized
  delays; INT/RAD skip; pause/resume on beacon toggles.
- **Safety interlocks**: chocks removal requires park brake set; GPU removal blocked while external
  power is connected (unless forced); GPU & chocks auto-removed when beacon comes on.
- **Arrival**: stable-parked detection before services; optional GSX restart on taxi-in; gate
  assignment auto-fired at cruise (to GSX via `gate.select` and to ATC via SayIntentions).
- **INT/RAD ACP switch** doubles as a universal service trigger/advance input ("smart button").
- Cargo doors: open on boarding start, timed close after GSX loaders finish; keep-open options.
- Refuel: GSX hose connect starts ProSim dataref stepping (fixed kg/s or time-target rate);
  FOB save/restore per aircraft registration; round-up-to-100 kg option; variance-tolerant
  completion correction.
- Boarding: GSX counters → ProSim zone amounts + seat map, seat-level reconciliation at complete,
  optional no-show/extra randomization with cargo-weight adjustment.

## 4. In-sim handler / VDGS

A Stackless-Python handler (`gsx_handler.py`) deployed with GSX aircraft profiles to
`%APPDATA%\Virtuali\Airplanes` calls back into the companion's web server (loopback,
**token-exempt** — Stackless Python can't carry a bearer header): `GET /api/gsxmenu/events`,
`GET /api/gsxmenu/flight-info`. The deployer rewrites `PROSIM2GSX_PORT = <port>` inside the handler.
Still the VDGS event source even after the Remote API migration. GSX SimBrief reload can be
triggered for VDGS data.

## 5. MSFS-version specifics

- Walkaround skip is MSFS 2024 only.
- **LVAR access (RESOLVED 2026-08-01)**: native SimConnect handles LVARs directly on both MSFS
  2020 (SU12+) and 2024 — pass the full `"L:Name"` into `AddToDataDefinition` with unit
  `"number"` (FLOAT64), request with `SIM_FRAME` + `CHANGED`, write via `SetDataOnSimObject`.
  No WASM module, no MobiFlight, no version branching (verified from CFIT.SimConnectLib source —
  this is why Prosim2GSX's installer *removes* MobiFlight). Caveats: the sim silently
  auto-creates unknown LVAR names as 0 (typos fail invisibly — keep names centralized in
  `ProsimDataRefNames.Lvars`); after a write, the sim echoes the value on the next frame —
  treat the echo as authoritative, don't suppress it.
- Full Remote API wire protocol: see [gsx-remote-api.md](gsx-remote-api.md).
