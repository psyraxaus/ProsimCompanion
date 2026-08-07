# Wiring for the wt-gsxctl slice (GSX service commands, voice control, status API)

Everything in this slice is wired **except** two touch-points that live in files this branch
deliberately does not edit (`Program.cs`, `SpeechServiceCollectionExtensions.cs`). Apply the
two snippets below when merging; nothing else is required.

Already wired on this branch (no action needed):

- `AddGsxServices()` registers `GsxServiceControl` (+ `IGsxServiceControl`,
  `IGsxTriggerDispatcher`, `IGsxGroundPrepStatus`) — the per-service seam behind the
  `gsx.request*`/`gsx.retract*` commands.
- `CommandsBootstrap.RegisterAll` registers the eleven new commands via
  `GsxServiceCommandHandlers` (seam resolved with `GetService`, so an absent GSX pillar
  answers `unavailable` as usual).
- `gsx.voiceControlEnabled` (default `true`) exists on `GsxOptions`; the settings-defaults
  writer will pick it up automatically next time defaults are materialized.

## 1. Program.cs — map the status API

In `BuildWebHost`, directly after the existing `web.MapCommandApi();` line:

```csharp
// Read-only status for Stream Deck key faces (same commandApi gate + auth as the commands).
web.MapStatusApi();
```

(`MapStatusApi` lives in `ProsimCompanion.App.Hosting.StatusApiEndpoints`, same namespace as
`MapCommandApi` — no new using needed.)

## 2. SpeechServiceCollectionExtensions.cs — register the GSX voice feature

In `AddSpeechServices`, with the other voice features (order only matters for dispatch
precedence; the phrases are exact-match, so last is fine — e.g. next to the tech-log/debrief
registrations):

```csharp
// GSX ground-services voice control (community request): exact-match phrases dispatched
// into the CommandRegistry — same guards as the web UI / Stream Deck, no second write path.
services.AddSingleton<Gsx.GsxVoiceService>();
services.AddSingleton<IVoiceFeature>(p => p.GetRequiredService<Gsx.GsxVoiceService>());
```

`GsxVoiceService` takes `CommandRegistry`, `ISpeechArbiter`, `IOptionsMonitor<GsxOptions>`,
`ILogger<>` and an optional `IGsxDepartureControl` — all already registered in the one
container (`CommandRegistry` in `Program.cs`, the departure seam in `AddGsxServices`). It has
no `Start()`; registration alone is enough.

## Smoke check after wiring

1. `commandApi.enabled: true` in settings, then
   `curl -H "Authorization: Bearer <token>" http://localhost:5320/api/status` → the JSON
   documented in `docs/integrations/command-api.md`.
2. `POST /api/command/gsx.requestPushback` → 409 `preconditionFailed` explaining the beacon
   flow (by design).
3. Say "request boarding" → spoken outcome tagged `gsx.voice`; "cabin crew start boarding"
   additionally answers "Boarding underway." as the cabin crew when boarding really starts.
