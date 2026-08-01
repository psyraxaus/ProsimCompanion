# SayIntentions.AI integration reference

- Base: `https://apipri.sayintentions.ai` (SAPI under `/sapi/`).
- API key: auto-discovered from `%LOCALAPPDATA%\SayIntentionsAI\flight.json` →
  `flight_details.api_key`; re-read per call (it changes between sessions). Manual override allowed.
- Flight context: poll `flight.json` (~1 s) for callsign, gate, runway, route.

## Endpoints used

| Endpoint | Use |
|---|---|
| `GET /sapi/assignGate?api_key&gate&airport` | push assigned arrival gate to ATC |
| `GET /sapi/getWX?api_key&icao[&with_comms=1]` | METAR (+ comm frequencies for auto-tune) |
| `GET /sapi/getCurrentFrequencies?api_key` | CPDLC/frequency info |
| `GET /sapi/sayAs?api_key&channel&message` | FO transmits a phrase on COM1 (voice ATC requests) |

## Behaviours to preserve

- Arrival gate is assigned to **both** GSX (`gate.select`) and SayIntentions (`assignGate`),
  auto-fired at cruise.
- Voice ATC requests (clearance, start, push+start, taxi, takeoff) with ICAO/FAA phraseology
  variants; departure comms gating (FO takes comms near the runway, tunes Tower, hands back after
  clearance).
- Radio-clear gating before transmitting, via MobiFlight WASM LVARs `SIAI_COM1_RECEIVING` and
  `SIAI_RADIO_PTT` (registered through `MF.SimVars.Add.(L:var)` client-data channels); fully
  self-degrading when the WASM module is absent.
- Optional frequency auto-tune from `getWX` comms data — always standby-then-swap, never write the
  active frequency directly.
