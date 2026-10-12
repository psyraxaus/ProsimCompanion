# ADR-0014: UI localisation through JSON language packs, picked on Settings → Appearance

Date: 2026-10-13
Status: accepted (2026-10-13, owner picks: JSON packs over .resx; the picker on the Appearance
page, not the footer; the voice First Officer stays English)
Amends: ADR-0007 (one more seeded user-config folder, `config\lang`), ADR-0010 (the Appearance
page gains a Language section).

## Context

Every text in the web UI was an English literal in a Razor file: about 1,400 attribute strings
and as many inline texts over 56 pages and components, plus the texts some services build for
the UI (status pills, event-log lines, banner sentences). The owner wants the app readable in
English, Spanish, French, German, Japanese and Korean, and wants adding a seventh language to be
cheap for a community member who has never opened the solution.

Three surfaces were weighed and only one is in scope:

| Surface | Decision |
|---|---|
| Web UI chrome (pages, components, settings, banners) | **Localise** — this ADR. |
| The voice First Officer (phrases, checklists, crew hails, the spoken-command router) | **Stays English.** Real cockpits fly English SOP whatever the crew's language; the ASR router and the checklists are English content, and translating them is a product decision, not a string job. |
| CMTrace / session logs, the telemetry feed, the Stream Deck plugin | **Stay English.** The flight-verification probes grep log text; a translated log breaks every probe. The Stream Deck plugin has Elgato's own per-language files and is a separate, small job. |

The standard .NET path is `.resx` + `IStringLocalizer` + `RequestLocalization`, with the culture
fixed per Blazor Server circuit from a cookie and a page reload on every change. It was rejected
for three reasons: `.resx` is XML that needs a build and Visual Studio, so a community translator
cannot contribute a file; a per-circuit `CurrentCulture` silently changes `double.Parse` and
`ToString` on every settings page, and this app formats everything the ICAO way (decimal point,
kg, UTC) whatever the language; and the reload loses page state, where the theme picker next to
it already applies live.

## Decision

1. **One JSON pack per language**, `config\lang\{code}.json`, shipped beside the exe and seeded
   with keep-user-edits semantics into `%LOCALAPPDATA%\ProsimCompanion\config\lang` (ADR-0007).
   The file name is the BCP 47 code (`de`, `pt-BR`); `$language.name` holds the language's
   own name for the picker; everything else is nested objects of strings, flattened to dotted
   ids on load (`layout.nav.flightStatus`). A member with a dot in its name is allowed, so a
   text and its sub-texts can sit side by side (`"nextService": …`, `"nextService.complete": …`).
   **`en.json` is the source of truth**: every other pack must hold exactly its ids
   (`ShippedLanguagePackGuardTests`), and anything a pack lacks renders in English.
2. **Components inject the standard `IStringLocalizer`** (`@inject IStringLocalizer T`,
   `@T["layout.nav.fuel"]`, `@T["appearance.theme.applied", name]`), implemented in-repo by
   `UiText` over `LanguagePackCatalog` — scoped per circuit, so the language is resolved on
   every lookup from the configured code or, when none is set, this browser's own
   `Accept-Language` list (captured in `App.razor`, handed to the circuit through `Routes`).
   `CultureInfo.CurrentCulture` is **not** touched. Placeholders are `{0}`-style, formatted
   invariant. Texts the pack lacks fall back to English, then to the id, logged once.
3. **The setting is `webUi.language`** (empty = follow each browser), mirrored live by
   `UiLanguageStore`; `MainLayout` watches it and re-renders the whole tree, so the sim PC and
   the tablet switch together with no reload, exactly like the theme.
4. **The picker lives on Settings → Appearance**, a Language section above Theme: "Follow the
   browser (here: Deutsch)" first, then every pack by its native name. The footer stays a
   status bar (and is hidden on phones). The page hint names the pack folder and the file to
   add, so power users find the JSON route without reading the manual.
5. **CJK fonts** come from the system through `html:lang(ja)` / `html:lang(ko)` font stacks
   (Yu Gothic UI / Meiryo / Hiragino; Malgun Gothic / Apple SD Gothic Neo), never a shipped font
   file, and the Latin small-caps letter-spacing is reduced under those languages.
6. **Identifiers never contain the word "key"** (owner rule 2026-10-13, Snyk Code's
   hard-coded-credential heuristic): the lookup name is a *text id*, the records carry
   `LabelTextId`.

## Consequences

- Localising a page is mechanical: move its literals into `en.json` under a section named
  after the page, replace them with `@T["…"]`, and the guard test tells the translator what
  is new. The first slice (this change) covers the main layout, the settings rail, the
  reconnect overlay and the whole Appearance page; the remaining pages follow page by page.
- Texts built in Core/Gsx/Speech for the UI (status pills, event-log lines) stay English until
  they are moved behind an id; that is the hard 20 % and is done last, page by page.
- A new language is one file: copy `en.json`, translate, drop it in the user folder — no build,
  no restart (the folder is re-read at most every 2 s and when the Appearance page opens).
  Shipping it means adding it beside the others and letting the guard test prove it complete.
- Screenshots in the manual stay English (RC rule unchanged). The manual's settings reference
  names `webUi.language`.
- German runs about 30 % longer than English; the sidebar labels and the rail were checked
  at that length, and any later page must be too.
