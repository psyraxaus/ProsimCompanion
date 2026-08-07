# Wiring: speaker-role voices + LLM debrief styling (Phase 6)

This slice ships fully built and tested but **partially unwired** — the files below were
off-limits to the implementing agent. Until these edits are applied the app runs unchanged:
every `SpeechRequest` defaults to `SpeechRole.FirstOfficer`, `VoicesOptions` binds its
compiled defaults (unconfigured `IOptionsMonitor<VoicesOptions>` still resolves), and the
debrief's LLM styling is already live because it rides the existing `briefing` LLM keys plus
the auto-written `debrief.useLlm` default.

No new singleton registrations are required: `OpenAiChatClient` is created internally by
`BriefingService`/`DebriefService` (its optional ctor parameter is the test seam), and the
changed constructors (`SpeechArbiterService`, `TtsPrewarmService`, `DebriefService`) only
added `IOptionsMonitor<...>` parameters, which DI always resolves.

## 1. `src/ProsimCompanion.Core/DependencyInjection/CoreServiceCollectionExtensions.cs`

Add with the other `services.Configure<...>` calls (beside `SpeechOptions`, line ~63):

```csharp
services.Configure<VoicesOptions>(configuration.GetSection(VoicesOptions.SectionName));
```

## 2. `src/ProsimCompanion.Core/Configuration/SettingsDefaultsWriter.cs`

Add to the `sections` array (beside the other speech-pillar sections):

```csharp
(VoicesOptions.SectionName, new VoicesOptions()),
```

This self-documents the new `voices` section in `config/settings.json`:

```json
"voices": {
  "purser": "af_heart",
  "company": "am_onyx",
  "purserIntercomFilter": true,
  "companyIntercomFilter": false
}
```

(It also writes the new `debrief.useLlm: true` key automatically — `DebriefOptions` is
already in the array.)

## 3. `src/ProsimCompanion.Speech/Cabin/CabinCrewService.cs` — purser role

In `DeliverReportAsync` (~line 172), tag the **purser report** with its role. The FO
acknowledgement that follows stays role-less (it is the FO speaking):

```csharp
await _arbiter.EnqueueAsync(new SpeechRequest(
    purserText, SpeechPriority.Normal, Ttl: ttl, Tag: tag,
    Role: SpeechRole.Purser)).ConfigureAwait(false);
```

## 4. `src/ProsimCompanion.Speech/Company/CompanyChannelService.cs` — company role

Add `Role: SpeechRole.Company` to the three content deliveries (the loadsheet ~line 271, the
cruise message ~line 306, and the repeat-last ~line 335 — the repeat replays company content,
so it keeps the company voice). The two FO-status lines ("The loadsheet is not available
yet.", "No company messages received.") stay role-less — that is the FO talking, not dispatch.

```csharp
await _arbiter.EnqueueAsync(new SpeechRequest(
    spoken, SpeechPriority.Normal, Tag: "company.loadsheet",
    Chime: options.Chime ? "company" : null,
    Role: SpeechRole.Company)).ConfigureAwait(false);
```

```csharp
await _arbiter.EnqueueAsync(new SpeechRequest(
    text, SpeechPriority.Low, Ttl: TimeSpan.FromSeconds(60),
    IsStillValid: () => _flight.CurrentPhase == FlightPhase.Cruise,
    Tag: "company.message",
    Chime: _options.CurrentValue.Chime ? "company" : null,
    Role: SpeechRole.Company)).ConfigureAwait(false);
```

```csharp
await _arbiter.EnqueueAsync(new SpeechRequest(
    last, SpeechPriority.Normal, Tag: "company.repeat",
    Chime: _options.CurrentValue.Chime ? "company" : null,
    Role: SpeechRole.Company)).ConfigureAwait(false);
```

## What the wiring activates

- **Purser/company voices**: the arbiter resolves each request's `Role` against
  `VoicesOptions` (`RoleVoiceResolver`), threads the voice id through
  `TtsRouter → ITtsProvider.SynthesizeAsync(..., voiceOverride)` (Kokoro/Google honour it and
  key their disk caches on the effective voice; WinRT/SAPI ignore it, once-logged), and makes
  the per-call intercom decision in `ISpeechPlayback.PlayAsync` — purser band-passed per
  `voices.purserIntercomFilter`, company clean per `voices.companyIntercomFilter`, chimes
  always unfiltered. A blank role voice falls back to the FO voice, logged once per role.
- **Prewarm**: `TtsPrewarmService` additionally warms the purser voice with the ACTUAL
  configured `cabin.cabinSecureText` / `cabinReadyText` / `boardingDelayText` wording and the
  company voice with "Loadsheet.", skipping any role whose voice is blank or equal to the
  serving provider's FO voice.
- **Debrief styling** (no wiring needed, listed for completeness): when `briefing.llmEnabled`
  + `briefing.llmModel` are set and `debrief.useLlm` is true, the debrief is styled by the LLM
  behind the shared `NumberVerifier` (one strict re-ask, then the deterministic template on
  any failure; each call gets its own timeout budget). Outcome recorded as `debrief.styled
  { llm, verified }` in the session event log.
