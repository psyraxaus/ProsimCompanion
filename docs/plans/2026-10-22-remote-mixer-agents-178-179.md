# Plan: several VoicemeeterBridge agents (#178) + two-headset guidance and shared-target warnings (#179)

**Status 2026-10-22: packages A–D built on this branch (three commits after this plan);
4939 tests green; package E (live checks 2–4) still owed. Deviation from the plan:
`TidyAgents` never re-points rows (only the page's explicit rename does), so a duplicate
name's suffix cannot move a row to the wrong PC.**

Branch `feature/remote-mixer-agents`, cut 2026-10-22 from `refactor/deepening-2026-10` @ 5e36b4a
(0.7.0-rc.11). The rc.11 refactors are **unmerged and unverified live**; this branch stacks on
them, so it ships after (or together with) the deepening branch.

Issues: [#178](https://github.com/psyraxaus/ProsimCompanion/issues/178) (enhancement) and
[#179](https://github.com/psyraxaus/ProsimCompanion/issues/179) (documentation + enhancement).
Both came from Guenther's support thread 2026-10-18 → 10-22 (two-pilot rig: main PC with
Banana, one PC per pilot with Potato + ProSim Audio 2; INT cannot be split from VHF on the
main PC because both land on the same headset bus).

## What exists today (read 2026-10-22)

| Piece | Where | Shape |
|---|---|---|
| Options | `Core/Configuration/MixerOptions.cs` | one `Host`/`Port`/`Token`, section-wide reconnect/timeout/gain span, `PanelStrips`/`PanelBuses`, `Mappings` (`MixerMapping`: Acp, Channel, StripIndex, IsBus, UseLatch, Enabled) |
| Secret | `Core/Configuration/SecretProtector.cs` | `mixer:token` in `SecretPaths`; wildcard `section:list:*:field` already supported (notifications precedent) |
| Client | `Audio/Mixer/MixerClient.cs` (713 lines) | one `BackgroundService` = one socket, backoff, watch list, set/result, channel labels, probe; writes `ConnectionStatusStore` (`Subsystems.Mixer`) and `MixerStatusStore` |
| Mapping | `Audio/Mixer/MixerMappingService.cs` | one coalescer, bindings keyed by parameter name, flush → `IMixerClient.SetAsync` |
| Store | `Core/State/MixerStatusStore.cs` | `MixerStatusSnapshot` (one state, one endpoint, rows) |
| Web | `Web/Pages/AudioSettings.razor` section `mixer`; `Web/Components/MixerStatusPanel.razor`; `Web/Settings/AudioPageRules.cs` | connection card + Test, status-panel tick boxes, mapping rows with target picker `strip:n` / `bus:n` |
| Footer | `Web/Layout/MainLayout.razor` | one `Mixer` dot, hidden while Disabled |
| Docs | `docs/manual/09-remote-mixer.md`, `docs/integrations/voicemeeter-bridge.md`, `docs/manual/04-settings-reference.md` row `mixer` | single agent everywhere |
| Tests | `tests/.../Mixer/MixerClientTests.cs` + `FakeBridgeServer.cs`, `MixerMappingMathTests.cs`, `Web/SettingsPageRulesTests.cs`, `Web/SettingsPagesRenderTests.Pages.cs` (mixer defaults/configured), `Web/OptionFieldCompletenessTests.cs` (exempts `MixerMapping.*`) | |

Precedents to copy: **Notifications targets** (a list of items with per-item secrets, per-item
`<OptionField Item ItemOf>` and `<SecretUnreadableHint Path="notifications:targets:{i}:token">`),
and **wake-on-LAN** (`BriefingOptions.LlmWakeOnLan`: the legacy single value stays readable, the
page moves it into the list on its first visit).

## Decisions taken in this plan

1. **Agents are named.** `MixerMapping.Agent` is the agent's `Name` (string). Empty name =
   the first agent, so every existing mapping row keeps working without a rewrite. Renaming an
   agent on the page re-points its mapping rows in the draft. A name no mapping can find shows
   `no_agent` in the row's Live column and binds nothing.
2. **Legacy `host`/`port`/`token` stay readable for one release** (as the issue asks), no
   `SettingsMigrator` bump. `MixerOptions.EffectiveAgents()` returns `Agents` when non-empty,
   else one agent built from the legacy fields (name "Mixer PC"). The Audio page moves the
   legacy values into `Agents[0]` on first visit (wake-on-LAN precedent) and clears them.
   Remove the legacy properties in the release after.
3. **One agent = today's page.** With a single agent the target picker shows `Strip 3 — vPilot`
   exactly as now; the `PC1 · ` prefix appears only with two or more agents. Screenshots in
   chapter 9 stay valid for the single-agent case (retake at the RC anyway, owner rule).
4. **Strips/buses for the status card move into the agent** (`MixerAgentOptions.PanelStrips`
   / `PanelBuses`): the inventory is per Voicemeeter, so the tick boxes belong under each
   agent's connection block. Legacy section-level lists migrate into agent 0 with the host.
5. **Footer keeps one `Mixer` dot**, aggregated: Connected when every enabled agent is
   connected; Connecting when none is down but one is still connecting; Disconnected when
   any enabled agent is down (reason "1 of 2 agents connected"); Disabled when the feature is
   off or no agent is enabled.
6. **#179's "which strip does the audio really arrive on" meter is out of scope.** The bridge
   protocol v1 has no level read; the minimum the issue asks for (hint + shared-target
   warning) ships here. A level meter is a follow-up issue if wanted (needs a protocol v2
   `level` op in VoicemeeterBridge).
7. **No probe, no replay.** Nothing here changes flight behaviour; the verification is unit
   tests over two fake agents plus a live check with one real agent (list at the end).

## Work packages

### A. Core: options, secrets, rules (no UI yet)

1. `MixerAgentOptions` (new, `Core/Configuration/MixerOptions.cs`):
   `Name` (Text), `Enabled` (Bool, default true), `Host` (Text, Host kind), `Port` (Number,
   5088), `Token` (Text, Password), `PanelStrips`, `PanelBuses` (lists, exempt in the
   completeness test like `MixerMapping.*`). Every scalar carries `[OptionField]` with ids
   under `audioSettings.mixer.agent.*` (new text ids). Rule: no identifier containing `key`.
2. `MixerOptions`: add `List<MixerAgentOptions> Agents = []`; keep `Host`/`Port`/`Token`/
   `PanelStrips`/`PanelBuses` **without** `[OptionField]` (completeness exemption named
   "legacy single agent, moved into agents[0] by the Audio page"); add `EffectiveAgents()`
   (pure, tested) and `HasLegacyAgent`.
3. `MixerMapping.Agent` (string, default ""). `MixerMappingStatus` gains `Agent`.
4. `SecretProtector.SecretPaths`: add `"mixer:agents:*:token"`; keep `"mixer:token"`.
   Test: a two-agent JSON gets both tokens protected; `ExpandSecretKeys` lists
   `mixer:agents:0:token`, `mixer:agents:1:token`.
5. `AudioPageRules`:
   - `MigrateLegacyAgent(MixerOptions)` → bool (moved something): builds agent 0 from the
     legacy fields + panel lists, clears them. Called once when the draft loads.
   - `BeforeSave`: trim agent names; empty name → "Mixer PC n"; duplicate names → suffix
     " (2)"; per-agent panel lists distinct + sorted; a mapping whose `Agent` names no agent
     is kept (the Live column says why) — never silently re-pointed.
   - `RenameAgent(MixerOptions, oldName, newName)`: re-points mapping rows.
   - `SharedTargets(IEnumerable<(string Agent, bool IsBus, int Index, AcpSide Acp, AudioChannel Channel)>)`
     → the targets driven by two or more rows, with the rows' labels (pure; used by both
     editors for #179's warning).
6. `MixerStatusSnapshot` → `Agents: IReadOnlyList<MixerAgentStatus>` (Name, Endpoint, State,
   Voicemeeter, LastError, ConnectedSinceUtc, FailedSets) + `Mappings` (rows carry Agent).
   Keep `Enabled`. Helper `WithAgent(name, Func<MixerAgentStatus, MixerAgentStatus>)`.

### B. Audio: one client per agent, one host, routed mappings

1. `MixerClient` becomes a **per-agent session** (not a hosted service): constructor takes
   `(string name, Func<MixerAgentOptions?> agent, Func<MixerOptions> section, MixerStatusStore, IWireTrace, ILogger)`;
   `RunAsync(CancellationToken)` is today's `ExecuteAsync` loop; `IMixerClient` gains
   `string Name { get; }` and `MixerAgentStatus Status { get; }`. It updates **its own** agent
   entry in the store, never `ConnectionStatusStore`. `ProbeAsync` moves to a static
   `MixerProbe.ProbeAsync(host, port, token)` (no session state involved).
   Logs gain the agent name: `Mixer [PC1] connection: Connected` (keep the existing words so
   the integration note's "log lines to look for" stays true with the prefix added).
2. `MixerClientHost : BackgroundService` (new) — the single hosted service:
   - watches `IOptionsMonitor<MixerOptions>`; per `EffectiveAgents()` entry with
     `Enabled && Host != ""` keeps one `MixerClient` running under its own linked token;
     adds/removes/restarts on change (compare name + host + port + token + enabled);
   - implements `IMixerClients` (Core): `IReadOnlyList<IMixerClient> All`,
     `IMixerClient? Find(string name)` (empty name → first), `event ClientsChanged`;
   - aggregates the footer dot (`Subsystems.Mixer`) per decision 5;
   - disposes removed clients (today's `Dispose` releases `_sendLock`).
3. `MixerMappingService`: bindings resolve their client through `IMixerClients.Find(mapping.Agent)`;
   one `LatestValueCoalescer` **per agent** (parameter names collide across PCs); flush and
   reconnect-replay run per agent; a mapping with no client gets `LastResult = "no_agent"`
   and no subscription. Subscribes to `ClientsChanged` to rebind when the host adds a client.
4. DI (`AudioServiceCollectionExtensions`): register `MixerClientHost` as singleton +
   hosted + `IMixerClients`; drop the `IMixerClient` singleton. The composition guard test
   (`ServiceCompositionTests`) proves nothing still asks for `IMixerClient`.
5. Tests (xunit + Moq, two `FakeBridgeServer`s):
   - host starts one client per enabled agent; disabling one disposes only that client;
     a changed token restarts only that client;
   - **acceptance**: captain VHF1 → agent A, FO VHF1 → agent B: a captain knob move lands
     only on server A, an FO move only on server B;
   - **acceptance**: server B aborted → A's sets keep landing, B's rows read `not_connected`,
     B reconnects on its own backoff, no await on B in A's flush;
   - legacy config (host/port/token, no agents) connects exactly as before;
   - footer aggregation table (all / one down / none enabled);
   - existing `MixerClientTests` re-targeted at the per-agent client (same assertions).

### C. Web: settings page, status card, footer, seven language packs

1. **Remote Mixer section** (`AudioSettings.razor`, section `mixer`):
   - card 1: `Enabled` + advanced section-wide fields (unchanged);
   - one `<Section Title="PC1">` per agent (Notifications-targets layout): `Name`, `Enabled`,
     `Host`, `Port`, `Token` + `<SecretUnreadableHint Path="mixer:agents:{i}:token">`,
     **Test connection** (per agent, `MixerProbe`), live pill (that agent's status),
     **Remove**; the status-panel tick boxes / text boxes + **Reload channels** move under
     the same section (per agent inventory). **Add agent** button below the last section;
   - on draft load: `MigrateLegacyAgent` → mark the draft dirty so the user sees a Save;
   - mapping rows: the target picker's entries become `{agent}|strip:n` / `{agent}|bus:n`
     over every enabled agent's inventory (generic 8/8 for an agent not yet connected);
     label `PC1 · Strip 3 — vPilot` only when more than one agent exists (decision 3);
     `AddMixerMapping` defaults the first agent;
   - under the mapping table: **shared-target warning** (`AudioPageRules.SharedTargets`)
     — "Bus A2 is driven by Captain VHF1 and First Officer VHF1. Each headset needs its own
     bus." (warn tone, nothing blocks Save; #179 item 2).
2. **VoiceMeeter section** (local backend, #179 item 2): the same shared-target warning
   over `_audio.Value.VoiceMeeterMappings` (across ACPs, the validator's own rule but shown
   while editing, not only after a failed bind), plus a new hint under the tab:
   "Per-headset volume needs a bus per headset; a shared strip cannot have two volumes." with
   a More fold that names manual chapter 11.
3. **MixerStatusPanel**: one group per agent (name pill + state + Voicemeeter + endpoint +
   failed writes), rows under it; `EnsureWatched` per client; `Client.TryGetValue` through
   `IMixerClients.Find(agent)`.
4. **MainLayout footer**: no change in markup; the host writes the aggregate state. Tooltip
   reason comes from `ConnectionStatusStore` as today.
5. **Language packs** (`config/lang/{en,de,es,fr,ja,ko,nl}/audioSettings.json`), all seven in
   the same change (owner rule 2026-10-13): new ids `audioSettings.mixer.agent.*` (name,
   enabled, host, port, token labels/hints, test, remove, add, sectionTitle "{0}"),
   `audioSettings.mixer.agents.count` "{0} of {1} agents connected",
   `audioSettings.mixer.mappings.noAgent`, `audioSettings.mixer.mappings.sharedTarget`
   "{0} is driven by {1} and {2}. Each headset needs its own bus.",
   `audioSettings.voiceMeeter.perHeadset` + `.perHeadsetMore`,
   `audioSettings.remoteMixer.agent` texts. Run the localisation guard
   (`dotnet test tests/ProsimCompanion.Core.Tests --filter "FullyQualifiedName~Localization"`)
   and the leftover-literal grep from `docs/agents/localisation.md`.
6. Render tests: `SettingsPagesRenderTests.Pages.cs` mixer "configured" with two agents and
   a legacy-only config; a `MixerStatusPanel` render with two agents; `SettingsPageRulesTests`
   for every new `AudioPageRules` rule; `OptionFieldCompletenessTests` passes with the new
   exemptions.

### D. Docs

1. `docs/manual/09-remote-mixer.md`: 9.3 becomes "Connect the app — one block per agent";
   new **9.7 Two pilot PCs**: Captain INT → PC1 VAIO strip, Captain VHF1 → PC1 VBAN strip,
   First Officer the same on PC2; what the status card shows; the one-down behaviour.
2. New `docs/manual/11-two-headsets.md` (#179 item 1): the reference layout that worked for
   Guenther — Banana on the main PC, ProSim Audio 2 CPT/FO groups → VAIO/AUX, online client
   on a cable into a hardware strip, knobs on Bus A2/A3; Potato on each pilot PC with VBAN
   into a device-less strip; an ASCII wiring diagram like chapter 8; **the INT caveat**
   (needs a spare bus per pilot on the main PC, or the Remote Mixer per pilot PC → 9.7);
   **the VHF2 explanation** (IVAO Altitude, vPilot and most clients emit COM1+COM2 as one
   stream, nothing to put on a second bus — not planned); "a shared strip has one fader".
   Link from 8.1, 9.5 and the README table.
3. `docs/integrations/voicemeeter-bridge.md`: JSON block with `agents[]`, `mappings[].agent`,
   legacy fallback rule, the `[name]`-prefixed log lines.
4. `docs/manual/04-settings-reference.md` row `mixer`; `docs/feature-inventory.md` line;
   `CHANGELOG.md` under Unreleased (two entries: #178 feature, #179 guidance + warnings).
5. Screenshots `docs/img/remote-mixer.png`, `remote-mixer-status.png`: retake with two agents
   at the RC (owner rule: every RC retakes all screenshots) — note as owed in the memory.

### E. Verify

1. `dotnet build` + `dotnet test` (whole suite, 4911+ tests); Snyk-clean names (no `key`).
2. Live, one real agent (the existing bridge box): legacy settings.json loads, page shows one
   agent block pre-filled and dirty, Save writes `agents[0]` with a `dpapi:` token, knob works
   as before, footer dot unchanged.
3. Live, two agents: second VoicemeeterBridge on any spare PC (or a second instance of the
   agent on another port on the same PC): both pills green, captain row → agent 1 only,
   FO row → agent 2 only, kill agent 2 → agent 1 keeps working, dot turns amber with
   "1 of 2 agents connected", agent 2 back → green within the backoff.
4. Ask Guenther to confirm on the real two-pilot rig (the acceptance in #178).

## Order and size

A → B → C → D can run as one agent in sequence (shared types), or A first, then B and C in
parallel worktrees once A's types compile, then D. Rough size: ~1 500 lines changed
(MixerClient split is the largest piece), seven pack files, two manual chapters.

## Triage

Both issues are `needs-triage`. On start: label `ready-for-agent`, comment with this plan's
path. On ship: close both from the release notes. GitHub MCP token was expired on
2026-10-22 (and `gh` is logged out) — re-authorise before closing.
