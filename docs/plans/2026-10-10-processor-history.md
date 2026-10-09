# Processor History Graph Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Tap the Processor row on the phone's Status page → the shared history sheet shows processor use over 1/6/24 h as bars split by app (icon colours), with a "Who used it" list that follows the finger.

**Architecture:** The Mac reads macOS's power log table `PLCoalitionAgent_EventInterval_CoalitionInterval` (per-app processor seconds per logged stretch), groups rows per app, and sends stretches + an app list (`GET /cpu-history`). App icons come from `GET /cpu-icon`, limited to apps seen in that history. The page adds a third kind `cpu` to the existing `HISTORY` sheet: it spreads stretches over the sheet's slots and draws stacked bars instead of range bars.

**Tech Stack:** Swift (AppKit, SQLite3), one-file HTML/CSS/JS page in `Dashboard.swift`, shell checks (`tests/live_checks.sh` + the app's `--selftest`), Python mock server for page previews.

Spec: `docs/specs/2026-10-10-processor-history-design.md`. Work in worktree `~/Developer/LidAwake-cpugraph` (branch `cpu-graph`).

Compile check used throughout (does NOT install):

```bash
cd ~/Developer/LidAwake-cpugraph && T=$(mktemp -d) && swiftc -O -o "$T/LidAwake" *.swift && echo COMPILED "$T/LidAwake"
```

---

### Task 1: Share the icon drawing and the power-log query (no behaviour change)

**Files:**
- Modify: `PhoneApps.swift` (`iconPNG`)
- Modify: `PowerHistory.swift` (`rows`)

- [ ] **Step 1: Split icon drawing out of `PhoneApps.iconPNG`**

Replace the body of `iconPNG` with a call to a new `iconBitmap`:

```swift
    // The app's icon drawn at `size` px (the processor graph also reads its colours).
    static func iconBitmap(_ path: String, size: Int) -> NSBitmapImageRep? {
        guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8,
                                         samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                         colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0) else { return nil }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        NSWorkspace.shared.icon(forFile: path).draw(in: NSRect(x: 0, y: 0, width: size, height: size))
        NSGraphicsContext.restoreGraphicsState()
        return rep
    }

    // The app's icon as a PNG: 256 px for the grid, 512 px for the big zoom when opening.
    static func iconPNG(_ path: String, size: Int = 256) -> Data? {
        let size = size >= 512 ? 512 : 256
        if let cached = iconCache[path + "#\(size)"] { return cached }
        let png = iconBitmap(path, size: size)?.representation(using: .png, properties: [:])
        iconCache[path + "#\(size)"] = png
        return png
    }
```

- [ ] **Step 2: Split the SQLite open/prepare/step loop out of `PowerHistory.rows`**

```swift
    private static func rows(_ columns: String, hours: Double, _ point: (Double, [Double]) -> [Double]?) -> Data {
        let count = columns.split(separator: ",").count
        var points: [[Double]] = []
        query("SELECT timestamp, \(columns) FROM PLBatteryAgent_EventBackward_Battery WHERE timestamp > ? ORDER BY timestamp", hours: hours) { stmt in
            if (1...count).contains(where: { sqlite3_column_type(stmt, Int32($0)) == SQLITE_NULL }) { return }
            let values = (1...count).map { sqlite3_column_double(stmt, Int32($0)) }
            if let p = point(sqlite3_column_double(stmt, 0).rounded(), values) { points.append(p) }
        }
        let body: [String: Any] = ["points": points, "now": Date().timeIntervalSince1970.rounded()]
        return (try? JSONSerialization.data(withJSONObject: body)) ?? Data("{}".utf8)
    }

    // One read-only query on the power log, for rows newer than `hours` ago (1…48); `?` = that time.
    private static func query(_ sql: String, hours: Double, _ row: (OpaquePointer) -> Void) {
        let since = Date().timeIntervalSince1970 - min(max(hours, 1), 48) * 3600
        var db: OpaquePointer?
        if sqlite3_open_v2("file:\(logPath)?mode=ro", &db, SQLITE_OPEN_READONLY | SQLITE_OPEN_URI, nil) == SQLITE_OK {
            sqlite3_busy_timeout(db, 500)
            var stmt: OpaquePointer?
            if sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK, let stmt {
                sqlite3_bind_double(stmt, 1, since)
                while sqlite3_step(stmt) == SQLITE_ROW { row(stmt) }
            }
            sqlite3_finalize(stmt)
        }
        sqlite3_close(db)
    }
```

- [ ] **Step 3: Compile** — run the compile check. Expected: `COMPILED …`.
- [ ] **Step 4: Commit** — `git commit -am "Share icon drawing and power-log query"`

---

### Task 2: `/cpu-history` and `/cpu-icon` on the Mac

**Files:**
- Modify: `tests/live_checks.sh` (after the battery history check)
- Modify: `main.swift` (selftest lines, after `battery history`)
- Modify: `PowerHistory.swift` (new Processor section, `import AppKit`)
- Modify: `Server.swift` (routes after `/battery-history`)

- [ ] **Step 1: Failing checks** — `tests/live_checks.sh`, after the `bh=` battery block:

```sh
ch=$(grep "^cpu history:" "$LOG")
if echo "$ch" | grep -q "history: [1-9][0-9]* stretches, [1-9]"; then echo "PASS cpu history (${ch#cpu history: })"
else echo "FAIL cpu history: '$ch'"; fail=1; fi
ci=$(grep "^cpu icon:" "$LOG")
if echo "$ci" | grep -q "icon: [1-9][0-9]* bytes, unlisted refused"; then echo "PASS cpu icon (${ci#cpu icon: })"
else echo "FAIL cpu icon: '$ci'"; fail=1; fi
```

`main.swift`, right after the `var lines = [...]` array (before `let done = DispatchSemaphore`):

```swift
    // The processor graph: stretches and apps from the same log, and its icon route's guard.
    let cpu = (try? JSONSerialization.jsonObject(with: PowerHistory.cpu(hours: 6))) as? [String: Any]
    let cpuApps = cpu?["apps"] as? [[String: Any]] ?? []
    let cpuApp = cpuApps.first { $0["kind"] as? String == "app" }?["key"] as? String
    lines.append("cpu history: \((cpu?["intervals"] as? [Any])?.count ?? 0) stretches, \(cpuApps.count) apps in 6 h")
    lines.append("cpu icon: \(cpuApp.flatMap { PowerHistory.iconPNG(key: $0) }?.count ?? 0) bytes, unlisted "
                 + (PowerHistory.iconPNG(key: "com.apple.finder.not-listed") == nil ? "refused" : "SENT"))
```

- [ ] **Step 2: Compile — expect failure** `value of type 'PowerHistory' has no member 'cpu'`.

- [ ] **Step 3: Implement** — `PowerHistory.swift`: change `import Foundation` to `import AppKit`, update the header comment to mention processor use, and add inside `enum PowerHistory`:

```swift
    // MARK: Processor

    // Processor use per app. Every 10 s to a few minutes macOS logs how many processor-seconds each
    // app or background job used. Answer: {now, cores, apps: [{key, name, kind: app|macos|other, color?}],
    // intervals: [[start, end, {"app index": seconds}, other seconds]]} oldest first. Per stretch, apps
    // under 0.5% of its total and under 0.2 s are summed into "other" to keep the answer small.
    static func cpu(hours: Double) -> Data {
        var apps: [[String: Any]] = [], index: [String: Int] = [:], seen: Set<String> = []
        var intervals: [Any] = []
        var stretch: (start: Double, end: Double, secs: [Int: Double])?
        func close() {
            guard let s = stretch, s.end > s.start else { return }
            let total = s.secs.values.reduce(0, +)
            var listed: [String: Double] = [:], other = total
            for (i, secs) in s.secs where secs >= 0.2 || secs >= total * 0.005 {
                listed[String(i)] = round2(secs)
                other -= secs
            }
            intervals.append([s.start, s.end, listed, round2(max(0, other))])
        }
        query("SELECT timestamp, timestampEnd, BundleId, LaunchdName, cpu_time FROM PLCoalitionAgent_EventInterval_CoalitionInterval WHERE timestamp > ? ORDER BY timestamp", hours: hours) { stmt in
            let start = sqlite3_column_double(stmt, 0).rounded(), end = sqlite3_column_double(stmt, 1).rounded()
            if stretch?.start != start { close(); stretch = (start, end, [:]) }
            stretch?.end = max(stretch?.end ?? end, end)
            let secs = sqlite3_column_double(stmt, 4)
            guard secs > 0 else { return }
            let job = who(bundle: text(stmt, 2), launchd: text(stmt, 3))
            let i: Int
            if let known = index[job.key] { i = known } else {
                i = apps.count
                index[job.key] = i
                var app: [String: Any] = ["key": job.key, "name": job.name, "kind": job.kind]
                if let color = job.color { app["color"] = color }
                apps.append(app)
                if job.kind == "app" { seen.insert(job.key) }
            }
            stretch?.secs[i, default: 0] += secs
        }
        close()
        lock.lock(); iconKeys.formUnion(seen); lock.unlock()
        let body: [String: Any] = ["now": Date().timeIntervalSince1970.rounded(), "cores": ProcessInfo.processInfo.activeProcessorCount,
                                   "apps": apps, "intervals": intervals]
        return (try? JSONSerialization.data(withJSONObject: body)) ?? Data("{}".utf8)
    }

    // The icon of an app seen in the processor history, and only those. Call on the main thread.
    static func iconPNG(key: String) -> Data? {
        lock.lock(); let allowed = iconKeys.contains(key); lock.unlock()
        guard allowed, let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: key) else { return nil }
        return PhoneApps.iconPNG(url.path)
    }

    // Who a log row belongs to: an app (found by its bundle id), one of macOS's own jobs, or another helper.
    private struct Job { let key: String, name: String, kind: String, color: String? }
    private static let lock = NSLock()
    private static var jobs: [String: Job] = [:]      // by log id; under `lock`
    private static var iconKeys: Set<String> = []     // apps /cpu-icon may show; under `lock`

    private static func who(bundle: String, launchd: String) -> Job {
        let id = bundle.isEmpty ? launchd : bundle
        lock.lock(); let cached = jobs[id]; lock.unlock()
        if let cached { return cached }
        let job: Job = onMain {
            switch id {
            case "com.apple.WindowServer": return Job(key: "macos.screen", name: "Screen & windows", kind: "macos", color: nil)
            case "com.apple.kernel_task": return Job(key: "macos.core", name: "macOS core", kind: "macos", color: nil)
            default:
                if let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: id) {
                    var name = FileManager.default.displayName(atPath: url.path)
                    if name.hasSuffix(".app") { name.removeLast(4) }
                    return Job(key: id, name: name, kind: "app", color: iconColor(url.path))
                }
                if id.hasPrefix("com.apple.") { return Job(key: "macos.other", name: "macOS (other)", kind: "macos", color: nil) }
                let short = id.split(separator: ".").last.map(String.init) ?? id
                return Job(key: "other." + short, name: short, kind: "other", color: nil)
            }
        }
        lock.lock(); jobs[id] = job; lock.unlock()
        return job
    }

    // The colour that stands out in an app's icon: its most common bright hue, averaged.
    // Grey or black-and-white icons give nil (the page then uses grey).
    private static func iconColor(_ path: String) -> String? {
        guard let rep = PhoneApps.iconBitmap(path, size: 32) else { return nil }
        var buckets = [(w: Double, r: Double, g: Double, b: Double)](repeating: (0, 0, 0, 0), count: 12)
        for x in 0..<32 { for y in 0..<32 {
            guard let c = rep.colorAt(x: x, y: y)?.usingColorSpace(.deviceRGB), c.alphaComponent > 0.8,
                  c.saturationComponent > 0.35, c.brightnessComponent > 0.3 else { continue }
            let i = min(11, Int(c.hueComponent * 12)), w = Double(c.saturationComponent)
            buckets[i].w += w
            buckets[i].r += Double(c.redComponent) * w
            buckets[i].g += Double(c.greenComponent) * w
            buckets[i].b += Double(c.blueComponent) * w
        }}
        guard let best = buckets.max(by: { $0.w < $1.w }), best.w > 20 else { return nil }
        return String(format: "#%02x%02x%02x", Int(best.r / best.w * 255), Int(best.g / best.w * 255), Int(best.b / best.w * 255))
    }

    private static func onMain<T>(_ work: () -> T) -> T { Thread.isMainThread ? work() : DispatchQueue.main.sync(execute: work) }
    private static func text(_ stmt: OpaquePointer, _ column: Int32) -> String {
        sqlite3_column_text(stmt, column).map { String(cString: $0) } ?? ""
    }
    private static func round2(_ v: Double) -> Double { (v * 100).rounded() / 100 }
```

`Server.swift`, after the `/battery-history` case:

```swift
        case ("GET", "/cpu-history"):
            reply(c, "200 OK", "application/json", PowerHistory.cpu(hours: Double(req.query["hours"] ?? "") ?? 24))
        case ("GET", "/cpu-icon"):
            // Only apps seen in the processor history, so the page can't fetch just any app's icon.
            let png: Data? = DispatchQueue.main.sync { PowerHistory.iconPNG(key: req.query["key"] ?? "") }
            guard let png else { return text(c, "404 Not Found", "No such app.") }
            reply(c, "200 OK", "image/png", png)
```

- [ ] **Step 4: Compile** — expected `COMPILED`.
- [ ] **Step 5: Run the compiled binary's selftest directly** (not installed; screen lines will fail, only the cpu lines matter):

```bash
"$T/LidAwake" --selftest; grep -E "^(cpu|temperature|battery) " ~/Library/Logs/LidAwake-selftest.log
```
Expected: `cpu history: <n≥1> stretches, <m≥1> apps in 6 h` and `cpu icon: <bytes≥1> bytes, unlisted refused`; temperature/battery lines still have readings.
If the binary refuses to run outside launchd, skip to Task 6's install check.

- [ ] **Step 6: Payload size** — `"$T/LidAwake"` can't serve; instead check via the mock in Task 5 (`curl -s localhost:8898/cpu-history | wc -c`, expect well under 1 MB for 24 h).
- [ ] **Step 7: Commit** — `git commit -am "Processor history and app icons from the power log"`

---

### Task 3: Mock server for page previews

**Files:**
- Create: `$SCRATCH/mock/serve.py` (copy of the old session's mock: `/private/tmp/claude-501/-Users-edenheiser-Desktop/41593699-87e4-47ad-8f7c-4c6a45eb5be6/scratchpad/mock/`), `SRC` = this worktree's `Dashboard.swift`
- Modify: `~/Desktop/.claude/launch.json` (add `lidawake-cpu-mock` on port 8898)

- [ ] **Step 1:** Copy the mock folder to this session's scratchpad. Add to `do_GET`, before the final 404:

```python
        if path == "/cpu-history": return self.send(200, json.dumps(cpu_history()).encode(), "application/json")
        if path == "/cpu-icon":
            key = self.path.split("key=", 1)[-1]
            hue = sum(map(ord, key)) * 37 % 360
            svg = f'<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64"><rect width="64" height="64" rx="14" fill="hsl({hue},65%,52%)"/></svg>'
            return self.send(200, svg.encode(), "image/svg+xml")
```

and at module level a Python version of `PowerHistory.cpu` reading the real power log (simplified names: bundle id → last part capitalised, colour from a fixed table for Claude `#d97757`, Docker `#1d63ed`, Chrome `#f4b400`, else none):

```python
import sqlite3, time
LOG = "/private/var/db/powerlog/Library/BatteryLife/CurrentPowerlog.PLSQL"
COLORS = {"com.anthropic.claudefordesktop": "#d97757", "com.docker.docker": "#1d63ed", "com.google.Chrome": "#f4b400"}
def who(bundle, launchd):
    i = bundle or launchd or ""
    if i == "com.apple.WindowServer": return ("macos.screen", "Screen & windows", "macos")
    if i == "com.apple.kernel_task": return ("macos.core", "macOS core", "macos")
    if bundle: return (bundle, bundle.split(".")[-1].capitalize(), "app")
    if i.startswith("com.apple."): return ("macos.other", "macOS (other)", "macos")
    return ("other." + i.split(".")[-1], i.split(".")[-1], "other")
def cpu_history(hours=24):
    db = sqlite3.connect(f"file:{LOG}?mode=ro", uri=True)
    rows = db.execute("SELECT timestamp, timestampEnd, BundleId, LaunchdName, cpu_time FROM PLCoalitionAgent_EventInterval_CoalitionInterval WHERE timestamp > ? ORDER BY timestamp", (time.time() - hours * 3600,))
    apps, index, out, cur = [], {}, [], None
    def close():
        if not cur or cur[1] <= cur[0]: return
        total = sum(cur[2].values()); listed = {}; other = total
        for i, s in cur[2].items():
            if s >= .2 or s >= total * .005: listed[str(i)] = round(s, 2); other -= s
        out.append([cur[0], cur[1], listed, round(max(0, other), 2)])
    for t, e, b, l, s in rows:
        if not cur or cur[0] != round(t): close(); cur = [round(t), round(e), {}]
        cur[1] = max(cur[1], round(e))
        if not s or s <= 0: continue
        key, name, kind = who(b, l)
        if key not in index:
            index[key] = len(apps); a = {"key": key, "name": name, "kind": kind}
            if key in COLORS: a["color"] = COLORS[key]
            apps.append(a)
        cur[2][index[key]] = cur[2].get(index[key], 0) + s
    close()
    return {"now": round(time.time()), "cores": os.cpu_count(), "apps": apps, "intervals": out}
```

- [ ] **Step 2:** Add to `~/Desktop/.claude/launch.json` configurations:

```json
    {
      "name": "lidawake-cpu-mock",
      "runtimeExecutable": "env",
      "runtimeArgs": ["SRC=/Users/edenheiser/Developer/LidAwake-cpugraph/Dashboard.swift", "python3", "<scratchpad>/mock/serve.py", "8898"],
      "port": 8898
    }
```

- [ ] **Step 3:** `preview_start lidawake-cpu-mock`; `curl -s localhost:8898/cpu-history | python3 -c 'import json,sys; d=json.load(sys.stdin); print(len(d["intervals"]), len(d["apps"]))'` → two positive numbers; `curl -s localhost:8898/cpu-history | wc -c` well under 1 MB.

(Mock lives outside the repo — nothing to commit.)

---

### Task 4: Processor row opens the sheet; `cpu` kind loads

**Files:** Modify `Dashboard.swift` (CSS near `.health-list` rules ~line 232; Mac list markup ~line 989; sheet markup ~line 1222; history JS ~lines 3510–3800)

- [ ] **Step 1: Row markup** — replace the Processor row:

```html
        <div class="row tappable-row" id="cpuRow" role="button" tabindex="0" aria-label="Processor, show the last hours"><span class="title">Processor</span><div class="meter"><div id="cpuBar" style="width:0"></div></div><span class="value" id="cpuValue">—</span><svg class="row-chev" viewBox="0 0 8 13" aria-hidden="true"><path d="M1.5 1.5 6.5 6.5 1.5 11.5"/></svg></div>
```

CSS (after `.health-list .row.plain .value { flex: 1; }`):

```css
  .tappable-row { cursor: pointer; -webkit-tap-highlight-color: transparent; transition: opacity .2s ease; }
  .tappable-row:active { opacity: .6; }
  .row-chev { width: 8px; height: 13px; flex: none; margin-left: -6px; fill: none; stroke: var(--faint); stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }
  .health-list .tappable-row .value { flex-basis: 82px; }
```

- [ ] **Step 2: Sheet markup** — after the `hist-chart-card` div, before `#histStory`:

```html
    <div class="hist-apps" id="histAppsBox" hidden>
      <div class="section-head" id="histAppsHead">Who used it</div>
      <div class="list" id="histApps"></div>
    </div>
```

CSS (after `.hist-empty`):

```css
  .hist-apps { display: grid; gap: 8px; }
  .hist-apps[hidden] { display: none; }
  .hist-apps .row { min-height: 48px; }
  .hist-apps .app-ico { width: 28px; height: 28px; border-radius: 7px; flex: none; display: grid; place-items: center;
                        overflow: hidden; color: #fff; font-size: 15px; background: var(--fill); }
  .hist-apps .app-ico img { width: 100%; height: 100%; display: block; }
```

- [ ] **Step 3: State + loading** — `histData` gains `cpu: { points: null, at: 0, failed: false }`. In `loadHistory`, replace `h.points = (await res.json()).points || [];` with:

```js
      const json = await res.json(), k = HISTORY[kind];
      h.points = k.parse ? k.parse(json) : json.points || [];
      if (!h.points) throw new Error("unreadable");
```

- [ ] **Step 4: `HISTORY.cpu`** (after `battery`):

```js
    cpu: {
      title: "Processor", url: "cpu-history", chartLabel: "Processor use graph, split by app", stacked: true,
      foot: "Each bar is the average processor use in its slot, split by app. From the log your Mac keeps itself.",
      live: () => data && data.mac && data.mac.cpu != null ? data.mac.cpu : null,
      fmt: v => Math.round(v) + "%",
      range: (lo, hi) => (hi < 1 ? "<1" : Math.round(hi)) + "%",
      nowText: v => v < 30 ? "Light use" : v < 70 ? "Busy" : "Very busy",
      parse: json => Array.isArray(json.intervals) && Array.isArray(json.apps) ? json : null,
      onPick: bin => showCpuApps(bin),
    },
```

- [ ] **Step 5: Tap** — extend the tile loop: `[["batteryTile", "battery"], ["tempTile", "temp"], ["cpuRow", "cpu"]]`.

(The sheet now opens but `renderHistory` would treat the object as points — Task 5 adds the branch; do Tasks 4 and 5 before previewing.)

---

### Task 5: Stacked bars, colours, "Who used it"

**Files:** Modify `Dashboard.swift` history JS.

- [ ] **Step 1: Shared frame + grid** — add before `renderHistory`, and make the range-bar code use them (the plugged band stays drawn before the grid):

```js
  // The graph's frame: x for times, y for values, one slot per bin.
  function historyFrame(svg, sc, from, to, n) {
    const W = svg.getBoundingClientRect().width || 320, H = 240;
    const L = 0, R = W - 34, T = 6, B = H - 24;
    svg.setAttribute("viewBox", `0 0 ${W} ${H}`);
    const x = s => L + (s - from) / (to - from) * (R - L);
    const y = v => B - (v - sc.yMin) / (sc.yMax - sc.yMin) * (B - T);
    const slot = (R - L) / n;
    return { W, H, L, R, T, B, x, y, slot, bw: Math.min(14, slot * .62) };
  }
  // Grid: values on the right, times along the bottom, and the turn-off limit if any.
  function drawHistoryGrid(svg, f, sc, from, to) {
    const { H, L, R, x, y } = f, offset = new Date().getTimezoneOffset() * 60;
    for (let v = sc.yMin; v <= sc.yMax + 1e-9; v += sc.step) {
      svgEl("line", { class: "grid", x1: L, x2: R, y1: y(v), y2: y(v) }, svg);
      svgEl("text", { x: R + 8, y: y(v) + 4 }, svg).textContent = sc.label(v);
    }
    const tickEvery = { 1: 900, 6: 3600, 24: 6 * 3600 }[histHours];
    for (let s = Math.ceil((from - offset) / tickEvery) * tickEvery + offset; s < to; s += tickEvery) {
      if (x(s) < 14 || x(s) > R - 14) continue;
      const label = histHours === 1 ? clock(s).replace(/\s?[AP]\.?M\.?$/i, "") : clock(s, false);
      svgEl("text", { x: x(s), y: H - 6, "text-anchor": "middle" }, svg).textContent = label;
    }
    if (sc.limit) {
      svgEl("line", { class: "limit", x1: L, x2: R, y1: y(sc.limit.at), y2: y(sc.limit.at) }, svg);
      svgEl("text", { class: "limit-label", x: L + 4, y: y(sc.limit.at) - 6 }, svg).textContent = sc.limit.text;
    }
  }
  const slotLabel = bin => `${clock(bin.s).replace(/\s?[AP]\.?M\.?$/i, "")}–${clock(Math.min(bin.s + bin.size, Date.now() / 1000))}`;
```

In `renderHistory`: at the very top add `histCpu = null; $("histAppsBox").hidden = true;`; after computing `n`/`to`, add `if (k.stacked) return renderCpuHistory(h, svg, size, now, from, n, to);`; replace the `W/H/L/R/T/B/x/y/slot/bw` block with `const f = historyFrame(svg, sc, from, to, n), { W, T, B, x, y, slot, bw } = f;`; replace the grid/ticks/limit block (after the plugged band) with `drawHistoryGrid(svg, f, sc, from, to);`. In `showHistoryReading`, use `slotLabel(bin)` for the time and end with `if (k.onPick) k.onPick(bin);`.

- [ ] **Step 2: Colours**

```js
  // Each app's colour comes from its icon (sent by the Mac); macOS's own jobs and grey icons get greys.
  // A colour too close to one already used is lightened so neighbouring segments stay apart.
  const MAC_GREYS = ["#8e8e93", "#aeaeb2", "#636366"];
  const hexRgb = h => [1, 3, 5].map(i => parseInt(h.slice(i, i + 2), 16));
  function cpuColors(d, keys) {
    const colors = new Map(), used = [];
    let grey = 0;
    for (const key of keys) {
      let c = d.apps[key].color || MAC_GREYS[grey++ % MAC_GREYS.length];
      const rgb = hexRgb(c);
      if (used.some(u => Math.hypot(u[0] - rgb[0], u[1] - rgb[1], u[2] - rgb[2]) < 70))
        c = "#" + rgb.map(v => Math.round(v + (255 - v) * .45).toString(16).padStart(2, "0")).join("");
      used.push(hexRgb(c));
      colors.set(key, c);
    }
    return colors;
  }
```

- [ ] **Step 3: Stacked render**

```js
  // Processor: one bar per slot, its height the slot's average use, split by app: the range's three
  // busiest apps in their icon colours at the bottom, everyone else as the grey top of the bar.
  let histCpu = null;
  function renderCpuHistory(h, svg, size, now, from, n, to) {
    $("histStory").hidden = true;
    const d = h.points, cores = (d && d.cores) || 1;
    const bins = [];
    for (let i = 0; i < n; i++) bins.push({ s: from + i * size, size, covered: 0, secs: new Map(), total: 0, count: 0 });
    // Each logged stretch counts in every slot it overlaps, in proportion.
    for (const [s, e, apps, other] of d ? d.intervals : []) {
      if (e <= from || e <= s) continue;
      for (let i = Math.max(0, Math.floor((s - from) / size)); i < n && bins[i].s < e; i++) {
        const b = bins[i], part = Math.min(e, b.s + size) - Math.max(s, b.s);
        if (part <= 0) continue;
        const share = part / (e - s);
        b.covered += part;
        for (const key in apps) b.secs.set(+key, (b.secs.get(+key) || 0) + apps[key] * share);
        b.secs.set(-1, (b.secs.get(-1) || 0) + other * share);
      }
    }
    const pct = (secs, covered) => secs / (covered * cores) * 100;
    const range = new Map();
    let rangeCovered = 0, top = 0;
    for (const b of bins) {
      if (b.covered < 1) continue;
      b.count = 1;
      for (const [key, secs] of b.secs) { b.total += secs; range.set(key, (range.get(key) || 0) + secs); }
      b.lo = b.hi = pct(b.total, b.covered);
      rangeCovered += b.covered;
      top = Math.max(top, b.hi);
    }
    const ranked = [...range].filter(([key]) => key >= 0).sort((a, b) => b[1] - a[1]).map(([key]) => key);
    const top3 = ranked.slice(0, 3);
    histCpu = { d, range, rangeCovered, top3, colors: d ? cpuColors(d, top3) : new Map(), pct };
    const empty = $("histEmpty"), hasData = bins.some(b => b.count);
    empty.hidden = hasData;
    empty.textContent = h.failed ? "Couldn't load the history from your Mac." : !d ? "Loading…" : "No readings for this time yet.";
    showCpuApps(null);
    if (!hasData) return;

    const yMax = [10, 25, 50, 100].find(m => top <= m) || 100;
    const sc = { yMin: 0, yMax, step: { 10: 5, 25: 5, 50: 10, 100: 25 }[yMax], label: t => t + "%", limit: null };
    const f = historyFrame(svg, sc, from, to, n), { x, y, slot, bw, B } = f;
    drawHistoryGrid(svg, f, sc, from, to);
    bins.forEach((b, i) => {
      const cx = x(b.s) + slot / 2, left = cx - bw / 2;
      if (!b.count) {
        // Asleep (or not reached yet): a small grey dash on the floor.
        if (b.s < now) b.el = svgEl("rect", { class: "bar-off", x: left, y: B - 4, width: bw, height: 4, rx: 2 }, svg);
        return;
      }
      const g = svgEl("g", { class: "bar" }, svg), topY = Math.min(y(b.hi), B - 3);
      const clip = svgEl("clipPath", { id: "cpuClip" + i }, g);
      svgEl("rect", { x: left, y: topY, width: bw, height: B - topY, rx: Math.min(bw / 2, 4) }, clip);
      const inner = svgEl("g", { "clip-path": `url(#cpuClip${i})` }, g);
      svgEl("rect", { x: left, y: topY, width: bw, height: B - topY, fill: "var(--fill)" }, inner);  // everyone else
      let base = 0;
      for (const key of top3) {
        const v = pct(b.secs.get(key) || 0, b.covered);
        if (v <= 0) continue;
        svgEl("rect", { x: left, y: y(base + v), width: bw, height: y(base) - y(base + v) + .5, fill: histCpu.colors.get(key) }, inner);
        base += v;
      }
      b.el = g;
    });
    histGraph = { bins: bins.filter(b => b.count), x, slot, svg, left: () => svg.getBoundingClientRect().left,
                  scale: () => f.W / svg.getBoundingClientRect().width };
  }
```

- [ ] **Step 4: "Who used it" list**

```js
  // "Who used it": the range's top three and everyone else, or (finger on a bar) that bar's own.
  const RANGE_WORDS = { 1: "last hour", 6: "last 6 hours", 24: "last 24 hours" };
  const cpuIcons = new Map();  // app key → promise of a blob URL (or null)
  function cpuIcon(key) {
    if (!cpuIcons.has(key)) cpuIcons.set(key, fetchWithin(8000, "cpu-icon?key=" + encodeURIComponent(key))
      .then(r => r.ok ? r.blob() : null).then(b => b && URL.createObjectURL(b)).catch(() => null)
      .then(url => { if (!url) cpuIcons.delete(key); return url; }));
    return cpuIcons.get(key);
  }
  const pctText = v => v < .1 ? "<0.1%" : v < 10 ? v.toFixed(1) + "%" : Math.round(v) + "%";
  function cpuAppRow(app, key, v) {
    const row = document.createElement("div");
    row.className = "row";
    const ico = document.createElement("span");
    ico.className = "app-ico";
    const color = app ? histCpu.colors.get(key) || app.color || MAC_GREYS[0] : null;
    if (app && app.kind === "app") {
      cpuIcon(app.key).then(url => {
        if (!url) { ico.style.background = color; return; }
        const img = new Image();
        img.alt = "";
        img.src = url;
        ico.append(img);
      });
    } else if (app) {
      ico.style.background = color;
      if (app.kind === "macos") ico.textContent = "";
    }
    const title = document.createElement("span");
    title.className = "title";
    title.textContent = app ? app.name : "Everything else";
    const value = document.createElement("span");
    value.className = "value";
    value.textContent = pctText(v);
    row.append(ico, title, value);
    return row;
  }
  function showCpuApps(bin) {
    const box = $("histAppsBox"), c = histCpu;
    if (!c || !c.rangeCovered) { box.hidden = true; return; }
    box.hidden = false;
    const secs = bin ? bin.secs : c.range, covered = bin ? bin.covered : c.rangeCovered;
    const keys = bin ? [...bin.secs].filter(([k, s]) => k >= 0 && s > 0).sort((a, b) => b[1] - a[1]).slice(0, 3).map(([k]) => k) : c.top3;
    let total = 0;
    for (const s of secs.values()) total += s;
    let rest = total;
    const rows = keys.map(k => { rest -= secs.get(k) || 0; return cpuAppRow(c.d.apps[k], k, c.pct(secs.get(k) || 0, covered)); });
    if (rest > 0.005 * total) rows.push(cpuAppRow(null, null, c.pct(rest, covered)));
    $("histAppsHead").textContent = "Who used it · " + (bin ? slotLabel(bin) : RANGE_WORDS[histHours]);
    $("histApps").replaceChildren(...rows);
  }
```

- [ ] **Step 5: Preview checks (mock, port 8898)** — open the page, tap Processor (`#cpuRow`), then verify with `read_page`/screenshots:
  - Sheet titled "Processor", big number = mock `mac.cpu` (14%), "Now · Light use".
  - 6H: stacked bars, coloured bottoms (Claude orange, Docker blue), grey tops; y labels 0–25% (or the fitting scale); time labels along the bottom.
  - "Who used it · last 6 hours" with 3 apps + Everything else; app icons load (mock squares).
  - Scrub (pointer events via `javascript_tool` dispatching `pointerdown`/`pointermove` on `#histChart`): big number = bar total, list header = slot time, list changes; release restores.
  - 1H and 24H both render; no console errors.
  - Temperature and Battery tiles still open their graphs with story timeline, and no app list (regression).
  - Light mode (`resize_window colorScheme: light`) — bars and greys readable.
- [ ] **Step 6: Commit** — `git commit -am "Processor graph on the phone: bars split by app, who used it"`

---

### Task 6: Install, live checks, PR

- [ ] **Step 1:** Ask the user before running `build.sh` (the `apps-tips` branch is currently installed and not merged; installing `cpu-graph` replaces it on the phone until both are merged).
- [ ] **Step 2:** `./build.sh`, wait ~5 s, then `tests/live_checks.sh; echo EXIT $?` (do not chain anything after it). Expected: all PASS incl. `PASS cpu history`, `PASS cpu icon`; `EXIT 0`. Re-run once if socket checks fail with ECONNREFUSED right after the build.
- [ ] **Step 3:** Ask the user to try it on the iPhone (Status → Processor).
- [ ] **Step 4:** When the user says merge: rebase on master, push with the EdenH123 credential helper, `gh pr create` + `gh pr merge` (see memory `lid-awake-merge-via-pr`), pull master, update the missions memory.
