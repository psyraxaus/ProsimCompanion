# Wiring the command registry (composition-root lines to add)

The Phase 6 command slice ships fully built and testable without touching the shared Core
files; until these lines are added, `/api/command*` routes answer 503 with a reason pointing
here (or 404 while `commandApi.enabled` is false). Everything else — options binding and
endpoint mapping — is already done in `src/ProsimCompanion.App/Program.cs`.

## 1. Register the registry (Program.cs, `BuildWebHost`, with the other `AddSingleton` lines)

```csharp
builder.Services.AddSingleton<ProsimCompanion.Core.Commands.CommandRegistry>();
```

(Or add `services.AddSingleton<Commands.CommandRegistry>();` to
`CoreServiceCollectionExtensions.AddCoreServices` if you prefer it lives with the other Core
singletons — either works; the endpoints resolve it per request.)

## 2. Populate it (Program.cs, `Main`, after `BuildWebHost` returns and before `web.Start()`)

```csharp
ProsimCompanion.Core.Commands.CommandsBootstrap.RegisterAll(
    web.Services.GetRequiredService<ProsimCompanion.Core.Commands.CommandRegistry>(),
    web.Services);
```

`RegisterAll` resolves every seam with `GetService` (not `GetRequiredService`) — a pillar that
is absent or disabled still gets its commands registered, answering `unavailable`, so the API
shape is stable and startup can never fail here. Note it does instantiate the resolved
singletons (e.g. `ChecklistService`) slightly earlier than the host otherwise would.

## 3. Opt in (config/settings.json — the section is not in SettingsDefaultsWriter, which this
slice was told not to touch; add it there too if you want it written by default)

```json
"commandApi": {
  "enabled": true,
  "requireTokenOnLoopback": true
}
```

## 4. Smoke test

```
curl http://localhost:5320/api/commands -H "Authorization: Bearer <webUi.accessToken>"
curl -X POST http://localhost:5320/api/command/minima.set ^
     -H "Authorization: Bearer <token>" -H "Content-Type: application/json" ^
     -d "{\"kind\":\"da\",\"altitudeFt\":740}"
```

Expected: 200 `{"outcome":"success","reason":"Arrival minima set: DecisionAltitude 740 ft."}`,
visible on the /speech page. Full contract: docs/integrations/command-api.md.

Delete this file once wired.
