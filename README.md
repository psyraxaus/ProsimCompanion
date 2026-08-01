# ProsimCompanion

One companion app for the ProSim A320 (A322) in MSFS 2020/2024 — consolidating
**Prosim2GSX** (GSX ground-services automation), **ProsimInterface** (ProSim connectivity, W&B,
loadsheets), and **Prosim2FO** (voice First Officer) into a single, modern application.

- **Web-first**: a small WPF window starts the app; everything else — configuration, EFB, status —
  runs in your browser (Blazor, served on the LAN, default `http://localhost:5320`).
- **Degrades gracefully**: runs with or without ProSim, MSFS, GSX, or any optional service.
- **.NET 10**, Generic Host, single process, no external frameworks.

## Status

Early development — Phase 0 (foundation scaffold) complete. See [docs/ROADMAP.md](docs/ROADMAP.md)
for the plan and [docs/feature-inventory.md](docs/feature-inventory.md) for migration parity.

## Building

```
dotnet build ProsimCompanion.slnx
dotnet test  ProsimCompanion.slnx
dotnet run --project src/ProsimCompanion.App
```

Requires the .NET 10 SDK on Windows. `ProSimSDK.dll` is loaded at runtime from your ProSim
installation and is never bundled.

## Documentation

- [Architecture](docs/ARCHITECTURE.md)
- [Roadmap](docs/ROADMAP.md)
- [Architecture decision records](docs/decisions/)
- [Integration references](docs/integrations/) — ProSim, GSX, SimBrief, SayIntentions, audio,
  speech/AI
