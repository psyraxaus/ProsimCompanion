# Versioning & releases

One number, one place: **`<Version>` in `Directory.Build.props`**. Everything else derives
from it — never version anything separately.

## Who consumes the version

| Consumer | How |
|---|---|
| Assemblies | MSBuild stamps `AssemblyVersion` / `FileVersion` / `InformationalVersion` from `<Version>` |
| Update banner | `UpdateCheckService` compares the assembly informational version against the latest GitHub release tag (3-component compare, `v` prefix and `+metadata`/`-prerelease` suffixes stripped) |
| Installer | `installer/build-installer.ps1` reads `Directory.Build.props` and names the output `ProsimCompanion-Setup-<version>.exe` |
| Web UI | the banner shows "running X" next to "latest Y" |

## Scheme

Semantic versioning, three components, pre-1.0 rules:

- **0.x.0 (minor)** — new features, new pages, new pillars, parity batches. The normal bump.
- **0.x.y (patch)** — fixes only, no new settings keys.
- **1.0.0** — first release after a full sim-verified pass of every "Unverified live" marker.
- After 1.0: **major** = breaking `settings.json` shape (a `SettingsMigrator` step is then
  mandatory), **minor** = features, **patch** = fixes.

Pre-release builds may use `-beta.N` suffixes (`0.2.0-beta.1`); the update check treats a
pre-release as equal to its release (3-component compare), so a beta user is offered the
final release only when the number itself grows.

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
