# Cockpit audio control reference

From Prosim2GSX (`AudioController`, CoreAudio 1.40.0 + VoiceMeeter).

## Sources (ProSim ACPs)

- Knobs: `system.analog.A_ASP{1|2|3}_*_VOLUME`, range **0–1024**; latches
  `system.switches.S_ASP*_*_REC_LATCH` (8 channels per ACP).
- ACP1 = Captain, ACP2 = First Officer, ACP3 = Observer.
- **Power gating** (ignore knob values when the ACP is unpowered):
  CPT — AC/DC ESS present and audio-switching ≠ 0; FO — audio-switching ≠ 2; OBS — DC1.

## Targets

- **CoreAudio** per-app session volumes. Default channel→app mappings:
  VHF1 → vPilot / BeyondATC / Pilot2ATC; INT → Couatl (GSX); CAB → FlightSimulator.
  Device blacklist supported; detect elevated processes (can't control their sessions — warn).
- **VoiceMeeter** strips/buses via `VoicemeeterRemote64.dll`, dynamically loaded (never
  redistributed; typical path `C:\Program Files (x86)\VB\Voicemeeter\VoicemeeterRemote64.dll`).
  Mapping: 0–100 % → −60 dB … +12 dB. Per-ACP strip/bus maps; live backend switching between
  CoreAudio and VoiceMeeter without restart.

## Rewrite decisions (Phase 4, 2026-08-06)

- **Unified power gate**: the full per-ACP gate (AC/DC ESS + `S_AUDIO_SWITCHING` for CPT/FO,
  DC1 for OBS) applies to BOTH backends. The predecessor gated CoreAudio only on DC ESS at
  service start — writes continued through a power loss. While unpowered, knob events are
  suppressed (targets hold); on power restore current values re-emit so targets catch up.
- **Blacklist semantics**: entry matches the START of the device friendly name (the
  predecessor's README wording). The predecessor code had the `StartsWith` arguments reversed;
  full-name entries behaved identically, so config migrates cleanly.
- **Neutral-reset ordering fixed**: the predecessor called `ResetStripsToNeutral()` AFTER
  unbind, which made it dead code (bindings already cleared) — a knob's last attenuation stayed
  in the VoiceMeeter chain. The rewrite resets to 0 dB unmuted while still bound.
- **Elevated probe**: `Process.MainModule` read; `Win32Exception`/`InvalidOperationException`
  ⇒ elevated (the same integrity barrier hides the app's audio sessions from unelevated
  CoreAudio enumeration, so the probe is a reliable proxy).
- Predecessor-proven quirks kept: idempotent `VBVMR_Login` (suspend writes on backend switch,
  logout only at shutdown), full knob = +12 dB (past unity), `UseLatch=false` ⇒ mute never
  written, per-mapping write coalescer (CoreAudio writes can take tens of ms; VM writes are
  sub-ms and stay synchronous), Process-handle disposal every scan (a ~200–500 handle/tick
  leak used to degrade the audio stack in minutes), MTA-thread COM enumerator, PA excluded
  from the native-window clear.

## Cabin/crew sounds

Chimes ("ding") on: startup-ready, final loadsheet received, parked, deboard complete; MECH call on
chocks. Momentary presses via overhead call datarefs — serialize through the press channel.
When we own audio, disable ProSim's native audio channel control (see prosim.md §3).
