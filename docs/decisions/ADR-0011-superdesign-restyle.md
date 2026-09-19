# ADR-0011: Restyle the web UI to the Superdesign "KLM navy instrument panel" design

- **Status:** accepted (owner decision, 2026-09-20)
- **Supersedes:** the Prosim2GSX web-EFB look recreated in Phase 2.5 (`docs/ROADMAP.md`)

## Context

The web UI's look was a faithful recreation of the Prosim2GSX web EFB: flat navy cards, system
fonts, a horizontal tab strip and Solari split-flap displays in the header. The owner designed
a new look on the Superdesign canvas (project "A320 Flight Bag App"): a KLM-navy "instrument
panel" language — gradient bevelled cards with deep shadows, JetBrains Mono figures with a cyan
glow for live data, Public Sans caps labels, gold as the warm highlight, an icon sidebar and a
status footer, and a 13-theme airline selector. Five pages were designed there (Flight Status,
OFP, W&B, Performance, Checklists); the drafts use Tailwind, Iconify and Google Fonts from CDNs
and store the chosen theme in `localStorage`.

## Decision

1. **Adopt the visual language, keep our shell structure.** Tokens, fonts, card, button, pill,
   input and tab styling follow the canvas. The navigation stays our horizontal tab strip plus
   the settings hub (ADR-0010) — the canvas's 4-item icon sidebar does not fit our 11 pages on an
   iPad in landscape. A status footer with real connection signals replaces the canvas's fake
   CPU/MEM/wifi/battery widgets; the tactical radar and ND map are dropped (no data source).
2. **Split-flap stays, on the clock only.** The Solari split-flap header displays (FLT NO, SIM/UTC
   time, date) are kept; numeric figures elsewhere are plain glowing mono digits, as on the
   canvas.
3. **Keep our W&B instruments.** `CgEnvelope` and `A320Silhouette` (pixel ports of Prosim2GSX)
   are kept as-is inside the new cards.
4. **No CDN, no Tailwind.** The app runs on a sim PC and an iPad that may be offline, and
   ADR-0002 rules out a Node toolchain. JetBrains Mono and Public Sans are bundled as woff2
   under `wwwroot/fonts` (SIL OFL, recorded in `THIRD_PARTY.md`); icons are inline SVG; the
   Tailwind utility classes of the drafts are hand-ported into `app.css` and the page sheets.
5. **Themes are replaced, not added.** The 7 legacy built-in themes (Default, Dark, Light, Delta,
   Finnair, Lufthansa, Qantas) are replaced by the canvas's 13 (KLM Royal Dutch, Lufthansa,
   Swiss International, British Airways, Air France, Singapore Airlines, Emirates, Qatar
   Airways, United Airlines, Qantas, Finnair, Light, Dark). The default is KLM Royal Dutch; the
   name "Default" is kept as an alias so existing settings files resolve. Themes stay server-side
   in `settings.json` via the Display settings page (owner rule: every option has a web control)
   — not in `localStorage`. The Prosim2GSX theme JSON schema is unchanged: the canvas tokens map
   `--bg-cockpit → contentBackground`, `--bg-panel → sectionBackground/headerBackground/
   tabBarBackground`, `--accent-cyan → primaryColor`, `--accent-gold → accentColor`,
   `--text-primary → contentText/headerText`, `--text-dim → categoryText`. `ThemeCssBuilder` now
   also emits `--accent-gold`, `--accent-gold-soft` and `--bg-inset`; the canvas themes that put
   light text on light panels (Emirates, Qatar, Qantas) are corrected so every card stays
   readable.

## Delivery

Three phases, each shippable: **reskin** (tokens, fonts, cards — all pages), **shell** (header,
tab strip, footer), **pages** (Flight Status, OFP, W&B, Performance, Checklists rebuilt to the
designed layouts). Pages without a design (INIT, Loadsheet, Fuel, Tech Log, Duty Day, Settings)
receive the new look through the shared tokens and cards only.

## Consequences

- Users' predecessor theme files still load, but they were authored for a lighter, flatter
  language; they render coherently (derivation rules unchanged) yet may look off-brand.
- `--warning` moves from `#ff9800` to the canvas amber `#ffb703`, `--success` to `#10b981`,
  `--danger` to `#ef4444`; page sheets keep their own instrument colours (INIT cyan/green,
  perf amber/red) which were always separate from the theme.
- The Superdesign init snapshot lives in `.superdesign/init/` (untracked); the canvas is the
  design source of record for further iterations.
