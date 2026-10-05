# Versioning & releases

One number, one place: **`<Version>` in `Directory.Build.props`**. Everything else derives
from it — never version anything separately.

## Who consumes the version

| Consumer | How |
|---|---|
| Assemblies | MSBuild stamps `AssemblyVersion` / `FileVersion` / `InformationalVersion` from `<Version>` |
| Update banner | `UpdateCheckService` compares the assembly informational version against the latest GitHub release tag by semantic-version precedence (`ReleaseVersion`: `v` prefix and `+metadata` dropped, the `-prerelease` part compared) |
| Installer | `installer/build-installer.ps1` reads `Directory.Build.props` and names the output `ProsimCompanion-Setup-<version>.exe` |
| Web UI | the banner shows "running X" next to "latest Y" |

## Scheme

Semantic versioning, three components, pre-1.0 rules:

- **0.x.0 (minor)** — new features, new pages, new pillars, parity batches. The normal bump.
- **0.x.y (patch)** — fixes only, no new settings keys.
- **1.0.0** — first release after a full sim-verified pass of every "Unverified live" marker.
- After 1.0: **major** = breaking `settings.json` shape (a `SettingsMigrator` step is then
  mandatory), **minor** = features, **patch** = fixes.

Pre-release builds use `-beta.N` / `-rc.N` suffixes (`0.6.0-rc.18`). The update check
compares them (issue #159): `rc.18` is newer than `rc.16`, `rc.10` is newer than `rc.2`
(numbers compare as numbers), `rc.1` is newer than `beta.9`, and the final `0.6.0` is newer
than every `0.6.0-…` pre-release. Until 2026-10-06 the suffix was stripped from the running
version only, the tag `v0.6.0-rc.18` never parsed, and no release-candidate user was ever
offered an update.

## Release checklist

1. Bump `<Version>` in `Directory.Build.props` (the only edit).
2. Full build + test suite green.
3. Commit (`Release 0.2.0`), tag **`v0.2.0`** (annotated, signed), push commit + tag.
4. `installer\build-installer.ps1` → attach `ProsimCompanion-Setup-0.2.0.exe` to a GitHub
   release for the tag. The release must be a **release**, not a draft/pre-release, for the
   update banner to see it (`releases/latest` ignores drafts and pre-releases).
5. Release notes: the commit subjects since the previous tag are the skeleton
   (`git log v0.1.0..v0.2.0 --format="- %s"`).

Note the settings file has its own independent migration counter
(`SettingsMigrator.CurrentVersion`) — bump that only when a settings key changes shape, not
on every release.
