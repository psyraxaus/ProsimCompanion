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
- LVAR access strategy (resolved at Phase 2 start, 2026-08-01): native SimConnect LVAR support
  (MSFS 2020 SU12+ and 2024, identical mechanism — `"L:Name"` straight into data definitions).
  No WASM/MobiFlight component needed; details in docs/integrations/gsx.md §5.
