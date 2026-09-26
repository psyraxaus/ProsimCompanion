# Changelog

## 0.5.0-rc.4

- Fix: the app crashed at a ProSim reconnect when simulator.time arrived as a local-kind date (sim clock threw ArgumentException; the Monitor page's timer thread terminated the process). The sim clock now degrades to "not live" instead of throwing, and the Monitor tick and the loadsheet STD tick are guarded. Probe: sim-clock-local-kind-crash.

## 0.5.0-rc.3

- New: every session file and the CMTrace log are version-stamped (session-started/session-rotated header payload, startup banner).
- New: warnings and errors are mirrored into the session file as log.warning / log.error / log.fatal (logging.mirrorToSession, Logs page).
- New: "Export diagnostics" support bundle on the Logs page (/api/diagnostics/bundle) with redacted settings, hash manifest and a "Copy version" button.
- New: tools/ProsimCompanion.Reduce support reducer CLI + docs/agents/support-bundle.md; nine probes gained machine-checkable rules.
- Fix: the Advanced settings tab opened a 404; it now lands on Flight Phase Engine.

## 0.5.0-rc.2

- Fix: gradual ground-equipment removal pulled the GPU immediately after placement at cold and dark (external power not yet on). Ground prep now logs waiting/stalled stages.
