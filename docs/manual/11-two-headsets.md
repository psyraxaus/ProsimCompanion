# 11. Two headsets with VoiceMeeter and online ATC: a reference layout

Two pilots, two headsets, one online-ATC client, ProSim's own intercom and callouts — and
one VoiceMeeter. This chapter shows the layout that works, what each audio-panel knob can
and cannot do in it, and the two questions that come up every time: *"when will the INT
knob work?"* and *"can VHF2 go on its own bus?"*. The short answers: the INT knob works
today, but only once INT has a bus of its own; VHF2 cannot have its own bus because the
online client sends COM1 and COM2 as one stream.

The layout was worked out with a two-pilot cockpit (support thread, October 2026): a main
PC running ProSim and ProsimCompanion with VoiceMeeter Banana, and one PC per pilot with
ProSim Audio 2 and VoiceMeeter Potato. A single-PC cockpit uses the same ideas with fewer
boxes.

## 11.1 The one rule: one fader per strip or bus

VoiceMeeter has one volume control per strip and one per bus. A knob on the audio panel
sets one of those faders. So:

- Two headsets on one bus have **one** volume. The captain's knob and the first officer's
  knob would fight over the same fader; the last one to move wins.
- INT and VHF arriving on the same headset bus share **one** volume. An INT knob mapped to
  that bus turns the ATC audio down with it.
- A strip that feeds both headsets cannot be loud for one and quiet for the other.

Per-headset volume therefore needs **a bus per headset**, and a separate INT level needs
**a separate path for the intercom audio** on top of that. Both editors in the app warn you
when two rows share a strip or bus (Settings → Audio Control → VoiceMeeter, and → Remote
Mixer → Mappings): *"Bus A2 is driven by Captain VHF1, First Officer VHF1. One target has
one volume"*. The warning does not block saving; it tells you which fader two knobs are
pulling on.

## 11.2 The main PC (VoiceMeeter Banana)

What arrives, and where it goes:

```
ProSim Audio 2, CPT group  ──► Virtual input (VAIO)  ──► A2 (captain headset)
ProSim Audio 2, FO group   ──► Virtual input (AUX)   ──► A3 (first officer headset)
Online ATC client (IVAO/vPilot) ──► a virtual cable ──► hardware strip 1 ──► A2 + A3
```

1. In ProSim Audio 2 put the captain's sounds (intercom, callouts, the voice First Officer
   if you use it) in one group and the first officer's in another. Send the captain group
   to *Voicemeeter Input* (VAIO) and the first officer group to *Voicemeeter AUX Input*.
2. Set the online client's output device to a virtual cable (VB-Cable or a second
   VoiceMeeter output) and bring that cable in on a **hardware strip** (one of the first
   three on Banana). Route that strip to both headset buses.
3. Set **A2** to the captain's headset and **A3** to the first officer's. Route VAIO → A2
   only, AUX → A3 only.
4. Microphones come in on hardware strips and go to the online client the way you already
   do it (B1 as the client's microphone device, for example). Nothing here changes.

Map the knobs (Settings → Audio Control → VoiceMeeter):

| Panel | Channel | Target | What it does |
|---|---|---|---|
| Captain | VHF1 | Bus A2 | The captain's headset volume. ATC *and* intercom together — one bus, one fader (11.1). |
| First Officer | VHF1 | Bus A3 | The same for the first officer. |

This is the layout most two-headset cockpits end up with. It gives each pilot a headset
volume. It does **not** give a separate INT level: INT arrives on the same bus as the ATC
audio. The next two sections are the two ways to get one.

## 11.3 INT on its own: a spare bus per pilot

On the main PC a separate intercom level needs a bus per pilot that carries only the
intercom. Banana has three hardware buses (A1–A3) and two virtual ones (B1, B2); the
headsets take two of the hardware buses, the microphone path takes a virtual one, and there
is nothing left. **Potato** (five hardware buses A1–A5, three virtual) has the room:

```
VAIO (captain intercom/callouts) ──► A4 ──┐
                                           ├──► captain headset (two outputs, one device)
cable (ATC)                      ──► A2 ──┘
```

Most headset devices accept only one VoiceMeeter bus, so "two buses into one headset"
means a second physical or virtual output into the same device, or the loop trick from
[chapter 8](08-loudspeaker-dial.md) (a bus sent back in over VBAN onto a strip that feeds
the headset bus, with the knob on the looped bus). It works; it costs a bus, a strip and a
little latency per pilot. If you have Potato on the main PC and spare buses, this is the
cheapest route. Map **Captain INT → Bus A4** and **Captain VHF1 → Bus A2** (and the first
officer's pair likewise) and the two knobs drive two faders.

## 11.4 INT on its own: drive each pilot's PC (Remote Mixer)

When each pilot has a PC of their own with ProSim Audio 2 and VoiceMeeter Potato, the
separation already exists there: the online client's audio arrives from the main PC over
VBAN on one strip, and ProSim Audio 2 on that PC (intercom, callouts) sits on the VAIO
strip. Two strips, two faders — the app only needs to reach that VoiceMeeter. That is what
the **Remote Mixer** does with one VoicemeeterBridge agent per pilot PC
([chapter 9](09-remote-mixer.md), section 9.7 for this exact case):

| Panel | Channel | Agent · target |
|---|---|---|
| Captain | INT | PC1 · Strip 6 (VAIO, ProSim Audio 2) |
| Captain | VHF1 | PC1 · Strip 1 (the VBAN strip carrying ATC) |
| First Officer | INT | PC2 · Strip 6 |
| First Officer | VHF1 | PC2 · Strip 1 |

The main PC's VoiceMeeter keeps doing the routing; no spare buses are needed there. One
pilot PC being off does not affect the other — each agent reconnects on its own.

## 11.5 Why VHF2 cannot have its own bus

IVAO Altitude, vPilot and most online clients play COM1 and COM2 as **one** audio stream
out of one output device. There is nothing to put on a second bus: whatever you route, both
radios arrive together. The VHF2 knob can still be mapped to any strip or bus — the app
does not stop you — but it will set the volume of the whole ATC stream, exactly like VHF1
on the same target. The app will not try to split a client's audio; that would need the
client to offer two outputs, which these clients do not.

What you *can* do with VHF2: map it to the strip of a second audio source that really is
separate (a second client, a radio-chatter program, an ATIS stream). Then the knob is
meaningful.

## 11.6 The pilot PCs (VoiceMeeter Potato)

For a rig as in 11.4, on each pilot PC:

1. Potato with the headset on **A1**. Microphone on a hardware strip, sent to the main PC
   over VBAN (outgoing stream, main PC's address).
2. **Incoming VBAN** stream from the main PC (the ATC audio for this pilot) on a strip
   with **no input device selected** — the device-less strip is the VBAN landing spot.
   Route it to A1.
3. ProSim Audio 2 on this PC: its output device *Voicemeeter Input* (VAIO strip). Route
   VAIO to A1.
4. VoicemeeterBridge running in the tray, token copied into the app's agent block for this
   PC (chapter 9.2–9.3).

On the main PC, send each pilot's ATC audio out over VBAN: an outgoing stream per pilot
with the source bus that pilot's ATC bus and the destination that pilot PC's address.

## 11.7 Checklist

- [ ] Each headset has its own bus — or its own PC.
- [ ] No two rows in a mapping editor name the same strip or bus (no warning showing).
- [ ] INT has a path of its own (11.3 or 11.4) if you want an INT level that is not the
      VHF level.
- [ ] VHF2 is not expected to control a separate radio unless a separate source exists.
- [ ] Status → Remote Mixer shows every agent green before you judge a knob.
