# 7. The app on a tablet: HTTPS, home screen, keep-awake

The EFB works in any browser over `http://<sim-pc>:5320`. Two things need more: **keeping
the screen awake** during a flight, and running as an **installed app** without the browser
bars. Both need the page to be *secure* — `https://` — because browsers only hand those
features to secure pages (`http://localhost` counts on the sim PC itself; `http://192.168…`
on a tablet does not).

There is **no offline mode**. The app is a live link to the sim PC; an installed icon just
opens that link full-screen. Nothing is cached for use without the PC.

## 7.1 Make the sim PC trusted: a certificate from your own CA

The app never makes or installs certificates — you supply one, and the tablet must trust the
authority that signed it. The easiest way is **mkcert** (free, open source):

1. On the sim PC, install mkcert (`winget install FiloSottile.mkcert`, or the release zip).
2. Make a local CA: `mkcert -install` (this trusts it on the sim PC — fine, optional).
3. Make the certificate for the address the tablet will use. The LAN IP is what the QR code
   carries, so include it; add a hostname if you have one:
   `mkcert -pkcs12 -p12-file C:\ProsimCompanion\certs\simpc.pfx 192.168.1.20 simpc.local`
   mkcert's PFX password is `changeit`.
4. Give the sim PC a **DHCP reservation** (router settings) so the IP in the certificate does
   not change on the next lease.

A Windows CA, or any other CA, works the same way: export the leaf certificate **with its
private key** as a PFX, and have the CA's root certificate ready for the tablet.

## 7.2 Turn on HTTPS in the app

Settings → Setup → **Web Interface → HTTPS (optional)**:

- **Enable HTTPS**: on.
- **HTTPS port**: 5321 (open it on the sim PC's firewall like 5320).
- **Certificate file (PFX)**: the file from 7.1.
- **Certificate password**: `changeit` for mkcert. It is stored encrypted on this PC.

Save, restart the app. The **Status** pill reads `HTTPS ON` with the address; the desktop
window's QR code now carries the HTTPS address, and the hint under it lists the plain HTTP
address too. If the pill reads `HTTPS FAILED`, the reason is next to it and in the banner at
the top of every page: file not found, wrong password, no private key. HTTP keeps working
whatever happens to HTTPS, so you can never lock yourself out. An **expired** certificate
still answers, with a warning — renew it.

## 7.3 Trust the CA on the iPad

The tablet must trust mkcert's root, or Safari shows a warning page and refuses the secure
features.

1. Find the root: on the sim PC run `mkcert -CAROOT`; the file is `rootCA.pem` in that folder.
2. Get it onto the iPad — AirDrop, email to yourself, or a file share. Open it on the iPad
   and tap **Allow** when iOS asks to download a configuration profile.
3. **Settings → General → VPN & Device Management** → the profile → **Install**.
4. **Settings → General → About → Certificate Trust Settings** → switch on full trust for the
   mkcert root. (Without this step the profile is installed but not trusted.)

Android / Chrome: Settings → Security → Encryption & credentials → Install a certificate →
CA certificate → pick `rootCA.pem`.

## 7.4 Open the app over HTTPS and sign in

Scan the QR code in the desktop window (it carries the HTTPS address and the sign-in token),
or type `https://192.168.1.20:5321/?token=…`. Each address — HTTP and HTTPS — signs in
separately; a tablet that used the HTTP address before scans once more for HTTPS. The
address bar shows the padlock; if it does not, go back to 7.3.

## 7.5 Add to Home Screen (iPad)

1. Open the HTTPS address in **Safari** (not Chrome — iOS only installs from Safari).
2. Tap **Share** (the square with the arrow), then **Add to Home Screen**, then **Add**.
3. Open the new **Companion** icon. The app runs full-screen; the status bar blends in.

Android / Chrome: open the address, menu (⋮) → **Install app** or **Add to Home screen**.

Settings → Appearance → **Screen & Install** shows `INSTALLED` when the app is running from
the home-screen icon, and the "This device" line says which browser and address it has.

## 7.6 Keep the screen awake

Settings → Appearance → **Screen & Install → Keep the screen awake**: on. The pill next to it
reads:

| Pill | Meaning |
|---|---|
| `ACTIVE` | The screen stays on while the app is open. |
| `OFF` | The switch is off on this device. |
| `NEEDS HTTPS` | You opened the app over plain HTTP — go through 7.1 to 7.4. |
| `NOT SUPPORTED` | This browser has no Screen Wake Lock (iOS needs Safari 16.4 or newer). |
| `REQUESTING` | The browser refused for now (often a low battery); it tries again when you return to the page. |

The choice is stored **on that device** (the browser's local storage), not in `settings.json`
— each tablet decides for itself. The browser drops the lock when you switch apps or lock the
tablet, and the app takes it again when you come back. Closing the tab releases it.

## 7.7 Checks after setup

- `https://…:5321` opens with a padlock on the tablet, and the plain `http://…:5320` still
  opens too.
- Appearance → Screen & Install: `ACTIVE` with the switch on; `OFF` with it off.
- Leave the tablet alone for longer than its auto-lock time with the app open: the screen
  stays on.
- Switch to another app and back: the pill returns to `ACTIVE` within a second.
- The home-screen icon opens full-screen with the Companion logo.
- Stop the app on the PC: the installed app shows the usual "reconnecting" state — no offline
  page, by design.
