# Outbound event notifications (issue #151)

ProsimCompanion can push a short message at the flight's milestones to a phone or a webhook.
Off by default (`notifications.enabled`); every target is a row on Settings → Notifications.
Nothing here reads from or writes to the aircraft.

## Milestones (the `event` field)

| event | When | Extra `details` |
|---|---|---|
| `refuel-complete` | GSX reports the refuelling service completed | `fuelOnBoardKg` |
| `boarding-complete` | GSX reports boarding completed | `paxCount` |
| `final-loadsheet-sent` | the final loadsheet is sent (again for each revised edition) | `edition`, `zfwKg`, `towKg`, `fuelKg`, `paxCount` |
| `ready-for-pushback` | every departure service completed or skipped | — |
| `cabin-secure` | the purser reports the cabin secure | — |
| `deice-holdover-expiring` | the holdover card's low figure drops under `holdoverExpiringMinutes` (5), or expires | `remainingLowSeconds`, `expired`, `fluid` |
| `top-of-descent-approaching` | the once-per-flight 3:1 estimate crossing (issue #145) | `minutesToTod`, `distanceToGoNm`, `estimate: true` |
| `landed` | the landing time stamp | — |
| `on-blocks` | the on-blocks time stamp | `blockMinutes` |
| `deboarding-complete` | GSX reports deboarding completed | — |
| `test` | the "Send test" button | — |

Every milestone fires **once per flight cycle** (a GSX re-sync does not repeat it); the
flight-cycle reset re-arms them. A revised final loadsheet is news and fires again.

## Target kinds and wire shapes

All three are an HTTP **POST** with a 5 s timeout (`sendTimeoutSeconds`). An optional bearer
token is sent as `Authorization: Bearer <token>` on every kind.

### `webhook` — the generic JSON body (schema `prosimcompanion.notification/1`)

```json
{
  "schema": "prosimcompanion.notification/1",
  "event": "boarding-complete",
  "title": "Boarding complete",
  "text": "All passengers are on board. 150 passengers aboard.",
  "flightNumber": "BAW552",
  "route": "EGLL–LIRF",
  "atUtc": "2026-10-03T14:05:12Z",
  "details": { "paxCount": 150 },
  "source": "ProsimCompanion"
}
```

- `Content-Type: application/json`.
- `flightNumber`, `route` and `details` are **omitted** when unknown (no OFP loaded, nothing
  extra to say) — never null, never empty strings.
- `flightNumber` is the OFP callsign when present, else the flight number.
- `atUtc` is the milestone's time, ISO 8601 UTC with a `Z`.
- **Versioning**: `schema` changes only for a breaking change. New `details` keys and new
  events are additive and keep `/1`. Consumers should ignore unknown fields and events.

### `ntfy`

`POST <topic url>` with the message text as the plain-text body and ntfy's headers:

| Header | Value |
|---|---|
| `Title` | `BAW552 - Boarding complete` (flight prefix when known; ASCII only) |
| `Tags` | an emoji shortcode per event (`fuelpump`, `busts_in_silhouette`, `page_facing_up`, `airplane_departure`, `lock`, `snowflake,warning`, `airplane_arriving`, `checkered_flag`, `door`, `bell`) |
| `Priority` | `high` for `deice-holdover-expiring`, else `default` |

Example — a private topic on ntfy.sh with an access token:

1. In the ntfy app (iOS / Android) subscribe to a topic with an unguessable name, e.g.
   `prosim-7f3a9c`. Treat the topic name as a password.
2. Settings → Notifications → Add target: Kind **ntfy**, URL `https://ntfy.sh/prosim-7f3a9c`,
   Bearer token = an ntfy access token if the topic is protected (ntfy.sh Pro or your own
   server), else empty.
3. **Send test** → the phone shows "BAW552 - Test notification" (or "Test notification" with
   no OFP loaded).

Your own ntfy server works the same with its URL.

### `discord`

`POST <channel webhook url>` with `{ "content": "**BAW552 · Boarding complete** — All passengers are on board. 150 passengers aboard." }`.
Discord → channel settings → Integrations → Webhooks → New webhook → copy the URL. The URL
carries the webhook's own secret.

## Delivery rules (NotificationDispatcher)

- **Never blocks a raiser**: a milestone is a `TryWrite` into a bounded channel (32) and the
  signal handler returns. One background task sends.
- **Queue full → the newest message is dropped** and counted (`notify.failed` with
  `reason: "queue-full"`); older queued milestones still go out in order.
- **Failure cooldown**: a target whose send failed (timeout, 4xx, 5xx, DNS, bad URL) is left
  alone for `failureCooldownSeconds` (60). Milestones during the cooldown are dropped for
  that target (`notify.failed`, `reason: "cooldown"`). A later success clears it.
- **"Send test"** ignores the master switch and the cooldown — the pilot pressed the button.

## Secrets

- `notifications.targets[*].url` and `.token` are DPAPI-protected at rest (`dpapi:…`): an
  ntfy topic or a Discord webhook URL *is* the credential. `SecretProtector` resolves the
  `*` to every list entry; the configuration provider decrypts
  `notifications:targets:0:url`, `…:1:token`, and so on.
- The log and the session events carry the target **name**, the event and the HTTP status
  (`notify.sent` `{ event, target, kind, status, ms }`, `notify.failed` `{ event, target, kind,
  status, reason, ms }`) — never the URL, the token or the body.
- The diagnostics bundle redacts every property named exactly `url` and every `token`.

## Session events

| type | payload |
|---|---|
| `notify.sent` | `event`, `target`, `kind`, `status`, `ms` |
| `notify.failed` | `event`, `target` (null for queue-full), `kind`, `status` (null without a response), `reason`, `ms` |
