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

## Cabin/crew sounds

Chimes ("ding") on: startup-ready, final loadsheet received, parked, deboard complete; MECH call on
chocks. Momentary presses via overhead call datarefs — serialize through the press channel.
When we own audio, disable ProSim's native audio channel control (see prosim.md §3).
