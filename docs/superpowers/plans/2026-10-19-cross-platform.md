# Lid Awake on Any Computer and Any Phone — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One Lid Awake project that works on all four combinations — Mac + iPhone (today), Mac + Samsung, Windows + iPhone, Windows + Samsung — with the phone page recognizing the phone and the computer by itself.

**Architecture:** The phone page stays one shared web app (moved out of Swift into `web/`). Each computer has its own small agent — today's Swift app for Mac, a new C#/.NET app for Windows — that serves the same `web/` files and answers the same requests (documented in `docs/API.md`). The page finds out the phone from the browser, and the computer from `/status` (`os`, `features`), and adapts its words, keys and visible features.

**Tech Stack:** Swift + AppKit (Mac, unchanged), HTML/JS (shared page), C# / .NET 8 + WinForms tray + ASP.NET Core Kestrel (Windows), Tailscale Serve on both, Node for page checks, xUnit for Windows tests.

---

## Roadmap

| Phase | What works afterwards | This file |
|---|---|---|
| **1. Shared page + device recognition** (Tasks 1–7) | Mac + iPhone, **Mac + Samsung** | Detailed |
| **2. Windows foundation** (Tasks 8–15) | **Windows + iPhone/Samsung**: sign in, status, battery, CPU, memory, keep awake, turn off from phone | Detailed |
| **3. Windows feature parity** | Screen, control, apps, alerts, Face ID/fingerprint, camera, history graphs on Windows | One follow-up plan per feature (list at the end) |

Phase 1 is independent and ships on its own. Phase 2 needs Tasks 1–6 done (it reuses `web/`, `os`, `features` and `docs/API.md`).

## File map

**Phase 1**
- Create `web/index.html` — the phone page (moved from `Dashboard.swift`, unchanged at first).
- Create `web/sw.js` — the service worker (moved from `ServiceWorker.swift`).
- Create `web/manifest.webmanifest` — the app manifest (moved from `Server.swift:402`).
- Modify `Dashboard.swift` — becomes a tiny loader for `web/` files.
- Modify `ServiceWorker.swift` — loader for `web/sw.js`.
- Modify `Server.swift:401-403` — serve the manifest from `web/`.
- Modify `build.sh` — copy `web/` into the app.
- Create `Platform.swift` — `os` and `features` for this computer.
- Modify `main.swift:758-788` (`statusJSON`) — merge `Platform.info`.
- Create `tests/page_check.js`, `tests/page_check.sh` — the served page matches `web/` and its CSP.
- Create `tests/platform/main.swift`, `tests/platform_check.sh`.
- Create `tests/words_check.js`, `tests/words_check.sh` — no hard-coded "iPhone"/"Face ID" left in visible text.
- Create `docs/API.md` — the contract every computer agent implements.
- Modify `Setup.swift`, `main.swift`, `README.md` — "iPhone" → "phone" where it means any phone; Samsung steps.

**Phase 2**
- Create `windows/LidAwake.Windows/` — the Windows agent (files listed per task).
- Create `windows/LidAwake.Windows.Tests/` — xUnit tests.
- Create `windows/install.ps1`, `windows/uninstall.ps1`, `Install Lid Awake (Windows).cmd`.

---

# Phase 1 — Shared page and device recognition

### Task 1: Move the phone page into `web/index.html`

**Files:**
- Create: `web/index.html`
- Modify: `Dashboard.swift` (whole file)
- Modify: `build.sh:21-22`
- Create: `tests/page_check.js`, `tests/page_check.sh`

- [ ] **Step 1: Write the failing check**

`tests/page_check.js`:

```js
// The page the running app serves on 127.0.0.1:8787 is web/index.html (with the iPhone
// launch-screen links filled in), and every inline script is allowed by its CSP hashes.
const fs = require("fs");
const crypto = require("crypto");
const http = require("http");

const file = fs.readFileSync(process.argv[2], "utf8");
let failed = false;
const check = (name, ok, detail = "") => {
  console.log((ok ? "PASS " : "FAIL ") + name + (detail ? ` (${detail})` : ""));
  if (!ok) failed = true;
};

http.get({ host: "127.0.0.1", port: 8787, path: "/" }, res => {
  let body = "";
  res.setEncoding("utf8");
  res.on("data", d => (body += d));
  res.on("end", () => {
    check("status 200", res.statusCode === 200, String(res.statusCode));
    const splash = file.indexOf("<!--SPLASH-->");
    check("file has splash marker", splash > 0);
    check("served start matches file", body.startsWith(file.slice(0, splash)));
    check("served end matches file", body.endsWith(file.slice(splash + "<!--SPLASH-->".length)));
    check("splash marker filled in", !body.includes("<!--SPLASH-->"));
    const csp = res.headers["content-security-policy"] || "";
    const scripts = [...body.matchAll(/<script>([\s\S]*?)<\/script>/g)].map(m => m[1]);
    check("has inline scripts", scripts.length >= 2, `${scripts.length}`);
    scripts.forEach((s, i) => {
      const h = "'sha256-" + crypto.createHash("sha256").update(s, "utf8").digest("base64") + "'";
      check(`script ${i + 1} allowed by CSP`, csp.includes(h));
    });
    process.exit(failed ? 1 : 0);
  });
}).on("error", e => { console.log("FAIL app not reachable on 127.0.0.1:8787: " + e.message); process.exit(1); });
```

`tests/page_check.sh`:

```sh
#!/bin/sh
# The running app serves web/index.html as the phone page (run after build.sh).
cd "$(dirname "$0")" || exit 1
node page_check.js "$(cd .. && pwd)/web/index.html"
```

Run: `chmod +x tests/page_check.sh`

- [ ] **Step 2: Run it to make sure it fails**

Run: `tests/page_check.sh`
Expected: `ENOENT ... web/index.html` (the file doesn't exist yet).

- [ ] **Step 3: Move the page**

Check the string has no Swift interpolation (must print nothing), and that line 1 / the last line are the delimiters:

```bash
grep -n '\\#(' Dashboard.swift
head -1 Dashboard.swift   # let dashboardHTML = #"""
tail -1 Dashboard.swift   # """#
```

Then extract everything between them:

```bash
mkdir -p web
sed '1d;$d' Dashboard.swift > web/index.html
```

Replace `Dashboard.swift` entirely with:

```swift
import Foundation

// The phone page and its service worker and manifest live in web/, shared by every computer
// version of Lid Awake (Mac and Windows serve the same files). build.sh copies web/ into the
// app; these read them once.
func webFile(_ name: String) -> Data? {
    guard let dot = name.lastIndex(of: "."),
          let url = Bundle.main.url(forResource: String(name[..<dot]), withExtension: String(name[name.index(after: dot)...]),
                                    subdirectory: "web") else { return nil }
    return try? Data(contentsOf: url)
}

let dashboardHTML: String = webFile("index.html").flatMap { String(data: $0, encoding: .utf8) }
    ?? "<!doctype html><meta charset=\"utf-8\"><p>Lid Awake's phone page is missing. Run build.sh again.</p>"
```

In `build.sh`, after the line `cp "$SRC"/assets/sky-*.jpg "$APP/Contents/Resources/"` add:

```sh
# The phone page (shared with the Windows version).
rm -rf "$APP/Contents/Resources/web"
cp -R "$SRC/web" "$APP/Contents/Resources/web"
```

- [ ] **Step 4: Build and run the check**

Run: `./build.sh && sleep 5 && tests/page_check.sh`
Expected: every line `PASS`, exit 0. Then open the page on the iPhone once and sign in: it must look and behave exactly as before.

- [ ] **Step 5: Commit**

```bash
git add web/index.html Dashboard.swift build.sh tests/page_check.js tests/page_check.sh
git commit -m "Phone page: move it to web/index.html, shared by every computer version"
```

---

### Task 2: Move the service worker and manifest into `web/`

**Files:**
- Create: `web/sw.js`, `web/manifest.webmanifest`
- Modify: `ServiceWorker.swift`
- Modify: `Server.swift:398-403`

- [ ] **Step 1: Extract the service worker**

```bash
grep -n '\\#(' ServiceWorker.swift   # must print nothing
awk '/^let serviceWorkerJS = #"""$/{on=1;next} /^"""#$/{on=0} on' ServiceWorker.swift > web/sw.js
```

Replace `ServiceWorker.swift` with its header comment (lines 1-5, unchanged) followed by:

```swift
// Served at /sw.js; the file is web/sw.js.
let serviceWorkerJS: String = webFile("sw.js").flatMap { String(data: $0, encoding: .utf8) } ?? ""
```

- [ ] **Step 2: Extract the manifest**

Create `web/manifest.webmanifest` (same content as `Server.swift:402`, plus `id` so Android keeps one installed app):

```json
{"id":"/","name":"Lid Awake","short_name":"Lid Awake","start_url":"/","display":"standalone","background_color":"#000000","theme_color":"#000000","icons":[{"src":"/apple-touch-icon.png","sizes":"180x180","type":"image/png"},{"src":"/icon-512.png","sizes":"512x512","type":"image/png"}]}
```

In `Server.swift`, replace the `case "/manifest.webmanifest":` body (the `let manifest = ...` line and its `return`) with:

```swift
            return reply(c, "200 OK", "application/manifest+json", webFile("manifest.webmanifest") ?? Data("{}".utf8))
```

- [ ] **Step 3: Build and verify**

Run: `./build.sh && sleep 5 && curl -s http://127.0.0.1:8787/sw.js | head -3 && curl -s http://127.0.0.1:8787/manifest.webmanifest`
Expected: the first lines of `web/sw.js`, then the manifest JSON above. Run `tests/page_check.sh` again: all `PASS`.

- [ ] **Step 4: Commit**

```bash
git add web/sw.js web/manifest.webmanifest ServiceWorker.swift Server.swift
git commit -m "Service worker and manifest: serve them from web/ too"
```

---

### Task 3: The computer says what it is (`os`, `features`)

**Files:**
- Create: `Platform.swift`
- Modify: `main.swift` (`statusJSON`, before `return`)
- Create: `tests/platform/main.swift`, `tests/platform_check.sh`

- [ ] **Step 1: Write the failing test**

`tests/platform/main.swift`:

```swift
import Foundation

// Platform.swift (compiled together by platform_check.sh).
var failed = false
func check(_ name: String, _ ok: Bool) {
    print((ok ? "PASS " : "FAIL ") + name)
    if !ok { failed = true }
}

let info = Platform.info
check("os is mac", info["os"] as? String == "mac")
let features = info["features"] as? [String] ?? []
for f in ["screen", "control", "camera", "apps", "history", "memory", "push", "passkeys"] {
    check("feature \(f)", features.contains(f))
}
check("info is valid JSON", JSONSerialization.isValidJSONObject(info))
exit(failed ? 1 : 0)
```

`tests/platform_check.sh`:

```sh
#!/bin/sh
# Platform.swift: what this computer tells the phone page about itself.
cd "$(dirname "$0")" || exit 1
OUT="$(mktemp -d)"
swiftc -o "$OUT/platform" platform/main.swift ../Platform.swift || { rm -rf "$OUT"; exit 1; }
"$OUT/platform"; status=$?
rm -rf "$OUT"
exit $status
```

Run: `chmod +x tests/platform_check.sh`

- [ ] **Step 2: Run it to make sure it fails**

Run: `tests/platform_check.sh`
Expected: compile error `cannot find 'Platform' in scope`.

- [ ] **Step 3: Write `Platform.swift`**

```swift
import Foundation

// Which computer this is, for the phone page: it adapts its drawing, key names and wording to
// it, and hides what this computer can't do. The Windows version answers the same fields with
// "windows" and its own list (docs/API.md).
enum Platform {
    static let os = "mac"
    static let features = ["screen", "control", "camera", "apps", "history", "memory", "push", "passkeys"]
    static var info: [String: Any] { ["os": os, "features": features] }
}
```

In `main.swift` `statusJSON()`, after `s.merge(SystemInfo.shared.snapshot()) { $1 }` add:

```swift
        s.merge(Platform.info) { $1 }
```

- [ ] **Step 4: Run the test and the build**

Run: `tests/platform_check.sh && ./build.sh`
Expected: 10 `PASS` lines, then "Lid Awake rebuilt and restarted."

- [ ] **Step 5: Commit**

```bash
git add Platform.swift main.swift tests/platform/main.swift tests/platform_check.sh
git commit -m "Status: say which computer this is and what it can do"
```

---

### Task 4: The page recognizes the phone and the computer

**Files:**
- Modify: `web/index.html` (main `<script>`, right after `const $ = id => document.getElementById(id);`, and `poll()`)

- [ ] **Step 1: Add the device profile**

Right after `const $ = id => document.getElementById(id);` (first line of the main script) insert:

```js
  // ---------- Which phone, which computer ----------
  // The phone is known at once from the browser; the computer from its status (remembered, so the
  // next opening is right from the start). Words, keys and features follow both.
  const PHONE = /Android/i.test(navigator.userAgent) ? "android"
    : /iPhone|iPad|iPod/.test(navigator.userAgent) || (/Macintosh/.test(navigator.userAgent) && navigator.maxTouchPoints > 1) ? "ios"
    : "other";
  const PHONE_WORDS = {
    ios: {
      phone: "iPhone", unlock: "Face ID",
      unlockPrivacy: "Skip the password next time. Your face never leaves this iPhone.",
      hapticsHelp: "Only on this iPhone. If you don't feel them, turn on System Haptics in iPhone Settings → Sounds & Haptics.",
      installHelp: "To use alerts, open Lid Awake from its Home Screen icon (in Safari: Share → Add to Home Screen).",
      alertsBlocked: "Notifications are blocked. Allow them in iPhone Settings → Notifications → Lid Awake.",
    },
    android: {
      phone: "phone", unlock: "fingerprint",
      unlockPrivacy: "Skip the password next time. Your fingerprint never leaves this phone.",
      hapticsHelp: "Only on this phone. If you don't feel them, turn on touch vibration in Settings → Sounds and vibration.",
      installHelp: "To use alerts, add Lid Awake to your Home screen (in Chrome: ⋮ → Add to Home screen).",
      alertsBlocked: "Notifications are blocked. Allow them in Chrome: ⋮ → Settings → Site settings → Notifications.",
    },
    other: {
      phone: "phone", unlock: "passkey",
      unlockPrivacy: "Skip the password next time. Your passkey never leaves this device.",
      hapticsHelp: "Only on this phone.",
      installHelp: "To use alerts, add Lid Awake to your Home screen.",
      alertsBlocked: "Notifications are blocked. Allow them in your browser's settings.",
    },
  }[PHONE];
  const COMPUTER_WORDS = {
    mac:     { computer: "Mac", cmdKey: "⌘", cmdName: "Command", altKey: "⌥", altName: "Option" },
    windows: { computer: "PC",  cmdKey: "Ctrl", cmdName: "Control", altKey: "Alt", altName: "Alt" },
  };
  let COMPUTER = "mac", FEATURES = null;  // FEATURES null = not known yet: show everything
  try { COMPUTER = localStorage.getItem("computer") === "windows" ? "windows" : "mac"; } catch (e) {}
  const word = key => key in PHONE_WORDS ? PHONE_WORDS[key] : COMPUTER_WORDS[COMPUTER][key];
  const can = feature => !FEATURES || FEATURES.includes(feature);
  // Fills every <span data-w="key"> and relabels the modifier keys. Safe to call again.
  function applyDevice() {
    document.documentElement.dataset.phone = PHONE;
    document.documentElement.dataset.computer = COMPUTER;
    document.querySelectorAll("[data-w]").forEach(el => { el.textContent = word(el.dataset.w); });
    document.querySelectorAll('[data-mod="cmd"]').forEach(b => { b.textContent = word("cmdKey"); b.setAttribute("aria-label", word("cmdName")); });
    document.querySelectorAll('[data-mod="alt"]').forEach(b => { b.textContent = word("altKey"); b.setAttribute("aria-label", word("altName")); });
  }
  // From each status: switch computer if it changed, and keep its feature list.
  function learnDevice(status) {
    const os = status.os === "windows" ? "windows" : "mac";
    FEATURES = Array.isArray(status.features) ? status.features : null;
    if (os !== COMPUTER) {
      COMPUTER = os;
      try { localStorage.setItem("computer", os); } catch (e) {}
      applyDevice();
    }
  }
  applyDevice();
```

Note: the main script is at the end of `<body>`, so all elements exist when `applyDevice()` runs.

- [ ] **Step 2: Learn the computer from each status**

In `poll()`, after `data = await res.json();` add:

```js
      learnDevice(data);
```

- [ ] **Step 3: Alerts work in Android's browser without installing**

Android Chrome can receive Web Push from a normal tab; iPhone only from the Home Screen app. Find the line (around `const possible = pushPossible() && standalone();`) and replace it, and the two text lines after it:

```js
    const possible = pushPossible() && (PHONE !== "ios" || standalone());
    $("alertsSection").classList.toggle("unavailable", !possible);
    const note = !possible ? word("installHelp")
      : Notification.permission === "denied" ? word("alertsBlocked")
      : "";
```

- [ ] **Step 4: Verify in the browser**

Run: `./build.sh && sleep 5 && tests/page_check.sh` → all `PASS` (CSP hashes are recomputed from the file, so editing the script is fine).

Then on a Mac browser open `http://127.0.0.1:8787/` with the dev tools console and run:
`document.documentElement.dataset.phone` → `"other"`, `document.documentElement.dataset.computer` → `"mac"`.
In Chrome dev tools device mode choose "Samsung Galaxy S20 Ultra" and reload: `dataset.phone` → `"android"`.

- [ ] **Step 5: Commit**

```bash
git add web/index.html
git commit -m "Phone page: recognize the phone and the computer, and word things to match"
```

---

### Task 5: Replace hard-coded "iPhone" / "Face ID" in what people see

**Files:**
- Modify: `web/index.html`
- Create: `tests/words_check.js`, `tests/words_check.sh`

- [ ] **Step 1: Write the failing check**

`tests/words_check.js`:

```js
// No visible "iPhone" or "Face ID" left hard-coded in the page: they must come from word()
// (JS) or a <span data-w="..."> (HTML), so an Android phone sees its own words.
// Comments and the PHONE_WORDS table itself are allowed.
const fs = require("fs");
let html = fs.readFileSync(process.argv[2], "utf8");
html = html
  .replace(/<!--[\s\S]*?-->/g, "")
  .replace(/\/\*[\s\S]*?\*\//g, "")
  .replace(/^\s*\/\/.*$/gm, "")
  .replace(/\s\/\/ .*$/gm, "")
  .replace(/const PHONE_WORDS = \{[\s\S]*?\}\[PHONE\];/, "")
  .replace(/<span data-w="[a-zA-Z]+">[^<]*<\/span>/g, "");
const bad = html.split("\n").map((l, i) => [i + 1, l]).filter(([, l]) => /iPhone|Face ID/.test(l));
bad.forEach(([n, l]) => console.log(`FAIL line ~${n}: ${l.trim().slice(0, 140)}`));
console.log(bad.length ? `FAIL ${bad.length} hard-coded phone words` : "PASS no hard-coded phone words");
process.exit(bad.length ? 1 : 0);
```

`tests/words_check.sh`:

```sh
#!/bin/sh
# The phone page words "iPhone"/"Face ID" only through word() or data-w, never hard-coded.
cd "$(dirname "$0")" || exit 1
node words_check.js ../web/index.html
```

Run: `chmod +x tests/words_check.sh`

- [ ] **Step 2: Run it to make sure it fails**

Run: `tests/words_check.sh`
Expected: around 30 `FAIL line ~N` lines (the sign-in screen, Settings, network rows, camera messages, alerts…), exit 1.

- [ ] **Step 3: Replace each reported line, using these rules**

1. **Whole sentences that exist in `PHONE_WORDS`** — wrap the sentence in a `data-w` span of that key (the iPhone text stays inside, so the page reads right even before the script runs). Example (the Face ID offer):

   ```html
   <div class="faceid-text"><b>Sign in with <span data-w="unlock">Face ID</span>?</b><span data-w="unlockPrivacy">Skip the password next time. Your face never leaves this iPhone.</span></div>
   ```

   and the haptics footnote:

   ```html
   <div class="section-foot"><span data-w="hapticsHelp">Only on this iPhone. If you don't feel them, turn on System Haptics in iPhone Settings → Sounds &amp; Haptics.</span></div>
   ```

2. **Single words in HTML** — wrap the word: `this <span data-w="phone">iPhone</span>`, `Adding <span data-w="unlock">Face ID</span> asks for your password.` For `placeholder="Password, to add Face ID"` (an attribute, spans can't go there), give the input `data-w-placeholder="Password, to add {unlock}"` and add to `applyDevice()`:

   ```js
    document.querySelectorAll("[data-w-placeholder]").forEach(el => {
      el.placeholder = el.dataset.wPlaceholder.replace(/\{(\w+)\}/g, (_, k) => word(k));
    });
   ```

   and change the check's last `.replace` to also drop `data-w-placeholder="[^"]*"`:

   ```js
     .replace(/<span data-w="[a-zA-Z]+">[^<]*<\/span>/g, "").replace(/data-w-placeholder="[^"]*"/g, "");
   ```

3. **Strings in JS** — use template strings with `word()`. Examples:

   ```js
   $("gateText").textContent = $("faceIdBtn").hidden ? "Enter your Lid Awake password." : `Use ${word("unlock")}, or enter your Lid Awake password.`;
   if (e.name !== "NotAllowedError") $("loginError").textContent = `${word("unlock")} didn't work. Use your password.`;
   small.textContent = `Signed in ${dayFmt.format(new Date(d.created * 1000))} with ${d.faceID ? word("unlock") : "the password"}`;
   setCamMsg(`Confirm with ${word("unlock")} to open the camera.`);
   setCamMsg(`To talk, allow the microphone for this page on your ${word("phone")}.`);
   ```

   (`d.faceID` and `faceID` in JSON are field names in the API — leave them.)

4. **Leave alone:** comments, CSS, `class`/`id` names like `faceid-btn`, `faceIdBtn`, and anything in `PHONE_WORDS`.

Re-run `tests/words_check.sh` after every few edits until it prints `PASS no hard-coded phone words`.

- [ ] **Step 4: Verify both phones**

Run: `tests/words_check.sh && ./build.sh && sleep 5 && tests/page_check.sh`
Expected: all `PASS`.
On the iPhone: the page reads exactly as before ("Face ID", "this iPhone").
In Chrome dev tools device mode (Samsung) on `http://127.0.0.1:8787/`: the sign-in text says "fingerprint", Settings says "this phone".

- [ ] **Step 5: Commit**

```bash
git add web/index.html tests/words_check.js tests/words_check.sh
git commit -m "Phone page: say iPhone/Face ID on iPhone, phone/fingerprint on Android"
```

---

### Task 6: Write the contract every computer implements (`docs/API.md`)

**Files:**
- Create: `docs/API.md`

This is the list the Windows agent is built against. Every row is read from `Server.swift`, `LiveServer.swift` and `main.swift` (`statusJSON`).

- [ ] **Step 1: Write the general rules section**

Write these rules at the top of `docs/API.md` (all taken from `Server.swift:346-430`):

- Port `8787` on `127.0.0.1` (HTTP); live socket on `8788`. Tailscale Serve maps `https://<computer>.<tailnet>.ts.net/` → 8787 and `/live` → 8788.
- Requests carrying `Tailscale-Funnel-Request` → 403.
- Host `127.0.0.1`: only `/`, `/auth` (`{"loopback":true,"url":"https://<ts host>/"}`) and static files; everything else 403.
- Any other Host must be this computer's Tailscale name, and `Tailscale-User-Login` must be the Tailscale account owner; else 403.
- Non-static, non-`/` requests with `Sec-Fetch-Site` other than `same-origin`/`none` → 403.
- Every POST must carry `X-LidAwake: 1`, else 403.
- While Lid Awake is off: only the page, static files, and `/auth` → `{"off":true}`.
- Session: cookie `__Host-la_session` (HttpOnly, Secure, SameSite=Strict, Path=/, 7 days). Missing/invalid → 401.
- Every response: `Cache-Control: no-store`, `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Cross-Origin-Resource-Policy: same-origin`, `Referrer-Policy: no-referrer`.
- `/` gets the CSP from `Server.swift:327-331`, with a `sha256-` hash of every inline `<script>` body of `web/index.html`.

- [ ] **Step 2: Write the static-files table**

| Path | Serves |
|---|---|
| `/` | `web/index.html` with `<!--SPLASH-->` replaced by the launch-screen links (empty on Windows is fine) |
| `/sw.js` | `web/sw.js` |
| `/manifest.webmanifest` | `web/manifest.webmanifest` |
| `/apple-touch-icon.png`, `/apple-touch-icon-precomposed.png` | 180×180 icon |
| `/icon-512.png` | 512×512 icon |
| `/sky/{sunrise,day,sunset,night}.jpg` | `assets/sky-*.jpg` |
| `/splash/...` | iPhone launch screens (optional) |

- [ ] **Step 3: Write one row per route**

For each route below, read its `case` in `Server.swift` (line numbers as of this plan) and write: method + path, request body/query, success response (code + JSON fields), error responses, and which `features` entry it belongs to (`core` if always required).

| Line | Route | Feature |
|---|---|---|
| 435 | GET `/auth` | core |
| 440 | POST `/login` `{password}` → 204 + cookie / 401 / 429 / 409 | core |
| 455–494 | `/passkey/login-options`, `/passkey/login`, `/passkey/register-options`, `/passkey/register` | passkeys |
| 495, 505 | POST `/camera/options`, `/camera/unlock` | camera |
| 518 | GET `/activity` | core |
| 522, 528 | GET `/sessions`, POST `/sessions/signout` | core |
| 540–553 | GET `/push/key`, POST `/push/subscribe`, `/push/settings` | push |
| 560 | POST `/logout` | core |
| 566 | POST `/awake` `{on:false}` | core |
| 576–627 | `/app-icon.png`, `/open-app`, `/focus-app`, `/quit-app`, `/apps-order` | apps |
| 634, 640 | POST `/limits`, `/timer` | core |
| 648 | GET `/status?wl=` | core |
| 668–674 | `/temp-history`, `/battery-history`, `/cpu-history`, `/cpu-icon` | history |
| 681 | GET `/memory` | memory |
| 685 | GET `/screen.jpg?after=` | screen |
| 708 | POST `/input` | control |
| 724 | POST `/power` | control |

- [ ] **Step 4: Write the `/status` fields**

List every key `statusJSON()` returns (`main.swift:758-788`), plus `powerInfo()` (`Server.swift:9-26`), `SystemInfo.snapshot()` (`mac`, `memTotal`, `net`, `uptime` and what's inside each), `link`, `os`, `features`. For each: type, meaning, and the **safe default** a computer that can't measure it must send (e.g. `apps: []`, `otherApps: []`, `claude: null`, `thermalState: "nominal"`, `remoteScreen: false`, `cameraAllowed: false`, `screenPermission: false`, `controlPermission: false`, `lidClosed: false`, `displayAsleep: false`).

- [ ] **Step 5: Write the live socket section**

From `LiveServer.swift`: handshake checks (Host, Origin, cookie, awake, screen allowed), binary frame layout (read where frames are sent), and every JSON text message both ways (`ping`/`pong`, input, quality). State that modifier `"cmd"` means **the computer's shortcut key** (⌘ on Mac, Ctrl on Windows) and `"alt"` means ⌥ / Alt.

- [ ] **Step 6: Commit**

```bash
git add docs/API.md
git commit -m "docs: the requests every Lid Awake computer answers"
```

---

### Task 7: Mac app and README say "phone", and add Samsung steps

**Files:**
- Modify: `main.swift:275`, `Setup.swift:95,99,113,131,143`, `README.md`

- [ ] **Step 1: Mac app wording**

- `main.swift:275`: `"Set up iPhone…"` → `"Set up phone…"`.
- `Setup.swift`: in the visible `Text(...)`/`detail:` strings at lines 95, 99, 113, 131, 143, replace "iPhone" with "phone" ("your iPhone" → "your phone", "On your iPhone" → "On your phone"). Leave comments.

Run: `grep -n 'iPhone' Setup.swift main.swift | grep -v '//'` → no output.

- [ ] **Step 2: README**

- Intro: "and an iPhone app" → "and a phone app (iPhone or Android, such as Samsung)".
- Step 1: under "On your iPhone" add "**On an Android phone (Samsung…)**: install **Tailscale** from Google Play, sign in with the same account, allow the VPN."
- Step 5: add an Android block: "Point the **Camera** at the QR code and tap the link (it opens in Chrome). Tap **⋮** → **Add to Home screen** (or **Install app**). Open **Lid Awake** from the Home screen and type the phone password. Optional: ⚙︎ → **Fingerprint** and **Alerts**."
- Replace "Set up iPhone…" with "Set up phone…" everywhere.

- [ ] **Step 3: Build, run every check, try on a Samsung**

Run: `./build.sh && sleep 5 && tests/page_check.sh && tests/words_check.sh && tests/platform_check.sh && tests/live_checks.sh`
Expected: all `PASS`.
Manual, on a Samsung with Tailscale: scan the QR, install to Home screen, sign in with the password, add fingerprint, turn on an alert and get one (turn Lid Awake off), open the Screen tab (video plays), use the trackpad and keyboard.

- [ ] **Step 4: Commit**

```bash
git add main.swift Setup.swift README.md
git commit -m "Mac + Android: say phone, not iPhone, and explain Samsung setup"
```

**Phase 1 done: Mac + iPhone and Mac + Samsung both work.**

---

# Phase 2 — Windows foundation

Goal of this phase: on a Windows laptop, a tray app that keeps the PC awake with the lid closed, and the same phone page (iPhone or Samsung) can sign in, see status, battery, CPU and memory, and turn it off. Screen, control, apps, alerts, fingerprint and camera come in Phase 3; their parts of the page are hidden through `features`.

Requires on the Windows PC: Windows 10 1903+ or 11, .NET 8 SDK (to build), Tailscale for Windows.

### Phase 2 execution notes (added 2026-10-19)

- **Builds and tests run on GitHub Actions** (`windows-latest`), not on the Mac: `.github/workflows/windows.yml` (Task 7b). It runs only for pushes to `claude/windows-phase-2` that touch `windows/`, `web/` or the workflow, plus a manual run button; 15-minute timeout; telemetry off. When Phase 2 is finished it is disabled with `gh workflow disable windows.yml`.
- **"Verify by hand" steps** that need a real Windows laptop (tray icon, closing the lid, real battery, phones over Tailscale) are collected into one checklist for the owner at the end instead of being done per task.
- **Where a by-hand step can be automated on the CI machine, it becomes a test:** KeepAwake's `powercfg` lid values (Task 9: set to 0 when on, restored when off), and the web server end to end on 127.0.0.1:8787 (Task 11: page + CSP, static files, loopback rules, POST header rule, password login → cookie → `/status`).

### Task 7b: Windows build on GitHub Actions

**Files:**
- Create: `.github/workflows/windows.yml`

- [ ] **Step 1: Write the workflow**

```yaml
# Builds and tests the Windows version of Lid Awake on a Windows machine at GitHub.
# Only for the Windows branch; turned off (gh workflow disable windows.yml) when not needed.
name: Windows
on:
  push:
    branches: [claude/windows-phase-2]
    paths: ["windows/**", "web/**", ".github/workflows/windows.yml"]
  workflow_dispatch:
concurrency:
  group: windows-${{ github.ref }}
  cancel-in-progress: true
permissions:
  contents: read
jobs:
  build:
    runs-on: windows-latest
    timeout-minutes: 15
    env:
      DOTNET_CLI_TELEMETRY_OPTOUT: "1"
      DOTNET_NOLOGO: "1"
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "8.0.x"
      - name: Build
        run: dotnet build windows/LidAwake.Windows/LidAwake.Windows.csproj -c Release
      - name: Test
        if: hashFiles('windows/LidAwake.Windows.Tests/*.csproj') != ''
        run: dotnet test windows/LidAwake.Windows.Tests/LidAwake.Windows.Tests.csproj -c Release --logger "console;verbosity=normal"
```

- [ ] **Step 2: Commit** (pushed together with Task 8, the first time there is something to build)

```bash
git add .github/workflows/windows.yml
git commit -m "Windows: build and test on GitHub Actions"
```

### Task 8: Windows project skeleton and tray icon

**Files:**
- Create: `windows/LidAwake.Windows/LidAwake.Windows.csproj`
- Create: `windows/LidAwake.Windows/Program.cs`
- Create: `windows/LidAwake.Windows/Settings.cs`
- Create: `windows/LidAwake.Windows.Tests/LidAwake.Windows.Tests.csproj`

- [ ] **Step 1: Create the projects**

`windows/LidAwake.Windows/LidAwake.Windows.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>LidAwake</AssemblyName>
    <RootNamespace>LidAwake</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <!-- The shared phone page, next to the exe. -->
    <None Include="..\..\web\**\*" Link="web\%(RecursiveDir)%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
    <None Include="..\..\assets\sky-*.jpg" Link="sky\%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

`windows/LidAwake.Windows.Tests/LidAwake.Windows.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\LidAwake.Windows\LidAwake.Windows.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Settings (the Windows `UserDefaults`)**

`windows/LidAwake.Windows/Settings.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LidAwake;

// Everything Lid Awake remembers, in %APPDATA%\LidAwake\settings.json (the Mac keeps the same
// things in UserDefaults). Written whole on every change; small enough not to matter.
public sealed class Settings
{
    readonly string path;
    readonly object gate = new();
    JsonObject data;

    public Settings(string? folder = null)
    {
        folder ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LidAwake");
        Directory.CreateDirectory(folder);
        path = Path.Combine(folder, "settings.json");
        data = File.Exists(path) ? (JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new()) : new();
    }

    public T? Get<T>(string key)
    {
        lock (gate) return data.TryGetPropertyValue(key, out var v) && v is not null ? v.Deserialize<T>() : default;
    }

    public void Set<T>(string key, T? value)
    {
        lock (gate)
        {
            data[key] = value is null ? null : JsonSerializer.SerializeToNode(value);
            File.WriteAllText(path, data.ToJsonString());
        }
    }
}
```

- [ ] **Step 3: Tray app**

`windows/LidAwake.Windows/Program.cs`:

```csharp
namespace LidAwake;

// Lid Awake for Windows: a tray icon with the on/off switch. The phone page and its requests
// are the same as on the Mac (docs/API.md).
static class Program
{
    [STAThread]
    static void Main()
    {
        using var single = new Mutex(true, "local.lidawake", out bool first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(new Settings()));
    }
}

sealed class TrayApp : ApplicationContext
{
    readonly Settings settings;
    readonly NotifyIcon icon;
    readonly ToolStripMenuItem toggle = new("Lid Awake on");

    public bool IsOn { get; private set; }

    public TrayApp(Settings settings)
    {
        this.settings = settings;
        toggle.Click += (_, _) => SetAwake(!IsOn);
        var menu = new ContextMenuStrip();
        menu.Items.Add(toggle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Lid Awake", null, (_, _) => { SetAwake(false); icon!.Visible = false; ExitThread(); });
        icon = new NotifyIcon { Icon = SystemIcons.Application, Text = "Lid Awake", ContextMenuStrip = menu, Visible = true };
    }

    public void SetAwake(bool on)
    {
        IsOn = on;
        toggle.Checked = on;
        icon.Text = on ? "Lid Awake: on" : "Lid Awake: off";
    }
}
```

- [ ] **Step 4: Build**

Run (on Windows, in `windows\`): `dotnet build LidAwake.Windows\LidAwake.Windows.csproj`
Expected: `Build succeeded.` Run `LidAwake.Windows\bin\Debug\net8.0-windows10.0.19041.0\LidAwake.exe`: a tray icon appears; its menu toggles "Lid Awake on".

- [ ] **Step 5: Commit**

```bash
git add windows/
git commit -m "Windows: project skeleton with a tray switch"
```

---

### Task 9: Keep the PC awake with the lid closed

**Files:**
- Create: `windows/LidAwake.Windows/KeepAwake.cs`
- Modify: `windows/LidAwake.Windows/Program.cs` (`SetAwake`)

How Windows does what `pmset disablesleep` does on the Mac: (1) set the power plan's **lid close action** to "Do nothing" for plugged-in and battery, remembering the old values; (2) hold `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_AWAYMODE_REQUIRED)` so idle sleep doesn't happen. Both are undone when turned off and on quit. Neither needs admin rights.

- [ ] **Step 1: Write `KeepAwake.cs`**

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace LidAwake;

// Lid closed and idle: the PC keeps running while Lid Awake is on. The lid action is a power-plan
// setting (powercfg), the idle part a request held by this app; both are put back when turned off.
public static class KeepAwake
{
    const string Sub = "4f971e89-eebd-4455-a8de-9e59040e7347";      // SUB_BUTTONS
    const string LidAction = "5ca83367-6e45-459f-a27b-476b1d01c936"; // LIDACTION
    const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_AWAYMODE_REQUIRED = 0x40;

    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);

    static Thread? holder;
    static ManualResetEventSlim? release;

    public static void On(Settings settings)
    {
        var (ac, dc) = ReadLidAction();
        if (settings.Get<int?>("savedLidAc") is null) { settings.Set("savedLidAc", ac); settings.Set("savedLidDc", dc); }
        SetLidAction(0, 0);  // 0 = do nothing
        if (holder != null) return;
        // The request belongs to a thread, so one quiet thread holds it until Off().
        release = new ManualResetEventSlim(false);
        holder = new Thread(() => {
            SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_AWAYMODE_REQUIRED);
            release.Wait();
            SetThreadExecutionState(ES_CONTINUOUS);
        }) { IsBackground = true, Name = "keep-awake" };
        holder.Start();
    }

    public static void Off(Settings settings)
    {
        // Only put back what On() saved: never touch a lid setting Lid Awake didn't change.
        if (settings.Get<int?>("savedLidAc") is int ac && settings.Get<int?>("savedLidDc") is int dc)
        {
            SetLidAction(ac, dc);
            settings.Set<int?>("savedLidAc", null);
            settings.Set<int?>("savedLidDc", null);
        }
        release?.Set();
        holder = null;
    }

    public static (int ac, int dc) ReadLidAction()
    {
        var output = Run($"/query SCHEME_CURRENT {Sub} {LidAction}");
        var values = Regex.Matches(output, @"0x([0-9a-fA-F]{8})").Select(m => Convert.ToInt32(m.Groups[1].Value, 16)).ToList();
        // The last two hex values are the current AC and DC settings.
        return values.Count >= 2 ? (values[^2], values[^1]) : (1, 1);
    }

    static void SetLidAction(int ac, int dc)
    {
        Run($"/setacvalueindex SCHEME_CURRENT {Sub} {LidAction} {ac}");
        Run($"/setdcvalueindex SCHEME_CURRENT {Sub} {LidAction} {dc}");
        Run("/setactive SCHEME_CURRENT");
    }

    static string Run(string args)
    {
        var p = Process.Start(new ProcessStartInfo("powercfg.exe", args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }
}
```

- [ ] **Step 2: Call it from the tray**

In `TrayApp.SetAwake`, first line:

```csharp
        if (on) KeepAwake.On(settings); else KeepAwake.Off(settings);
```

And at startup, put back a lid action left over from a crash: at the end of the `TrayApp` constructor add `KeepAwake.Off(settings);`.

- [ ] **Step 3: Verify by hand**

Run `powercfg /query SCHEME_CURRENT SUB_BUTTONS LIDACTION` → note the values. Turn Lid Awake on → the same query shows `0x00000000` for both. Close the lid for 2 minutes with a `ping -t` from another device to the PC's Tailscale IP: pings keep answering. Turn off → the original values are back.

- [ ] **Step 4: Commit**

```bash
git add windows/LidAwake.Windows/KeepAwake.cs windows/LidAwake.Windows/Program.cs
git commit -m "Windows: keep running with the lid closed while on"
```

---

### Task 10: Password and sessions (same rules as `Auth.swift`)

**Files:**
- Create: `windows/LidAwake.Windows/Auth.cs`
- Test: `windows/LidAwake.Windows.Tests/AuthTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using LidAwake;
using Xunit;

public class AuthTests
{
    static Auth NewAuth() => new(new Settings(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())), iterations: 1000);

    [Fact] public void NoPasswordYet() => Assert.Equal(LoginResult.Kind.NoPassword, NewAuth().Login("anything", "Phone").Result);

    [Fact] public void RightPasswordGivesValidSession()
    {
        var a = NewAuth(); a.SetPassword("correct horse battery");
        var r = a.Login("correct horse battery", "Phone");
        Assert.Equal(LoginResult.Kind.Ok, r.Result);
        Assert.True(a.IsValid(r.Token));
        Assert.False(a.IsValid("not-a-token"));
    }

    [Fact] public void WrongPasswordRefusedThenLocked()
    {
        var a = NewAuth(); a.SetPassword("correct horse battery");
        for (int i = 0; i < Auth.MaxTries; i++) Assert.Equal(LoginResult.Kind.Wrong, a.Login("nope", "Phone").Result);
        Assert.Equal(LoginResult.Kind.Locked, a.Login("correct horse battery", "Phone").Result);
    }

    [Fact] public void LogoutEndsSession()
    {
        var a = NewAuth(); a.SetPassword("correct horse battery");
        var t = a.Login("correct horse battery", "Phone").Token!;
        a.Logout(t);
        Assert.False(a.IsValid(t));
    }

    [Fact] public void NewPasswordSignsEveryoneOut()
    {
        var a = NewAuth(); a.SetPassword("correct horse battery");
        var t = a.Login("correct horse battery", "Phone").Token!;
        a.SetPassword("another long password");
        Assert.False(a.IsValid(t));
    }
}
```

- [ ] **Step 2: Run to make sure they fail**

Run: `dotnet test windows\LidAwake.Windows.Tests`
Expected: build error, `Auth` not found.

- [ ] **Step 3: Write `Auth.cs`**

```csharp
using System.Security.Cryptography;
using System.Text;

namespace LidAwake;

public record LoginResult(LoginResult.Kind Result, string? Token = null, int Seconds = 0)
{
    public enum Kind { Ok, Wrong, Locked, NoPassword }
}

// Password lock for the phone page, as on the Mac: only a salted PBKDF2-SHA256 hash of the
// password is kept, sessions are SHA-256 hashes of random tokens (7 days), and too many wrong
// tries lock sign-in for a while.
public sealed class Auth
{
    public const string CookieName = "__Host-la_session";
    public const double SessionDays = 7;
    public const int MinPasswordLength = 10;
    public const int MaxTries = 5;
    const int LockSeconds = 60;

    record Session(string Hash, string Device, long Created);

    readonly Settings settings;
    readonly int iterations;
    readonly object gate = new();
    int wrongTries;
    DateTime lockedUntil = DateTime.MinValue;

    public Auth(Settings settings, int iterations = 600_000) { this.settings = settings; this.iterations = iterations; }

    public bool HasPassword => settings.Get<string>("pwHash") != null;

    public void SetPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        settings.Set("pwSalt", Convert.ToBase64String(salt));
        settings.Set("pwHash", Convert.ToBase64String(Derive(password, salt)));
        settings.Set("sessions", new List<Session>());  // a new password signs everyone out
    }

    public LoginResult Login(string password, string device)
    {
        lock (gate)
        {
            if (!HasPassword) return new(LoginResult.Kind.NoPassword);
            if (DateTime.UtcNow < lockedUntil) return new(LoginResult.Kind.Locked, Seconds: (int)Math.Ceiling((lockedUntil - DateTime.UtcNow).TotalSeconds));
            var salt = Convert.FromBase64String(settings.Get<string>("pwSalt")!);
            var hash = Convert.FromBase64String(settings.Get<string>("pwHash")!);
            if (!CryptographicOperations.FixedTimeEquals(Derive(password, salt), hash))
            {
                if (++wrongTries >= MaxTries) { lockedUntil = DateTime.UtcNow.AddSeconds(LockSeconds); wrongTries = 0; }
                return new(LoginResult.Kind.Wrong);
            }
            wrongTries = 0;
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var sessions = Live();
            sessions.Add(new Session(HashToken(token), device, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            settings.Set("sessions", sessions);
            return new(LoginResult.Kind.Ok, token);
        }
    }

    public bool IsValid(string? token) => token != null && Live().Any(s => s.Hash == HashToken(token));

    public void Logout(string? token)
    {
        if (token == null) return;
        lock (gate) settings.Set("sessions", Live().Where(s => s.Hash != HashToken(token)).ToList());
    }

    List<Session> Live()
    {
        var oldest = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)(SessionDays * 86400);
        return (settings.Get<List<Session>>("sessions") ?? new()).Where(s => s.Created > oldest).ToList();
    }

    byte[] Derive(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test windows\LidAwake.Windows.Tests`
Expected: `Passed! - Failed: 0, Passed: 5`.

- [ ] **Step 5: Tray item to set the password**

In `TrayApp`, add a menu item `"Set phone password…"` that opens a small `Form` with two `TextBox { UseSystemPasswordChar = true }`, refuses fewer than `Auth.MinPasswordLength` characters or a mismatch, and calls `auth.SetPassword(...)`. Pass one `Auth` instance (created in `Main`) to both `TrayApp` and the web server (Task 11).

- [ ] **Step 6: Commit**

```bash
git add windows/
git commit -m "Windows: phone password and sessions, same rules as the Mac"
```

---

### Task 11: Serve the shared page with the same checks as the Mac

**Files:**
- Create: `windows/LidAwake.Windows/Tailnet.cs`
- Create: `windows/LidAwake.Windows/PageHeaders.cs`
- Create: `windows/LidAwake.Windows/WebServer.cs`
- Test: `windows/LidAwake.Windows.Tests/PageHeadersTests.cs`

- [ ] **Step 1: Failing test for the CSP script hashes**

```csharp
using LidAwake;
using Xunit;

public class PageHeadersTests
{
    [Fact] public void HashesEveryInlineScript()
    {
        var html = "<script>a()</script><p>x</p><script>\n b();\n</script>";
        var hashes = PageHeaders.ScriptHashes(html);
        Assert.Equal(2, hashes.Split(' ').Length);
        Assert.Contains("'sha256-", hashes);
        var csp = PageHeaders.Csp(html, "pc.tail1234.ts.net");
        Assert.Contains("connect-src 'self' wss://pc.tail1234.ts.net", csp);
    }
}
```

Run: `dotnet test windows\LidAwake.Windows.Tests` → fails, `PageHeaders` not found.

- [ ] **Step 2: Write `PageHeaders.cs`** (port of `Server.swift:313-331`)

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LidAwake;

// The page may only run its own inline scripts and talk to this computer, as on the Mac.
public static class PageHeaders
{
    public static string ScriptHashes(string html) => string.Join(" ",
        Regex.Matches(html, "<script>([\\s\\S]*?)</script>")
             .Select(m => "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(m.Groups[1].Value))) + "'"));

    public static string Csp(string html, string host) =>
        $"default-src 'none'; script-src {ScriptHashes(html)}; style-src 'unsafe-inline'; "
        + $"img-src 'self' blob:; connect-src 'self' wss://{host}; worker-src 'self'; manifest-src 'self'; "
        + "base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    public static readonly Dictionary<string, string> Common = new()
    {
        ["Cache-Control"] = "no-store", ["X-Content-Type-Options"] = "nosniff", ["X-Frame-Options"] = "DENY",
        ["Cross-Origin-Resource-Policy"] = "same-origin", ["Referrer-Policy"] = "no-referrer",
    };
}
```

Run tests → `Passed`.

- [ ] **Step 3: Write `Tailnet.cs`** (port of `Server.swift:28-` `Tailnet`)

```csharp
using System.Diagnostics;
using System.Text.Json;

namespace LidAwake;

// Who may use the page: this PC's own Tailscale name, from a device signed in to the PC's
// Tailscale account. Learned from `tailscale status --json` and remembered.
public sealed class Tailnet
{
    public const string Exe = @"C:\Program Files\Tailscale\tailscale.exe";
    readonly Settings settings;
    public Tailnet(Settings settings) => this.settings = settings;

    public string? Host => settings.Get<string>("tailnetHost");
    public string? Owner => settings.Get<string>("tailnetOwner");

    public void Refresh()
    {
        var json = Run("status --json");
        if (json == null) return;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var dns = root.GetProperty("Self").GetProperty("DNSName").GetString() ?? "";
        if (!dns.EndsWith(".ts.net.")) return;
        settings.Set("tailnetHost", dns.TrimEnd('.').ToLowerInvariant());
        var userId = root.GetProperty("Self").GetProperty("UserID").GetRawText();
        if (root.TryGetProperty("User", out var users) && users.TryGetProperty(userId, out var me))
            settings.Set("tailnetOwner", me.GetProperty("LoginName").GetString());
    }

    // https://<pc>.ts.net/ → the page, /live → the screen socket (later).
    public void Serve() => Run("serve --bg --https=443 http://127.0.0.1:8787");
    public void Unserve() => Run("serve --https=443 off");

    public static bool IsLoopback(string host) => host is "127.0.0.1" or "localhost" or "[::1]";

    public bool IsOwner(string host, string? login) =>
        Host != null && Owner != null && host == Host && string.Equals(login, Owner, StringComparison.OrdinalIgnoreCase);

    static string? Run(string args)
    {
        if (!File.Exists(Exe)) return null;
        var p = Process.Start(new ProcessStartInfo(Exe, args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? output : null;
    }
}
```

- [ ] **Step 4: Write `WebServer.cs`** — static files, the guards from `docs/API.md`, `/auth`, `/login`, `/logout`

```csharp
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace LidAwake;

// The phone page and its requests on 127.0.0.1:8787, same rules as Server.swift (docs/API.md).
public sealed class WebServer
{
    readonly Auth auth;
    readonly Tailnet tailnet;
    readonly Func<bool> isOn;
    readonly Func<object> status;
    readonly Action turnOff;
    readonly string web = Path.Combine(AppContext.BaseDirectory, "web");
    readonly string html;

    public WebServer(Auth auth, Tailnet tailnet, Func<bool> isOn, Func<object> status, Action turnOff)
    {
        (this.auth, this.tailnet, this.isOn, this.status, this.turnOff) = (auth, tailnet, isOn, status, turnOff);
        html = File.ReadAllText(Path.Combine(web, "index.html")).Replace("<!--SPLASH-->", "");
    }

    static readonly string[] StaticPaths = { "/sw.js", "/manifest.webmanifest", "/apple-touch-icon.png", "/apple-touch-icon-precomposed.png", "/icon-512.png" };

    public void Start()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:8787");
        var app = builder.Build();
        app.Run(Handle);
        _ = app.RunAsync();
    }

    async Task Handle(HttpContext c)
    {
        var req = c.Request;
        foreach (var (k, v) in PageHeaders.Common) c.Response.Headers[k] = v;
        var host = (req.Host.Host ?? "").ToLowerInvariant();
        var path = req.Path.Value ?? "/";
        bool isStatic = StaticPaths.Contains(path) || path.StartsWith("/sky/");

        if (req.Headers.ContainsKey("Tailscale-Funnel-Request")) { await Text(c, 403, "Forbidden"); return; }
        if (Tailnet.IsLoopback(host))
        {
            if (path == "/") { await Page(c, host); return; }
            if (path == "/auth") { await Json(c, new { loopback = true, url = tailnet.Host is { } h ? $"https://{h}/" : "" }); return; }
            if (!isStatic) { await Text(c, 403, "Open Lid Awake at its Tailscale address."); return; }
        }
        else if (!tailnet.IsOwner(host, req.Headers["Tailscale-User-Login"]))
        {
            await Text(c, 403, "This device isn't signed in to the Tailscale account this PC uses."); return;
        }
        var site = req.Headers["Sec-Fetch-Site"].ToString();
        if (!isStatic && path != "/" && site != "" && site != "same-origin" && site != "none") { await Text(c, 403, "Forbidden"); return; }

        if (isStatic) { await Static(c, path); return; }
        if (path == "/") { await Page(c, host); return; }
        if (!isOn())
        {
            if (path == "/auth") { await Json(c, new { off = true }); return; }
            c.Response.StatusCode = 503; return;  // the page treats silence/failure as "off"
        }
        if (req.Method == "POST" && req.Headers["X-LidAwake"] != "1") { await Text(c, 403, "Forbidden"); return; }

        var token = req.Cookies[Auth.CookieName];
        switch (req.Method, path)
        {
            case ("GET", "/auth"):
                await Json(c, new { hasPassword = auth.HasPassword, loggedIn = auth.IsValid(token), faceID = false }); return;
            case ("POST", "/login"):
            {
                var body = await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(req.Body);
                var pw = body != null && body.TryGetValue("password", out var p) ? p.GetString() ?? "" : "";
                var r = auth.Login(pw, "Phone");
                switch (r.Result)
                {
                    case LoginResult.Kind.Ok:
                        c.Response.Headers.Append("Set-Cookie", $"{Auth.CookieName}={r.Token}; Path=/; Max-Age={(int)(Auth.SessionDays * 86400)}; HttpOnly; SameSite=Strict; Secure");
                        c.Response.StatusCode = 204; return;
                    case LoginResult.Kind.Wrong: await Text(c, 401, "Wrong password."); return;
                    case LoginResult.Kind.Locked: await Text(c, 429, $"Too many wrong tries. Try again in {r.Seconds} seconds."); return;
                    default: await Text(c, 409, "No password is set yet."); return;
                }
            }
        }
        if (!auth.IsValid(token)) { await Text(c, 401, "Sign in first."); return; }
        switch (req.Method, path)
        {
            case ("POST", "/logout"):
                auth.Logout(token);
                c.Response.Headers.Append("Set-Cookie", $"{Auth.CookieName}=; Path=/; Max-Age=0; HttpOnly; SameSite=Strict; Secure");
                c.Response.StatusCode = 204; return;
            case ("GET", "/status"):
                await Json(c, status()); return;
            case ("POST", "/awake"):
            {
                var body = await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(req.Body);
                if (body == null || !body.TryGetValue("on", out var on) || on.ValueKind != JsonValueKind.False)
                { await Text(c, 400, "Lid-awake can only be turned on from the PC."); return; }
                turnOff(); c.Response.StatusCode = 204; return;
            }
            default:
                await Text(c, 404, "Not available on Windows yet."); return;
        }
    }

    Task Page(HttpContext c, string host)
    {
        c.Response.Headers["Content-Security-Policy"] = PageHeaders.Csp(html, host);
        c.Response.ContentType = "text/html; charset=utf-8";
        return c.Response.WriteAsync(html);
    }

    Task Static(HttpContext c, string path)
    {
        string? file = path switch
        {
            "/sw.js" => Path.Combine(web, "sw.js"),
            "/manifest.webmanifest" => Path.Combine(web, "manifest.webmanifest"),
            "/apple-touch-icon.png" or "/apple-touch-icon-precomposed.png" or "/icon-512.png" => Path.Combine(AppContext.BaseDirectory, "icon-512.png"),
            _ when path.StartsWith("/sky/") && new[] { "sunrise.jpg", "day.jpg", "sunset.jpg", "night.jpg" }.Contains(path[5..])
                => Path.Combine(AppContext.BaseDirectory, "sky", "sky-" + path[5..]),
            _ => null,
        };
        if (file == null || !File.Exists(file)) return Text(c, 404, "Not found");
        c.Response.ContentType = Path.GetExtension(file) switch
        {
            ".js" => "text/javascript; charset=utf-8", ".webmanifest" => "application/manifest+json",
            ".png" => "image/png", _ => "image/jpeg",
        };
        return c.Response.SendFileAsync(file);
    }

    static Task Text(HttpContext c, int code, string msg) { c.Response.StatusCode = code; c.Response.ContentType = "text/plain; charset=utf-8"; return c.Response.WriteAsync(msg); }
    static Task Json(HttpContext c, object o) { c.Response.ContentType = "application/json"; return c.Response.WriteAsync(JsonSerializer.Serialize(o)); }
}
```

Icons: generate `icon-512.png` once on the Mac with `tools/make_icons.swift` (it already draws the app icon) and commit it to `windows/LidAwake.Windows/icon-512.png` with `CopyToOutputDirectory`.

- [ ] **Step 5: Start it from the tray**

In `Main`: create `Settings`, `Auth`, `Tailnet` (call `Refresh()`), `TrayApp`, then `new WebServer(auth, tailnet, () => tray.IsOn, () => StatusInfo.Build(tray.IsOn, Environment.MachineName), () => tray.OnUi(() => tray.SetAwake(false))).Start()`. Add to `TrayApp` (the tray must be changed on its own thread; the web server runs on others): a field `readonly SynchronizationContext ui;` set as the last line of the constructor with `ui = SynchronizationContext.Current!;` (WinForms installs it once the menu control exists), and `public void OnUi(Action a) => ui.Post(_ => a(), null);`. `StatusInfo` comes in Task 12 — until then pass `() => new { lidAwake = tray.IsOn, os = "windows" }`. In `SetAwake`: `if (on) tailnet.Serve();` (Serve is idempotent; it's how Mac's install sets it up too).

- [ ] **Step 6: Verify**

Run the app, then in PowerShell: `curl.exe -si http://127.0.0.1:8787/ | Select-Object -First 12`
Expected: `HTTP/1.1 200`, a `Content-Security-Policy` with two `sha256-` hashes, and the page HTML.
Then, from the repo root: `node tests\page_check.js web\index.html` → all `PASS` (the splash marker is replaced by nothing on Windows, which the check accepts).

- [ ] **Step 7: Commit**

```bash
git add windows/
git commit -m "Windows: serve the shared phone page with the Mac's checks, password sign-in"
```

---

### Task 12: Status for the phone (`/status` on Windows)

**Files:**
- Create: `windows/LidAwake.Windows/StatusInfo.cs`
- Test: `windows/LidAwake.Windows.Tests/StatusInfoTests.cs`

- [ ] **Step 1: Failing test — every field the page needs, with safe defaults**

```csharp
using System.Text.Json;
using LidAwake;
using Xunit;

public class StatusInfoTests
{
    [Fact] public void HasEveryFieldThePageReads()
    {
        var json = JsonSerializer.Serialize(StatusInfo.Build(isOn: true, host: "PC"));
        using var doc = JsonDocument.Parse(json);
        var s = doc.RootElement;
        Assert.Equal("windows", s.GetProperty("os").GetString());
        Assert.Contains("memory", s.GetProperty("features").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain("screen", s.GetProperty("features").EnumerateArray().Select(e => e.GetString()));
        foreach (var key in new[] { "host", "lidAwake", "timerMinutes", "apps", "otherApps", "claude", "batteryGuard", "heatGuard",
                                    "batteryLimit", "maxBatteryTemp", "thermalState", "remoteScreen", "remoteControl", "cameraAllowed",
                                    "screenPermission", "controlPermission", "screenWidth", "screenOff", "lidClosed", "displayAsleep" })
            Assert.True(s.TryGetProperty(key, out _), key);
        Assert.Equal(JsonValueKind.Array, s.GetProperty("apps").ValueKind);
    }
}
```

Run → fails, `StatusInfo` not found.

- [ ] **Step 2: Write `StatusInfo.cs`**

```csharp
using System.Runtime.InteropServices;

namespace LidAwake;

// /status on Windows: the same fields as the Mac's statusJSON (docs/API.md), with what Windows
// can't tell sent as the safe default, and "features" saying what this PC can do so far.
public static class StatusInfo
{
    public static readonly string[] Features = { "memory" };  // grows as Phase 3 lands

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    public static Dictionary<string, object?> Build(bool isOn, string host)
    {
        var s = new Dictionary<string, object?>
        {
            ["os"] = "windows", ["features"] = Features,
            ["host"] = host, ["lidAwake"] = isOn, ["timerMinutes"] = 0,
            ["apps"] = Array.Empty<object>(), ["otherApps"] = Array.Empty<object>(), ["claude"] = null,
            ["batteryGuard"] = false, ["heatGuard"] = false, ["batteryLimit"] = 0, ["maxBatteryTemp"] = 0,
            ["thermalState"] = "nominal",
            ["remoteScreen"] = false, ["remoteControl"] = false, ["cameraAllowed"] = false,
            ["screenPermission"] = false, ["controlPermission"] = false,
            ["screenWidth"] = Screen.PrimaryScreen?.Bounds.Width ?? 0, ["screenOff"] = false,
            ["lidClosed"] = false, ["displayAsleep"] = false,
            ["uptime"] = Environment.TickCount64 / 1000,
        };
        if (GetSystemPowerStatus(out var p) && p.BatteryFlag != 128 && p.BatteryLifePercent <= 100)
        {
            s["battery"] = (int)p.BatteryLifePercent;
            s["onBattery"] = p.ACLineStatus == 0;
            s["charging"] = (p.BatteryFlag & 8) != 0;
            s["charged"] = p.ACLineStatus == 1 && p.BatteryLifePercent == 100;
            s["minutesToEmpty"] = p.BatteryLifeTime > 0 ? p.BatteryLifeTime / 60 : -1;
            s["minutesToFull"] = -1;
        }
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref m)) s["memTotal"] = m.ullTotalPhys;
        return s;
    }
}
```

Then compare against `docs/API.md` (Task 6, Step 4): for every `/status` key the Mac sends that isn't here (`mac`, `net`, `memTotal` shape, CPU load…), add it with the documented safe default or a real Windows value (CPU: `PerformanceCounter("Processor", "% Processor Time", "_Total")`; network: `NetworkInterface.GetAllNetworkInterfaces()` byte counters). Add each key to the test's list.

- [ ] **Step 3: Run tests**

Run: `dotnet test windows\LidAwake.Windows.Tests` → all pass.

- [ ] **Step 4: Wire it in** — in `Main`, replace the temporary status lambda with `() => StatusInfo.Build(tray.IsOn, Environment.MachineName)`.

- [ ] **Step 5: Commit**

```bash
git add windows/
git commit -m "Windows: status for the phone (battery, memory, uptime) with Mac-compatible fields"
```

---

### Task 13: The page hides what this computer can't do yet, and looks like a PC

**Files:**
- Modify: `web/index.html`

- [ ] **Step 1: Hide unsupported parts**

At the end of `learnDevice(status)` (Task 4) add a call to `applyFeatures()`, and define it right after `learnDevice`:

```js
  // Parts of the page the computer can't do (yet) are hidden, and never call their requests.
  function applyFeatures() {
    document.querySelector('.tabs [data-tab="screen"]').hidden = !can("screen");
    document.querySelector('.tabs [data-tab="apps"]').hidden = !can("apps");
    $("alertsSection").hidden = !can("push");
    $("faceIdRow").hidden = $("faceIdRow").hidden || !can("passkeys");
    $("camNotch").hidden = !can("camera");
    document.querySelectorAll("[data-feature]").forEach(el => { el.hidden = !can(el.dataset.feature); });
  }
```

Then find the history graphs and the memory row: `grep -n 'histRange\|id="mem' web/index.html`. Add `data-feature="history"` to the section that contains `#histRange`, and `data-feature="memory"` to the row that opens the Memory window.

Before each fetch of a feature's request, guard it. Find them with `grep -nE 'fetch(Within)?\(\s*\d*,?\s*"(passkey|push|camera|temp-history|battery-history|cpu-history|memory|activity|sessions|open-app|focus-app|quit-app|apps-order|screen)' web/index.html` and add `if (!can("<feature>")) return;` at the start of the function that makes the call (feature from the table in Task 6, Step 3).

- [ ] **Step 2: Computer words**

Run `node -e "..."`-style check (copy `tests/words_check.js` to `tests/computer_words_check.js`, change the pattern to `/\bMac\b|MacBook/`, and the allowed span to `data-w="computer"`). For every reported visible "Mac": wrap it as `<span data-w="computer">Mac</span>` in HTML, or `${word("computer")}` in JS. Keep "MacBook" only inside the drawing code.

- [ ] **Step 3: A laptop drawing without the Apple logo on Windows**

Add to the page's `<style>`:

```css
  [data-computer="windows"] .apple-logo { display: none; }
```

(The MacBook drawing's notch is the camera button; with `camera` missing on Windows it is already hidden by Step 1.)

- [ ] **Step 4: Verify on Mac and Windows**

Mac: `./build.sh && tests/page_check.sh && tests/words_check.sh` → all PASS; on the iPhone everything is still there.
Windows: rebuild (`dotnet build`), open the page on a Samsung and an iPhone through Tailscale: sign in works, Status shows battery and memory, there are no Screen/Apps tabs, no alerts section, no camera notch, keys say Ctrl/Alt (when Phase 3 brings the keyboard), text says "PC". Turning Lid Awake off from the phone turns the tray switch off.

- [ ] **Step 5: Commit**

```bash
git add web/index.html tests/computer_words_check.js
git commit -m "Phone page: hide what the computer can't do, say PC on Windows"
```

---

### Task 14: Windows installer and uninstaller

**Files:**
- Create: `windows/install.ps1`, `windows/uninstall.ps1`
- Create: `Install Lid Awake (Windows).cmd`, `Uninstall Lid Awake (Windows).cmd`

- [ ] **Step 1: `windows/install.ps1`**

```powershell
# Installs Lid Awake for Windows: checks Tailscale, builds, copies to %LOCALAPPDATA%\LidAwake,
# starts at sign-in, and starts it now. Run again to update; settings stay.
$ErrorActionPreference = "Stop"
$src = Split-Path -Parent $MyInvocation.MyCommand.Path
$dest = Join-Path $env:LOCALAPPDATA "LidAwake"
function ok($m) { Write-Host "  ✓ $m" -ForegroundColor Green }
function fail($m) { Write-Host "  ✗ $m" -ForegroundColor Red; Read-Host "Press Enter to close"; exit 1 }

$ts = "C:\Program Files\Tailscale\tailscale.exe"
if (!(Test-Path $ts)) { Start-Process "https://tailscale.com/download/windows"; fail "Install Tailscale and sign in, then run this again." }
& $ts status *> $null; if ($LASTEXITCODE -ne 0) { fail "Sign in to Tailscale, then run this again." }
ok "Tailscale"

if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { Start-Process "https://dotnet.microsoft.com/download/dotnet/8.0"; fail "Install the .NET 8 SDK, then run this again." }
Get-Process LidAwake -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet publish "$src\LidAwake.Windows\LidAwake.Windows.csproj" -c Release -r win-x64 --self-contained -o $dest | Out-Null
if ($LASTEXITCODE -ne 0) { fail "Build failed." }
ok "Lid Awake installed in $dest"

Set-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "LidAwake" -Value "`"$dest\LidAwake.exe`""
ok "Starts when you sign in"
Start-Process "$dest\LidAwake.exe"
Write-Host "`nAlmost done: right-click the Lid Awake icon by the clock → Set phone password…, then turn it on."
Read-Host "Press Enter to close"
```

- [ ] **Step 2: `windows/uninstall.ps1`**

```powershell
$ErrorActionPreference = "SilentlyContinue"
$dest = Join-Path $env:LOCALAPPDATA "LidAwake"
Get-Process LidAwake | Stop-Process -Force
& "C:\Program Files\Tailscale\tailscale.exe" serve --https=443 off *> $null
Remove-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "LidAwake"
# Put the lid action back if Lid Awake was on (values saved in settings.json).
$settings = Join-Path $env:APPDATA "LidAwake\settings.json"
if (Test-Path $settings) {
  $s = Get-Content $settings | ConvertFrom-Json
  if ($null -ne $s.savedLidAc) {
    powercfg /setacvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION $s.savedLidAc
    powercfg /setdcvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION $s.savedLidDc
    powercfg /setactive SCHEME_CURRENT
  }
  if ((Read-Host "Also delete your settings and phone password? (y/N)") -eq "y") { Remove-Item -Recurse (Split-Path $settings) }
}
Remove-Item -Recurse $dest
Write-Host "Lid Awake removed."; Read-Host "Press Enter to close"
```

- [ ] **Step 3: Double-click launchers at the repo root**

`Install Lid Awake (Windows).cmd`:

```bat
@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0windows\install.ps1"
```

`Uninstall Lid Awake (Windows).cmd`:

```bat
@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0windows\uninstall.ps1"
```

- [ ] **Step 4: Verify on a clean Windows PC**

Download the ZIP, double-click `Install Lid Awake (Windows).cmd`: three ✓, tray icon appears. Set a password, turn on, open `https://<pc>.<tailnet>.ts.net/` on an iPhone and a Samsung: sign in, status shows. Reboot: the tray icon comes back. Uninstall: lid action restored (`powercfg /query SCHEME_CURRENT SUB_BUTTONS LIDACTION`), icon gone.

- [ ] **Step 5: Commit**

```bash
git add windows/install.ps1 windows/uninstall.ps1 "Install Lid Awake (Windows).cmd" "Uninstall Lid Awake (Windows).cmd"
git commit -m "Windows: double-click install and uninstall"
```

---

### Task 15: README for Windows

**Files:**
- Modify: `README.md`

- [ ] **Step 1:** Add a section **"On a Windows laptop"** after the Mac install steps: Tailscale for Windows (same account, MagicDNS + HTTPS certificates as in Step 1), .NET 8 SDK, double-click **Install Lid Awake (Windows)** (if SmartScreen warns: **More info → Run anyway**), right-click the tray icon → **Set phone password…** → **Lid Awake on**, open `https://<pc-name>.<tailnet>.ts.net/` on the phone (iPhone: Safari → Share → Add to Home Screen; Samsung: Chrome → ⋮ → Add to Home screen). Add a "What works on Windows so far" list: status, battery, memory, keep awake with lid closed, turn off from phone, password sign-in. "Coming next: screen, control, apps, alerts, fingerprint/Face ID, camera."

- [ ] **Step 2: Commit**

```bash
git add README.md
git commit -m "README: install on Windows"
```

**Phase 2 done: Windows + iPhone and Windows + Samsung work for the essentials.**

---

### Task 16: Phase 2 follow-ups (from the reviews)

Each part is its own dispatch and review. Build and tests run on GitHub Actions only.

**16a. Safer state on Windows**
- **Phone link.** Clear `servedByLidAwake` only after `tailscale serve --https=443 off` succeeded or `serve status` shows no mapping for our port. A failed removal is retried at the next start, turn-off and minute check. Do the same in `windows/uninstall.ps1`.
- **Saved lid values are machine state.** Move `savedLidAc`/`savedLidDc` out of the roaming `%APPDATA%\LidAwake\settings.json` into `%LOCALAPPDATA%\LidAwake\machine.json`. Move old values on first start. Also save the power plan they came from (`powercfg /getactivescheme`) and restore into that plan, not `SCHEME_CURRENT`. `uninstall.ps1` reads the new file and, for older installs, the old one.
- **Activity log.** If rolling to `.old` fails, still append the line. `Write` never throws, and tray calls can't crash the UI thread.
- **Start menu shortcut.** `install.ps1` adds "Lid Awake" to the Start menu (per user), and `uninstall.ps1` removes it. CI asserts both.

**16b. Wi-Fi signal on Windows.** Read the connected Wi-Fi's signal through the Native Wifi API: `WlanOpenHandle`, `WlanEnumInterfaces`, `WlanQueryInterface(wlan_intf_opcode_current_connection)` → `wlanSignalQuality` 0–100. Convert it to an approximate dBm for `/status` `net.rssi` (`dBm = quality/2 - 100`). Send `net.wifi` (the SSID) where the API gives it. If anything fails, leave both out, as today. Remove the page's Windows-only "Connected without bars" rule only if `rssi` is now always present when on Wi-Fi; otherwise keep it as the fallback.

**16c. API doc precision.**
- docs/API.md: the 5th wrong password already answers 429.
- The §6.4 safe-default column, and `batteryTemp` comes from `batteryTemperature()`.
- The `/memory` `top[].id` rule.
- Method-agnostic static/loopback/off paths.
- `/push/settings` without `off` clears the list.
- `/input` text length counts grapheme clusters.
- The video timestamp epoch is arbitrary.
- Video allows up to 6 unconfirmed frames (JPEG 2).
- `/focus-app` matches any running app with a bundle URL.
- The Windows log path.


---

# Phase 3 — Windows feature parity

Branch `claude/windows-phase-3`, stacked on `claude/windows-phase-2`. Each task adds its feature name to the Windows `/status` `features` list, so the page shows that part automatically. Built and tested on GitHub Actions. Every task ends with a checklist of what the owner must try on a real laptop.

### Task 17: Safety limits on Windows (battery and heat)

**Contract:** docs/API.md `POST /limits`, `/status` §6.1 `batteryGuard`/`heatGuard`/`batteryLimit`/`maxBatteryTemp`, §6.4 `batteryTemp`. Mac reference: main.swift `safetyStop`, `safetyStopAlertReason`, the 15 s check, the menu items "Turn off below 10% battery" and "Turn off if battery is hotter than 40°C", and the defaults (both on).

**Feature split (ruling P3-1):**
- `"limits"` covers the battery limit and `POST /limits`.
- A new feature `"heat"` covers the heat limit. The Mac sends both.
- Windows always sends `"limits"`. It sends `"heat"` only while a temperature source works, re-checked with each status.
- The page hides the heat row without `"heat"`, and hides both rows without `"limits"` (as today).
- docs/API.md §4 gets the new name.

**Windows behaviour:**
- **Battery.** On battery (GetSystemPowerStatus) and below `batteryLimit` (10) percent:
  - turn off;
  - refuse a turn-on with a tray balloon saying why;
  - log it.
- **Heat temperature source, in order:**
  1. The battery's own temperature: `IOCTL_BATTERY_QUERY_INFORMATION` with `BatteryTemperature`, in tenths of a kelvin. Find the battery device through SetupAPI with `GUID_DEVCLASS_BATTERY`.
  2. ACPI thermal zones through WMI `root\WMI` `MSAcpi_ThermalZoneTemperature`. This often needs admin; access denied counts as "unavailable".
- **No source.** `heat` is not in features, `heatGuard` is still the stored setting, and `batteryTemp` is omitted.
- **Source present.** At or above `maxBatteryTemp` (40 °C): turn off and refuse a turn-on. `batteryTemp` is sent.
- **Check rate.** Every 15 s while on, off the UI thread, as on the Mac.
- **Tray.** Two checkable items with the Mac's wording. The heat item is greyed out with "(no temperature on this PC)" when there's no source. They are stored in roaming settings and default to on.
- **`POST /limits`.** Same answers as the Mac; it applies at once. Sent from the phone, `heat` is accepted even without a source, because the setting is stored.
- **`/status`.** Sends the real `batteryGuard`/`heatGuard`, which replaces Ruling P2-12's `false`. `thermalState` stays `"unknown"`.
- **Activity log line.** For example `Turned off: battery below 10%`.
- **README.** Replace "no low-battery or too-hot shutoff on Windows" with the real behaviour, and say that the heat limit appears only if the laptop reports a temperature.

**Tests:**
- Fake readings for the guard logic: both limits, refusal of a turn-on, guards off, heat with no source.
- Parsing for the battery IOCTL temperature (tenths of a kelvin to °C) and the WMI value (tenths of a kelvin).
- Real-machine calls never throw and return null or a sane value.
- `/limits` end to end.
- `/status` fields.

**Owner checklist:**
- Unplug at 11% and watch it turn off at 9%.
- Check whether the tray's heat item is available.
- Toggle both from the phone.



Remaining Phase 3 features, one task each, in this order:

| Feature | Mac source to port | Windows technology |
|---|---|---|
| `passkeys` (Face ID on iPhone, fingerprint on Samsung) | `Passkeys.swift` | .NET `ECDsa` P-256 to verify WebAuthn signatures; same JSON |
| `push` (alerts) | `WebPush.swift` | .NET `ECDiffieHellman` + `AesGcm` (RFC 8291), ES256 JWT (VAPID) |
| `screen` | `FrameStream.swift`, `VideoEncoder.swift`, `LiveServer.swift` | Windows.Graphics.Capture + Media Foundation H.264 encoder, same binary frames on port 8788 |
| `control` | `ScreenControl.swift` | `SendInput`; `"cmd"` → Ctrl, `"alt"` → Alt; key names → Windows virtual-key codes |
| `apps` | `PhoneApps.swift` | Start-menu `.lnk` list, `Process.Start`, `SetForegroundWindow`, `CloseMainWindow` |
| `history` | `PowerHistory.swift` | Lid Awake samples battery/CPU itself every minute into a small file (Windows has no readable power log) |
| `camera` | `CameraStream.swift` | Windows.Media.Capture, same H.264 + μ-law audio format |
| Lid closed screen | `LidScreen.swift` | Keep display on with `ES_DISPLAY_REQUIRED`, brightness via WMI `WmiMonitorBrightnessMethods` |
| Safety limits (battery low, too hot) | `main.swift` `safetyStop` | `GetSystemPowerStatus`; temperature only where WMI `MSAcpi_ThermalZoneTemperature` exists |
