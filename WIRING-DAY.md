# Wiring: company day mode

Everything below is additive. The feature builds and tests green without these lines; until
they are applied the `/day` page shows "not wired into this build" and no day tracking runs.

## 1. `src/ProsimCompanion.Core/DependencyInjection/CoreServiceCollectionExtensions.cs`

Options binding — with the other `services.Configure<...>` lines:

```csharp
services.Configure<DayOptions>(configuration.GetSection(DayOptions.SectionName));
```

Singletons — beside the post-flight bookkeeping registrations:

```csharp
// Company day mode (Core side): state persistence, web-facing store, summary composer.
services.AddSingleton<Day.DayStateFile>();
services.AddSingleton<Day.DayStatusStore>();
services.AddSingleton<Day.IDaySummaryComposer, Day.DaySummaryComposer>();
```

## 2. `src/ProsimCompanion.Core/Configuration/SettingsDefaultsWriter.cs`

In the `sections` array (so `settings.json` self-documents the `day` section):

```csharp
(DayOptions.SectionName, new DayOptions()),
```

## 3. `src/ProsimCompanion.Speech/SpeechServiceCollectionExtensions.cs`

After the existing `Company.CompanyChannelService` registrations (exact-match phrases, so the
dispatch position is not critical; keep it with the other post-flight features):

```csharp
services.AddSingleton<Company.ICompanyChannel>(p => p.GetRequiredService<Company.CompanyChannelService>());
// Company day mode: voice start/end, Order-40 session-finalization step (leg-fact fill +
// turnaround summary), web Start/End control, hosted bootstrap.
services.AddSingleton<Day.CompanyDayService>();
services.AddSingleton<IVoiceFeature>(p => p.GetRequiredService<Day.CompanyDayService>());
services.AddSingleton<Core.Sessions.ISessionFinalizationStep>(p => p.GetRequiredService<Day.CompanyDayService>());
services.AddSingleton<Core.Day.IDayControl>(p => p.GetRequiredService<Day.CompanyDayService>());
services.AddHostedService<Day.DayBootstrapService>();
```

## 4. `src/ProsimCompanion.Web/Layout/MainLayout.razor`

Nav link, after the Tech Log tab:

```razor
<NavLink class="tab" href="day">Duty Day</NavLink>
```

## 5. `src/ProsimCompanion.Speech/Debrief/DebriefService.cs` — day-context line

The per-sector debrief gains one line ("Leg 2 of 4 complete.") read from the Core store, so
DebriefService needs no reference to the day service itself.

Using (top of file):

```csharp
using ProsimCompanion.Core.Day;
```

Field + constructor parameter (with the other collaborators):

```csharp
private readonly DayStatusStore _dayStore;
```

```csharp
DayStatusStore dayStore,
```

```csharp
ArgumentNullException.ThrowIfNull(dayStore);
```

```csharp
_dayStore = dayStore;
```

In `Run(bool manual)`, immediately after the logbook-comparison append block
(`text = text.TrimEnd() + " " + comparison;` and its closing brace):

```csharp
// Company day mode's context line ("Leg 2 of 4 complete."); empty when no day is open.
var dayLine = _dayStore.Snapshot().DebriefContextLine;
if (!string.IsNullOrWhiteSpace(dayLine))
{
    text = text.TrimEnd() + " " + dayLine;
}
```

Note the finalization ordering already guarantees correctness here: the debrief (Order 10)
runs before the day step (Order 40), and the context line's leg index is stamped on the
Shutdown phase edge — before any finalization step runs.

## Configuration reference (`config/settings.json` → `day`)

| Setting | Default | Notes |
|---|---|---|
| `enabled` | `false` | master switch for automatic tracking (voice/web start always work) |
| `autoStart` | `true` | begin a day at the first Preflight |
| `postFlightAllowanceMinutes` | `15` | added after the last on-blocks for the duty figure |
| `autoCloseIdleMinutes` | `90` | auto-close after this idle time in a turnaround (0 = never) |
| `rotationsFolder` | `"rotations"` | planned-rotation JSON folder (relative → app base dir) |
| `turnaroundSummary` | `true` | speak the on-blocks turnaround summary |
| `path` | `""` | daystate.json override; blank = `%LOCALAPPDATA%\ProsimCompanion\daystate.json` |

Planned rotation file example (`rotations/*.json`, first ordinal-ordered file with legs wins):

```json
{
  "dayId": "2026-08-08-A",
  "reportTimeUtc": "2026-08-08T05:30:00Z",
  "legs": [
    { "from": "EGLL", "to": "EGCC", "flightNo": "BA123", "scheduledOffUtc": "2026-08-08T07:15:00Z", "scheduledOnUtc": "2026-08-08T08:05:00Z" },
    { "from": "EGCC", "to": "EGLL", "flightNo": "BA124", "scheduledOffUtc": "2026-08-08T09:00:00Z", "scheduledOnUtc": "2026-08-08T09:55:00Z" }
  ]
}
```

## Design notes / deviations from Prosim2FO

- **Leg facts at Order 40**, not a 1700 ms delay: the fill runs as a session-finalization step
  after debrief/logbook/tech-log have read the same file.
- **Deviation watch is post-hoc** (at leg completion, from the extracted destination): this
  codebase emits `flight.route` only when a briefing resolves — there is no continuous
  in-flight destination event to drive the predecessor's mid-flight advisory. Documented in
  `CompanyDayService` remarks.
- **ONE duty formula** (`DayMath.DutyMinutes`) shared by view, summary and logbook record —
  the predecessor computed it twice with different fallbacks.
- **daystate.json path is options-overridable**; corrupt files go aside as
  `daystate.json.corrupt-<ts>.bak` (unified naming, was `.bad-*`).
- **Rotation loading skips** malformed/empty candidates instead of silently losing the plan.
- No `day.updated` event spam: the web page reads `DayStatusStore` (Changed event) instead of
  the predecessor's per-minute event-log records.
