# ADR-0004: GSX ground automation is the first feature pillar

**Status:** Accepted (2026-08-01, project owner decision)

## Context

After the Phase 1 foundation, three pillars compete to be ported first: GSX ground automation
(Prosim2GSX), the voice First Officer (Prosim2FO), or the EFB/flight-data experience.

## Decision

Port GSX ground automation first (Phase 2), then flight data/EFB (Phase 3), audio (Phase 4), and the
voice FO (Phase 5).

## Consequences

- The most self-contained pillar lands first and exercises all foundation seams (ProSim writes,
  SimConnect LVARs, flight phases, state stores, profiles) with the fewest extra subsystems.
- The GSX Remote API migration knowledge (fresh in Prosim2GSX's `GsxRemoteApiImplementation` branch
  and `docs/00-shared-context.md`) is ported while it is current — its locked decisions carry over
  verbatim.
- Speech/LLM/audio subsystems wait; Prosim2FO remains the daily driver for FO features until Phase 5.
