# Cockpit audio control reference

From Prosim2GSX (`AudioController`, CoreAudio 1.40.0 + VoiceMeeter).

## Sources (ProSim ACPs)

- Knobs: `system.analog.A_ASP{1|2|3}_*_VOLUME`, range **0–1024**; latches
  `system.switches.S_ASP*_*_REC_LATCH` (8 channels per ACP).
- ACP1 = Captain, ACP2 = First Officer, ACP3 = Observer.
- The cockpit LOUD SPEAKER dials are a ninth source on the Captain / First Officer side —
  see "Loudspeaker dial" below.
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

## Loudspeaker dial (2026-10-05, ours — no predecessor read it)

- Source: `system.analog.A_MIP_LOUDSPEAKER_CAPT` / `_FO`, catalogued **0–1023**; the owner's
  hardware dial reads **0–1020**. No observer dial, no REC latch. It rides the ACP feed as
  `AudioChannel.Loudspeaker` on the Captain / First Officer keys and is normalized on the same
  0–1024 scale as the knobs (full dial = +11.7 dB on a VoiceMeeter target).
- **Mute = dial fully down** (bottom 1 % of travel, `VolumeMath.DialZeroBand`): −60 dB on a
  VoiceMeeter target is still faintly audible. VoiceMeeter writes that mute whatever the
  mapping's `UseLatch`; CoreAudio keeps its `UseLatch` rule (a session volume of 0 is silent
  anyway). Only the crossing is written, and logged: `Captain Loudspeaker dial fully down —
  target muted`.
- **Power**: essential buses only (`AcpPowerGate.IsPowered(acp, channel, …)`). The
  audio-switching swap takes an ACP out of the loop but the dial is not on the ACP. Our
  rule — the real bus that feeds the loudspeaker amplifier was not looked up.
- **Routing headset sound to the speakers is a VoiceMeeter-side setup** (manual chapter 8),
  verified on the owner's Potato 2026-10-05:
  - VoiceMeeter has no per-route level, so the radio strips go to the headset bus **and** a
    spare bus (B3 = Bus 8); that copy comes back into a spare strip routed to the speaker
    bus, and the dial drives the level of **Bus 8**.
  - VoiceMeeter does **not** list its own `Voicemeeter Out B*` devices in a strip's
    hardware-input selector (our first instruction assumed it did). The loop is **VBAN to
    `127.0.0.1:6980`**: outgoing stream source `BUS B3`, incoming stream of the same name →
    the spare strip. Owner's verdict: "next to no delay" (Net Quality Optimal).
  - The app writes one gain and one mute; it never touches routing buttons or VBAN.
- `system.gates.B_LOUDSPEAKER_MUTING` (ProSim's mute-speakers-while-transmitting gate) exists
  and is **not** used yet — the obvious next step if speaker sound reaches the microphone.

## Cabin/crew sounds

Chimes ("ding") on: startup-ready, final loadsheet received, parked, deboard complete; MECH call on
chocks. Momentary presses via overhead call datarefs — serialize through the press channel.
When we own audio, disable ProSim's native audio channel control (see prosim.md §3).
