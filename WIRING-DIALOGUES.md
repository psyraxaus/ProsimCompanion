# Wiring: tech-log voice dialogues + mic-ownership seam

DI lines that could not be added on this branch (`SpeechServiceCollectionExtensions.cs` is
off-limits to the worktree agent). All go in `AddSpeechServices()` in
`src/ProsimCompanion.Speech/SpeechServiceCollectionExtensions.cs`. Build and tests are green
without them, but the two recognition lines are **required for the app to start**: the
spoken-checklist engine's constructor now takes `IRecognitionWindow` + `IMicOwnership`.

```csharp
// 1. REQUIRED — immediately after services.AddSingleton<RecognitionController>():
//    the controller's listening-window surface, plus the exclusive-mic seam over it.
services.AddSingleton<IRecognitionWindow>(p => p.GetRequiredService<RecognitionController>());
services.AddSingleton<IMicOwnership, MicOwnership>();

// 2. Beside the existing TechLog.TechLogVoiceService lines — the guided raise/rectify
//    dialogues (IVoiceFeature, exact-match phrases so dispatch position is uncritical) and
//    the post-abnormal shutdown offer (finalization step Order 40, after the tech-log fold).
services.AddSingleton<TechLog.TechLogDialogueService>();
services.AddSingleton<IVoiceFeature>(p => p.GetRequiredService<TechLog.TechLogDialogueService>());
services.AddSingleton<Core.Sessions.ISessionFinalizationStep>(
    p => p.GetRequiredService<TechLog.TechLogDialogueService>());
```

Notes:

- `TechLogDialogueService` needs no `Start()` and no bootstrap edit — feature dispatch and
  session finalization both arrive via the DI collections above.
  `PostFlightVoiceBootstrapService` is untouched.
- New option `techLog.offerFromAbnormalAtShutdown` (default `true`) is picked up by
  `SettingsDefaultsWriter` automatically (it serializes `new TechLogOptions()`); no writer
  change needed.
- Delete this file once the lines are merged into the registration extension.
