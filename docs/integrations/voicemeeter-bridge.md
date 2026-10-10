# Remote mixer: VoicemeeterBridge

ProsimCompanion can drive a Voicemeeter that runs on **another PC** — typically the one
running vPilot or your ATC client — through the VoicemeeterBridge agent. The agent sits in
that PC's tray and exposes Voicemeeter over a small WebSocket protocol
(`claude/mixer-protocol.md`, protocol v1). The agent lives in its own repository,
<https://github.com/psyraxaus/VoicemeeterBridge> (installer on its Releases page; first
published build 0.1.0, 2026-10-10). This is separate from the local VoiceMeeter
backend of the audio pillar, which drives the Voicemeeter on the sim PC through its DLL.

What you get:

- ACP knobs and REC latches (or any readable ProSim dataref) drive strip/bus gains and mutes
  on the mixer PC, about 20 writes per second per parameter, latest value wins.
- A status card (Settings → Audio Control → Status) with the agent link, the Voicemeeter
  edition, and the strips/buses you chose with their label, gain and a Mute button.
- A `Mixer` dot in the footer while the feature is on, and a `Mixer: …` line in the
  diagnostics bundle's `versions.txt`.

Off by default. While off, no socket is opened and nothing is subscribed.

## Setup on the mixer PC (where Voicemeeter runs)

1. Install and run VoicemeeterBridge. Leave Voicemeeter running (any edition: Standard,
   Banana or Potato).
2. Tray menu → **Copy token**. This is the shared secret; the app needs it once.
3. Note the agent's port (default **5088**).
4. Allow inbound TCP on that port in Windows Firewall for the private network, or at least
   from the sim PC's address.
5. Quick check from the sim PC in a browser: `http://<mixer-pc>:5088/health` should answer
   `{"voicemeeter":{"connected":true},"clients":0}`. No token is needed for this page.

## Setup on the sim PC (ProsimCompanion)

1. Settings → **Audio Control → Remote Mixer**.
2. Enter the mixer PC's host name or IP, the port and the token. Click **Test connection**.
   The pill reports the agent's welcome and the Voicemeeter edition it sees, before you save.
3. Tick **Enable remote mixer** and save. The pill beside the Test button shows the live
   state of the saved connection; it reconnects by itself with a doubling delay
   (3 s → 30 s) when the mixer PC is off.
4. **Status Panel**: type the strip and bus numbers to show, as Voicemeeter numbers them
   (`1, 2, 3` for the first three strips; buses `1` = A1). They appear on the Status section.
5. **Mappings**: see below. **Add knob mapping** and **Add latch mapping** create rows with
   the right defaults; fill in the source and the parameter.

The token is DPAPI-protected in `config/settings.json` like the other keys and never appears
in the log or the wire trace (the hello frame is traced with the token redacted).

## Mapping format

`settings.json`, section `mixer`:

```json
"mixer": {
  "enabled": true,
  "host": "192.168.1.20",
  "port": 5088,
  "token": "dpapi:…",
  "reconnectDelayMs": 3000,
  "reconnectMaxDelayMs": 30000,
  "setTimeoutMs": 3000,
  "gainMinDb": -60,
  "gainMaxDb": 12,
  "panelStrips": [0, 1, 2],
  "panelBuses": [0],
  "mappings": [
    { "enabled": true, "acp": "captain", "channel": "vhf1", "stripIndex": 2, "isBus": false, "useLatch": true },
    { "enabled": true, "acp": "firstOfficer", "channel": "loudspeaker", "stripIndex": 0, "isBus": true, "useLatch": true }
  ]
}
```

| Field | Meaning |
|---|---|
| `acp` | `captain`, `firstOfficer` or `observer` — which audio panel the channel is read from. The knob is `system.analog.A_ASP{,2,3}_<CH>_VOLUME` (0–1024), the push-button `system.switches.S_ASP{,2,3}_<CH>_REC_LATCH` (1 = open), the same catalog refs the local backends use (`AcpDataRefCatalog`). |
| `channel` | `vhf1`, `vhf2`, `vhf3`, `hf1`, `hf2`, `intercom`, `cabin`, `pa`, or `loudspeaker` (captain / first officer only — the cockpit LOUD SPEAKER dial, no push-button). |
| `stripIndex` / `isBus` | The target as the Remote API counts: 0-based strip, or bus with `isBus: true` (bus 0 = A1). The page shows the mixer PC's own names. |
| `useLatch` | Drive the target's `Mute` from the REC push-button (latch 0 → Mute 1). Off: the mute is never written. The loudspeaker dial mutes fully down (bottom 1 % of travel) instead. |
| `gainMinDb` / `gainMaxDb` | Section-wide knob → `Gain` span, default −60 … +12 dB like the local VoiceMeeter backend (0 dB near 83 % of the knob). |
| `panelStrips` / `panelBuses` | 0-based indices shown on the Status card (the page offers them as tick boxes by name once connected). |

Parameters written: `Strip[n].Gain` / `Bus[n].Gain` and, with `useLatch`, `Strip[n].Mute` / `Bus[n].Mute`.

Channel names: after every `welcome` the client sends one `get` for `Strip[0..7].Label` and
`Bus[0..7].Label`; names the agent answers in `errors` do not exist in that edition and are
dropped, so the inventory matches the running Voicemeeter (Standard 3/2, Banana 5/5,
Potato 8/8). The settings page builds its strip/bus drop-downs and the status-card tick
boxes from it; **Reload channels** repeats the `get`. No agent change was needed for this.

Rules the mapping layer follows:

- Values are debounced: one write per parameter per 50 ms, carrying the latest value; a value
  equal to the last one sent is skipped.
- After a reconnect (agent restart, Voicemeeter restart, app start) every current output is
  sent again once, so the mixer catches up without a knob touch.
- A failed write is logged (`Mixer set Strip[2].Gain=-6 failed: unknown parameter`) and shown
  in the row's **Last** column; the next knob move retries it.
- No ACP power gate: the knob value goes out whether the audio panel is powered or not.

## Log lines to look for

```
Connected to the mixer agent at ws://192.168.1.20:5088/ws; sending hello
Mixer agent welcome: Voicemeeter connected (potato 3.1.1.2)
Mixer connection: Connected
Mixer mappings bound: 2
Mixer agent unavailable at 192.168.1.20:5088: … ; retrying quietly   (first failure only)
Mixer agent refused the session: unauthorized                       (wrong token)
```

Wire trace channel: `Mixer` (`logging.wireTrace`), every frame both ways, hello redacted.
