# The MCDU check ("check the box") — page layouts and rules

Issue #247. Implemented in `ProsimCompanion.Speech/Mcdu` (`McduBoxCheckService`,
`BoxCheckPages`, `BoxCheckEvaluator`) over the existing `McduReader` / `McduActuator`.
Everything below was captured from the owner's rig on **2026-11-15** (ProSim A322, A320-200 /
V2527-A5, BAW504 EGLL–LPPT at the gate) by pressing the FO's keys through the ProSim gateway
and reading `aircraft.mcdu2.display` after each. The capture is a test fixture:
`tests/ProsimCompanion.Core.Tests/Speech/Fixtures/mcdu-egll-lppt-2026-11-15.txt`.

## How the capture was made (repeat it for another ProSim version)

- Read: `POST http://{prosimHost}:5000/graphql`
  `query { dataRef { v: dataRef(name: "aircraft.mcdu2.display") {value} } }`.
- Press: `writeInt` 1, wait 150 ms, `writeInt` 0 on `system.switches.S_CDU2_KEY_<suffix>`.
- Wait until two reads 300 ms apart are identical (the display double-buffers, see
  `McduReader.Read`). In 48 reads on 2026-11-15 no torn frame was seen.

## What the old macro did (and why it was replaced)

The `commands.json` macro pressed DATA, INIT, FPLN, SEC_FPLN, RAD_NAV, ARROW_RIGHT, PERF,
FPLN with 250 ms between keys. Observed: each page on screen for 0.6–1 s; **ARROW_RIGHT on
RADIO NAV does nothing**, so INIT B was never shown; DATA and SEC_FPLN open index pages only;
PROG was not in the list; nothing was read.

## Navigation facts

| To reach | Keys | Title when there |
|---|---|---|
| A/C STATUS | `DATA`, then the LSK of the row whose data is `<A/C STATUS` (4L) | the aircraft type (`A320-200`) |
| IRS MONITOR | `DATA`, row with label `IRS` and data `<MONITOR` (2L) | `IRS MONITOR` |
| GPS MONITOR | `DATA`, row with label `GPS` and data `<MONITOR` (3L) | `GPS MONITOR` |
| INIT A | `INIT` | `INIT` |
| INIT B | `INIT`, `ARROW_RIGHT` | `INIT FUEL PREDICTION` |
| F-PLN, next line | `FPLN`, then `ARROW_UP` | `FROM … <flight number>` |
| SEC F-PLN | `SEC_FPLN` (title `SEC INDEX`), row `SEC F-PLN` (2L); 1L is `COPY ACTIVE` | `FROM …` |
| RAD NAV / PERF / PROG | `RAD_NAV` / `PERF` / `PROG` | `RADIO NAV` / `TAKE OFF RWY 27R` / `PREFLIGHT BAW504` |

- **`ARROW_UP` moves the F-PLN one line towards the destination**; EGLL–LPPT took 33 presses,
  then the list wraps to the origin. The order is: active plan, `END OF F-PLN`, alternate
  plan, `END OF ALTN F-PLN`, origin again. The sixth row is always the destination summary.
  The check presses four times, reads, and joins the screens on their overlap; it stops at
  `END OF F-PLN`.
- A discontinuity is the line `---F-PLN DISCONTINUITY--`. Computed points are in brackets:
  `(T/D)`, `(DECEL)`, `(LIM)`, `(T/C)` — not fixes.
- An empty secondary plan shows `------END OF F-PLN------` on its first line.

## Field facts

- Colour and font are single lower-case letters (`McduDisplayParser`); a mandatory empty
  field is amber `###` (V1/VR/V2), an optional empty one is cyan brackets `[  ]`.
- INIT A: `NONE  EGLL/LPPT` (CO RTE, FROM/TO), `LPFR` (ALTN), `BAW504  IRS INIT>`,
  `5   34421` (cost index, tropopause — one number alone is the tropopause),
  `FL370 /-55°`.
- INIT B: `0.2   53.9/ 25.3` (taxi, ZFW/ZFWCG), `6.2/0225   8.5` (trip/time, block). The
  weight unit follows ProSim's Units → Weight; the check compares in the unit shown.
- A/C STATUS: `29OCT26-25NOV26` under `ACTIVE NAV DATA BASE`.
- IRS MONITOR: the mode (`NAV`) is the small line **under** each `<IRSn`.
- GPS MONITOR: `100M   49   NAV/6` under `MERIT  GPS ALT  MODE/SAT`.
- PROG: `<GPS   GPS PRIMARY`; `1.0NM   HIGH   0.03NM` under `REQUIRED ACCUR ESTIMATED`.
- The scratchpad carried the message `WIND DATA UPLINK`; one CLEAR removes a message.

**Not seen on the rig** (the reader uses the real aircraft's forms; verify on a flight):
entered V speeds, `1/UP0.5` in FLAPS/THS, `F52°` in FLEX TO TEMP, an empty cost index /
flight number / city pair, every page in flight, and all layouts on ProSim 1.74b8 / 1.75.1.

## Write rules

- **Without `mcdu.allowActuation`**: only `McduControls`' navigation allow-list (DATA, INIT,
  FPLN, SEC_FPLN, RAD_NAV, PERF, PROG, the four arrows) and `FollowLinkAsync` — an LSK
  pressed only when the settled screen shows the expected link text on that row. Owner
  decision 2026-11-15; the old macro pressed the same page keys with no switch at all.
- **Dataref-first**: INIT B through `IFmsInitSync`; V speeds, flex and flaps through
  `aircraft.fms.perf.takeOff.*` (already in the write gate). No key.
- **By keys, armed only**: flight number, cost index and cruise level (clear the scratchpad,
  type, check the scratchpad equals the entry, press the field's LSK, read the field back) and
  COPY ACTIVE. Each only after a yes to the FO's question.
- **Never**: city pair, alternate, route, SID, STAR, discontinuities.
