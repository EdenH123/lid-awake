# Processor history graph — design (2026-10-10)

Tap the **Processor** row on the Status page → the shared history sheet (same one as Temperature
and Battery) opens with the Mac's processor use over the last 1 / 6 / 24 hours, split by app.
The user picked mockup **B · Coloured by app** (over A "range bars + busiest apps" and C "bars + chips on touch").

## What the user sees

- Status → Mac list: the Processor row gets a `›` and becomes tappable (tick pad, Enter/Space, like the tiles).
  Not tappable while the page is in `main.waiting`.
- Sheet title "Processor", 1H / 6H / 24H segmented control (default 6H, shared with the other kinds).
- Big number: live processor use from `/status` (`mac.cpu`), "Now · <word>" — light use (<30%),
  busy (30–70%), very busy (>70%). Falls back to the last slot's value when the live reading is missing.
- Graph: one stacked bar per slot (5 min in 1H, 15 min in 6H, 1 h in 24H, the same bins as the other
  graphs). Bar height = average processor use in the slot (% of all cores, the same measure as the live
  row). Segments bottom-up: the 3 busiest apps of the visible range in rank order, then "Everything else"
  in grey. Slots with no data before now = grey floor dash (asleep), like the other graphs.
- Y scale: 0 to the smallest of 10 / 25 / 50 / 100 % that fits the tallest bar, gridlines with labels on the right.
- "Who used it · <range>" list under the graph: icon, name, share (% of all cores, averaged over the range),
  top 3 + "Everything else". Hidden when there is no data.
- Holding a finger on a bar: the other bars fade (existing `picking` style), big number = that bar's total,
  sub-line = "8:15–8:30 PM", the list header becomes "Who used it · 8:15–8:30 PM" and lists that bar's
  apps (its top 3 by that slot, + Everything else). Letting go restores the whole range.
- Foot note: "Each bar is the average processor use in its slot, split by app. From the log your Mac keeps itself."
- No timeline ("story") list for this kind.

## Names, icons, colours

- Rows are grouped per app: by `BundleId` when present (several Claude/Chrome coalitions → one row),
  otherwise by `LaunchdName`.
- Apps with a bundle id: display name from the app bundle (`NSWorkspace.urlForApplication(withBundleIdentifier:)`
  → `FileManager.displayName`), icon from that bundle. If the bundle can't be found: last part of the id.
- Apple background jobs (`LaunchdName` starting `com.apple.`): `com.apple.WindowServer` → "Screen & windows",
  `com.apple.kernel_task` → "macOS core", all others merged into one "macOS (other)" row. They use a grey
  Apple-logo icon drawn by the page (no image request).
- Other background helpers: last dot-separated part of the name (e.g. "hindsight-shared-memory"), a plain
  grey rounded-square icon drawn by the page.
- Colour per app: the Mac computes it from the app's icon (average of saturated, opaque pixels of a 32 px
  render; grey if the icon is colourless) and sends it as `#rrggbb`. Page side: if a segment's colour is too
  close to the one before it in the same bar's palette (simple RGB distance under a threshold), it is
  lightened. macOS rows = system grey shades; "Everything else" = `var(--fill)`-like grey.

## Data

`GET /cpu-history?hours=24` (behind the session like the other history routes). Reads
`PLCoalitionAgent_EventInterval_CoalitionInterval` (columns timestamp, timestampEnd, BundleId, LaunchdName,
cpu_time) from the same power log, read-only, for the last `hours` (1…48).

Response:

```json
{
  "now": 1791581585,
  "cores": 16,
  "apps": [{"key": "com.anthropic.claudefordesktop", "name": "Claude", "color": "#d97757", "kind": "app"},
           {"key": "macos.windowserver", "name": "Screen & windows", "kind": "macos"},
           {"key": "hindsight-shared-memory", "name": "hindsight-shared-memory", "kind": "other"}],
  "intervals": [[start, end, {"0": 12.3, "1": 4.1}, otherSeconds], ...]
}
```

- `intervals` oldest first; each holds per-app processor seconds keyed by index into `apps`. To keep the
  payload small, per interval only apps with ≥ 0.5 % of that interval's total processor time (or ≥ 0.2 s)
  are listed; the rest is summed into `otherSeconds`.
- `cores` = `ProcessInfo.activeProcessorCount`; use % = seconds / (duration × cores) × 100.
- Page bins by spreading each interval's seconds across the slots it overlaps, proportionally to overlap.
  A slot's % = its seconds / (covered seconds × cores), where covered seconds = how much of the slot the
  intervals cover (so a partly covered newest slot isn't diluted). Slot with zero coverage = no data.
- App names/colours are cached in memory on the Mac per key (icons don't change often).

`GET /cpu-icon?key=<bundle id>` (behind the session): 64 px PNG of that app's icon, only when `key` is an
app key from the most recent `/cpu-history` answer (kept in memory); otherwise 404. Reuses
`PhoneApps.iconPNG(path:size:)`. Page sets `loading="lazy"` images with a grey fallback on error.

## Code layout

- `PowerHistory.swift`: new `cpu(hours:)` + small grouping/naming helpers; keeps the set of app keys for `/cpu-icon`.
  Icon colour helper lives here too (it only serves this graph).
- `Server.swift`: two routes next to `/temp-history` and `/battery-history`.
- `Dashboard.swift`:
  - Processor row markup (`id="cpuRow"`, chevron), list markup `#histApps` in the sheet.
  - `HISTORY.cpu` entry; the sheet's render is split so `cpu` draws stacked bars and the app list while
    temp/battery keep range bars + story. Shared: open/close, range control, scrubbing, bins/time axis.
- `main.swift` selftest + `tests/live_checks.sh`: `/cpu-history` returns ≥ 1 interval and ≥ 1 app;
  `/cpu-icon` with a key not in the list → 404.

## Out of scope

Per-app history beyond 48 h, energy/watts graph, killing apps from the list, alerts on high processor use.
