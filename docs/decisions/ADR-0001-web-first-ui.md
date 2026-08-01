# ADR-0001: Web-first UI with a minimal WPF shell

**Status:** Accepted (2026-08-01, project owner decision)

## Context

Prosim2GSX maintained a full WPF settings UI *and* a React web EFB mirroring most of it — every
setting built twice. Prosim2FO kept all config in a 2,000-line WPF view-model with a read-only web
dashboard. Both duplicated effort or limited remote access.

## Decision

ProsimCompanion has exactly one configuration/feature UI: the browser, served by embedded Kestrel.
The WPF app is a thin shell: connection status, web UI URL + QR code, open-browser button, and the
web-server settings themselves (port/bind/token). Those stay in WPF purely for lockout prevention —
if the web config is broken you can still fix it.

## Consequences

- Every setting/page is built once and works from any LAN device (sim PC, tablet, second PC).
- The web server becomes a hard dependency of configuration → keep it minimal-risk: sensible default
  port (5320), loopback bind by default, bearer-token auth for LAN.
- WPF work is intentionally frozen at "shell" scope; resist adding feature UI there.
