# VoicemeeterBridge WebSocket protocol, version 1

This document is enough to write a client without reading the agent source.

## Transport

- Endpoint: `ws://<bridge-host>:<port>/ws` (default port `5088`). Plain WebSocket, no TLS.
- Every frame is one WebSocket **text** message holding one JSON object. A binary frame closes the connection with status `1003`.
- Maximum frame size: 64 KiB. Larger frames close the connection with status `1009`.
- Property names are camelCase. Unknown properties are ignored.
- Every frame has an `op` string that says what it is.
- The agent never sends more than one JSON object per frame and never sends partial frames.
- The agent sends WebSocket ping frames every 30 seconds. Standard WebSocket libraries answer these automatically. The JSON `ping`/`pong` pair below is separate and optional.

## Connection lifecycle

1. Open the socket.
2. Send `hello` as the **first** frame, within 5 seconds (configurable on the agent).
3. Receive `welcome`.
4. Send any other frames in any order. Receive frames in any order.
5. Either side may close with a normal WebSocket close.

If the first frame is not a valid `hello`, or arrives too late, or the token is wrong, the agent closes the socket with WebSocket status `1008` (policy violation) and one of these reasons:

| Reason | Meaning |
|---|---|
| `hello timeout` | No frame arrived in time. |
| `hello required` | The first frame was not a `hello`, or was not valid JSON. |
| `unsupported protocol` | `protocol` was not `1`. |
| `unauthorized` | The token did not match. |

After a successful `hello`, a malformed frame does **not** close the socket. The agent answers with an `error` frame and keeps going.

When the agent shuts down it closes with status `1001` and reason `server shutting down`.

## Parameter names

A parameter name is the Voicemeeter Remote API name, for example `Strip[0].Gain`, `Bus[1].Mute`, `Strip[2].Label`, `Strip[0].device.name`, `Option.sr`. The agent passes names to the Voicemeeter DLL unchanged, after checking them against this pattern:

```
^[A-Za-z][A-Za-z0-9]*(\[[0-9]{1,3}\])?(\.[A-Za-z][A-Za-z0-9]*(\[[0-9]{1,3}\])?)*\z
```

Maximum length 96 characters. Names that fail the check are rejected before they reach Voicemeeter, with the error `invalid parameter name '<name>'`. Names are case-insensitive in Voicemeeter itself, but the agent treats them as case-sensitive strings: watch `Strip[0].Gain` and you get `changed` frames for `Strip[0].Gain`, spelled that way.

Which names exist depends on the Voicemeeter edition. See the VB-Audio *Voicemeeter Remote API* PDF for the full list.

## Values

A parameter value is either a JSON **number** or a JSON **string**. Voicemeeter decides which: gains, mutes, routing flags and so on are numbers; labels and device names are strings. The agent reports each parameter in its natural type. For a `set`, send a number for numeric parameters and a string for text parameters. A string sent to a numeric parameter is passed through and Voicemeeter parses it; a number sent to a text parameter fails.

Booleans (`true`/`false`) in a `set` are accepted and converted to `1`/`0`.

Text values in a `set` are limited to 256 printable Latin-1 characters. Voicemeeter's own limit for labels is shorter; it truncates.

## Client to agent frames

### hello

```json
{"op":"hello","token":"<token>","protocol":1}
```

Must be the first frame. `token` is the shared secret from the agent's `appsettings.json` override (tray menu, *Copy token*). `protocol` must be `1`.

### watch

```json
{"op":"watch","params":["Strip[0].Gain","Bus[1].Mute"]}
```

Adds the names to this connection's watch list and triggers a `snapshot` with their current values. Watching a name twice is harmless. Each connection has its own watch list. Maximum 512 names per frame.

### unwatch

```json
{"op":"unwatch","params":["Strip[0].Gain"]}
```

Removes names from the watch list. No reply. Unknown names are ignored.

### set

```json
{"op":"set","id":"abc","param":"Strip[0].Gain","value":-6.0}
{"op":"set","id":"abd","param":"Strip[0].Label","value":"Mic"}
```

Writes one parameter. `id` is optional and is echoed in the `result`. Writes to the same parameter in quick succession are **coalesced**: the agent sends Voicemeeter at most 20 writes per second per parameter and always the latest value. Every `set` still gets its own `result`; the ones that were superseded report the outcome of the write that carried the final value.

A successful `result` means Voicemeeter accepted the write. The new value is reported separately by a `changed` frame, if the parameter is watched, once Voicemeeter reports it.

### get

```json
{"op":"get","id":"abc","params":["Strip[0].Gain","Strip[0].Label"]}
```

Reads the names once, without watching them. Reply is a `values` frame with the same `id`.

### levels

```json
{"op":"levels","enabled":true,"channels":[{"type":3,"channel":0},{"type":3,"channel":1}]}
{"op":"levels","enabled":false}
```

Starts or stops level meter streaming for this connection. The `channels` list **replaces** any previous subscription. `type` selects the meter stage, as in the Remote API:

| type | Meter |
|---|---|
| 0 | Input strip, pre-fader |
| 1 | Input strip, post-fader |
| 2 | Input strip, post-mute |
| 3 | Output bus |

`channel` is a zero-based channel index, `0`–`255`. Note that these are *audio channels*, not strip or bus numbers: a stereo bus occupies channels `0` and `1` of its meter block, and in Potato each physical strip has 2 channels while each virtual strip has 8. See the Remote API PDF for the channel layout of each edition.

While enabled, the agent sends a `levels` frame about every 50 ms (agent setting). Channels that Voicemeeter rejects as out of range are silently left out of the frames.

### ping

```json
{"op":"ping"}
```

Answered with `pong`. Useful as an application-level keep-alive.

## Agent to client frames

### welcome

```json
{"op":"welcome","protocol":1,"voicemeeter":{"connected":true,"kind":"potato","version":"3.1.1.2"}}
{"op":"welcome","protocol":1,"voicemeeter":{"connected":false}}
```

Sent once, right after a valid `hello`. `kind` is `standard`, `banana` or `potato`. `kind` and `version` are present only when `connected` is `true`.

### status

```json
{"op":"status","voicemeeter":{"connected":false}}
{"op":"status","voicemeeter":{"connected":true,"kind":"potato","version":"3.1.1.2"}}
```

Sent when the agent loses or regains Voicemeeter. The agent keeps polling while Voicemeeter is closed and reconnects automatically when it starts again (it retries every 2 seconds). After a reconnect the agent re-reads every watched parameter and sends `changed` for any whose value differs from what it last reported. Values that did not change are **not** re-sent, so a client's cached state stays valid across a Voicemeeter restart.

### snapshot

```json
{"op":"snapshot","values":{"Strip[0].Gain":-6.0,"Bus[1].Mute":0},"errors":{"Strip[9].Gain":"unknown parameter"}}
```

Reply to `watch`. `values` holds every name that could be read. `errors` is present only when at least one name could not be read; its value is a short reason. Names in `errors` stay on the watch list and are retried on every poll, so if they become valid later (for example after switching Voicemeeter edition) they start producing `changed` frames.

### changed

```json
{"op":"changed","param":"Strip[0].Gain","value":-6.0}
```

A watched parameter has a new value. Sent for any cause: a `set` from this client, a `set` from another client, the Voicemeeter window, a macro button, MIDI, anything. Only one `changed` per parameter per poll; if a value moves several times inside one 30 ms poll interval only the last value is reported. A `changed` is sent only when the value actually differs from the last one reported to you.

### result

```json
{"op":"result","id":"abc","ok":true}
{"op":"result","id":"abc","ok":false,"error":"unknown parameter"}
```

Reply to `set`. `id` is omitted if the `set` had none. Possible `error` strings:

| error | Meaning |
|---|---|
| `unknown parameter` | Voicemeeter does not know this name (wrong edition, or a typo). |
| `voicemeeter not running` | Voicemeeter is closed; try again after a `status` with `connected: true`. |
| `error` | Voicemeeter rejected the write (for example a number sent to a text parameter). |
| `internal error` | Something failed inside the agent; see its log. |

### values

```json
{"op":"values","id":"abc","values":{"Strip[0].Gain":-6.0},"errors":{"Nope":"unknown parameter"}}
```

Reply to `get`. Same shape as `snapshot` plus the echoed `id`.

### levels

```json
{"op":"levels","values":[{"type":3,"channel":0,"db":-18.2},{"type":3,"channel":1,"db":-200}]}
```

One batch of meter readings for the channels this connection subscribed to. `db` is dBFS, rounded to one decimal. Silence is reported as `-200`, never as `-Infinity`.

### pong

```json
{"op":"pong"}
```

Reply to `ping`.

### error

```json
{"op":"error","error":"invalid parameter name 'Strip[0];Gain'"}
```

A frame after `hello` could not be used. The socket stays open. Sent for invalid JSON, an unknown `op`, a missing or wrong-typed field, or an invalid parameter name. Because the frame was rejected before parsing finished, `error` carries no `id`; a client that needs to correlate should avoid sending invalid frames rather than rely on this.

## Ordering and timing guarantees

- Frames from the agent arrive in the order the agent produced them, on one connection.
- A `snapshot` always follows its `watch`; a `values` always follows its `get`; a `result` always follows its `set`. Replies to different requests may interleave with `changed`, `levels` and `status` frames, and `result` frames may arrive out of order relative to each other because of coalescing. Use `id` to correlate.
- Parameter polling runs every 30 ms (agent setting). A change made in the Voicemeeter window normally reaches the client within about 50 ms.
- All connections share one Voicemeeter session. The agent serialises every DLL call on one thread, so clients cannot corrupt each other, but a very large watch list on one client slows polling for all.

## Minimal client walk-through

```
-> {"op":"hello","token":"cc4e…","protocol":1}
<- {"op":"welcome","protocol":1,"voicemeeter":{"connected":true,"kind":"potato","version":"3.1.1.2"}}
-> {"op":"watch","params":["Strip[0].Gain","Strip[0].Mute"]}
<- {"op":"snapshot","values":{"Strip[0].Gain":0,"Strip[0].Mute":0}}
-> {"op":"set","id":"1","param":"Strip[0].Gain","value":-6}
<- {"op":"result","id":"1","ok":true}
<- {"op":"changed","param":"Strip[0].Gain","value":-6}
   (user clicks Mute in the Voicemeeter window)
<- {"op":"changed","param":"Strip[0].Mute","value":1}
   (user closes Voicemeeter)
<- {"op":"status","voicemeeter":{"connected":false}}
   (user starts Voicemeeter again; Strip[0].Mute came back as 0)
<- {"op":"status","voicemeeter":{"connected":true,"kind":"potato","version":"3.1.1.2"}}
<- {"op":"changed","param":"Strip[0].Mute","value":0}
```

## Other endpoints

`GET http://<bridge-host>:<port>/health` returns `{"voicemeeter":{"connected":true},"clients":1}` without authentication. It is meant for a quick check from a browser or `curl`; it exposes nothing else.
