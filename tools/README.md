# tools/

Developer tools. None of this ships in the installer.

| Tool | What it is | How to run |
|---|---|---|
| `ProsimCompanion.Reduce/` | .NET console that reduces a diagnostics bundle (Logs page → *Export diagnostics*) or a session recording to a report against the probe catalog `docs/agents/verification-probes.json`. Part of the solution; its tests live in `tests/ProsimCompanion.Core.Tests`. | `dotnet run --project tools/ProsimCompanion.Reduce -- --bundle <zip-or-folder>`; CLI, report schema and verdict semantics in `docs/agents/support-bundle.md` |
| `crash-hunt/` | PowerShell scripts for a hard crash with no log line: read the exception and the native stack out of a WER minidump, enable page heap on the sim PC. | see `crash-hunt/README.md` |
| `build-atlas.js` | Node script that builds the offline atlas for the FO's "where are we" answers (#153) from Natural Earth + GeoNames into `src/ProsimCompanion.Core/Geo/Data/atlas.json.gz`. The four source files (`countries.geojson` NE 110m admin_0, `marine50.geojson` NE 50m marine polys, `regions50.geojson` NE 50m geography regions, `cities15000.txt` GeoNames) must sit beside the script and are not committed. | `node tools/build-atlas.js` (Node 22) — the output path is fixed by design |
| `SkalarkiSpike/` | The 2026-10-27 throwaway console that proved the SkalarkiIO Profiler SDK shape before ADR-0017 (presence event, `Outputs.All` stack overflow, load-context design). Not in the solution; needs the user's SDK files in the git-ignored `libs/skalarki/`. Kept as the primary source for the integration doc. | `dotnet run --project tools/SkalarkiSpike` with `libs/skalarki/` present |
