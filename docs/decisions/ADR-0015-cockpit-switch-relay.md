# ADR-0015: Cockpit switches from the Stream Deck through a configured-row write gate

Date: 2026-10-24
Status: accepted (owner decision 2026-10-24: one door — the ProsimCompanion plugin — instead of a
second plugin's bridge; switches and knobs only, no circuit breakers yet)
Amends: the write-safety rule in docs/ARCHITECTURE.md (a runtime-widened, bounded allow-list)

## Context

The owner's Stream Deck moved to a second PC. Until then three Korry push buttons he has no
hardware for — the leak measurement valves B / G / Y — were pressed through a chain of
Fragtality's PilotsDeck → FSUIPC on the sim PC → a Lua script flipping bits in FSUIPC offsets
`0x04E0`–`0x04E2` → ProSim reading those offsets as hardware inputs. From a second PC
PilotsDeck cannot reach FSUIPC, so the chain broke. He also wants more such switches over
time, drawn like the real Airbus Korry buttons.

The app already talks to ProSim directly over the SDK and already ships ProSim's switch
catalogue (`SwitchCatalog`, the smart-button picker). The missing piece is a way for a remote
key to press a catalogued switch without widening the write surface to "any dataref a client
names".

## Decision

1. **One door.** The ProsimCompanion Stream Deck plugin gains a *Cockpit switch* key (and a
   *Camera view* key for the vJoy relay). No bridge to a second plugin.
2. **Configured rows, not free names.** Settings → Cockpit Switches holds rows; each names one
   `system.switches.S_*` dataref picked from the catalogue, plus how the key looks and what a
   press does. A command (`cockpit.press {id}`, `cockpit.set {id,value}`) refers to a row id,
   never to a dataref. Nothing arriving over the network is a dataref name.
3. **The write gate stays code-level, with one bounded hook.** `ProsimWriteGate` gains a single
   predicate, installed by `CockpitSwitchService` and consulted only for names that already
   start with `system.switches.S_`. The predicate says yes only for a name that is (a) a
   configured row, (b) in the switch catalogue (shipped CSV or the live ProSim list) and (c)
   while the feature is on. Circuit breakers (`CB_*`), indicators and everything else can never
   pass through it, whatever the predicate returns — the family check is in the gate itself.
4. **Read once, cache.** Each row's switch and its optional lamp indicators are push
   subscriptions; the key faces are built from the cache in `/api/status`, never from a read
   round-trip.
5. **Presses are bounded.** Toggle writes the other of the first two catalogue values;
   Momentary goes through the serialised press worker; Cycle steps through the catalogue list
   (long press backwards). A row with no catalogue entry is skipped and logged once.

## Consequences

- The three leak valves work from any PC with a button on the page that adds them pre-filled.
  FSUIPC, its offsets and the Lua script are no longer needed for them (the owner is removing
  FSUIPC separately).
- The write surface grows only by what the pilot configures, visibly, on one page; the audit
  trail is the session event `cockpit.press` with the dataref, the from/to values and the
  source.
- Circuit breakers are a deliberate follow-up: they need their own family rule and a look.
- The predicate is static state on `ProsimWriteGate`; tests that touch it run in one
  collection and reset it. A second implementer of the hook is a bug — there is one service.
