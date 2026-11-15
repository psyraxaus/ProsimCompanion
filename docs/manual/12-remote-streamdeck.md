# 12. A Stream Deck on another PC: cockpit switches and camera views

Many cockpits keep the Stream Deck on a second PC. The ProsimCompanion plugin runs there just as
well, and two key types let it do things you have no hardware for:

- **Cockpit switch** keys press ProSim switches — Korry push buttons, toggles, rotary knobs,
  guarded switches — and show their state the way the real overhead does (lights out until the
  aircraft lights a lamp).
- **Camera view** keys change ChasePlane views through a vJoy button on the sim PC.

## 12.1 The plugin on another PC

On the **sim PC**:

1. Settings → Setup → **Web Interface**: turn on **Allow other devices (LAN)**. Restart ProsimCompanion.
2. Allow inbound TCP **5320** in Windows Firewall (private network). Windows may ask on the
   first LAN start; if it did not, add the rule yourself.
3. Settings → Setup → **Stream Deck & External Tools**: turn on **Command API**.
4. Copy `%LOCALAPPDATA%\Programs\ProsimCompanion\streamdeck\com.prosimcompanion.streamdeck.streamDeckPlugin`
   to the other PC (the app installs per user, not under Program Files).

On the **Stream Deck PC**:

5. Double-click the copied file. The Stream Deck app installs the plugin.
6. Drag any ProsimCompanion key onto a page, open its settings and paste the **onboarding
   link** from the ProsimCompanion desktop window on the sim PC (`http://<sim-pc>:5320/?token=…`).
   All ProsimCompanion keys share that one connection.

Keys showing *connecting* or *offline* mean the sim PC does not answer: check the address, the
port, the firewall and that LAN access is on. *re-pair* means the token was changed — paste a
fresh link. *API off* means **Command API** is switched off on the sim PC (step 3); the keys
recover by themselves once it is on. *set up* on every key means the plugin has no connection
yet (step 6).

## 12.2 Cockpit switches: set up the rows

A key can press only a switch that is a saved line on Settings → **Cockpit Switches** in the
sim PC's web UI. Each line is one ProSim switch: press **Add switch**, pick it in **ProSim
switch**, set **Key title**, **Look** and **Press does**, then **Save**. **Add the three leak
measurement valves** adds those three lines in one click. If you want their lamp to follow the
real overhead light, type `system.indicators.I_OH_HYD_LMV_BLUE_L` (and the green and yellow
ones) into **Lower lamp from**. Every field, the lamps and colours, the test **Press** and what
to do when a line does not work: see [chapter 24](24-cockpit-switches.md).

## 12.3 Cockpit switches: the keys

In the Stream Deck app, drag a **Cockpit Switch** key onto a page and pick the switch in its
settings. The list is the rows you saved. The key:

- draws the switch in the look you chose, with the row's title on top;
- lights a lamp as an outlined box in its colour with the legend inside, the real-world way;
- shows a short blue ring when a press went through and an amber ring with the reason when
  ProsimCompanion refused (switch off, ProSim away, a row no longer saved);
- dims with a note (*switches off*, *prosim away*, *no value*) while presses are switched off,
  ProSim is away, or ProSim has not sent a value yet;
- shows a plain plate with a dash and the connection word while the plugin has no connection.

On a knob key, hold the key to step backwards.

## 12.4 Camera views: ChasePlane from the other PC

A vJoy button pressed on the Stream Deck PC exists only there. The relay fixes that: the key
asks ProsimCompanion, and ProsimCompanion presses a vJoy button on the sim PC.

On the **sim PC**:

1. Install the vJoy driver. In **vJoyConf** create one device, for example device 1 with 32
   buttons. Restart ProsimCompanion.
2. Settings → Setup → **vJoy relay**: switch it on. Leave **vJoyInterface.dll** empty unless
   vJoy is not in `C:\Program Files\vJoy\x64`. **vJoy device**: the number from vJoyConf. Save.
3. Press **Check**. The pill goes READY and shows the button count. *NO DRIVER* means the DLL
   was not found or vJoy is not running; *NO DEVICE* means the number does not exist in
   vJoyConf; *DEVICE BUSY* means another program owns it — pick another device number.
   *OFF* means the relay is switched off; *FAILED* shows the reason beside it.
4. **Add view** once per ChasePlane view: a name (`wing`, `cockpit`, `tail`) and a vJoy button
   number. Save.
5. In ChasePlane, bind each view to that vJoy button: open the view's controls, press the
   **Press** button on the ProsimCompanion page while ChasePlane listens for a binding, and it
   records "vJoy Device button N".

On the **Stream Deck PC**, drag a **Camera View** key onto a page and pick the view.

## 12.5 Troubleshooting

| Symptom | Meaning / fix |
|---|---|
| Key says **set up** | On one key: no switch or view picked yet — open the key's settings. On every key: the plugin is not paired yet — paste the onboarding link (12.1 step 6). |
| Key says **connecting** or **offline** | The sim PC does not answer. Check the address, the port, the firewall and that LAN access is on. |
| Key says **re-pair** | The token was changed. Paste a fresh onboarding link into any key's settings. |
| Key says **API off** | **Command API** is off on the sim PC: Settings → Setup → **Stream Deck & External Tools**. The keys recover by themselves once it is on. |
| Key says **not found** | The switch line or the view was renamed or removed in ProsimCompanion. Pick again. |
| Key says **switches off** | Settings → Cockpit Switches: the tick box **Cockpit switches** is off. The key still shows the state; every press is refused. |
| Key says **relay off** | Settings → Setup → **vJoy relay** is switched off. Switch it on and save. |
| Key says **prosim away** / **no value** | ProSim is not connected, or it has not sent this switch's value yet. |
| Amber ring + a reason | ProsimCompanion refused the press; the reason is the app's own words. |
| Lamp never lights, or a leak valve shows **ON** while pushed | See "If it does not work" in [chapter 24](24-cockpit-switches.md). |
| vJoy relay pill **NO DRIVER** | vJoy is not installed on the sim PC, is not running, or the DLL path points at the 32-bit DLL — use the `x64` folder. |
| **Press** says READY but ChasePlane does nothing | The view is not bound to that vJoy button in ChasePlane. Bind it while pressing **Press** (12.4 step 5). |

---

**Next:** [Cockpit Switches](24-cockpit-switches.md) · **See also:** [Diagnostics](13-diagnostics.md), [Troubleshooting](05-troubleshooting.md) · [Manual contents](README.md)
