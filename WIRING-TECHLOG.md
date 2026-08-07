# Wiring: tech log & MEL, pilot logbook, post-flight debrief (Phase 6)

This slice ships fully built and tested but **unregistered** — the files below were off-limits
to the implementing agent. Apply these exact edits to activate it. Until then the app runs
unchanged (the `/techlog` page shows a "not wired" notice instead of failing).

## 1. `src/ProsimCompanion.Core/DependencyInjection/CoreServiceCollectionExtensions.cs`

Add inside `AddCoreServices(...)`, with the other `services.Configure<...>` calls:

```csharp
services.Configure<TechLogOptions>(configuration.GetSection(TechLogOptions.SectionName));
services.Configure<LogbookOptions>(configuration.GetSection(LogbookOptions.SectionName));
services.Configure<DebriefOptions>(configuration.GetSection(DebriefOptions.SectionName));
```

Add with the other singleton registrations (before the `return services;`):

```csharp
// Post-flight bookkeeping pillar: tech log & MEL, pilot logbook, session finalizer.
services.AddSingleton<TechLog.TechLogService>();
services.AddSingleton<TechLog.ITechLogService>(p => p.GetRequiredService<TechLog.TechLogService>());
services.AddSingleton<Debrief.IDebriefFactExtractor, Debrief.DebriefFactExtractor>();
services.AddSingleton<Logbook.LogbookService>();
services.AddSingleton<Logbook.ILogbookService>(p => p.GetRequiredService<Logbook.LogbookService>());
// Finalization steps run in explicit Order (debrief 10 → logbook 20 → techlog 30),
// so registration order here does not matter.
services.AddSingleton<Sessions.ISessionFinalizationStep>(p => p.GetRequiredService<Logbook.LogbookService>());
services.AddSingleton<Sessions.ISessionFinalizationStep>(p => p.GetRequiredService<TechLog.TechLogService>());
services.AddSingleton<Sessions.SessionFinalizer>();
services.AddHostedService<Hosting.PostFlightBootstrapService>();
```

(`Hosting.PostFlightBootstrapService` ships in this slice and calls the `Start()` methods in
the correct order — no other start wiring is needed on the Core side.)

## 2. `src/ProsimCompanion.Speech/SpeechServiceCollectionExtensions.cs`

Add inside `AddSpeechServices(...)`, after the existing voice-feature registrations (their
order is dispatch precedence — these are exact-match phrases, so last is fine):

```csharp
// Post-flight voice: tech-log brief + spoken debrief (deterministic template).
services.AddSingleton<TechLog.TechLogVoiceService>();
services.AddSingleton<IVoiceFeature>(p => p.GetRequiredService<TechLog.TechLogVoiceService>());
services.AddSingleton<Debrief.DebriefService>();
services.AddSingleton<IVoiceFeature>(p => p.GetRequiredService<Debrief.DebriefService>());
// The debrief is also the first session-finalization step (Order 10).
services.AddSingleton<ProsimCompanion.Core.Sessions.ISessionFinalizationStep>(
    p => p.GetRequiredService<Debrief.DebriefService>());
services.AddHostedService<PostFlightVoiceBootstrapService>();
```

(`PostFlightVoiceBootstrapService` ships in this slice; `SpeechBootstrapService` is untouched.)

## 3. `src/ProsimCompanion.App/ProsimCompanion.App.csproj`

Add to the `<ItemGroup>` with the other shipped config content:

```xml
<!-- Random-wear pool for the tech log (benign, purely procedural; techLog.randomWear). -->
<Content Include="config\techlog\wear-pool.json" CopyToOutputDirectory="PreserveNewest" />
```

The file `src/ProsimCompanion.App/config/techlog/wear-pool.json` already exists in this slice.

## 4. Optional: `src/ProsimCompanion.Core/Configuration/SettingsDefaultsWriter.cs` + settings.json

Every option has a safe default, so no config is required. To surface the sections in a fresh
`config/settings.json`, add these defaults wherever the writer emits its sections:

```jsonc
"techLog": {
  "enabled": true,
  "autoRectifyOnDueDate": false,
  "randomWear": false,
  "path": "",                 // blank = %LOCALAPPDATA%\ProsimCompanion\techlog.json
  "wearPoolPath": "",         // blank = config/techlog/wear-pool.json beside the exe
  "categoryADays": 3,
  "categoryBDays": 3,
  "categoryCDays": 10,
  "categoryDDays": 120
},
"logbook": {
  "enabled": true,
  "path": ""                  // blank = %LOCALAPPDATA%\ProsimCompanion\logbook.json
},
"debrief": {
  "enabled": true,
  "verbosity": "full"         // "full" | "brief"
}
```

## What activates

- **Tech log & MEL** — `/techlog` web page (raise/rectify/remove, due dates, OVERDUE badge,
  sectors carried); voice brief on "tech log" / "read the tech log" / "tech log brief" /
  "brief the tech log" / "any open items" / "open items"; automatic once-per-flight brief at
  Preflight when open items exist; sector fold at shutdown; optional auto-rectify and random
  wear. Store: `%LOCALAPPDATA%\ProsimCompanion\techlog.json`.
- **Pilot logbook** — folds each flight at shutdown (idempotent by session id);
  `LogbookService.Backfill()` seeds from existing session logs (no UI button yet). Store:
  `%LOCALAPPDATA%\ProsimCompanion\logbook.json`.
- **Post-flight debrief** — deterministic spoken summary at shutdown (Low priority, expires
  in 10 min or when a new flight starts), voice phrases "debrief" / "debrief now" / "flight
  debrief" / "post flight debrief" / "give me the debrief", text persisted as
  `<session>.debrief.txt` beside the session log, with the logbook "landing number N into X"
  line appended.
- **SessionFinalizer** — the single shutdown-edge coordinator (one 1.5 s flush delay, then
  debrief → logbook fold → tech-log fold, each failure-isolated).

## Deferred (deliberately out of this slice)

- Raise/rectify **voice dialogues** (need the free-form capture seam) — web form + brief only.
- **Post-abnormal shutdown offer** (needs the confirm dialogue); the per-flight abnormal
  memory it needs already exists (`TechLogVoiceService.FiredAbnormals`).
- **LLM styling** of the debrief (template only; number verification comes with it).
- **Company day mode** (logbook `days` array ships empty).
- Procedural hooks (`adviseOnPhase` / `extraChecklistLine`) from the predecessor's model.
- Logbook voice queries ("logbook summary", "how many landings", …) and a logbook web page.
