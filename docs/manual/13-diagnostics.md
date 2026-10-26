# Diagnostics: the ECAM-style system pages

The **Diagnostics** item under Settings in the sidebar opens five pages drawn like the
aircraft's ECAM system displays. They answer one question at a glance: what is the app doing
right now, and which link is unhappy? The badge on the sidebar item shows how many cautions
are up (amber), or turns red when one of them is a warning.

The pages live at `/diagnostics` (VOICE), `/diagnostics/sim`, `/diagnostics/remote`,
`/diagnostics/gsx` and `/diagnostics/status`. The row of names under the memo strip switches
pages; they are ordinary links, so they work on the tablet too. The drawing scales to the
window as one piece and never rearranges itself.

## How to read a page

| You see | It means |
|---|---|
| Green box, green value | Working normally. |
| Amber box or value | Caution: reconnecting, degraded, queued, stale, lost. The app is still running; something is waiting or falling back. |
| Red box or value | Warning: a hard failure that blocks a feature until you act. |
| `XX` in amber | The app has no data for this box right now (the source is off, not connected, or has not reported yet). It is never a stale number. |
| `- - -` in white | This box is intentionally not in use (a feature that is switched off, a GSX service the automation decided not to call). |
| Dashed line with an arrow | A link between two boxes. It takes the colour of the worse of its two ends. |
| Cyan small text | A unit or qualifier (`MS`, `HZ`, `GB`, `T`, `%`), or an action line on the STATUS page. |
| Grey page name | A page you are not on. The current page is green and underlined. |

Every page ends with the same strip: the app version, something page-specific (the model,
the simulator, the client count, the GSX state), the time the app has been up, and the flight
phase.

[screenshot: voice page]

## VOICE

The pilot-to-First-Officer pipeline as six boxes: **MIC** (level and sample rate), **VAD**
(the voice-activity gate: SPEECH or SILENCE, and which gate), **STT** (the speech server,
with the last round trip in ms), **LLM** (the language model, with its first-token time),
**TTS** (the voice server, with the last synthesis time) and **OUT** (PLAYING or IDLE). The
small label above each box is the machine it runs on: the address of the configured server
until you name it on Settings → Advanced → Diagnostics.

Top left: the **E2E** dial is the time from the moment you stop talking to the first sound of
the FO's answer, amber from 2.5 s, red from 4 s. The **GPU** dial shows `XX` unless a future
server exposes it: the app reads no graphics card. VRAM fills when the language model is
served by Ollama; TOK/S is the model's generation rate; RTF is the speech server's processing
time divided by the length of what you said; QUEUE is how many lines are waiting to be spoken.

Top right: the last thing the app heard, with its confidence and length, what the router made
of it (INTENT, ACTION), which gate was in use and how many utterances were rejected in the
last minute (BLEED: noise, empty transcripts, the FO's own voice heard back).

[screenshot: sim page]

## SIM

Three sources across the top (**PROSIM**, **SIMCONNECT**, **GSX**) feeding the dataref and
simvar bus bars, with the update rate beside each feed and the number of subscriptions and
stale values under each bar. The four cells show the aircraft as the app sees it: flight phase
and gate, on ground or airborne with the park brake, electrical power, doors. The rows below
are the plan (origin, destination, runway), the load (pax, zero-fuel weight, block fuel) and
the weather the app is working from.

## REMOTE

Everything connected to the app over the network. **WEB HOST** in the middle is the app's own
web server: how many browser sessions are open and the request rate of its API, with the
listen address and whether HTTPS is on. **DISPLAYS** on the left are the open browsers (which
browser and device, which page, the round trip in ms). **CONTROLLERS** on the right: the
Stream Deck (how long since it last polled) and the remote mixer agents as numbered squares.
The **AUDIO ROUTING** row shows each configured mixer channel, ON or OFF, its level and the
strip or bus it drives.

[screenshot: remote page]

## GSX

The aircraft from above with its doors (an amber marker is an open door) and the ground
services around it, each box wired to where that service meets the aircraft: above the nose,
**pushback** (the tug at the nose tip) and **GPU** (the cable on the forward right fuselage);
on the left, **boarding** at the top (tied to the jetway, and to the stairs when they are in
use), the **jetway** to door 1L, the **stairs** to door 4L; on the right, **catering** beside the service door your GSX profile uses (1R at
the top, or 4R at the bottom: set it under Settings → Ground Services → Doors → Catering
door), **refuel** to the right wing and **de-icing**. Each box shows the GSX stage (`AVAIL`,
`REQ`, `CONN`, `PROG`, `DONE`, or `- - -` when not required) with a detail line. The three
bars are the boarding, cargo and refuel progress. The memo strip here is GSX-specific: the
Couatl connection, what the smart button would do next, how long the turnaround has been
running.

## STATUS

The summary page. The left column lists every caution grouped by system, each with the
actions the app took or you can take underneath (for example `WOL MAGIC PKT ...... SENT 06:42`).
The right column lists **INOP SYS** (features blocked by a caution), **DEGRADED** (features
running with reduced function) and **RECENT** (the last state changes with their time). The
memo strip counts cautions and warnings, and the **BUNDLE** cell is the same support export as
the Logs page: the bundle now includes the diagnostics snapshot and the RECENT list, so a bug
report carries what the pages showed.

[screenshot: status page]

## Common cautions and what to do

| Caution | What it means | What to do |
|---|---|---|
| `VOICE  LLM HOST NOT RESPONDING` (amber) | The language-model PC does not answer on the network at all; most often it is asleep. The action line says whether a wake-on-LAN packet has been sent and when. | Wait a minute if a packet was sent. Otherwise press **Wake PCs** on the Voice First Officer status page, or check the PC. Briefings use the template meanwhile. |
| `VOICE  LLM SERVER DOWN` (red) | The PC is up but the model server on it is not answering. A wake will not help. | Start Ollama / llama-server on that PC, or check its port in Settings → Voice First Officer → Briefings. |
| `VOICE  LLM AUTH REJECTED` (red) | The server answered 401. | Check the API key. |
| `VOICE  STT SERVER NOT RESPONDING` / `TTS SERVER NOT RESPONDING` | The speech or voice server is unreachable; the app reconnects by itself. | Nothing, unless it stays amber: then check the box and its URL. |
| `SIM  SIMCONNECT LOST` / `PROSIM LOST` | The feed dropped; values hold their last reading (`HOLD LAST`) and the app reconnects by itself. | Nothing, unless MSFS or ProSim is really gone. |
| `SIM  PROSIM SDK OFF` | No ProSim SDK folder is configured or the SDK does not match. | Settings → Setup, point the app at the ProSim SDK folder. |
| `REMOTE  MIXER AGENT n LOST` | A VoicemeeterBridge agent on another PC dropped; the channels it drives are listed as `MUTED`. | Check that PC and the agent; the app reconnects by itself. |
| `REMOTE  STREAM DECK NOT POLLING` | The plugin stopped polling for more than a few seconds. | Check the Stream Deck PC and the plugin's pairing. |
| `GSX  <service> QUEUED` | A ground service was called and GSX has not started it yet. | Normal for a short while; the detail shows the wait. |
| `GSX  <service> CALL DROPPED` | The GSX menu was busy and a call was not delivered. | Close the open GSX menu, the automation retries. |

## Settings

Settings → Advanced → **Diagnostics** holds the two knobs: how often the pages redraw (one
second by default) and the short names shown above the VOICE boxes for each configured
server. Both apply live.
