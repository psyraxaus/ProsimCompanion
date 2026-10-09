# 9. The remote mixer: a Voicemeeter on another PC

Many cockpits run vPilot (or another ATC client) on a second PC with its own Voicemeeter.
The app can drive **that** Voicemeeter from the cockpit: an audio panel knob sets a strip
or bus gain, a REC push-button mutes it, and a status card shows what the mixer is doing
with a Mute button per channel. This works through **VoicemeeterBridge**, a small agent
that runs in the tray of the mixer PC and talks to its Voicemeeter for us.

This is separate from the VoiceMeeter backend of Audio Control (Settings → Audio Control →
Volume Control), which drives the Voicemeeter on the **sim PC** through its DLL. You can use
both at the same time.

![Remote Mixer settings](../img/remote-mixer.png)

## 9.1 What you need

- Voicemeeter (Standard, Banana or Potato) running on the mixer PC.
- **VoicemeeterBridge** installed and running on that same PC (follow its own install
  guide). It shows a tray icon.
- Both PCs on the same network. The sim PC must reach the mixer PC on the agent's port
  (**5088** unless you changed it in the agent).

## 9.2 Set up the mixer PC (once)

1. Start Voicemeeter, then VoicemeeterBridge.
2. Right-click the tray icon → **Copy token**. Paste it somewhere safe for the next step.
   It is the shared secret; without it the app cannot talk to the agent.
3. Allow the agent's port through Windows Firewall on the mixer PC (inbound TCP 5088, private
   network, or only from the sim PC's address).
4. Check from the sim PC: open `http://<mixer-pc>:5088/health` in a browser. You should see
   `{"voicemeeter":{"connected":true},"clients":0}`. If the browser cannot connect, fix the
   firewall or the address before you go on. If it says `"connected":false`, Voicemeeter is
   not running on the mixer PC.

## 9.3 Connect the app

Settings → **Audio Control → Remote Mixer**:

1. **Host**: the mixer PC's name or IP address. **Port**: 5088. **Token**: paste it.
2. Click **Test connection**. A green pill reads *Connected. Voicemeeter potato 3.1.1.2 is
   running.* (your edition and version). A red pill names the problem: *unauthorized* is a
   wrong token, *No answer* is the address, port or firewall.
3. Tick **Enable remote mixer** and **Save**.

The pill beside the Test button then shows the live state of the saved link. A `Mixer` dot
joins the footer while the feature is on. The link reconnects by itself when the mixer PC
is off or restarts; the first failure is logged, later retries are quiet.

The token is stored encrypted in `settings.json` and never written to the log.

## 9.4 Pick the strips and buses for the status card

Still on the Remote Mixer section, **Status Panel**. Once the agent is connected the app
has read the strip and bus names from the mixer PC, and they appear as tick boxes:
*Strip 3 — vPilot*, *Bus A1 — Headset*, and so on. Tick the ones you want on the card.
**Reload channels** reads the names again after you rename a strip in Voicemeeter.

Before the first connect the section shows two text boxes instead: strip numbers as
Voicemeeter counts them from the left (`1, 2, 3`), and bus numbers (`1` is A1).

Save. Settings → Audio Control → **Status** now has a **Remote Mixer** card with each
channel's Voicemeeter label, gain and mute state, live, and a **Mute** / **Unmute** button.
The card follows changes made anywhere: a knob, the Voicemeeter window on the mixer PC, a
macro button.

![Remote Mixer status card](../img/remote-mixer-status.png)

(The picture was taken with the agent unreachable — *Connecting…* and dashes. With the link
up the pill is green and the rows show the real labels, gains and mute states.)

## 9.5 Map a knob and its push-button

Settings → Audio Control → Remote Mixer → **Mappings**. Each row sends one ProSim value to
one Voicemeeter parameter.

Each mapping takes two lines: the first says *what* (source, parameter, type, invert), the
second holds the numbers.

**A volume knob → a strip gain**

1. Click **Add knob mapping**. The row comes filled in: source *Captain VHF1*
   (`system.analog.A_ASP_VHF_1_VOLUME`), parameter `Strip[0].Gain`, type *Level*, input
   0 … 1024 (the knob's travel), output −60 … +12 dB.
2. **ProSim source**: the drop-down lists every knob, REC push-button and loudspeaker dial
   by panel — *Captain*, *First Officer*, *Observer*. Pick another one if VHF1 is not the
   channel you want. The last entry, *Other dataref…*, opens a text box for a name that is
   not in the list.
3. **Strip / bus**: pick the channel by its Voicemeeter name — *Strip 3 — vPilot*,
   *Bus A1 — Headset*. The names come from the mixer PC; before the first connect the
   list shows plain numbers. **Property**: *Gain* for a knob.
   *Other parameter…* opens a text box for any other Remote API name
   (`Strip[2].Comp`, say) — you will rarely need it.

**The REC push-button → the strip mute**

1. Click **Add latch mapping**. The row comes filled in too: *Captain VHF1 REC*
   (`system.switches.S_ASP_VHF_1_REC_LATCH`) onto the first strip's *Mute*, type *Toggle*,
   **Invert** already ticked.
2. Pick the push-button that matches the knob above, and the same strip.

Why Invert: ProSim reports the latch as 1 when the channel is **open** (button out), and
Voicemeeter's Mute wants 1 when **muted**. Invert swaps them.

Save. Turn the knob: the **Last** column shows the value in and the value out (for example
`612 → -17`) and, if the agent refused the write, why (`unknown parameter` means the
strip or bus does not exist in that Voicemeeter edition — check the number).

### Fine-tuning

- **Out max dB**: +12 dB means the knob fully up is louder than unity, like the local
  backend; 0 dB sits at about 83 % of the knob. Set it to 0 for a strip that must never go
  past unity.
- **Out min dB**: −60 dB is as good as silent. Use the latch mapping for a true mute.
- **In min / In max**: for a source that is not a 0 … 1024 knob, enter its real range.
- **Threshold** (toggle only): the input value at or above which the toggle sends 1.
  0.5 fits a 0/1 switch.
- **On**: untick a row to keep it without using it.
- The knob value goes out whether the audio panel is powered or not.
- Writes are limited to 20 per second per parameter; a knob at rest sends nothing. After a
  reconnect every current value is sent once, so the mixer catches up on its own.

## 9.6 If it does not work

| Symptom | Check |
|---|---|
| Test says *No answer* | Address and port; firewall on the mixer PC; the agent is running (tray icon). Try the `/health` page from 9.2. |
| Test says *unauthorized* | Copy the token again from the agent's tray menu and paste it fresh. |
| Pill says *Connected — Voicemeeter not running on the mixer PC* | Start Voicemeeter there. The agent reconnects to it by itself and the pill turns green. |
| Status card shows `—` for a channel | That strip or bus number does not exist in this Voicemeeter edition (Standard has 3 strips, Banana 5, Potato 8). The log says `parameter Strip[7].Gain cannot be read`. |
| Knob moves, nothing happens | Is the row **On**? Does the **Last** column show a value? `not_connected` means the link is down; `unknown parameter` means the parameter name; no value at all means the source name is wrong (pick it from the list). |
| The REC button mutes the wrong way | Tick or untick **Invert** on that row. |
| The level jumps between two values | Two rows send to the same parameter. Keep one. |

The log (Settings → Logs) prefixes every line with `Mixer`. The wire trace channel is
`Mixer` if support asks for frames.
