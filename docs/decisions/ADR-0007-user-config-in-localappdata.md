# ADR-0007: User-editable config lives in %LOCALAPPDATA%\ProsimCompanion\config

Date: 2026-08-15
Status: accepted
Revises: the "user config beside the exe" aspect of the original layout (CLAUDE.md); settings.json is unaffected.

## Context

Until 0.3.0-beta.5 every user-editable content file (checklists, checklist sets, abnormal
procedures, voice commands, phrase pools, ATC requests, drop-in themes) lived in
`{app}\config\` beside the exe, deployed by the installer's bulk copy.

Two failures follow from that layout, and the 2026-08-15 flight test hit one (issue #55):

1. **Look-alike copies.** Users expect editable config under a profile directory — the
   standard Windows convention — so copies accumulate in places like
   `%APPDATA%\ProsimCompanion\...`. The flight-test user edited such a copy for weeks; the
   app never read it, and the divergence only became visible when an edit mattered.
2. **Update clobbering.** The installer copies the publish payload with `ignoreversion`;
   anything user-edited beside the exe is one update away from being reverted (only
   `settings.json` was excluded).

## Decision

- User-editable content files are read ONLY from `%LOCALAPPDATA%\ProsimCompanion\config`
  (`UserConfigPaths`). Covered: `checklists\` (+ `sets\`), `abnormals\`, `themes\`,
  `atc-requests.json`, `commands.json`, `phrases.json`.
- Shipped defaults continue to deploy to `{app}\config\` untouched; the installer may
  overwrite them freely — they are now app-owned source material, not the live copies.
- At every startup, before the host builds, `UserConfigSeeder` mirrors the shipped defaults
  into the user tree with keep-user-edits semantics (the installer's GSX-profile sidecar
  rule, done app-side with a `.shipped-manifest.json` of last-seeded SHA-256 hashes):
  missing → seed; unedited-but-shipped-changed → refresh; user-edited → keep; files with no
  shipped counterpart are never touched. First run under this scheme migrates whatever sits
  beside the exe — including edits made there under the old layout.
- `settings.json` deliberately stays beside the exe: the installer surgically owns two path
  keys in it, and the WPF shell reads the web bind/port/token from it before the host exists
  (lockout prevention). `config\techlog\wear-pool.json` also stays — app-owned procedural
  content, not user config.
- The Checklists web page displays the absolute source folder and last-reload time, so a
  save that lands in the wrong place is self-diagnosing.

## Consequences

- Editing the files beside the exe now does nothing (they are seed source only). The web
  pages and manual must always name the real folder — never say "the config folder" bare.
- Uninstall leaves `%LOCALAPPDATA%\ProsimCompanion` in place (already the rule for logs),
  so user content now survives a full reinstall too.
- A future "reset to defaults" affordance is trivial: delete a file from the user tree and
  restart — the seeder restores the shipped copy.
