# ADR-0013: An optional HTTPS listener beside HTTP, for secure-context browser features

Date: 2026-10-03
Status: proposed (awaiting owner OK — October 2026 batch, package 6, issue #150)
Amends: ADR-0001 (the web server gains a second, optional endpoint; the lockout rule is
unchanged).

## Context

The tablet EFB is opened over `http://<sim-pc-ip>:5320`. Two things the tablet needs are
behind the browser's **secure context** rule: the Screen Wake Lock API (keep the screen on
during a flight) and a proper installed web app (standalone display from the home screen with
the Apple web-app meta tags honoured). `http://localhost` qualifies as secure; `http://<lan
ip>` does not — so on the tablet the wake lock simply does not exist, and nothing in the app
keeps the screen awake today.

`Program.cs` binds exactly one URL, `http://{host}:{port}`, from `webUi.port` /
`webUi.bindToAllInterfaces`. Those two and the access token are the WPF-resident settings of
ADR-0001: if the web configuration is broken, the desktop window can still fix it.

Browsers trust an HTTPS site only when the certificate chains to a CA the device trusts. On a
home or sim LAN that means an **internal CA installed on the tablet** (mkcert, a Windows CA, or a
self-signed root) and a leaf certificate for the sim PC's hostname or IP. No public CA will
issue for a private IP.

## Decision

1. **A second Kestrel endpoint, HTTPS, optional, off by default.** HTTP stays exactly as it is
   and keeps the port 5320 default. HTTPS defaults to **5321** on the same bind address
   (loopback, or all interfaces when LAN access is on). Both endpoints serve the same app, the
   same middleware, the same token rule.

2. **The certificate is the user's.** `webUi.https.pfxPath` names a PKCS#12 file;
   `webUi.https.pfxPassword` is stored DPAPI-protected like every other secret
   (`SecretProtector` gains `webUi:https:pfxPassword`). The app **never generates, requests,
   renews or installs** a certificate, and never touches the Windows certificate stores. The
   manual shows one way to make one (mkcert) and how to put the CA on an iPad.

3. **Settings live in `settings.json` and on the web Setup page (Web Interface card) — not in
   WPF.** ADR-0001 keeps bind/port/token in the desktop window because a broken value there
   locks the user out of the only UI. HTTPS cannot lock anyone out: if it fails, HTTP is still
   there, on the same port as before. So the WPF shell gains **no new controls**; it only
   *reports* (status line: "HTTPS on 5321" / "HTTPS off" / "HTTPS failed — see Settings") and
   the QR code adapts (point 5). Changes take effect on restart, like the port.

4. **Failure degrades to HTTP-only, loudly.** At startup the PFX is loaded before the listener
   plan is built (`WebListenerPlan`, pure, tested):
   - file missing, unreadable, wrong password, no private key → **no HTTPS endpoint**, HTTP
     unchanged, a warning in the log, and a `ConfigProblemStore` entry
     (`webUi.https`) so the existing config-problem banner shows the reason on every page;
   - certificate **expired** (or not yet valid) → the listener **starts anyway** (the file is
     valid, the browser will warn and the user may accept it) and the same banner says
     "expired on …, browsers will warn";
   - HTTPS port already in use → the endpoint fails at bind time; Kestrel's exception is caught
     for that endpoint only, HTTP keeps serving, banner;
   - `enabled` true with an empty path → treated as "not configured", banner.
   Whether HTTPS actually came up is published in a small `WebListenerStatus` store
   (HTTP URL, HTTPS URL or failure reason) read by the WPF window and the Setup page.

5. **The QR code offers the HTTPS URL when the HTTPS listener is up**, otherwise the HTTP URL
   as today. The hint text under the QR lists both addresses when both exist. The onboarding
   `?token=` flow is unchanged (same cookie, same middleware); a device that scanned the HTTPS
   QR gets its cookie on the HTTPS origin. Cookies are per origin, so the same tablet must be
   onboarded once per scheme — the hint says so.

6. **The token middleware allow-lists only the install assets**, by exact path:
   `/manifest.webmanifest`, `/apple-touch-icon.png`, `/apple-touch-icon-180.png`,
   `/icons/*.png` (the committed PNG set), `/favicon.svg`. They are public branding with no
   data in them; iOS fetches some of them without cookies during "Add to Home Screen". Nothing
   else changes: every page, API and other static file still needs the cookie, bearer or
   `?token=`.

7. **No service worker, no offline mode.** Blazor Server needs its live circuit; an offline
   shell would show a dead page. The manifest is for the home-screen install and the standalone
   display only, and the manual says so.

8. **Keep-awake is a per-device choice** (localStorage), not a server setting: whether a given
   tablet's screen should stay on is that tablet's business. The Appearance page shows the live
   state — active / not supported / needs HTTPS — and links to the HTTPS setup.

## Consequences

- One more port to open on the sim PC's firewall when LAN access is on (5321 by default).
- A tablet that trusts the internal CA gets the wake lock and the standalone app; a tablet that
  does not sees a certificate warning and, after accepting it, a page that still works — the
  wake lock then depends on the browser (Safari grants secure-context APIs on a user-accepted
  certificate; Chrome does not always). The manual leads with installing the CA.
- The certificate's subject alternative names must include whatever the tablet types or the QR
  carries — the LAN **IP** by default. A new DHCP lease breaks the match; the manual recommends
  a DHCP reservation or a hostname SAN plus mDNS.
- Nothing in this ADR touches the aircraft, ProSim or GSX; it is web-host plumbing only.
- Alternatives rejected: a self-generated self-signed certificate (every browser warns, iOS
  needs the CA anyway, and the app would own a key it should not); Windows certificate-store
  lookup by thumbprint (works for Windows CA shops but adds a second configuration path and a
  store dependency — can be added later if asked); binding HTTPS only (breaks every existing
  bookmark and the lockout rule).

## Open points for the owner

- Default HTTPS port 5321 — fine, or something else?
- QR offers HTTPS when up (point 5) — or always HTTP with HTTPS only in the hint text?
- Expired certificate: start anyway + banner (point 4) — or refuse and stay HTTP-only?
