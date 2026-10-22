# Remote mixer: VoicemeeterBridge

ProsimCompanion can drive a Voicemeeter that runs on **another PC** — typically the one
running vPilot or your ATC client, or (since #178, 2026-10-22) one PC per pilot — through the
VoicemeeterBridge agent. The agent sits in that PC's tray and exposes Voicemeeter over a
small WebSocket protocol (`claude/mixer-protocol.md`, protocol v1). The agent lives in its
own repository, <https://github.com/psyraxaus/VoicemeeterBridge> (installer on its Releases
page; first published build 0.1.0, 2026-10-10). This is separate from the local VoiceMeeter
backend of the audio pillar, which drives the Voicemeeter on the sim PC through its DLL.

What you get:

- ACP knobs and REC latches drive strip/bus gains and mutes on the mixer PCs, about 20
  writes per second per parameter per agent, latest value wins.
- A status card (Settings → Audio Control → Status) with one group per agent: the agent
  link, the Voicemeeter edition, and the strips/buses you chose with their label, gain and
  a Mute button.
- One `Mixer` dot in the footer while the feature is on — an aggregate of every agent
  (green = all connected; amber with "1 of 2 agents connected" = one down).

Off by default. While off, no socket is opened and nothing is subscribed.

## Setup on each mixer PC (where a Voicemeeter runs)

1. Install and run VoicemeeterBridge. Leave Voicemeeter running (any edition: Standard,
   Banana or Potato).
2. Tray menu → **Copy token**. This is the shared secret; the app needs it once per agent.
3. Note the agent's port (default **5088**).
4. Allow inbound TCP on that port in Windows Firewall for the private network, or at least
   from the sim PC's address.
5. Quick check from the sim PC in a browser: `http://<mixer-pc>:5088/health` should answer
   `{"voicemeeter":{"connected":true},"clients":0}`. No token is needed for this page.

## Setup on the sim PC (ProsimCompanion)

1. Settings → **Audio Control → Remote Mixer**.
2. **Add agent**; enter a name (`PC1`), the mixer PC's host name or IP, the port and the
   token. Click **Test connection**. The pill reports the agent's welcome and the
   Voicemeeter edition it sees, before you save. Repeat per PC.
3. Tick **Enable remote mixer** and save. The pill beside each Test button shows the live
   state of that saved connection; each reconnects by itself with a doubling delay
   (3 s → 30 s) when its PC is off, independently of the others.
4. **Status Panel** (inside each agent block): tick the strips and buses to show for that
   PC (text boxes with Voicemeeter's numbers before the first connect, `1, 2, 3` for the
   first three strips; buses `1` = A1). They appear on the Status section under the agent.
5. **Mappings**: see below. **Add mapping** creates a row with the right defaults; pick the
   PC and the strip in the one **Strip / bus** list.

Every token is DPAPI-protected in `config/settings.json` like the other keys and never
appears in the log or the wire trace (the hello frame is traced with the token redacted).

## Mapping format

`settings.json`, section `mixer`:

```json
"mixer": {
  "enabled": true,
  "agents": [
    { "name": "PC1", "enabled": true, "host": "192.168.1.20", "port": 5088, "token": "dpapi:…", "panelStrips": [0, 5], "panelBuses": [] },
    { "name": "PC2", "enabled": true, "host": "192.168.1.21", "port": 5088, "token": "dpapi:…", "panelStrips": [0, 5], "panelBuses": [] }
  ],
  "reconnectDelayMs": 3000,
  "reconnectMaxDelayMs": 30000,
  "setTimeoutMs": 3000,
  "gainMinDb": -60,
  "gainMaxDb": 12,
  "mappings": [
    { "enabled": true, "acp": "captain",      "channel": "intercom", "agent": "PC1", "stripIndex": 5, "isBus": false, "useLatch": true },
    { "enabled": true, "acp": "captain",      "channel": "vhf1",     "agent": "PC1", "stripIndex": 0, "isBus": false, "useLatch": true },
    { "enabled": true, "acp": "firstOfficer", "channel": "intercom", "agent": "PC2", "stripIndex": 5, "isBus": false, "useLatch": true },
    { "enabled": true, "acp": "firstOfficer", "channel": "vhf1",     "agent": "PC2", "stripIndex": 0, "isBus": false, "useLatch": true }
  ]
}
```

| Field | Meaning |
|---|---|
| `agents[]` | One VoicemeeterBridge per entry (#178). `name` is the identity the mapping rows and the status card use (compared trimmed, case-insensitively; the page keeps names unique). `enabled: false` keeps the block without a socket. `panelStrips` / `panelBuses` are 0-based indices shown on the Status card (the page offers them as tick boxes by name once connected). |
| `host` / `port` / `token` / `panelStrips` / `panelBuses` **at section level** | **Legacy (pre-#178).** Still honoured while `agents` is empty: they run as one agent named `Mixer PC`. The Audio page lifts them into `agents[0]` on its first visit and clears them; a mapping row without an `agent` means "the first agent", so nothing else needs rewriting. The section-level `token` stays a registered secret (`mixer:token`) for one release. |
| `acp` | `captain`, `firstOfficer` or `observer` — which audio panel the channel is read from. The knob is `system.analog.A_ASP{,2,3}_<CH>_VOLUME` (0–1024), the push-button `system.switches.S_ASP{,2,3}_<CH>_REC_LATCH` (1 = open), the same catalog refs the local backends use (`AcpDataRefCatalog`). |
| `channel` | `vhf1`, `vhf2`, `vhf3`, `hf1`, `hf2`, `intercom`, `cabin`, `pa`, or `loudspeaker` (captain / first officer only — the cockpit LOUD SPEAKER dial, no push-button). |
| `agent` | The `name` of the agent whose strip the row drives. Empty = the first agent (every row written before #178). A name no block carries shows `no_agent` in the row's Live column and sends nothing; a block that is off shows `agent_off`. |
| `stripIndex` / `isBus` | The target as the Remote API counts: 0-based strip, or bus with `isBus: true` (bus 0 = A1). The page shows the mixer PC's own names, prefixed with the agent name when there is more than one agent. |
| `useLatch` | Drive the target's `Mute` from the REC push-button (latch 0 → Mute 1). Off: the mute is never written. The loudspeaker dial mutes fully down (bottom 1 % of travel) instead. |
| `gainMinDb` / `gainMaxDb` | Section-wide knob → `Gain` span for every agent, default −60 … +12 dB like the local VoiceMeeter backend (0 dB near 83 % of the knob). |

Secret paths: `mixer:agents:*:token` (one per list entry, like the notification targets) and
the legacy `mixer:token`.

Parameters written: `Strip[n].Gain` / `Bus[n].Gain` and, with `useLatch`, `Strip[n].Mute` / `Bus[n].Mute`.

Channel names: after every `welcome` each client sends one `get` for `Strip[0..7].Label` and
`Bus[0..7].Label`; names the agent answers in `errors` do not exist in that edition and are
dropped, so the inventory matches the running Voicemeeter (Standard 3/2, Banana 5/5,
Potato 8/8). The settings page builds its strip/bus drop-downs and the status-card tick
boxes from it per agent; **Reload channels** in a block repeats the `get` for that agent.
No agent change was needed for this.

Rules the mapping layer follows:

- Values are debounced per agent: one write per parameter per 50 ms, carrying the latest
  value; a value equal to the last one sent to that agent is skipped. `Strip[0].Gain` on
  PC1 and on PC2 are different parameters.
- After a reconnect of an agent (its restart, its Voicemeeter restart, app start) every
  current output bound to that agent is sent again once, so that mixer catches up without a
  knob touch. Other agents are untouched.
- A flush never waits on an agent that is down: its values stay pending for its next
  session; the other agents' writes go out on the same tick.
- A failed write is logged (`Mixer [PC1] set Strip[2].Gain=-6 failed: unknown parameter`)
  and shown in the row's **Live** column; the next knob move retries it.
- No ACP power gate: the knob value goes out whether the audio panel is powered or not.

## How the clients are run

`MixerClientHost` (Audio project, the app's `IMixerClients`) is the one hosted service: on
every options change it reconciles the running `MixerClient`s against `EffectiveAgents()`
(the list, or the legacy fields as one agent) — a block that vanished, was switched off or
changed host/port/token is cancelled and disposed, a new or changed one gets a fresh client.
Each `MixerClient` is a plain per-agent session object (connect → hello → welcome, receive
loop, watch replay, set/result correlation, backoff) and publishes its own entry into the
`MixerStatusStore` roster. `MixerMappingService` resolves each row's agent with
`MixerOptions.FindAgent` and keeps one `LatestValueCoalescer` per agent. The Test button
goes through the static `MixerProbe`, no session involved.

## Log lines to look for

```
Mixer agents running: 2 (PC1, PC2)
Mixer [PC1] connected to the mixer agent at ws://192.168.1.20:5088/ws; sending hello
Mixer [PC1] agent welcome: Voicemeeter connected (potato 3.1.1.2)
Mixer [PC1] connection: Connected
Mixer mappings bound: 4 over 2 agent(s)
Mixer [PC2] agent unavailable at 192.168.1.21:5088: … ; retrying quietly   (first failure only)
Mixer [PC2] agent refused the session: unauthorized                       (wrong token)
```

Wire trace channel: `Mixer/<agent name>` (`logging.wireTrace`), every frame both ways,
hello redacted.
