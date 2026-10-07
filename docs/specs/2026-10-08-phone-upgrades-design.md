# Lid Awake — phone upgrades (live stream, landscape, direct typing, lock)

Date: 2026-10-08 · Status: approved by user in chat

## Goal

Make remote use of the Mac from the iPhone page smoother and more complete:

1. Smoother screen with a quality choice, and snappier control.
2. Full-screen landscape view.
3. Typing straight from the iPhone keyboard.
4. Lock the Mac / turn its display off from the phone.

Out of scope (user chose not to now): phone alerts/notifications, timer/volume/clipboard
controls, Face ID login, lid-closed virtual display.

## Constraints that stay as they are

- Page is reachable only through Tailscale Serve, only while lid-awake is ON.
- Password session (cookie) required for everything except `/`, `/auth`, `/login`, `/logout`.
- Screen and control additionally require the "Allow screen & control from phone" menu toggle
  and the Screen Recording / Accessibility permissions.
- Host-header check (DNS-rebinding guard) applies to every request, including the new one.
- App is rebuilt with `build.sh` and signed with the "Lid Awake Local Signing" certificate.

## 1. Live stream (WebSocket)

**Server.** A new endpoint `GET /live` upgrades to a WebSocket. Same checks as `/screen.jpg`
(host, awake, session cookie, remote toggle, Screen Recording permission). Implemented with
Network.framework's WebSocket protocol on the existing listener: the listener gets
`NWProtocolWebSocket` options with `autoReplyPing`, and HTTP routes keep working because
the WebSocket handshake is only accepted for `/live`.

If adding WebSocket to the existing listener can't coexist with plain HTTP, run the
WebSocket on a second localhost port (8788) and have Tailscale Serve map path `/live`
to it (`tailscale serve --bg --set-path /live 8788`); `ensureTailscaleServe` then manages
both mappings.

**Frames (Mac → phone).** Binary WebSocket messages, each one complete JPEG. Source is the
existing `FrameStream`: every new SCStream frame is pushed to each connected socket. A
socket that still has a frame in flight skips intermediate frames (latest-wins), so a slow
phone never builds a backlog.

**Quality presets** (chosen by the phone, sent as a text message `{"quality": "..."}`;
the stream restarts with the new configuration):

| Preset   | Max width | JPEG quality | Max fps |
|----------|-----------|--------------|---------|
| Sharp    | 1600 px   | 0.7          | 12      |
| Balanced | 1200 px   | 0.5          | 20      |
| Fast     | 800 px    | 0.4          | 25      |

One SCStream is shared; when several phones/tabs watch with different presets, the most
recent choice wins (single-user app).

**Input (phone → Mac).** Text messages with the same JSON commands `/input` accepts today
(`move`, `scroll`, `click`, `mousedown`, `mouseup`, `key`, `text`), handled by
`RemoteInput.perform` on the main thread, in order. The Accessibility check is done per
message; on failure the server sends back `{"error": "..."}` which the page shows.

**Display wake.** Opening `/live` and every input message count as user activity (same
`IOPMAssertionDeclareUserActivity` throttle as today) unless the user pressed "Screen off"
(see §4).

**Phone side.** `startFrames()` opens the WebSocket; each binary message becomes an object
URL on the `<img>` (previous URL revoked). `send()` writes to the socket when open, and
falls back to the existing `POST /input` queue otherwise. On close/error: reconnect with
backoff (0.5 s, 1 s, 2 s, max 5 s). If the socket can't open at all (3 failures in a row),
fall back to the current long-poll `/screen.jpg` loop and keep retrying the socket every
30 s. Leaving the Screen tab or hiding the page closes the socket (stream then idles off
on the Mac as today).

**Quality control.** A small segmented control "Sharp · Balanced · Fast" above the screen;
choice stored in `localStorage` (default Balanced).

## 2. Full-screen landscape

Triggered when the Screen tab is active and the phone is landscape
(`matchMedia("(orientation: landscape) and (max-height: 500px)")`), so desktop browsers
aren't affected.

- `body.landscape-full`: header, tabs, zoom bar, hint, key panel, gesture row and trackpad
  hidden; `.screen-wrap` becomes `position: fixed; inset: 0` with the image letterboxed
  (`object-fit: contain`), safe-area insets respected, black background.
- Floating toolbar (top-right, semi-transparent): ⌨ keyboard, 🖱 trackpad mode, ⌘, ⌥, esc.
- **Trackpad mode** (toggle, landscape only): touches on the picture are handled by the
  same trackpad logic as the bottom pad (relative move, tap = click at pointer, two-finger
  scroll / tap = right-click, three-finger swipes) instead of absolute taps. Pinch-zoom is
  off while trackpad mode is on.
- Coordinates for absolute taps already come from `getBoundingClientRect()` of the image,
  so they stay correct letterboxed and zoomed; with `object-fit: contain` the rect is the
  element, not the drawn picture, so `norm()` must map through the image's natural aspect
  ratio to the drawn area.
- Back to portrait → normal layout (state kept).

## 3. Type directly

- A ⌨ button (in the key panel, and in the landscape toolbar) focuses a visually hidden
  `<input>` (`autocapitalize=off autocorrect=off autocomplete=off spellcheck=false`) so the
  iPhone keyboard opens; button shows "Keyboard on" while focused; blur closes it.
- `beforeinput` handling:
  - `insertText` / `insertReplacementText` → send `{type: "text", text}`; with ⌘/⌥ latched
    → per-character `{type: "key", char, mods}` (same rule as the Send bar).
  - `deleteContentBackward` → `{type: "key", key: "delete"}`.
  - `insertLineBreak` / `insertParagraph` (Return) → `{type: "key", key: "return"}`.
  - Everything is `preventDefault()`ed and the input kept empty, so iOS has nothing to
    autocorrect or replace.
- Composition input (e.g. some keyboards) — send on `compositionend` with the composed text.
- The existing Send bar stays.

## 4. Lock / Screen off

**Step 0 of this part — unlock test.** It can't be automated without locking the user's Mac,
so it's done with the user: they lock the Mac, then type the
password from the phone with direct typing and press Return. Result decides the warning text
below and is saved in the app's defaults (`unlockFromPhone = yes/no/unknown`).

**Buttons** (Screen tab, next to the desktop buttons; not in the landscape toolbar, which stays minimal):

- 🔒 **Lock Mac** → confirm dialog → server `POST /power {"action": "lock"}` → posts
  Control-⌘-Q (key code 12 with control+command).
- ◐ **Screen off** → confirm dialog → `POST /power {"action": "displayoff"}` → runs
  `/usr/bin/pmset displaysleepnow`, and sets `FrameStream.wakeSuppressed = true` so frame
  requests stop waking the display. The phone shows "Mac screen is off — tap to wake";
  tapping sends `POST /power {"action": "wake"}` which clears the flag and declares user
  activity.
- `wakeSuppressed` also clears on any input from the phone or after the user wakes the Mac
  locally (next frame received).
- Confirm text depends on the unlock test: if unlocking from the phone doesn't work (or is
  unknown), the dialog warns "You won't be able to unlock it from your phone — only at the
  Mac" (for Screen off: "…may lock the Mac…").
- Neither action touches lid-awake; the Status tab keeps working.

## Testing

- Build passes; page script passes `node --check`.
- Server checks with curl: `/live` refuses without session (401), with wrong host (403),
  while off (423); `/power` same.
- `--selftest` extended: for each quality preset, run `FrameStream` for 3 s and report frames
  produced and average frame size. (No test login is created — auth paths are covered by the
  curl checks above.)
- Manual on iPhone by the user, after each step: stream smoothness per preset, reconnect
  after toggling airplane mode, landscape layout + trackpad mode, direct typing in English
  and Hebrew, lock + unlock test, screen off + wake.

## Build order

1. Live stream + quality presets (foundation; input moves onto the socket).
2. Direct typing.
3. Full-screen landscape (+ trackpad mode).
4. Unlock test with the user → Lock / Screen off.
