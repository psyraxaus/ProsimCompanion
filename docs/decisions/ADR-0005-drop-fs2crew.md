# ADR-0005: Drop the FS2Crew bridge — superseded by the built-in voice FO

**Status:** Accepted (2026-08-01, project owner decision)

## Context

ProsimInterface carried a large bridge layer (~60 dataref→LVAR mirrors plus a reverse control
direction) whose sole purpose was to let **FS2Crew's Fenix A320 profile** — a third-party virtual
crew product — work with ProSim, by impersonating Fenix-style LVARs.

ProsimCompanion incorporates the Prosim2FO voice First Officer as a native pillar (Phase 5), which
replaces what FS2Crew provided: checklists, callouts, flows, and FO-side actions — driving ProSim
datarefs directly, with no LVAR impersonation layer.

## Decision

The FS2Crew bridge is not carried into ProsimCompanion. Its integration reference
(`docs/integrations/fs2crew.md`) is removed and its roadmap/feature-inventory entries dropped.

## Consequences

- One large, brittle mapping layer (Fenix LVAR impersonation, increment-detection press signaling)
  never needs porting or maintaining.
- Users who still run FS2Crew alongside ProSim keep using the legacy ProsimInterface/Prosim2GSX
  pair for that; ProsimCompanion does not aim to support it.
- Generic knowledge that happened to live in that doc was moved, not lost: the ProSim→MSFS baro
  sync encoding (`KOHLSMAN_SET`, hPa × 16, index 0 = captain) now lives in
  `docs/integrations/prosim.md`; the serialized momentary-press pattern and the 0–1024 analog scale
  were already documented there. The mappings themselves remain readable in the ProsimInterface
  repo (`Fs2CrewMappings.cs`) if ever needed for archaeology.
