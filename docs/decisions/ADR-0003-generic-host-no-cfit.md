# ADR-0003: .NET Generic Host + Microsoft.Extensions DI; no CFIT packages

**Status:** Accepted (2026-08-01, project owner decision)

## Context

Prosim2GSX/ProsimInterface are built on the CFIT.* stack (AppFramework, SimConnectLib, AppLogger)
from a local package feed: service-locator style (`AppService.Instance`), static logger, and a
framework the project doesn't control. Prosim2FO already uses the standard Generic Host.

## Decision

ProsimCompanion uses only standard `Microsoft.Extensions.*` hosting/DI/options/logging (Serilog as
the logging sink). No CFIT packages at all — including SimConnect: `ProsimCompanion.Sim` implements
the SimConnect + LVAR layer in-repo against the raw Microsoft SimConnect SDK.

## Consequences

- Constructor injection and narrow interfaces throughout; testable; no local NuGet feed to replicate.
- We re-solve SimConnect plumbing (connection lifecycle, LVAR registration, reconnect). Mitigation:
  the *behavioural* knowledge (which LVARs, encodings, timing quirks) is preserved in
  `docs/integrations/gsx.md` — only transport code is rewritten. CFIT.SimConnectLib and the
  MobiFlight WASM pattern in Prosim2FO's `Sim` project remain readable references next door.
- LVAR access strategy must be decided in Phase 1: MSFS 2024+ SimConnect exposes LVARs natively;
  fall back to a MobiFlight-WASM-style client-data channel only if needed for MSFS 2020.
