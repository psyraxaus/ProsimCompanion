# ADR-0010: One settings hub with progressive disclosure

- **Status:** accepted (owner decision, 2026-09-19)
- **Supersedes:** the 2026-08-08 "one tab per pillar" strip layout (#18)

## Context

By 0.3.0-beta.30 the web UI carried 17 top-level tabs mixing three jobs — fly-the-flight
pages (INIT, OFP, W&B …), settings pages (GSX, Audio, First Officer, App Settings, Flight
Phase, Aircraft Profiles) and diagnostics (Logs). The settings pages held about 257 fields
with no beginner/expert split: reconnect ceilings in milliseconds sat next to "connect the
jetway". The three pillar master switches were buried (the GSX one at the bottom of
"Connection & Timeouts"), two settings were editable from two pages (cabin chimes,
SayIntentions), section selection was in-page state so refresh / Back / banner links could
not target a section, and the hints doubled as the manual in 10 px text. Testers reported
the app "too much to handle".

## Decision

1. **Two navigation tiers.** The tab strip carries only the fly-the-flight pages plus one
   right-aligned *Settings* tab. Every configuration page hangs off a single settings hub
   with its own rail (`SettingsNav`), grouped by pillar: Setup · Ground Services · Voice
   First Officer · Audio Control · Display & Flight Data · Aircraft Profiles · Advanced
   (Flight Phase Engine, Logs).
2. **Setup is the landing page.** The three pillar master switches, the ProSim connection,
   nav data, the web interface and the command API live there — the things every install
   needs, first.
3. **URL-driven sections.** Each section of a page is a route (`settings/gsx/doors`), so a
   refresh, the Back button and links from banners land on the exact cards. The rail
   lives once in `SettingsLayout` (a nested layout); pages only render their cards.
4. **One home per setting.** A setting is editable on exactly one page. Cross-references
   are links, never a second control bound to the same key.
5. **Progressive disclosure.** Tuning constants (timeouts, intervals, VAD thresholds,
   phase-engine settle times) are marked advanced and hidden until *Show advanced settings*
   is switched on; the rule that every option ships with a web control (CLAUDE.md) still
   holds — the control exists, it is simply folded. Child fields are disabled while their
   parent is off. Min/max delay pairs are one row. Numeric fields show their default and
   can be reset.
6. **Plain hints.** A hint is one short sentence in the user's words; LVAR/dataref/issue/ADR
   names and JSON keys stay out of it. Longer explanations fold under "More".

## Consequences

- Old routes (`/gsx`, `/audio`, `/speech`, `/profiles`, `/logs`, `/settings/app`) still
  resolve so bookmarks and the manual keep working.
- The GSX master switch is part of the per-profile `gsx` block; the Setup page mirrors into
  the active profile when it changed, exactly as the GSX pages do.
- Flight Phase Engine is deliberately under *Advanced*: its fields are calibration data, not
  preferences.
- The `feature/tablet-efb-surface` branch (the `/efb` shell) will need a small merge against
  `MainLayout` and `app.css`.
