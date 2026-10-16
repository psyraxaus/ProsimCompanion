# Localising a page (ADR-0014)

How every text in the web UI reaches the seven shipped languages, and the exact recipe for
moving one page from English literals to language packs. Written for agents and contributors;
the owner's rules are marked **(rule)**.

## Where things live

| Thing | Where |
|---|---|
| Shipped packs | `src/ProsimCompanion.App/config/lang/{code}/{page}.json` — one folder per language, one JSON file per page |
| Shipped codes | `en` (source of truth), `de`, `es`, `fr`, `ja`, `ko`, `nl` |
| Seeded copies | `%LOCALAPPDATA%\ProsimCompanion\config\lang\{code}\` (keep-user-edits, ADR-0007) |
| Reader / catalog | `src/ProsimCompanion.Core/Localization/` |
| Per-circuit localizer | `src/ProsimCompanion.Web/Localization/UiText.cs` (`IStringLocalizer`) |
| Guard test | `tests/ProsimCompanion.Core.Tests/Localization/ShippedLanguagePackGuardTests.cs` |

`_Imports.razor` already imports `Microsoft.Extensions.Localization` and
`ProsimCompanion.Web.Localization`; a page needs only `@inject IStringLocalizer T`.

## Pack file format

```json
{
  "$language": { "name": "Deutsch" },
  "fuel": {
    "title": "Kraftstoff",
    "card": { "planned": "Geplant", "planned.hint": "Aus dem OFP" },
    "rows": "{0} von {1} Zeilen"
  }
}
```

- Nested objects flatten to dotted ids: `fuel.card.planned`. A member name may itself contain
  a dot, so a text and its sub-texts can sit side by side (`"planned"` and `"planned.hint"`).
- Every leaf is a string. Placeholders are `{0}`, `{1}` …, formatted invariant; every language
  must keep the same placeholder set (the guard checks).
- `$language.name` is the language's own name; only one file per folder needs it
  (`shell.json` has it) — do not repeat it in page files.
- Each file's top-level section is named after the page (`fuel`, `takeoff`, `gsxSettings`), and
  the file is `{page}.json`. One page, one file, in every language folder.

## Recipe for one page

1. **Collect** every English literal the pilot can read: `Title=`, `Hint=`, `More=`, `Label=`,
   `title=`, `placeholder=`, `aria-label=`, `<PageTitle>`, inline text between tags, and the
   strings the `@code` block builds for the UI (pill words, messages, `switch` arms). Leave
   alone: aviation codes and units (`OFP`, `INIT`, `W&B`, `kg`, `UTC`, `FL`, ICAO/IATA codes,
   `V1`), product names (ProSim, GSX, SimBrief, Kokoro, Stream Deck, VoiceMeeter), dataref and
   LVAR names, settings keys shown as `<code>`, anything the CMTrace log receives
   (`ILogger` messages stay English — the probes grep them), and `console`/telemetry text.
2. **Write `en/{page}.json`** first. Ids are camelCase, grouped by card/section, readable in
   the Razor file without the JSON open (`fuel.topUp.confirm`, not `fuel.btn3`). Keep the
   English text byte-identical to what the page showed, including `…`, `—` and `→`.
3. **Replace in the Razor** with `@T["fuel.title"]` (inline), `Hint="@T["fuel.card.hint"]"`
   (attribute), `T["fuel.rows", count, total]` (placeholders). In C# code use
   `T["…"].Value` where a `string` is required by a `switch`/ternary mixing types; a
   `LocalizedString` converts to `string` implicitly elsewhere. A Razor attribute cannot hold
   a backslash-escaped quote — the existing `RazorAttributeEscapeGuardTests` catches that.
4. **Markup inside a sentence** (`<strong>`, `<code>`, `<a>`): split the sentence into
   `…Before` / `…After` ids around the markup, or pass the plain value as a placeholder and
   drop the markup. Never build `MarkupString` from pack text (a pack is user-editable).
5. **Translate** into `de`, `es`, `fr`, `ja`, `ko`, `nl` — one file each with exactly the
   same ids. Register: calm, professional, second person (`du`/`tu`/`tú`/`je`), the way an
   airline EFB reads. Keep the aviation terms a real crew uses untranslated when that is what
   they say (de: Pushback, Loadsheet, Callouts; ja/ko: カタカナ/한글 transliterations for
   common EFB terms). German runs ~30 % longer: shorten labels that sit in the sidebar, rail,
   pills and table headers; hints may be long.
6. **Run the guards**:
   `dotnet test tests/ProsimCompanion.Core.Tests --filter "FullyQualifiedName~Localization"`
   then `dotnet build`. The guard fails on: an id a Razor file asks for that `en` lacks, a
   language missing a page file or an id, an extra id, a placeholder mismatch, an id defined
   in two files of one language.
7. **Render check**: if the page has a render test under `tests/.../Web/`, it still passes
   (English is the default language in tests). Pages with no render test: at least open the
   page once in the running app (`/settings/appearance` → pick the language).

## Rules

- **(rule, owner 2026-10-13)** Never use the word `key` in any identifier, not even compound
  (`textKey`, `LabelKey`). Say *text id* / `textId`. Snyk Code flags it.
- **(rule)** Logs, probes, telemetry, the voice First Officer and the Stream Deck plugin stay
  English. Do not localise `ILogger` calls, `IEventLog` lines or spoken phrases.
- **(rule)** `CultureInfo.CurrentCulture` is never changed. Numbers, weights and times keep
  their invariant formats in every language.
- **(rule)** No new settings.json-only knob: the language picker already exists; a page never
  adds another.
- Do not touch `_Imports.razor`, `App.razor`, `Routes.razor`, `MainLayout.razor`,
  `SettingsLayout.razor`, `SettingsNav.cs`, `AppearanceSettings.razor` or the `shell.json` /
  `appearance.json` packs while working on another page — those are shared and already done.
- Commit as the repo does: GPG-signed, a one-sentence subject that says what changed and
  why, no AI trailers.

## Layout rules for long and CJK languages (2026-10-17)

- Sidebar labels (`.side-label`) may wrap to two lines at a space; a single long word still
  overflows the 84 px rail, so shorten it in the pack (de `Tanklog`, fr `Journal carb.`).
- Button rows: a right-aligned pair goes in one flex block with `margin-left: auto` on the
  block, never on each button (INIT's two resets, `.init-actions-danger`).
- Figures beside wrapping labels: `justify-content: flex-end` on the column so the numbers
  share a baseline (`.fs-metric`).
- Japanese and Korean: the `:lang(ja)` / `:lang(ko)` block at the end of `app.css` raises the
  tracked-out uppercase labels (`.side-label`, `.btn`, `.rail-group`, `.label-caps`,
  `.section-title`) to 10–11 px with almost no letter spacing. Add new small-caps classes
  there when you create them.
- Check a page in de (longest), fr and ja before calling it done; the screenshot recipe in
  `rc-refresh-screenshots` takes the shots with `webUi.language` set per pass.
