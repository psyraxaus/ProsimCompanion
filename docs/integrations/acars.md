# ACARS uplinks (company → cockpit)

Everything the app sends to the MCDU RCVD MSGS list goes through ProSim's `efb.aoc.message.uplink`
(envelope and quirks in `prosim.md` → ACARS). This note is the catalogue of senders, the slot
table and the exact text layouts, so a new message never collides with an old one.

## The seam

`ProsimCompanion.Core.Aircraft.Acars.IAcarsUplink` — `Task<bool> SendAsync(AcarsMessage, ct)`.
Implemented by `ProsimCompanion.Prosim.Acars.AcarsUplink`; registered only when the Prosim
pillar is composed in, so Speech-side senders take it as an optional constructor dependency and
degrade to the spoken channel alone. `true` = the gateway accepted the write (ProSim dispatches
asynchronously; the cockpit shows the entry a moment later).

## Slot table

| Slot | Sender | `type` / `header` | `accept` | Replaces the prior entry? |
|---|---|---|---|---|
| `01` | preliminary loadsheet | `LOADSHEET` | true | yes — a regenerated prelim overwrites |
| `02` | final loadsheet | `LOADSHEET` | true | yes |
| `03` | LMC (last-minute change, package 2, branch `feature/lmc`) | see that branch | false | yes |
| `05` | diversion notice (package 5, branch `feature/diversion`) | see that branch | false | yes |
| `06` | movement message OFF (package 6) | `MVT` / `MOVEMENT` | false | yes — one per cycle |
| `07` | movement message IN (package 6) | `MVT` / `MOVEMENT` | false | yes — one per cycle |

Rules: never reuse `01`/`02` (the ATSU loadsheet-ACCEPT handling looks for them); `accept: true`
only when the message really wants the pilot's ACCEPT; a new sender takes the next free slot
and adds a row here.

## Movement message (MVT) layout — issue #185

Composed by `MovementMessageComposer` (Core, pure, tested). One field group per line, `\n`
separated; the GraphQL layer escapes the line breaks for transport. Times are the flight-times
tracker's **sim-clock** stamps as `HHMM` UTC; the day in the header is the OUT (or ON) stamp's
day of month, so a message across midnight keeps the departure day. The registration is written
without its hyphen (`GEUYB`), the airline convention.

OFF (sent at the take-off edge):

```
MVT
BAW123/03.GEUYB.EGLL
AD 1022/1035 EA 1240 LIRF
DL 15/0022
```

- line 2: `{flightNumber}/{dd}.{registration}.{departure ICAO}` — the OFP's flight number
  (callsign when empty), registration and origin;
- line 3: `AD {out HHMM}/{off HHMM} EA {eta HHMM} {destination ICAO}` — the EA clause is dropped
  when no ETA exists (no progress ETA and no OFP enroute time), leaving `AD 1022/1035 LIRF`;
- line 4 (only with a delay code on record and a positive delay): `DL {code}/{HHMM}` — the delay
  as hours and minutes.

IN (sent at the on-blocks edge):

```
MVT
BAW123/03.GEUYB.LIRF
AA 1203/1210
DL 15/0022
```

- line 2 names the **destination** station; line 3 is `AA {on HHMM}/{in HHMM}`; the DL line as
  above.

Session events: `acars.mvt` `{kind off|in, slot, text, sent, acars}` per message;
`flight.punctuality` `{outUtc, scheduledOutUtc, delayMinutes, toleranceMinutes, onTime}` at the
OUT edge; `flight.delayCode` `{code, title, delayMinutes, source, note}` when a code is filed.
The spoken confirmation (`company.movementMessagesSpoken`) goes through the company channel so
it wears the company voice and chime.

## Where the ETA comes from

`FlightProgressStore.EtaUtc` when the progress model has one (it needs a position and a
destination), else take-off + `OfpData.EstimatedEnroute`, else no EA clause. The EA is an
estimate at take-off and is not revised — the real IN time arrives with the AA message.
