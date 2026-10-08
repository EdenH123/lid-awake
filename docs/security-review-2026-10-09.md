# Lid Awake red-team review — 2026-10-09

Six parallel read-only reviewers (network, auth, web page, remote control, local Mac, parsers/crypto).
Live check: Tailscale Serve is tailnet-only, Funnel OFF.

## Pack 1 — Crashes & safety (no behaviour change)
- Server.swift:82-95 negative Content-Length → body.prefix(-1) trap (pre-auth, local). Cap 0...256KB, reject Transfer-Encoding.
- ScreenControl.swift:122 Int32(dx/dy) trap on huge scroll. Clamp.
- main.swift:443 Int(pct) trap from claude-usage.json / claude output. Require finite 0...1000.
- SystemInfo.swift:169 unsigned subtraction underflow (speculative). Use guard.
- No connection timeout / cap; buffer re-parsed per chunk (Server + LiveServer). 10s deadline, conn cap, header 8KB.
- Video outbox unbounded when peer stops reading / video without acks (LiveServer 103-118,153-166). videoReady false while sending; cap outbox; drop stalled sockets; per-peer readiness.
- Passkey challenges unbounded, public (Passkeys 28-37). Cap ~32.
- /logout (no auth) always posts sessionsChanged → closes all sockets. Notify only on real removal; close only that session's sockets.
- Sockets outlive session expiry / remote-off / mid-handshake closeAll. Recheck on timer; closeAll on toggle.
- Stuck mouse button on disconnect after mousedown. Send mouseUp on close.
- text input: no length cap, posts on main thread. Cap ~2k, rate-limit.
- `why` string logged raw (log injection). Strip control chars, cap 100.
- --selftest fixed token `selftest` on :18788 with all checks true. Random per-run token.
- No Funnel check (Server.swift:52-60). Parse serve JSON; refuse if AllowFunnel.

## Pack 2 — Lock the Mac side
- No hardened runtime (build.sh codesign, flags=0x0) → DYLD injection via LaunchAgent env inherits TCC. Add --options runtime.
- sessions / pwHash / pwSalt / passkeys / pushKey / pushSubscriptions / remoteScreen / phoneApps in UserDefaults → any same-user process can plant a session + enable remote → screen+input via our TCC (TCC bypass). Move to Keychain (ACL to our signature) or HMAC with Keychain key.
- claude runner: search path includes user-writable dirs; child inherits TCC; --setting-sources local still loads ~/.claude/settings.local.json hooks (unverified). Fixed path, owner check, empty temp cwd, no settings.
- build.sh copies sources + build.sh into Contents/Resources; signing key likely usable without prompt. Stop copying; consider key ACL.
- osascript admin fallback (main.swift:617) — drop, show "sudoers rule missing".

### Pack 2 outcome (2026-10-09)
Done: hardened runtime; claude runs disclaimed (posix_spawn + responsibility_spawnattrs_setdisclaim, CLOEXEC, fresh empty cwd, owner/permission check); no sources in the bundle; no osascript password fallback; notify() escapes quotes.
Not done, by design: moving secrets to the Keychain. Tested: legacy keychain items created by a self-signed app need a password prompt after every rebuild (partition = cdhash; read returned -128), anyone can still overwrite them, and the data-protection keychain needs a team entitlement (-34018). Root cause: the "Lid Awake Local Signing" private key is usable by codesign without a prompt, so same-user malware can sign its own binary as Lid Awake and get its TCC rights directly. The only real fix is requiring approval on each use of that key (user's choice; each update would need a click). Detection instead: Pack 3 sign-in alerts + Pack 4 on-Mac "being viewed" indicator.

## Pack 3 — Sign-in upgrades
- Face ID add needs only a cookie; no attestation; same credential ID overwrites (Passkeys:53); login-options leaks ids publicly. Require password to add, refuse existing IDs, push+notify on add, list/remove in menu, empty allowCredentials, stable user.id + excludeCredentials.
- Lockout global + in-memory (resets on restart) → tailnet DoS / local reset. Persist; key on Tailscale-User-Login; menu "clear lockout".
- Localhost cookie (127.0.0.1) sent to every local port; "Open live page" logs in on 127.0.0.1. Open ts.net URL instead; no login on loopback.
- Sibling *.ts.net hosts are same-site: can embed /screen.jpg (wakes display), cookie tossing. Sec-Fetch-Site check, CORP same-origin, X-LidAwake on API GETs, __Host- cookie.
- Host allow-list = any *.ts.net, rpId from Host. Pin exact MagicDNS name.
- Require Tailscale-User-Login == owner for non-loopback requests.
- Password reset doesn't clear push subscriptions; no sub cap. Clear + cap.
- No "sign out everywhere"; Remove Face ID leaves its sessions. Add.
- PBKDF2 200k / 6-char min → 600k / 10.
- CSP header (no sink today; defense in depth).
- Push: URLComponents exact host, no port/userinfo, refuse redirects; VAPID key gen under lock; sub = mailto.
- App actions (open/quit) not gated by remote toggle — decide.

## Pack 4 — Optional
- Audit log (who/when/what), on-Mac indicator while controlled, view-only mode, Face ID re-check for control.

## Sound
No XSS (textContent everywhere), CSRF header + no CORS, X-Frame-Options DENY, nosniff, HttpOnly/SameSite=Strict, WebSocket Origin exact, WebAuthn checks, CBOR bounds, RFC 8291/VAPID correct, sudoers exact two commands, absolute paths for spawned tools, service worker no fetch handler.
