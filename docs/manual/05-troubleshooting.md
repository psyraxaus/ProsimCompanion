# Troubleshooting

## Where to look, in order

1. **The footer of any page** — which connection dot is red (**ProSim**, **SimConnect**,
   **GSX**, **ASR**, **TTS**)? Hover a dot for the reason. The footer is hidden on phones.
   Each pillar degrades independently; a red ProSim dot never stops GSX, and vice versa.
2. **The decision log** (the card **Automation Decisions** on Settings → Ground Services →
   Status) — every automation action and every deliberate
   non-action with its reason ("waiting for the previous service to complete", "left for the
   user", "skipped — tankering", …). If GSX "didn't do something", the reason is here.
3. **Logs page** (Settings → Advanced → Logs) — live tail. **Capture level** sets one level
   for the whole app (raise it to Debug for a hunt; applies without restart).
4. **Log files** — `%LOCALAPPDATA%\ProsimCompanion\logs\`, CMTrace format (open with
   CMTrace/OneTrace for live coloring). **Wire trace** on the Logs page adds a separate raw
   protocol-frame log. Session event records (JSONL) live in `...\sessions\`.
   Debug and the wire trace are for a hunt, not for daily flying: the files grow fast.
   After 24 hours the app switches both
   back to Information by itself and says so in the log; the Logs page shows the clock, and
   **Keep verbose logging** stops it. The files are capped too (20 MB per main log file,
   50 MB per wire file, three wire files kept), and identical repeated frames are folded
   into one "repeated N more times" line.

## Common issues

| Symptom | Check |
|---|---|
| ProSim dot stays red | Settings → Setup, card **ProSim Connection**: is **ProSim SDK folder** the folder that really contains `ProSimSDK.dll`? ProSim System running? **ProSim host** right for a networked ProSim? |
| ProSim dot is not green and ProSim is running | Hover the dot, or read the **Getting Started** card on Flight Status: when the app cannot use the SDK in the configured folder it says why. The log line `ProSim SDK … loaded from …` names the SDK build the app found. |
| GSX dot red / no services | GSX Pro running with the Couatl Remote API enabled? The port is read live from `%APPDATA%\Virtuali\CouatlAddons.ini` — non-default ports are picked up automatically. |
| GSX doors/services act oddly at the gate | Are the ProSim A322 GSX profiles installed (`%APPDATA%\Virtuali\Airplanes\prosim-a322-*`)? The installer offers them; without them GSX guesses the aircraft geometry. |
| Doors reopen, GPU connects by itself, fuel or passengers load twice | ProSim's own options are doing the same job. Settings → Setup → **ProSim Setup Check**: amber rows show which (Door logic, Automatic ground power, Datalink loading, Refuelling rate). Click **Apply recommended**, or follow **Show me how** in ProSim System. |
| No services before pushback | The OFP gate: with **Wait for OFP before departure services** on (Settings → Ground Services → Departure Services, card **Automation**) nothing is called until the SimBrief plan is imported or an MCDU plan exists. The **Departure Status Board** (Settings → Ground Services → Status) shows the hold reason, "waiting for a flight plan (SimBrief OFP import or MCDU FMS plan)". |
| Fuel won't load | Refuel runs only while GSX's hose is connected; the decision log shows target latching and any defuel guard hold. |
| "Confirm fuel" brings no truck | The fuel on board already meets the plan (tankering): the decision log shows "skip Refueling — FOB … already meets planned … (tankering) — GSX refuel not called". This is intended; the preliminary loadsheet is still made. To force the truck, switch off **Skip refuel when FOB exceeds plan** (Settings → Ground Services → Refuel & Boarding, card **Refuel**) or raise FUEL RAMP on the INIT page. |
| Audio knobs do nothing | Settings → Audio Control → CoreAudio: is the mapped app running (elevated apps can't be controlled unless ProsimCompanion is elevated too)? Use **Write Debug Info** in that card's troubleshooting fold — it dumps devices/sessions to `%LOCALAPPDATA%\ProsimCompanion\logs\AudioDebug.txt`. The **Device-filter State** select beside it is troubleshooting-only. |
| Remote mixer: Test connection fails | *No answer from …* or *Could not connect: …*: address, port, firewall on the mixer PC, agent running (tray icon) — try `http://<mixer-pc>:5088/health` in a browser. *Agent refused the hello: …* with *unauthorized*: copy the token again from the agent's tray menu. Full walk-through in [chapter 9](09-remote-mixer.md). |
| Remote mixer: knob moves, mixer does nothing | Settings → Audio Control → Remote Mixer, card **Mappings**: is the row **On**, and what does the **Live** column say? `not_connected` = link down; `unknown parameter` = that strip or bus does not exist in this Voicemeeter edition; a dash = no knob value from ProSim yet. REC button does nothing = **Latch** not ticked. |
| FO talks to itself (answers its own lines) | Its voice reaches your microphone — speakers, or a Voicemeeter route that loops the FO strip back in. Fix the routing. The app also ignores the mic while the FO speaks and drops a transcript that repeats the FO's words (Settings → Voice First Officer → Listening & PTT, **Ignore the microphone while the FO speaks**, on); the session log shows `asr.self-echo` for each dropped one. |
| FO doesn't hear me | **Input device** on Settings → Voice First Officer → Listening & PTT; PTT bound and held; the speech recognition server reachable (the app falls back to offline recognition automatically) — see [chapter 16](16-speech-recognition-server.md). |
| FO voice robotic / silent / wrong | The provider the voice names is down, not configured or shut out by Local only, so the chain covered with a fallback voice (Status page pills say which). Provider failures cool down 60 s then retry. Walk-through: [chapter 10](10-voices.md). |
| No update banner | The check needs internet + a published GitHub release; it fails silently offline (by design). Is **Check for updates** on (Settings → Setup, card **Updates**)? |
| Web UI unreachable from phone | LAN binding enabled + restart? Token in the URL (scan the QR again after regenerating)? Windows Firewall may prompt on first LAN bind. |
| Remote page (iPad) stops updating | Mobile Safari drops the live connection on screen lock/app switch. The page recovers by itself: it reconnects to the session (kept 15 min) or reloads automatically — "Session expired — reloading…" flashing briefly is normal. If it ever sits stale, pull-to-refresh and report it. |
| The app vanishes (hard crash, no error in the log) | Windows keeps a dump in `%LOCALAPPDATA%\CrashDumps`. Send it with the diagnostics bundle. |
| Two instances / port conflict | Starting the app twice just activates the running window — if a stale process is stuck, end it in Task Manager. |

## Resetting things

- **Settings**: stop the app and delete keys/sections from `config\settings.json` — missing
  keys are rewritten with defaults on start. To run the import of the earlier apps'
  settings again, see [Installation](01-installation.md).
- **Flight state**: INIT page → **RESET FLIGHT (SOFT)**, then **CONFIRM RESET**, restarts the
  leg on the same OFP: it clears the INIT overrides and the loadsheet cycle and keeps the
  plan. **UNLOAD OFP (FULL)**, then **CONFIRM UNLOAD**, also unloads the plan from the ProSim
  EFB; no GSX service is called until you fetch a new OFP.
- **Web lockout**: the desktop window always keeps the **Web server** card (port, LAN, token) —
  fix it there, restart.

## Reporting a problem

Issues: <https://github.com/psyraxaus/ProsimCompanion/issues>. The quickest way is the
**Export diagnostics** button on the Logs page: one zip with the recent logs, session
records and your settings (keys and tokens removed). Attach that zip. Without it, attach the
day's log from `%LOCALAPPDATA%\ProsimCompanion\logs\` and, for GSX behaviour, note the
decision-log lines around the event — they usually contain the answer. For a GSX door,
menu or service problem, switch on **Wire trace** (Settings → Advanced → Logs) before you repeat
it, tick **Include wire trace** on the same page's **Support** card (off by default), then
export.

---

**Next:** [Settings reference](04-settings-reference.md) · **See also:** [Diagnostics](13-diagnostics.md), [Installation](01-installation.md) · [Manual contents](README.md)
