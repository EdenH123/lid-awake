# Memory window (tap the Memory row) — design

Date: 2026-10-10 · Branch `memory-sheet` · Picked by the user: mockup **C · Pressure graph**, graph **starts empty** (option 2).

## What the user sees

The Memory row on Status (Mac section) gets a chevron and opens a "Memory" sheet that looks and
behaves like the Temperature/Battery sheet (same head with Done, drag down to close, shade, tab bar hidden).

1. **Live pressure graph** (top card). Title "Memory pressure · live" with a pill on the right:
   Low (green) / Medium (yellow) / High (red), from macOS's own pressure level. The area graph shows
   pressure 0–100 % for the last 2 minutes, one point every 2 s, newest on the right
   ("2 min ago" … "now" under it). It starts empty each time the sheet opens and only fills while it is open.
   Line/fill colour follows the current level. Before the second point arrives it shows "Measuring…".
2. **Four tiles** (2×2):
   - Used — app + wired + compressed (same as the Memory row and Activity Monitor's "Memory Used")
   - Free + files — free pages + file cache (room macOS can hand out right away)
   - Apps — app memory
   - Overflow to disk — swap used
3. **Using the most** — top 5 apps by memory, each with its icon, name, size and a thin blue bar
   (relative to the first). Helper processes count toward their app (all Chrome helpers = "Google Chrome").
4. **Foot note** (grey): one or two plain sentences on what pressure means.

Sizes read like the rest of the page ("33 GB", "0.9 GB"). Dark and light mode both use system colours.

## Mac side

New `MemoryInfo.swift` with `MemoryInfo.snapshot() -> [String: Any]`:

- `total` (hw.memsize), `used`, `app`, `wired`, `compressed`, `cached` (file-backed), `free`,
  `swapUsed` (vm.swapusage) — bytes, from `host_statistics64(HOST_VM_INFO64)`; `used` computed the same
  way as `SystemInfo.memoryUsed()` (move that formula into MemoryInfo and have SystemInfo call it, so the
  row and the sheet always agree).
- `pressure` — 0–100, `100 - kern.memorystatus_level`.
- `level` — "normal" | "warn" | "critical" from `kern.memorystatus_vm_pressure_level` (1 / 2 / 4).
- `top` — up to 5 `{ name, id, bytes }`: walk `proc_listallpids`, read `proc_pid_rusage(RUSAGE_INFO_V4)`
  `ri_phys_footprint` (Activity Monitor's "Memory"), map each pid's path (`proc_pidpath`) to its
  outermost `.app` bundle and sum per bundle. Processes we can't read (other users / root) are skipped.
  Processes not inside an `.app` are left out (the list is "apps"). `id` = `PhoneApps.id(bundlePath)` when
  the app is in `NSWorkspace.runningApplications` (so `/app-icon.png?id=` already serves its icon via
  `PhoneApps.runningPath(for:)`), otherwise omitted and the page shows a plain placeholder icon.

Route in `Server.swift`: `GET /memory` → JSON from `MemoryInfo.snapshot()`, behind the session like
`/status`. No new icon route.

## Page side (`Dashboard.swift`)

- Memory row: chevron + `role="button"`, opens the sheet (tick on tap as other rows).
- New sheet markup `#memSheet` reusing the `.hist-sheet` look/drag code (generalise the drag-to-close
  handler to work on either sheet rather than copying it).
- While open: fetch `memory` every 2 s (`fetchWithin`), keep at most 60 points, redraw SVG graph,
  tiles, top list. Stop the timer on close, on leaving Status, and when the page is hidden.
- If a fetch fails, keep the last numbers and show a small "Can't reach the Mac" line in the graph card.

## Checks

- `--selftest` writes a memory line: total equals hw.memsize, used ≤ total, pressure in 0–100,
  level is one of the three words, `top` non-empty with descending bytes.
- `tests/live_checks.sh` reads that line (PASS/FAIL), like the temperature/battery history checks.
- Mock preview (`lidawake-mock`) gets a fake `/memory` that wiggles, to check the sheet in dark + light.

## Not doing

History before the sheet opens, 1H/6H/24H ranges, quitting apps from this list, per-process (non-app) rows.
