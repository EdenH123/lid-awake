# Lid Awake computer API

This is the contract every Lid Awake computer agent implements. The Mac app answers it today (`Server.swift`, `LiveServer.swift`, `main.swift`). The Windows app is built against this document.

The phone page (`web/index.html`) is shared. It only knows this API. If a computer answers the same requests the same way, the same page works on it.

Every fact here was read from the Mac code and the page. Where something could not be confirmed it is marked **(unverified)**.

## Contents

1. [How a request reaches the agent](#1-how-a-request-reaches-the-agent)
2. [General rules](#2-general-rules)
3. [Static files](#3-static-files)
4. [Features](#4-features)
5. [Routes](#5-routes)
6. [`/status` fields](#6-status-fields)
7. [Live socket (`/live`)](#7-live-socket-live)
8. [Remote input commands](#8-remote-input-commands)
9. [Tailscale setup the agent must do](#9-tailscale-setup-the-agent-must-do)
10. [Open points](#10-open-points)

---

## 1. How a request reaches the agent

| What | Value |
|---|---|
| HTTP server | `127.0.0.1:8787`, loopback only |
| Live socket (WebSocket) | `127.0.0.1:8788`, loopback only |
| Public address | `https://<computer>.<tailnet>.ts.net/`, made by Tailscale Serve |
| `/` and everything else | Serve forwards to `8787` |
| `/live` | Serve forwards to `8788` |

Nothing listens on the network directly. Tailscale Serve adds two headers the agent relies on:

- `Tailscale-User-Login`: the Tailscale account of the device that asked.
- `X-Forwarded-For`: that device's Tailscale (100.x) address. Only used for the optional `link` text in `/status`.

The page uses relative URLs (`status`, `login`, ...), so it must be served at `/`.

The Mac sends `Connection: close` and a `Content-Length` on every response. Keep-alive is fine for another agent; the page does not depend on either.

---

## 2. General rules

### 2.1 Request limits

| Limit | Value |
|---|---|
| Request headers | at most 16 KB |
| Request body | at most 64 KB; `Content-Length` must be 6 digits or fewer |
| `Transfer-Encoding` | refused |
| Time to receive a whole request | 10 seconds (then the connection is dropped) |
| Open connections | at most 64 |
| Malformed or too big | `400 Bad Request`, body `Bad request` |

Query strings are split on `&` and `=`. Values are **not** percent-decoded (except `wl` on `/status`). Keep ids and keys URL-safe.

### 2.2 Order of checks

Every request goes through these steps in this order. The first that fails answers.

| # | Check | Answer |
|---|---|---|
| 1 | Header `Tailscale-Funnel-Request` present (the request came from the public internet) | `403` `Forbidden` |
| 2 | Host is `127.0.0.1` or `localhost` (port ignored): see 2.3 | see 2.3 |
| 3 | Any other Host: must be this computer's Tailscale name, and `Tailscale-User-Login` must be the Tailscale account that owns this computer (case-insensitive) | `403` `This device isn't signed in to the Tailscale account this Mac uses.` |
| 4 | Not a static file and not `/`, and `Sec-Fetch-Site` is present and is neither `same-origin` nor `none` | `403` `Forbidden` |
| 5 | Static file | answered, see section 3 |
| 6 | Lid Awake is off: see 2.4 | `/` page, `/auth` `{"off":true}`, else `423` |
| 7 | Not a public route and no valid session cookie | `401` `Locked` |
| 8 | POST without header `X-LidAwake: 1` | `403` `Forbidden` |
| 9 | Route not found for this method and path | `404` `Not found` |

Public routes (no session needed): `/`, `/auth`, `/login`, `/logout`, `/passkey/login-options`, `/passkey/login`.

Step 7 runs before step 8, so an unauthenticated POST without the header gets `401`.

Method and path must both match. For example `GET /login` is `404`.

### 2.3 Host `127.0.0.1` (the Mac's own address)

Signing in on `127.0.0.1` would hand the session to every other local web server, so the agent only points to the Tailscale address.

| Request | Answer |
|---|---|
| `GET /` | the page (200) |
| `GET /auth` | `200` `{"loopback":true,"url":"https://<tailscale host>/"}`. `url` is `""` if the Tailscale name is not known yet |
| static files | answered |
| anything else | `403` `Open Lid Awake at its Tailscale address.` |

### 2.4 While Lid Awake is off

The agent still answers, but only:

- `GET /` (the page),
- static files,
- `GET /auth` -> `200` `{"off":true}`.

Every other path answers `423 Locked`, body `Lid Awake is off on your Mac.` This happens before the session check, so even a signed-in phone gets `423`, not `401`.

The page treats both `401` and `423` as "locked": it stops its sockets and shows the lock screen. While off, the whole Tailscale link is removed (section 9), so the phone usually gets no answer at all and the page shows "Can't reach".

### 2.5 Session

| Item | Value |
|---|---|
| Cookie name | `__Host-la_session` |
| Cookie attributes | `Path=/; Max-Age=604800; HttpOnly; SameSite=Strict; Secure` |
| Lifetime | 7 days from sign-in (not extended by use) |
| Token | 64 hex characters (32 random bytes). The agent stores only a SHA-256 of it |
| Missing or invalid | `401` `Locked` |

On every sign-in the Mac also sends a second `Set-Cookie: la_session=; Path=/; Max-Age=0` to delete an older cookie name. Another agent does not need it.

If the cookie name appears more than once in the `Cookie` header, any valid one counts.

Each session records: a short id (first 12 hex characters of the stored hash), the device name, creation time, and whether it was a passkey sign-in.

Device name comes from the `User-Agent`, first match wins:

| User-Agent contains | Device name |
|---|---|
| `iPhone` | `iPhone` |
| `iPad` | `iPad` |
| `Android` | `Android phone` |
| `Macintosh` | `Mac` |
| `Windows` | `Windows PC` |
| none of these | `Browser` |

A session is revoked when: the phone signs out, the owner signs it out, the owner sets a new password on the computer (this signs out everyone, deletes all passkeys and all push subscriptions), or it expires. Open live sockets of a revoked session close within 2 seconds.

### 2.6 Response headers

Every HTTP response carries:

```
Cache-Control: no-store
X-Content-Type-Options: nosniff
X-Frame-Options: DENY
Cross-Origin-Resource-Policy: same-origin
Referrer-Policy: no-referrer
```

### 2.7 Response bodies

- JSON answers use `Content-Type: application/json`.
- Success with no data is `204 No Content`, empty body, `Content-Type: text/plain`.
- Errors are `Content-Type: text/plain; charset=utf-8` with **one plain sentence**. The page shows that sentence to the person as is (login errors, app errors, screen messages). Another agent may reword it but must keep it a short readable sentence. The exact Mac sentences are listed per route below.

### 2.8 Page response and CSP

`GET /` answers `200`, `text/html; charset=utf-8`: the contents of `web/index.html` with `<!--SPLASH-->` replaced by the launch-screen `<link>` tags (empty on Windows is fine). It also carries this header:

```
Content-Security-Policy: default-src 'none'; script-src <hashes>; style-src 'unsafe-inline';
  img-src 'self' blob:; connect-src 'self' wss://<host>; worker-src 'self'; manifest-src 'self';
  base-uri 'none'; form-action 'none'; frame-ancestors 'none'
```

(one line in the real header)

- `<host>` is the request's Host header, lowercased, port removed.
- `<hashes>` is a space-separated list with one `'sha256-<base64>'` for the body of every inline `<script>` in `web/index.html`. The scan looks for the literal text `<script>` (no attributes) up to the next `</script>`, hashes the exact bytes between them, and base64-encodes the digest. Compute it once at start from the file, so editing the page never needs a code change. The Mac computes it from the file before `<!--SPLASH-->` is replaced, so the replacement must not touch script bodies.

---

## 3. Static files

Answered before the "off" check and before sign-in, to any caller that passes steps 1 to 3.

| Path | Serves | Content-Type |
|---|---|---|
| `/` | `web/index.html` (see 2.8). Also answered while off | `text/html; charset=utf-8` |
| `/sw.js` | `web/sw.js` | `text/javascript; charset=utf-8` |
| `/manifest.webmanifest` | `web/manifest.webmanifest` (`{}` if the file is missing) | `application/manifest+json` |
| `/apple-touch-icon.png`, `/apple-touch-icon-precomposed.png` | 180x180 app icon | `image/png` |
| `/icon-512.png` | 512x512 app icon | `image/png` |
| `/sky/sunrise.jpg`, `/sky/day.jpg`, `/sky/sunset.jpg`, `/sky/night.jpg` | the opening-animation photos (`assets/sky-*.jpg`). Any other name: `404` `Not found` | `image/jpeg` |
| `/splash/<w>x<h>.png` | iPhone launch screens, only for the sizes `Splash.swift` lists (pixels = points x scale). Optional: Windows may answer `404` `Not found` | `image/png` |

The page and its service worker, manifest and icons are needed by iOS before any sign-in, so they never require a session.

The service worker (`web/sw.js`) shows push alerts, keeps a copy of `/` so the home-screen app opens while the computer is off, and caches `/sky/*`. It never caches anything else. Push payloads it expects: JSON `{"title","body","tag"}`.

---

## 4. Features

`/status` carries `os` and `features` (section 6). The page reads them from every status answer.

| Feature | Routes and messages it covers |
|---|---|
| `core` | always required (not listed in `features`) |
| `passkeys` | `/passkey/*` |
| `camera` | `/camera/*`, camera messages on the live socket |
| `push` | `/push/*` |
| `apps` | `/app-icon.png`, `/open-app`, `/focus-app`, `/quit-app`, `/apps-order` |
| `history` | `/temp-history`, `/battery-history`, `/cpu-history`, `/cpu-icon` |
| `memory` | `/memory` |
| `screen` | `/screen.jpg`, the live socket and its screen frames |
| `control` | `/input`, `/power`, input messages on the live socket |
| `limits` | `/limits`, and the low-battery and too-hot safety switches (`batteryGuard`, `heatGuard`) |

The Mac sends: `["screen","control","camera","apps","history","memory","push","passkeys","limits"]`.

Dependencies on the Mac side:

- `camera` needs `passkeys` (the camera asks for a passkey every time) and `screen` (the live socket and the owner's screen switch).
- `control` needs `screen`.

A route for a feature the computer does not implement should answer the default `404` `Not found`.

**The page hides what is missing.** `can(feature)` is true while no status has arrived yet (so nothing changes before the first answer) and afterwards only for listed features. A missing feature hides its part of the page and the page never calls its routes:

| Missing | The page hides |
|---|---|
| `screen` | the Screen page and its tab (also stops the live socket) |
| `apps` | the Apps page and its tab, app icons |
| `push` | the Alerts section in Settings |
| `passkeys` | the Face ID / fingerprint rows, offer and sign-in button |
| `camera` | the camera notch on the Screen page |
| `history` | the graphs: the battery, temperature and processor readings stay but no longer open |
| `memory` | the memory window: the memory reading stays but no longer opens |
| `control` | clicking, typing, and the lock / screen-off buttons send nothing |
| `limits` | the Safety limits section (the page never posts `/limits`) |

With only the Status page left, the tab bar goes too.

`os` is `"mac"` or `"windows"`. The page treats any other value as `"mac"`. It uses it for wording (Mac or PC) and for the names of the two modifier keys (section 8).

---

## 5. Routes

Notation: `-> 204` means success with an empty body. All POST bodies are JSON. A body that is not valid JSON is treated as empty.

### 5.1 Index

| Method and path | Feature | Needs session | Purpose |
|---|---|---|---|
| `GET /auth` | core | no | lock state |
| `POST /login` | core | no | sign in with the password |
| `POST /logout` | core | no | sign out |
| `GET /sessions` | core | yes | signed-in devices |
| `POST /sessions/signout` | core | yes | sign a device out |
| `GET /activity` | core | yes | latest activity lines |
| `POST /awake` | core | yes | turn Lid Awake off |
| `POST /limits` | limits | yes | safety limits |
| `POST /timer` | core | yes | auto-off timer |
| `GET /status` | core | yes | everything the Status page shows |
| `GET /passkey/login-options` | passkeys | no | challenge for passkey sign-in |
| `POST /passkey/login` | passkeys | no | passkey sign-in |
| `POST /passkey/register-options` | passkeys | yes | challenge for adding a passkey |
| `POST /passkey/register` | passkeys | yes | add a passkey |
| `POST /camera/options` | camera | yes | challenge for opening the camera |
| `POST /camera/unlock` | camera | yes | passkey proof -> one-time ticket |
| `GET /push/key` | push | yes | the computer's push public key |
| `POST /push/subscribe` | push | yes | register a phone for alerts |
| `POST /push/settings` | push | yes | change or remove a phone's alerts |
| `GET /app-icon.png` | apps | yes | an app's icon |
| `POST /open-app` | apps | yes | open a listed app |
| `POST /focus-app` | apps | yes | front, hide or quit an open app |
| `POST /quit-app` | apps | yes | quit a listed app |
| `POST /apps-order` | apps | yes | reorder the app list |
| `GET /temp-history` | history | yes | battery temperature graph |
| `GET /battery-history` | history | yes | battery charge graph |
| `GET /cpu-history` | history | yes | processor-use graph |
| `GET /cpu-icon` | history | yes | icon for an app in the processor graph |
| `GET /memory` | memory | yes | memory window |
| `GET /screen.jpg` | screen | yes | one screen picture (long-poll) |
| `POST /input` | control | yes | click, type, scroll |
| `POST /power` | control (see note) | yes | lock, screen off, wake |

Every route passes the checks in 2.2 first. "Needs session" means step 7 there.

### 5.2 Core

#### `GET /auth`

Public. Answers `200` JSON:

```json
{"hasPassword": true, "loggedIn": false, "faceID": true}
```

| Field | Meaning |
|---|---|
| `hasPassword` | a phone password has been set on the computer |
| `loggedIn` | the request carries a valid session |
| `faceID` | at least one passkey is registered (the name is historical; it means "any passkey") |

Special answers: `{"loopback":true,"url":"..."}` (2.3) and `{"off":true}` (2.4).

The page calls this on load and again every 3 seconds while it shows the "off" or "set a password" screen. With no password set the page tells the person to set one on the computer. **Setting the password has no HTTP route**: it is done in the computer's own app (minimum 10 characters, enforced there).

#### `POST /login`

Public. Body: `{"password": "<text>"}`.

| Answer | When |
|---|---|
| `204` + `Set-Cookie` session | password correct |
| `401` `Wrong password.` | wrong |
| `429` `Too many wrong tries. Try again in <n> seconds.` | locked out |
| `409` `No password is set yet.` | no password on the computer |

Lockout: after 5 wrong tries in a row the computer refuses for 60 seconds, then 120, 240, and so on, up to 3600 seconds (1 hour). The count is kept across restarts and is shared with `/passkey/register-options` (which also checks the password). A correct password during a lockout is still `429`. Success, a passkey sign-in or a new password resets it.

Password storage on the Mac: PBKDF2-HMAC-SHA256, 600,000 rounds, 16-byte salt, 32-byte key, constant-time compare. Another agent may use any strong scheme.

After a successful sign-in the computer announces it: an on-screen notification, a `security` push to every subscribed phone, and a line in the activity log: title `New sign-in to Lid Awake`, body `With the password, on an iPhone.` (`on a browser.` if the device name is `Browser`; otherwise `on an <device name>.`).

#### `POST /logout`

Public (but needs `X-LidAwake: 1`). No body. Always `204` and a `Set-Cookie` that clears the cookie (`__Host-la_session=; Path=/; Max-Age=0; HttpOnly; SameSite=Strict; Secure`). If the cookie matches a real session, it is deleted and its sockets close. A made-up token changes nothing.

#### `GET /sessions`

`200` JSON, newest first:

```json
{"sessions": [{"id": "a1b2c3d4e5f6", "device": "iPhone", "created": 1760000000, "faceID": true, "current": true}]}
```

| Field | Type | Meaning |
|---|---|---|
| `id` | string, 12 hex chars | handle for sign-out |
| `device` | string | device name (2.5) |
| `created` | integer | Unix seconds |
| `faceID` | boolean | signed in with a passkey |
| `current` | boolean | this request's own session |

#### `POST /sessions/signout`

Body: `{"others": true}` or `{"id": "<12 chars>"}`.

| Answer | When |
|---|---|
| `204` | done. `others:true` signs out every session except the caller's. `id` signs out that one session |
| `400` `Which device?` | neither `others:true` nor `id` |

The caller's own session is never signed out by this route. An `id` that is not exactly 12 characters, or matches nothing, still answers `204` and changes nothing.

#### `GET /activity`

`200` JSON `{"lines": ["<time>  <text>", ...]}`: the newest 8 lines, **newest first**. Each line is an ISO 8601 time with offset (for example `2026-10-19T09:30:00+03:00`), then **two spaces**, then the text. The page splits at the first double space; if the time does not parse it shows the row without a time. Control characters are removed from the text and it is cut at 300 characters. The list is empty (`[]`) if there is no log.

The Mac writes the log to `~/Library/Logs/LidAwake-access.log`. What it records: sign-ins, passkey changes, screen viewing started and ended, control used, camera opened and closed, apps opened or quit, locking, screen off.

#### `POST /awake`

Body must be exactly `{"on": false}`.

| Answer | When |
|---|---|
| `204` | the computer turns Lid Awake off (only if it is on and not busy; the answer is `204` either way) |
| `400` `Lid-awake can only be turned on from the Mac.` | any other body |

The phone cannot turn it on: while off, the page is closed (2.4). After the request the page waits 1.5 seconds and asks `/auth`.

Turning off closes every live socket. The Mac logs `Lid Awake turned off from the phone` and shows a notification.

#### `POST /limits`

Body: `{"name": "battery" | "heat", "on": true | false}`.

| Answer | When |
|---|---|
| `204` | applied at once, same as the switch on the computer |
| `400` `Unknown limit.` | other name, or `on` missing or not a boolean |

The limits the switches control, checked every 15 seconds while on: `battery` turns Lid Awake off when running on battery below `batteryLimit` percent; `heat` turns it off when the battery is at or above `maxBatteryTemp` degrees, or the system thermal state is `serious` or `critical`. Both can also stop a turn-on request.

#### `POST /timer`

Body: `{"minutes": <integer>}`, one of `0, 15, 30, 60, 120, 240, 480`. `0` means never.

| Answer | When |
|---|---|
| `204` | set. While Lid Awake is on, the countdown restarts from now |
| `400` `Unknown timer length.` | any other value |

#### `GET /status`

`200` JSON. Fields are in section 6.

Optional query `wl`: text the page sends about its own wake-lock state (for example `held on status`). The Mac keeps letters, digits, spaces and `:()-`, cuts it at 60 characters, and writes a log line when it changes for that caller. An agent may ignore it.

The page asks every 3 seconds, with an 8-second timeout. If 8 seconds pass with no good answer after it has had one, it shows "Can't reach Mac".

### 5.3 `passkeys`

Passkeys are WebAuthn credentials on the phone (Face ID, fingerprint, ...). The computer stores only public keys and checks signatures.

Rules shared by all four routes:

- The Host must end in `.ts.net`, and it is the WebAuthn relying-party id (`rpId`) and the origin (`https://<host>`).
- Base64url means URL-safe base64 without `=` padding.
- A challenge is 32 random bytes (base64url), good for **2 minutes**, usable **once**, and bound to a purpose (`get`, `create` or `camera`). At most 32 are kept; the oldest is dropped.
- The page asks for user verification (`userVerification: "required"`) and ES256 only (`alg -7`, P-256), `attestation: "none"`. The user id it registers is always `lid-awake-owner`, so setting up again on the same phone replaces rather than adds a key.

#### `GET /passkey/login-options`

Public. `200` `{"challenge": "<base64url>", "rpId": "<host>"}`. No list of keys is returned (the phone offers its own).

#### `POST /passkey/login`

Public. Body, all base64url strings:

```json
{"id": "<credential id>", "clientData": "...", "authenticatorData": "...", "signature": "..."}
```

| Answer | When |
|---|---|
| `204` + session cookie | the signature checks out (see below) |
| `401` `Face ID sign-in didn't work. Use your password.` | any check fails, or no password is set |

The agent checks, in this order: a password exists; Host ends in `.ts.net`; `clientData` JSON has `type == "webauthn.get"`, `origin == "https://<host>"` and a `challenge` that was issued with purpose `get` and is unexpired (it is consumed); `authenticatorData` is at least 37 bytes, its first 32 bytes equal SHA-256 of the host, and both the user-present (0x01) and user-verified (0x04) flags are set; the credential id is stored; `signature` is an ECDSA P-256 DER signature over `authenticatorData || SHA-256(clientData)` by the stored public key; the signature counter (bytes 33 to 36, big-endian) is higher than the stored one. A counter that is 0 both stored and received is accepted (synced passkeys always report 0).

The session is marked as a passkey session. The Mac does **not** announce passkey sign-ins (only password sign-ins).

#### `POST /passkey/register-options`

Session needed. Body: `{"password": "<text>"}`. The password is checked again before the phone is even asked.

| Answer | When |
|---|---|
| `200` `{"challenge": "...", "rpId": "<host>", "ids": ["<credential id>", ...]}` | password correct. Purpose `create`. `ids` lists the stored credential ids so the phone can exclude them |
| `401` `Wrong password.` | wrong, or no password set |
| `429` `Too many wrong tries. Try again in <n> seconds.` | locked out (shares the counter with `/login`) |

#### `POST /passkey/register`

Session needed. Body (base64url): `{"clientData": "...", "attestation": "<attestationObject>"}`.

| Answer | When |
|---|---|
| `204` | stored |
| `400` `Couldn't turn on Face ID. Try again.` | any check fails |

Checks: Host ends in `.ts.net`; `clientData` has `type == "webauthn.create"`, the right origin, and a `create` challenge; `attestation` is CBOR with an `authData` byte string; `authData` rp hash equals SHA-256 of the host, flags user-present and user-verified set, attested-credential-data flag (0x40) set; the credential id is not already stored (an existing id is never replaced); fewer than 20 stored; the COSE key is EC2, ES256 (`alg -7`), P-256, with 32-byte x and y. The agent stores: credential id, the public key (`04 || x || y`), the counter, time added, device name.

On success the Mac announces it like a sign-in: title `Face ID or fingerprint added to Lid Awake`, body `On an iPhone. If this wasn't you, remove it from the Lid Awake menu on your Mac.` (`On a browser.` if the device name is `Browser`).

The owner removes a passkey in the computer's own app (no HTTP route).

### 5.4 `camera`

The camera needs a passkey **every time**. Flow: `options` -> phone signs -> `unlock` -> one-time `ticket` -> the ticket is given on the live socket (section 7.5).

Both camera routes require the owner's "allow camera" switch on the computer, which itself requires the "allow screen" switch (the status field `cameraAllowed`).

#### `POST /camera/options`

No body.

| Answer | When |
|---|---|
| `200` `{"challenge": "...", "rpId": "<host>", "ids": [...]}` | allowed. Purpose `camera` |
| `403` `The camera is off. On your Mac, tick “Allow camera & microphone from phone” in the Lid Awake menu.` | screen switch or camera switch off |
| `409` `The camera needs Face ID. Turn it on first in Settings → Sign-in.` | no passkey registered, or Host does not end in `.ts.net` |

#### `POST /camera/unlock`

Body: same four fields as `/passkey/login`.

| Answer | When |
|---|---|
| `200` `{"ticket": "<base64url>"}` | passkey valid (same checks as login, but with `webauthn.get` and a `camera` challenge) |
| `401` `Face ID didn't work, so the camera stays closed.` | any check fails, **or** the camera or screen switch is off |

The ticket is 32 random bytes (base64url), valid **60 seconds**, usable **once**, and bound to the caller's session. At most 16 live tickets are kept; if 16 already exist the answer still contains a ticket, but it will not be accepted.

### 5.5 `push`

Alerts use standard Web Push. The phone subscribes once. The computer later sends short encrypted messages through the phone's push service, even when the page is closed.

#### `GET /push/key`

`200` `{"key": "<base64url>"}`: this computer's VAPID public key, 65 bytes uncompressed P-256 (`04 || x || y`). The key pair is made once and kept. The page passes it as `applicationServerKey`.

#### `POST /push/subscribe`

Body: the browser's push subscription, plus the alerts this phone turned off:

```json
{"endpoint": "https://...", "keys": {"p256dh": "<base64url>", "auth": "<base64url>"}, "off": ["warm"]}
```

(`expirationTime` may also be present and is ignored.)

| Answer | When |
|---|---|
| `204` | stored (an existing entry with the same `endpoint` is replaced) |
| `400` `Couldn't turn on alerts.` | any check fails |

Checks: `endpoint` is `https`, under 1024 bytes, no port, no user or password, and the host is exactly one of `web.push.apple.com`, `fcm.googleapis.com`, `updates.push.services.mozilla.com` (the computer never posts to an address a page made up); `p256dh` decodes to 65 bytes starting with `0x04`; `auth` decodes to 16 bytes. Unknown names in `off` are dropped. At most 10 phones are kept; the oldest goes first. The computer also remembers `https://<Host>` as the VAPID contact.

#### `POST /push/settings`

Body: `{"endpoint": "...", "enabled": false}` or `{"endpoint": "...", "off": ["warm", ...]}`.

| Answer | When |
|---|---|
| `204` | `enabled:false` removes the phone. Otherwise `off` replaces that phone's list. An unknown endpoint is ignored |
| `400` `Missing phone.` | no `endpoint` |

#### Alert kinds

`off` and the push `tag` use these names: `turnedOff`, `lowBattery`, `claudeUsage`, `claudeSession`, `warm`. A sixth kind, `security`, cannot be turned off.

| Kind | Sent when (Mac) |
|---|---|
| `turnedOff` | the timer ended, or a safety limit turned Lid Awake off. Sent before it turns off |
| `lowBattery` | on battery at 20% or below (once; re-arms when plugged in or above 25%) |
| `warm` | battery within 3 degrees of `maxBatteryTemp` (once; re-arms below 5 degrees under it) |
| `claudeUsage` | Claude plan usage reaches 90% in a limit window (once per window) |
| `claudeSession` | a Claude Code session finishes or needs the owner |
| `security` | a new password sign-in, or a passkey added |

Sending (Mac behaviour): payload JSON `{"title": "...", "body": "...", "tag": "<kind>"}`, encrypted with RFC 8291 (`aes128gcm`), signed with VAPID (RFC 8292, ES256, JWT valid 12 hours, `aud` = the push service origin, `sub` = the remembered contact), posted with headers `TTL: 86400` and `Urgency: high`, 8-second timeout, no redirects. A `404` or `410` from the push service removes that phone.

### 5.6 `apps`

The list of apps comes from the owner, set in the computer's own app. There is no route to add or remove apps. The phone only ever sends an **id**, never a path. An id is opaque: the Mac uses the first 6 bytes of SHA-256 of the app's path as 12 hex characters. Another agent may use any stable, URL-safe string.

"On the list" means the owner chose it. "Open" means running now.

#### `GET /app-icon.png`

Query: `id` (required), `size` (optional integer; `512` or more gives 512, anything else 256).

| Answer | When |
|---|---|
| `200` `image/png`, square 256x256 or 512x512 | the id is on the list or is open now (the dock) |
| `404` `No such app.` | otherwise |

The page also uses this route for icons of the top apps in `/memory` (via their `id`).

#### `POST /open-app`

Body: `{"id": "..."}`. Starts the app and brings it to the front. Only apps on the list.

| Answer | When |
|---|---|
| `204` | done (also wakes the screen) |
| `404` `That app isn't on your list any more.` | id not on the list |

#### `POST /focus-app`

Body: `{"id": "...", "action": "focus" | "hide" | "quit"}`. `action` defaults to `focus`. Works on any open app (listed or in the dock list `otherApps`) but **cannot start** an app. `focus` shows and fronts it, `hide` hides it, `quit` asks it to quit like the Quit shortcut (it may still ask to save).

| Answer | When |
|---|---|
| `204` | done (also wakes the screen) |
| `400` `Unknown action.` | other action |
| `404` `That app isn't open any more.` | id not open |

#### `POST /quit-app`

Body: `{"id": "..."}`. Quit a listed app the polite way.

| Answer | When |
|---|---|
| `204` | the quit request was sent |
| `404` `That app isn't on your list any more.` | id not on the list |
| `409` `That app isn't open.` | listed but not running |

#### `POST /apps-order`

Body: `{"ids": ["...", ...]}`. Unknown ids are ignored; listed apps not mentioned keep their place at the end.

| Answer | When |
|---|---|
| `204` | saved |
| `400` `Missing app order.` | `ids` missing or not a list of strings |

### 5.7 `history`

All three history routes take an optional `hours` query (a number). It is clamped to 1 to 48 and defaults to 24. The page always asks for `hours=24` and slices ranges (1, 6, 24 hours) itself. Entries are oldest first. If the computer has no data, answer the same shape with empty lists. The page shows `No readings for this time yet.` for empty data, and `Couldn't load the history from your Mac.` when the request fails or the answer is unreadable.

The Mac reads macOS's own power log (`/private/var/db/powerlog/Library/BatteryLife/CurrentPowerlog.PLSQL`, read only). Lid Awake records nothing itself. The format is Apple's and undocumented, so on any problem the Mac answers empty lists.

#### `GET /temp-history`

```json
{"points": [[1760000000, 31.25], ...], "now": 1760003600}
```

`points`: `[unix seconds, degrees Celsius]`. Only readings above 0 and below 100. `now`: Unix seconds on the computer.

#### `GET /battery-history`

```json
{"points": [[1760000000, 87, 1, 0], ...], "now": 1760003600}
```

`points`: `[unix seconds, percent 0 to 100, plugged in (0 or 1), charging (0 or 1)]`.

#### `GET /cpu-history`

```json
{"now": 1760003600, "cores": 8,
 "apps": [{"key": "com.google.Chrome", "name": "Google Chrome", "kind": "app", "color": "#4285f4"}],
 "intervals": [[1760000000, 1760000060, {"0": 12.5, "2": 3.1}, 4.2]]}
```

| Field | Meaning |
|---|---|
| `cores` | integer, number of processor cores |
| `apps[]` | who used processor time. `key` unique string, `name` shown text, `kind` is `app`, `macos` or `other`, `color` optional `#rrggbb` (the page uses grey if absent) |
| `intervals[]` | `[start, end, perApp, other]`: Unix seconds start and end, an object from **index into `apps` (as a string)** to processor-seconds in that stretch, and processor-seconds not listed. Rounded to 0.01 |

The page needs both `apps` and `intervals` to be arrays; otherwise it shows the graph as failed. The Mac folds apps under 0.5% of a stretch and under 0.2 seconds into `other`.

`kind` drives the row's icon in the "who used it" list: `app` fetches the icon from `/cpu-icon` (falling back to a square in `color`), `macos` draws a colored square with the Apple logo character, anything else is a plain colored square. A Windows agent should use `app` for real programs and `other` for system jobs (`macos` would show the Apple logo).

#### `GET /cpu-icon`

Query: `key` (an `apps[].key` with `kind: "app"` from a recent `/cpu-history`). `200` `image/png` (256x256), or `404` `No such app.` Only keys seen in the processor history are allowed, so the page cannot fetch any app's icon.

### 5.8 `memory`

#### `GET /memory`

Read fresh on every call. The page asks every 2 seconds while the Memory window is open, with a 4-second timeout.

```json
{"total": 17179869184, "used": 9000000000, "app": 5000000000, "wired": 2000000000,
 "compressed": 2000000000, "cached": 5000000000, "free": 1000000000, "swapUsed": 0,
 "pressure": 38, "level": "normal",
 "top": [{"name": "Google Chrome", "bytes": 2100000000, "id": "a1b2c3d4e5f6"}],
 "otherPrograms": 800000000}
```

| Field | Type | Meaning |
|---|---|---|
| `total` | integer bytes | installed memory |
| `used` | integer bytes | memory in use (Mac: app + wired + compressed, as Activity Monitor) |
| `app`, `wired`, `compressed`, `cached`, `free` | integer bytes | the parts. The page shows "Free + files" as `free + cached` and "Apps" as `app`. The page prints a dash when `free` is missing |
| `swapUsed` | integer bytes | memory moved to disk |
| `pressure` | integer 0 to 100 | how hard the system works to find room |
| `level` | `"normal"`, `"warn"` or `"critical"` | pressure level. The page maps anything else to `normal` |
| `top` | list | up to 5 programs using the most. Each `{name, bytes, id?}`. `id` is only for apps on the owner's list or open (it makes the page fetch `/app-icon.png?id=`); without it the page draws a blank icon |
| `otherPrograms` | integer bytes | memory of everything readable that is not in `top` |

The page draws a line of `used / total` over the last 2 minutes and shows a row "macOS & system" for the part of `used` not explained by `top` and `otherPrograms` (its label is still Mac wording).

### 5.9 `screen`

#### `GET /screen.jpg`

The slow path for the screen picture (used when the live socket fails 3 times; the page retries the socket every 30 seconds). A long-poll.

Query: `after` (integer, default 0): the id of the last frame the phone has.

| Answer | When |
|---|---|
| `200` `image/jpeg`, header `X-Frame-Id: <integer>` | a frame newer than `after` exists (waits up to 2 seconds for one) |
| `204` | nothing newer within 2 seconds |
| `503` `Waking the Mac's screen… This usually takes a few seconds.` | capture is not running yet |
| `403` `Screen access is off. Turn on “Allow screen & control from phone” in the Lid Awake menu on your Mac.` | owner's screen switch off |
| `403` `On your Mac, allow Lid Awake in System Settings → Privacy & Security → Screen & System Audio Recording.` | system screen permission missing |

`X-Frame-Id` rises with every new frame. The page sends it back as `after`. On `403` the page hides the picture and controls and shows the text.

Asking for a frame also counts as activity: the computer keeps the display awake while someone watches (section 7.7).

### 5.10 `control`

#### `POST /input`

Body: one input command (section 8). It is the same JSON as a socket input message.

| Answer | When |
|---|---|
| `204` | accepted (the command runs afterwards; unknown or invalid commands are silently ignored) |
| `403` `Screen access is off.` | owner's screen switch off |
| `403` `View only: control from the phone is off in the Lid Awake menu on your Mac.` | owner's control switch off |
| `403` `On your Mac, allow Lid Awake in System Settings → Privacy & Security → Accessibility.` | system control permission missing |
| `400` `Bad request` | body is not a JSON object |
| `413` `That's too much text to type at once.` | `text` longer than 2000 characters |

The page uses this only when the live socket is not open. It sends commands one at a time, in order.

Any accepted input also wakes the computer's screen.

#### `POST /power`

Body: `{"action": "lock" | "displayoff" | "wake"}`. All three need the owner's **screen** switch on, else `403` `Screen access is off.`

| Action | What it does | Extra requirements and answers |
|---|---|---|
| `lock` | locks the computer (Mac: presses Control-Command-Q, the system's Lock Screen shortcut) | owner's control switch on (`403` `View only: ...`) and system control permission (`403` `On your Mac, allow ... Accessibility.`). The Mac logs `Mac locked from the phone` |
| `displayoff` | turns the display off and stops waking it until the phone wakes it | `409` `The lid is closed: the screen couldn't be turned back on from your phone until you open the lid, so it stays on (at the lowest brightness).` if the lid is closed. The Mac logs `Display turned off from the phone` |
| `wake` | wakes the display and lets the screen stream wake it again | none |

Success `204`. Any other `action`: `400` `Bad request`.

Feature note: only `lock` needs the control switch; `displayoff` and `wake` need just the screen switch. All three are in the `control` feature.

---

## 6. `/status` fields

`GET /status` answers one flat JSON object. It is built from three groups (the computer's own state, power, system info) plus `link`, `os` and `features`.

A computer that cannot measure a field sends the **safe default** in the last column. "Omit" means leave the key out entirely; the page checks for absence. "Required" means the page reads it without checking, so it must always be present.

### 6.1 Identity and state

| Field | Type | Meaning | Safe default |
|---|---|---|---|
| `host` | string | the computer's display name (Mac: `Host.current().localizedName`, else `"Mac"`). The page shows it under the greeting | the computer's name |
| `lidAwake` | boolean | Lid Awake is on | required |
| `timerMinutes` | integer | chosen auto-off length; `0` = never; one of 0, 15, 30, 60, 120, 240, 480 | `0`; required |
| `timerRemaining` | number, seconds | time left. Present only while on, with a timer, and in the future | omit |
| `batteryGuard` | boolean | "turn off on low battery" switch (Mac default on) | `true` |
| `heatGuard` | boolean | "turn off when hot" switch (Mac default on) | `true` |
| `batteryLimit` | integer, percent | battery level below which it turns off (Mac: `10`) | `10`; required (the page prints it) |
| `maxBatteryTemp` | integer, degrees C | temperature at which it turns off (Mac: `40`) | `40`; required (the page uses it) |
| `thermalState` | string | `"nominal"`, `"fair"`, `"serious"` or `"critical"` (Mac: from the system's thermal state). The page shows Normal, Warm, Hot, Critical, and a dash for any other value | `"nominal"` |

### 6.2 Remote access state

| Field | Type | Meaning | Safe default |
|---|---|---|---|
| `remoteScreen` | boolean | the owner allowed screen access from the phone (owner's switch, off by default on the Mac) | `false` |
| `remoteControl` | boolean | screen **and** control allowed (`remoteScreen && control switch`) | `false` (the page tests `=== false`) |
| `cameraAllowed` | boolean | screen **and** camera allowed (`remoteScreen && camera switch`). The current page does not read it | `false` |
| `screenPermission` | boolean | the system allows capturing the screen | `false` |
| `controlPermission` | boolean | the system allows sending clicks and keys | `false` |
| `screenWidth` | number | width of the main display (Mac: points). The page divides it by the picture's width on the phone to turn finger movement into scroll distance; `0` or absent makes it use a factor of 2 | `0` |
| `screenOff` | boolean | the phone turned the display off (via `/power` `displayoff`) and has not woken it | `false` |
| `lidClosed` | boolean | the lid is closed (Mac: `AppleClamshellState`) | `false` |
| `displayAsleep` | boolean | the main display is asleep | `false` |

The page draws the computer as dark when `lidClosed`, `displayAsleep` or `screenOff`.

### 6.3 Apps and Claude

| Field | Type | Meaning | Safe default |
|---|---|---|---|
| `apps` | list of `{id, name, running}` | the owner's chosen apps, in order. `id` string (see 5.6), `name` string, `running` boolean. Apps that no longer exist are left out | `[]` |
| `otherApps` | list of `{id, name}` | open ordinary windowed apps that are not on the list (the dock). Not the agent itself | `[]` |
| `claude` | object or `null` | Claude plan usage (below), or `null` | `null` |

`claude` object:

```json
{"updated": 1760000000, "fiveHour": {"pct": 74, "resetsAt": 1760010000}, "sevenDay": {"pct": 84, "resetsAt": 1760300000}}
```

| Field | Type | Meaning |
|---|---|---|
| `updated` | integer Unix seconds | when the numbers were saved (`0` if unknown) |
| `fiveHour`, `sevenDay` | object, each optional | `pct` number (percent used, 0 to 1000 accepted, normally 0 to 100); `resetsAt` integer Unix seconds, `0` if unknown |

The page hides the Claude section when `claude` is `null` or has neither window.

### 6.4 Power (omit all of it on a computer with no battery)

These come from `powerInfo()`. On the Mac, a computer with no battery answers `{}` for this group, so every key is absent.

| Field | Type | Meaning |
|---|---|---|
| `battery` | integer 0 to 100 | charge percent. The page shows the battery card only if present |
| `onBattery` | boolean | running on battery |
| `charging` | boolean | charging now |
| `charged` | boolean | fully charged |
| `minutesToEmpty` | integer | estimate; `-1` = unknown |
| `minutesToFull` | integer | estimate; `-1` = unknown |
| `batteryTemp` | number, degrees C, one decimal | battery temperature. Omit if unknown |

### 6.5 System info

These come from `SystemInfo.snapshot()`. Two objects, `mac` and `net` (the key is `mac` on every computer, including Windows).

`mac` (always present; individual keys may be absent):

| Field | Type | Meaning | If unknown |
|---|---|---|---|
| `memTotal` | integer bytes | installed memory | always sent |
| `uptime` | integer seconds | time since boot | always sent |
| `cpu` | number 0 to 100 | processor load since the **previous status request** (so the first request after start has none) | omit |
| `memUsed` | integer bytes | memory in use | omit |
| `diskFree` | integer bytes | free space on the system disk | omit |
| `diskTotal` | integer bytes | size of the system disk | omit |

`net` (always present, may be `{}`):

| Field | Type | Meaning | If unknown |
|---|---|---|---|
| `wifi` | string | Wi-Fi network name (the Mac needs Location permission to read it) | omit |
| `rssi` | integer, dBm | Wi-Fi signal. `0` is never sent | omit |
| `iface` | string | the network interface in use (for example `en0`) | omit |
| `wired` | boolean | the interface in use is not Wi-Fi. The page shows Ethernet when `true`, otherwise Wi-Fi (and "Not connected" with no `rssi`; when `os` is `"windows"` and `wired` is `false` with no `rssi`, it reads "Connected" without signal bars) | omit (set `true` on a desktop with no Wi-Fi) |
| `down` | number, bytes per second | received, since the previous status request | omit |
| `up` | number, bytes per second | sent, since the previous status request | omit |

### 6.6 Link, os, features

| Field | Type | Meaning | Safe default |
|---|---|---|---|
| `link` | string | how **this phone** reaches the computer, from Tailscale's view of the peer at `X-Forwarded-For`. Omitted when unknown or when Tailscale reports no live path. Page shows a dash if absent | omit |
| `os` | string | `"mac"` or `"windows"` | required |
| `features` | list of strings | feature names (section 4) | required |

`link` values the Mac sends: `Direct · same Wi-Fi`, `Direct · iPhone hotspot`, `Direct · over the internet`, `Through a relay in <city>`, `Through a relay server`. It comes from `tailscale status --json` (the peer's `CurAddr`, `Relay`, `Active`), refreshed in the background at most every 10 seconds. The `·` is a middle dot.

---

## 7. Live socket (`/live`)

The Screen tab and the camera use a WebSocket (RFC 6455) on `127.0.0.1:8788`. Tailscale Serve exposes it at `wss://<host>/live`. The page opens `wss://<host>/live` (or `ws://<hostname>:8788/live` if the page itself is on plain http). Binary messages go to the phone (pictures, sound); JSON text messages go both ways.

The Mac's server does not look at the request path, so any path would be accepted. **(unverified: whether Tailscale Serve strips the `/live` mount prefix before forwarding; the page and the Mac do not depend on it)**

### 7.1 Handshake

The upgrade is accepted only if **all** of these hold:

| Check | Rule |
|---|---|
| Funnel | no `Tailscale-Funnel-Request` header |
| Host | the computer's Tailscale name, with `Tailscale-User-Login` equal to the owner (same rule as HTTP step 3). Port ignored |
| `Origin` | exactly `https://<host>` (so only the page itself) |
| `Sec-Fetch-Site` | if the header is present it must be `same-origin`. Absent is fine |
| Session | the `Cookie` carries a valid `__Host-la_session` |
| Lid Awake | on |
| Screen switch | the owner's "allow screen" switch is on |
| System permission | the system allows screen capture |

Not required: the control switch (view-only sessions can watch). A refused handshake answers HTTP `400` according to `tests/live_checks.sh` **(unverified: the Mac code only says "reject"; the status comes from its test expectations)**. The page does not look at the status: any socket that never opened sends it back to `/auth` to find out whether it is locked or off.

Limits and housekeeping (Mac):

| Rule | Value |
|---|---|
| Open sockets | at most 8 |
| Handshake must finish | within 10 seconds |
| Incoming message size | at most 64 KB |
| WebSocket ping | answered automatically |
| Every 2 seconds | if Lid Awake is off or the screen switch is off, all sockets are closed. Sockets of a revoked or expired session are closed |
| Sender stuck | a socket that completes no send for 15 seconds is closed |
| Turning off | `/awake`, the owner's switch, or withdrawing screen access closes all sockets at once |

After a close the page reconnects after 0.5 seconds (or backs off to 5 seconds if it never opened), and asks `/auth` when the socket never opened.

### 7.2 Messages from the computer

#### Binary

Each binary message is one of these. The **first byte** tells which.

| First byte | Kind | Layout |
|---|---|---|
| `0xFF` | JPEG screen picture | the whole message is a plain JPEG file (JPEGs start `FF D8`). No header |
| `1` | H.264 screen video, keyframe | `[1][8-byte timestamp][H.264 data]` |
| `2` | H.264 screen video, other frame | `[2][8-byte timestamp][H.264 data]` |
| `3` | H.264 camera video, keyframe | `[3][8-byte timestamp][H.264 data]` |
| `4` | H.264 camera video, other frame | `[4][8-byte timestamp][H.264 data]` |
| `5` | camera sound | `[5][4-byte sample rate][sound bytes]` |

Video and camera layout, byte by byte:

| Bytes | Content |
|---|---|
| 0 | `1`/`2` (screen) or `3`/`4` (camera). Odd-numbered kind (`1`, `3`) means keyframe |
| 1 to 8 | timestamp in **microseconds**, signed 64-bit integer, **big-endian** |
| 9 to end | one H.264 access unit in **AVCC form**: NAL units each preceded by a 4-byte big-endian length. No Annex B start codes. SPS and PPS are not inside; they come in `description` of the setup message |

Camera sound layout, byte by byte:

| Bytes | Content |
|---|---|
| 0 | `5` |
| 1 to 4 | sample rate in Hz, unsigned 32-bit integer, **big-endian** (the Mac sends the microphone's real rate; it asks for 16000) |
| 5 to end | audio samples, **G.711 mu-law**, one byte per sample, one channel (the first channel only). The Mac sends about 0.1 seconds of sound per message |

Rules for video:

- A setup text message (7.2 text, `video` or `camera`) must arrive **before** the first video frame, and again whenever the setup changes (for example the size). Order on the socket is kept.
- A phone starts decoding at the first **keyframe** after setup. Frames before one are ignored by the page.
- The page treats a message as video if the first byte is not `0xFF`.
- Mu-law: decode as in the page (`u = ~byte & 0xFF; t = (((u & 0x0F) << 3) + 0x84) << ((u >> 4) & 7); sample = ((u & 0x80) ? 0x84 - t : t - 0x84) / 32768`).

JPEG vs video: a new screen socket first gets JPEG frames. If the phone sent `video: true`, the computer then switches that phone to H.264 and stops sending it JPEGs. A computer that only does JPEG may ignore `video`: the page accepts JPEG frames at any time and plays them.

#### Text (JSON objects)

| Message | Meaning |
|---|---|
| `{"video": {"codec": "avc1.4d0028", "description": "<base64>", "width": 1280, "height": 720}}` | screen video setup. `codec` is the WebCodecs codec string (`avc1.` plus profile, constraint and level bytes in hex). `description` is the base64 **avcC** record (carries SPS and PPS). `width` and `height` in pixels |
| `{"camera": {"codec": "...", "description": "...", "width": n, "height": n}}` | camera video setup, same fields |
| `{"error": "<sentence>"}` | a problem to show on the Screen tab. Sentences the Mac uses: `Waking the Mac's screen… This usually takes a few seconds.`, `Screen access is off.`, `View only: control from the phone is off in the Lid Awake menu on your Mac.`, `On your Mac, allow Lid Awake in System Settings → Privacy & Security → Accessibility.`, `That's too much text to type at once.` |
| `{"cameraError": "<sentence>"}` | a problem with the camera. An empty string clears the message |
| `{"cameraStopped": "<sentence>"}` | the camera was closed by the computer (the owner turned it off, or the 10-minute limit); the page closes its camera view and shows the sentence |
| `{"pong": true}` | answer to the input command `{"type":"ping"}` (used by the Mac's self-test) |

Camera messages (`camera`, `cameraError`, `cameraStopped`) only appear after the phone asked for the camera (7.5).

### 7.3 Messages from the phone

All are JSON text. Invalid JSON, or not an object, is ignored. The page's first message after the socket opens is:

```json
{"quality": "balanced", "acks": true, "video": true, "why": ""}
```

| Message | Meaning |
|---|---|
| `{"quality": "max" \| "sharp" \| "balanced" \| "fast"}` | picture quality preset (table below). The Mac keeps **one** preset for all viewers; the latest wins. Unknown names are ignored |
| `{"acks": true}` | turn on flow control: the phone will confirm every binary frame |
| `{"ack": 1}` | the phone has the last binary frame. Sent once per binary message on the screen socket (not on the camera socket) |
| `{"video": true}` or `{"video": false}` | the page can play H.264 (`true`), or it cannot and wants JPEG (`false`). Optional `"why": "<text>"` is only logged (the Mac keeps printable characters, cuts at 100) |
| `{"videoRestart": true}` | the decoder failed: send setup and a keyframe again. Optional `why`. The page sends it up to 3 times a minute before giving up on video and sending `{"video": false}` |
| `{"camera": true, "ticket": "<ticket>"}` | open the camera, using a ticket from `/camera/unlock` |
| `{"camera": false}` | close the camera |
| `{"cameraKey": 1}` | send a fresh camera keyframe |
| `{"talk": "<base64>"}` | the phone's voice for the computer's speakers (see 7.5) |
| any input command (section 8) | click, type, scroll. Same JSON as `POST /input` |

The `quality` presets (what the Mac does with them):

| Preset | JPEG max width / quality / fps | Video max width / fps / max bitrate |
|---|---|---|
| `max` | 4096 px (capped at the screen's real pixels) / 0.7 / 6 | 4096 px / 15 / 12 Mbit/s |
| `sharp` | 1600 / 0.7 / 12 | 1600 / 20 / 6 Mbit/s |
| `balanced` (default) | 1200 / 0.5 / 20 | 1280 / 30 / 4 Mbit/s |
| `fast` | 640 / 0.35 / 25 (a clearer frame follows when the picture is still) | 1024 / 30 / 2.5 Mbit/s |

A simpler agent can implement fewer presets or map them to what it can do. The page only sends these four names.

### 7.4 Flow control (what `acks` means)

Without it, a send "completes" when the data is handed to the network, not when the phone has it, so frames pile up on a slow link and the picture falls behind. With `acks: true` the Mac sends at most **two unconfirmed frames**: newer frames replace the one waiting (latest wins). If the phone stops confirming for 3 seconds the count resets so the stream does not freeze. A minimal agent can skip flow control and just send the newest frame whenever the previous send has finished, but it will fall behind on slow links.

For video the Mac also holds back new frames while the page is behind, adapts the bitrate to how fast confirmations come back, and restarts from a keyframe if more than 60 messages queue (screen) or 90 (camera). These are quality choices, not part of the contract.

### 7.5 Camera on the socket

1. The phone gets a ticket from `POST /camera/unlock` (5.4).
2. It opens a **new** socket (the screen socket is closed so only one video uses the link). The new socket briefly behaves as a screen socket (it gets JPEG frames) until the camera starts.
3. It sends `{"camera": true, "ticket": "..."}`.
4. The computer checks the owner's camera switch (else `cameraError`: `The camera is off. On your Mac, tick “Allow camera & microphone from phone” in the Lid Awake menu.`) and redeems the ticket (else `cameraError`: `Face ID is needed to open the camera. Try again.`). A ticket works once and only for the session that asked for it.
5. The computer stops this socket's screen frames and sends the `camera` setup, then camera video and sound (7.2).
6. The page sends `{"cameraKey":1}` after it sets up its decoder, and when it falls behind.
7. The camera closes on `{"camera": false}`, when the socket closes, when the owner's switch goes off (`cameraStopped`: `The camera was turned off on the Mac.`), or after **10 minutes** (`cameraStopped`: `The camera closed by itself after 10 minutes. Open it again to keep watching.`).

Mac camera details: 1280x720 when available, a plugged-in camera is preferred over the built-in one, the phone itself is never used as the camera. Problems are sent as `cameraError`.

**Talk back.** While the camera is open, the phone may send `{"talk": "<base64>"}`: audio for the computer's speakers. Format: **16 kHz, mono, mu-law, one byte per sample**, base64 of at most **4000 bytes** (the page sends about 40 ms at a time). It is ignored (silently) if the camera is not open or the owner's camera switch is off. The Mac plays chunks back to back and drops them if it is already about a second behind.

### 7.6 Input on the socket

Any message that is not one of the above is treated as an input command (section 8), after these checks, each answered with `{"error": "<sentence>"}`:

| Check | Sentence |
|---|---|
| Lid Awake on and the owner's screen switch on | `Screen access is off.` |
| owner's control switch on | `View only: control from the phone is off in the Lid Awake menu on your Mac.` |
| system control permission | `On your Mac, allow Lid Awake in System Settings → Privacy & Security → Accessibility.` |
| `text` is 2000 characters or fewer | `That's too much text to type at once.` |

More rules:

- At most **240 input messages per second** per socket; extra ones are silently dropped.
- Any accepted input wakes the screen, like `POST /input`.
- `{"type": "ping"}` is not a real command: it is answered `{"pong": true}` (after the checks above) and does nothing else.
- If a phone leaves while a mouse button or modifier is held, the computer lets go of them.

Messages with `quality`, `acks`, `video`, `camera` and the others in 7.3 are handled **before** these checks, so view-only phones can still change quality and open the camera (the camera has its own switch).

### 7.7 Keeping the screen available

While a phone is watching (a socket, or recent `/screen.jpg` polls) the Mac keeps the display awake (a "user activity" signal, at most once every 15 seconds) and runs capture. Capture stops 10 seconds after the last viewer or poll. After `POST /power` `displayoff` it stops waking the display until a `wake` or any accepted input. The Mac also keeps the screen usable behind a closed lid (at the lowest brightness).

---

## 8. Remote input commands

One JSON object with a `type`. The same objects are sent as `POST /input` bodies and as socket messages. Unknown types and bad values are ignored (no error). Numbers are clamped: `dx`, `dy` to -10000..10000. Non-finite numbers count as 0.

| `type` | Fields | What the computer does |
|---|---|---|
| `move` | `dx`, `dy` numbers | moves the pointer by that many points, relative to where it is, kept on the main display. If the left button is held (`mousedown`), this drags |
| `mousedown` | none | presses the left button at the pointer. Ignored if already down |
| `mouseup` | none | releases the left button. Ignored if already up |
| `click` | `x`, `y` numbers 0..1 (optional), `button` `"left"` or `"right"` (default left), `count` 1..3 (default 1), `mods` (optional) | moves to the point and clicks. `x` and `y` are fractions of the **main display**, top-left origin, clamped to 0..1. If either is missing, clicks at the pointer. `count` is the click count (2 = double-click). Held modifiers apply to the click |
| `scroll` | `dx`, `dy` numbers, `x`, `y` optional | scrolls by pixels: `dy` vertical, `dx` horizontal, passed unchanged to a pixel scroll-wheel event. With `x` and `y` the pointer moves there first. The page sends the finger's movement; the content follows the finger (a drag down is a positive `dy`) |
| `text` | `text` string, at most 2000 characters | types the text, one character at a time, as Unicode (layout-independent) |
| `key` | `key` name **or** `char`, optional `mods` | presses and releases one key with the modifiers held |

`key` names: `return`, `tab`, `space`, `delete` (Backspace), `escape`, `left`, `right`, `down`, `up`. `char` is a single character; it is mapped to a physical key position on a US layout so shortcuts work in any input language. Supported: letters `a`-`z` (an upper-case letter adds Shift), digits `0`-`9`, space, and `= - ] [ ' ; \ , / . `` ` ``. A `char` not in that set, or a `key` name not in the list, is ignored. If both are given, `key` wins when its name is known.

`mods` is a list of strings: `"ctrl"`, `"cmd"`, `"shift"`, `"alt"`. Others are ignored.

**Modifier meaning is per computer.** The page sends `"cmd"` for its first modifier button and `"alt"` for its second. The page labels them from `os`:

| Page sends | Mac | Windows |
|---|---|---|
| `"cmd"` | Command | **Ctrl** (the computer's shortcut key) |
| `"alt"` | Option | **Alt** |
| `"ctrl"` | Control | Control (the page sends it only with an arrow key, for its desktop-switching gestures, see below) |
| `"shift"` | Shift | Shift |

So an agent must map `"cmd"` to **the computer's main shortcut key** (Command on a Mac, Ctrl on Windows) and `"alt"` to Option or Alt.

The page's gestures send `{"type":"key","key":"left"|"right"|"up"|"down","mods":["ctrl"]}`. On the Mac these are Control plus an arrow (switch desktop, Mission Control, App windows). What they should do on Windows is for that agent to decide. **(unverified: the page does not change these per computer)**

Arrow keys on the Mac also carry the Fn and numeric-pad flags, as a real keyboard does. After a shortcut with modifiers the Mac explicitly releases every modifier key, because a stuck Control turns later clicks into right-clicks. An agent should do the equivalent.

---

## 9. Tailscale setup the agent must do

The agent learns two things from `tailscale status --json` and remembers them (so a restart while Tailscale is starting does not lock the owner out):

- **This computer's name**: `Self.DNSName` (must end in `.ts.net.`), lowercased, without the trailing dot.
- **The owner**: `User[Self.UserID].LoginName`, lowercased.

A request is allowed only if its Host equals that name and `Tailscale-User-Login` equals that owner (2.2 step 3).

While Lid Awake is **on** the agent makes sure Serve is configured (checked when it turns on or off, and every 60 seconds):

```
tailscale serve --bg 8787
tailscale serve --bg --set-path /live http://127.0.0.1:8788
```

It checks the output of `tailscale serve status --json` for the text `127.0.0.1:8787` and `127.0.0.1:8788` and only adds what is missing.

While Lid Awake is **off** it removes everything so nothing on the tailnet can reach the page:

```
tailscale serve --https=443 off
```

**Funnel is never allowed.** If `serve status --json` shows any `AllowFunnel` entry set to true, the agent runs `tailscale funnel --https=443 off`, logs it and notifies the owner. (Requests carrying `Tailscale-Funnel-Request` are refused in any case, 2.2 step 1.)

On the Mac the Tailscale binary is `/Applications/Tailscale.app/Contents/MacOS/Tailscale` run with the environment variable `TAILSCALE_BE_CLI=1`.

For `link` in `/status` (6.6) the agent runs `tailscale status --json` at most every 10 seconds, in the background.

---

## 10. Open points

Short list of things a Windows implementer should know that this document cannot settle from the Mac code:

- **Camera and passkeys depend on a page-side passkey.** The page offers passkeys only when it is on `https` and the browser supports them. An agent without `passkeys` should also leave out `camera` (the camera needs a passkey).
- **Error sentences mention the Mac.** The page shows them as written. A Windows agent should reword them, for example `Lid-awake can only be turned on from this PC.`
- **Wording of passkey errors** names Face ID. A neutral wording is fine; the page shows the sentence as is.
- **A local program can claim to be the owner.** The agent listens on `127.0.0.1`, so any program on the computer can connect and send `Host: <this computer's Tailscale name>` with a made-up `Tailscale-User-Login`, and pass 2.2 step 3. The Mac makes the same assumption (that only Tailscale Serve sends those headers). Such a program still needs the password (with its lockout, 5.2) or a stolen session cookie. A future hardening: accept the owner branch of step 3 only when the connection's other end (the TCP peer) belongs to tailscaled, found from the process ID that owns the peer's port.
- **The server's own errors have empty bodies.** On Windows, requests that Kestrel refuses before the agent sees them (`408` too slow, `431` headers too big, `400` malformed) answer with an empty body, not a sentence. Windows also drops a connection idle for 10 seconds between requests, and a body that arrives slower than 8 KB a second after a 5-second grace.
- **Items marked (unverified)**: the status code of a refused socket handshake; how Tailscale Serve forwards the `/live` path; what the page's Control-plus-arrow gestures should do on Windows.
