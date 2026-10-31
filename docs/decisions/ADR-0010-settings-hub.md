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

## Amendment 2026-11-01 — hub usability (issue #199)

The hub had grown past what one rail could hold: the Voice First Officer page alone was
sixteen rail items over fifty-five cards, and the rail (`position: sticky` with no height
cap) could only be reached by scrolling the page. Owner request, 2026-11-01:

7. **Two scroll areas, one page height.** `.settings-panel` is exactly `.main`'s height; the
   rail and the content column scroll on their own. The heading, the search box and the
   breadcrumb never move.
8. **Search over the pack.** A box at the top of the rail matches the language pack's texts
   (`SettingsSearch`): every second-level block of a page's pack file is a card, a titled
   third-level block is its own card, and `BlockSections` names the blocks whose name is not
   their section. A source-scanning test keeps that table true to the Razor pages. No markup
   is parsed at runtime, and nothing is hidden by a search — it only points.
9. **Groups follow the reader, not the project.** One page route may sit under several rail
   groups: `settings/speech/…` is *Voice First Officer* (who the FO is), *Flight Deck* (what
   they do at the controls and on the radios) and *Crew & Company* (the other voices).
   `SettingsNav.GroupFor` prefers the group that lists an item over the one whose base path
   prefixes it. Addresses and pack ids did not move.
10. **Cards fold per browser.** Every `Section` has a chevron; the state is this browser's
    (localStorage through `SectionFoldStore`), never a setting. A folded card's fields stay
    rendered and hidden so validity still counts. `DefaultFolded` gives a card most installs
    never touch a closed first look (the Setup page's rarely used cards). *Collapse all /
    Expand all* per page.
11. **The advanced fold is for numbers nobody retunes.** Twenty more numeric knobs (delays,
    windows, thresholds, chances) moved behind *Show advanced settings*; airline-policy
    figures (limits, fuel policy, placards) stay in the open.
12. **The heading is a breadcrumb of links** (Settings › group › section).
