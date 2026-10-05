# 8. The loudspeaker dial: headset sound on the cockpit speakers

The cockpit **LOUD SPEAKER** dial can set how loud the radio sound is on your cockpit
speakers, while the headset keeps its own sound — as in the aircraft. The app reads the dial
from ProSim and sets the level of one VoiceMeeter strip or bus. The sound routing itself is
done once in VoiceMeeter; the app does not create it.

You need **VoiceMeeter** (this guide uses Potato; Banana works the same way with fewer
strips and buses) and the app's audio backend set to VoiceMeeter (Settings → Audio Control →
Volume Control).

## 8.1 What the wiring does

VoiceMeeter has no volume control for one route of a strip: a strip that goes to the headset
and to the speakers has one fader for both. So the radio sound is sent a second time over a
spare bus, and that copy goes to the speakers:

```
ATC strip ──► A2 (headset)                      always, as before
          └─► B3 (spare bus) ─ VBAN ─► "Loudspeaker" strip ──► speaker bus
                 ▲
                 └── the LOUD SPEAKER dial sets this level
```

VoiceMeeter does not offer its own B outputs as a strip input, so the copy comes back in
over **VBAN** (VoiceMeeter's built-in network audio) on the same PC. Nothing is installed.
On the test rig the delay on the speakers was not noticeable.

## 8.2 Set up VoiceMeeter (once)

The names below are examples: `ATC` is the strip your radio sound arrives on, `A2` your
headset, `B3` a bus nothing else uses (no strip routed to it, no program using
*Voicemeeter Out B3* as its microphone).

1. On the `ATC` strip, switch **B3** on. Leave **A2** on.
2. Pick a spare hardware input strip (one of the first five). Right-click its name and type
   `Loudspeaker`. Leave its input device **unselected**.
3. On the `Loudspeaker` strip, switch on **only** the bus that feeds your cockpit speakers.
4. Click **VBAN** (top right) and switch VBAN **on**.
5. **Outgoing Streams**, first row: on · Source `BUS B3` · Stream Name `Loudspeaker` ·
   IP Address To `127.0.0.1` · port `6980` · Net Quality `Optimal`.
6. **Incoming Streams**, first row: on · Stream Name `Loudspeaker` (the same name) ·
   IP Address From `127.0.0.1` · port `6980` · destination: your `Loudspeaker` strip.

Check it by hand: play radio sound and move the `B3` bus fader. The speakers follow; the
headset does not change. Repeat step 1 for every other strip you want on the speakers (the
First Officer's voice, for example).

> **Never** switch `B3` on for the `Loudspeaker` strip — the sound would loop and howl. And
> keep the headset bus off on that strip, or the headset gets the sound twice.

## 8.3 Map the dial in the app

Settings → Audio Control → **VoiceMeeter** → **CPT Mappings** → *Add mapping*:

- **Channel**: `LOUDSPEAKER`
- **Target**: `Bus 8` (that is B3 on a Potato — the list shows your own labels)

Save. Use the F/O list for the first officer's dial. The observer has no loudspeaker dial,
so the OBS list does not offer it.

How the dial behaves:

- Full travel is −60 dB … +12 dB like the ACP knobs; 0 dB sits near 83 %.
- **Fully down mutes the target.** The dial has no REC latch, so the Latch tick is shown
  ticked and greyed for this row.
- It follows the essential buses: on a dark aircraft the level holds, and it catches up when
  power returns. The AUDIO SWITCHING selector does not affect it.
- A strip or bus can belong to one mapping only — do not also map an ACP knob to the same
  target.

On the Windows-volumes backend (CoreAudio) `LOUDSPEAKER` is in the channel list too: it
then sets one program's volume like any other knob. Windows cannot send one program to two
devices at different levels, so the speakers-and-headset wiring above needs VoiceMeeter.

## 8.4 If it does not work

| What you see | Look at |
|---|---|
| Speakers silent at every dial position | VBAN on? Both stream names the same? `Loudspeaker` strip routed to the speaker bus? `B3` not muted? |
| Dial moves, level does not | Settings → Audio Control → Status: the `LOUDSPEAKER` row shows the gain the app last wrote. A dash means the dial was never read — check the dial in ProSim (`system.analog.A_MIP_LOUDSPEAKER_CAPT`) and that the aircraft has electrical power. |
| Echo in the headset | The `Loudspeaker` strip is routed to the headset bus too. |
| Howl | `B3` is on for the `Loudspeaker` strip. Switch it off. |
| The voice First Officer hears ATC | The cockpit speakers reach your microphone. Lower the dial, or use push-to-talk. |
