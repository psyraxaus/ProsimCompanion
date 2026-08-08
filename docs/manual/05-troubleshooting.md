# Troubleshooting

## Where to look, in order

1. **Flight Status page** — which connection dot is red? Each pillar degrades independently;
   a red ProSim dot never stops GSX, and vice versa.
2. **GSX status page → decision log** — every automation action and every deliberate
   non-action with its reason ("held — waiting for the previous service", "left for the
   user", "skipped — tankering", …). If GSX "didn't do something", the reason is here.
3. **Logs page** — live tail with per-subsystem levels (raise a subsystem to Debug in App
   Settings; applies without restart).
4. **Log files** — `%LOCALAPPDATA%\ProsimCompanion\logs\`, CMTrace format (open with
   CMTrace/OneTrace for live coloring). `wireTrace` (App Settings) adds a separate raw
   protocol-frame log. Session event records (JSONL) live in `...\sessions\`.

## Common issues

| Symptom | Check |
|---|---|
| ProSim dot stays red | `prosim.sdkPath` set and pointing at the real `ProSimSDK.dll`? ProSim System running? Hostname right for a networked ProSim? |
| GSX dot red / no services | GSX Pro running with the Couatl Remote API enabled? The port is read live from `%APPDATA%\Virtuali\CouatlAddons.ini` — non-default ports are picked up automatically. |
| GSX doors/services act oddly at the gate | Are the ProSim A322 GSX profiles installed (`%APPDATA%\Virtuali\Airplanes\prosim-a322-*`)? The installer offers them; without them GSX guesses the aircraft geometry. |
| No services before pushback | The OFP gate: with *wait for OFP* enabled nothing is called until the SimBrief plan is imported or an MCDU plan exists. The status board shows the hold reason. |
| Fuel won't load | Refuel runs only while GSX's hose is connected; the decision log shows target latching and any defuel guard hold. |
| Audio knobs do nothing | Audio page: is the mapped app running (elevated apps can't be controlled unless ProsimCompanion is elevated too)? Use **Write Debug Info** on the Audio settings page — it dumps devices/sessions to `logs\AudioDebug.txt`. Device-filter selects are troubleshooting-only. |
| FO doesn't hear me | Input device on the First Officer page; PTT bound and held; LAN ASR endpoint reachable (falls back to offline recognition automatically). |
| FO voice robotic / silent | Kokoro endpoint reachable? Local-only mode blocks Google fallback by design. Provider failures cool down 60 s then retry. |
| No update banner | The check needs internet + a published GitHub release; it fails silently offline (by design, `updateCheck` section). |
| Web UI unreachable from phone | LAN binding enabled + restart? Token in the URL (scan the QR again after regenerating)? Windows Firewall may prompt on first LAN bind. |
| Two instances / port conflict | Starting the app twice just activates the running window — if a stale process is stuck, end it in Task Manager. |

## Resetting things

- **Settings**: stop the app and delete keys/sections from `config\settings.json` — missing
  keys are rewritten with defaults on start. Deleting the `predecessorImport` section re-runs
  the Prosim2GSX/Prosim2FO config import.
- **Flight state**: INIT page → RESET FLIGHT (two-click confirm) clears the plan, loadsheets
  and overrides for a fresh turnaround.
- **Web lockout**: the desktop window always keeps the web-server card (port, LAN, token) —
  fix it there, restart.

## Reporting a problem

Issues: <https://github.com/psyraxaus/ProsimCompanion/issues>. Attach the day's log from
`%LOCALAPPDATA%\ProsimCompanion\logs\` and, for GSX behaviour, note the decision-log lines
around the event — they usually contain the answer.
