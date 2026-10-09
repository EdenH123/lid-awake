# Memory window Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Tapping the Memory row on the phone's Status page opens a "Memory" sheet with a live pressure graph (last 2 min, only while open), four number tiles and the top 5 apps by memory.

**Architecture:** New `MemoryInfo.swift` reads RAM numbers, pressure and per-app footprints fresh on each call; `GET /memory` serves them (session-gated like `/status`). The page polls it every 2 s only while the sheet is open, keeping ≤60 points in JS. Sheet reuses the `.hist-sheet` look and the shared shade; the drag-to-close code becomes one helper used by both sheets.

**Tech Stack:** Swift (Darwin/mach, libproc), one-file HTML/CSS/JS in `Dashboard.swift`, shell checks in `tests/`.

Spec: `docs/specs/2026-10-10-memory-sheet-design.md`. Worktree `~/Developer/LidAwake-memsheet`, branch `memory-sheet`.

---

## File map

- Create `MemoryInfo.swift` — RAM counts, swap, pressure, top apps, `snapshot()`.
- Modify `SystemInfo.swift` — `memoryUsed()` calls `MemoryInfo` (one formula for row and sheet).
- Modify `Server.swift` — `GET /memory`.
- Modify `main.swift` — selftest line `memory: …`.
- Create `tests/memory/main.swift`, `tests/memory_check.sh` — unit checks against the real Mac.
- Modify `tests/live_checks.sh` — run memory_check + read the selftest line.
- Modify `Dashboard.swift` — tappable row, sheet markup + CSS, JS (open/close, polling, render), shared drag helper.

---

### Task 1: MemoryInfo + unit checks

**Files:** Create `MemoryInfo.swift`, `tests/memory/main.swift`, `tests/memory_check.sh`

- [ ] **Step 1: Write the failing check program** `tests/memory/main.swift`

```swift
import Foundation

// MemoryInfo.swift against this Mac's real numbers (compiled together by memory_check.sh).
var failed = false
func check(_ name: String, _ ok: Bool, _ detail: String = "") {
    print((ok ? "PASS " : "FAIL ") + name + (detail.isEmpty ? "" : " (\(detail))"))
    if !ok { failed = true }
}

check("helper counts toward its app", MemoryInfo.appBundle(of: "/Applications/Google Chrome.app/Contents/Frameworks/G.framework/Helpers/Google Chrome Helper (Renderer).app/Contents/MacOS/Google Chrome Helper (Renderer)") == "/Applications/Google Chrome.app")
check("plain program is not an app", MemoryInfo.appBundle(of: "/usr/libexec/trustd") == nil)
check("folder named .apps is not an app", MemoryInfo.appBundle(of: "/Users/a/my.apps/tool") == nil)

let s = MemoryInfo.snapshot { "id:" + $0 }
var mem: UInt64 = 0, size = MemoryLayout<UInt64>.size
sysctlbyname("hw.memsize", &mem, &size, nil, 0)
let total = s["total"] as? UInt64 ?? 0, used = s["used"] as? UInt64 ?? 0
check("total is hw.memsize", total == mem, "\(total)")
check("used is app + wired + compressed", used > 0 && used == (s["app"] as? UInt64 ?? 0) + (s["wired"] as? UInt64 ?? 0) + (s["compressed"] as? UInt64 ?? 0))
check("used ≤ total", used <= total)
check("free + files ≤ total", (s["free"] as? UInt64 ?? 0) + (s["cached"] as? UInt64 ?? 0) <= total)
let pressure = s["pressure"] as? Int ?? -1
check("pressure 0–100", (0...100).contains(pressure), "\(pressure)")
check("level is a known word", ["normal", "warn", "critical"].contains(s["level"] as? String ?? ""))
let top = s["top"] as? [[String: Any]] ?? []
let bytes = top.compactMap { $0["bytes"] as? UInt64 }
check("top apps 1–5", (1...5).contains(top.count), "\(top.map { $0["name"] as? String ?? "?" })")
check("top apps largest first", bytes == bytes.sorted(by: >) && bytes.allSatisfy { $0 > 0 })
check("top apps have names and ids", top.allSatisfy { !($0["name"] as? String ?? "").isEmpty && ($0["id"] as? String ?? "").hasPrefix("id:/") })
exit(failed ? 1 : 0)
```

`tests/memory_check.sh`:

```sh
#!/bin/sh
# The Memory window's numbers (MemoryInfo.swift), read from this Mac.
cd "$(dirname "$0")" || exit 1
OUT="$(mktemp -d)"
swiftc -o "$OUT/memory" memory/main.swift ../MemoryInfo.swift || { rm -rf "$OUT"; exit 1; }
result="$("$OUT/memory")"
rm -rf "$OUT"
echo "$result"
echo "$result" | grep -q "^FAIL" && exit 1
exit 0
```

- [ ] **Step 2: Run it, expect a compile failure** — `sh tests/memory_check.sh` → "cannot find 'MemoryInfo'".

- [ ] **Step 3: Write `MemoryInfo.swift`**

```swift
import Darwin
import Foundation

// The phone's Memory window: where the Mac's memory goes right now, how hard macOS is working
// to find room (pressure), and the apps using the most. Read fresh on every call; nothing is kept.
enum MemoryInfo {
    struct Counts { var app, wired, compressed, cached, free: UInt64 }

    // Bytes per kind, from the kernel's page counts (the same ones Activity Monitor uses).
    static func counts() -> Counts? {
        var stats = vm_statistics64()
        var count = mach_msg_type_number_t(MemoryLayout<vm_statistics64>.size / MemoryLayout<integer_t>.size)
        let ok = withUnsafeMutablePointer(to: &stats) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                host_statistics64(mach_host_self(), HOST_VM_INFO64, $0, &count)
            }
        }
        guard ok == KERN_SUCCESS else { return nil }
        let page = UInt64(vm_kernel_page_size)
        let anon = UInt64(stats.internal_page_count)
        return Counts(app: (anon - min(UInt64(stats.purgeable_count), anon)) * page,
                      wired: UInt64(stats.wire_count) * page,
                      compressed: UInt64(stats.compressor_page_count) * page,
                      cached: UInt64(stats.external_page_count) * page,
                      free: UInt64(stats.free_count) * page)
    }

    // Activity Monitor's "Memory Used": app + wired + compressed.
    static func used(_ c: Counts) -> UInt64 { c.app + c.wired + c.compressed }

    // Memory moved out to the disk because RAM ran short.
    static func swapUsed() -> UInt64 {
        var usage = xsw_usage(), size = MemoryLayout<xsw_usage>.size
        return sysctlbyname("vm.swapusage", &usage, &size, nil, 0) == 0 ? usage.xsu_used : 0
    }

    private static func sysctlInt(_ name: String) -> Int32? {
        var value: Int32 = 0, size = MemoryLayout<Int32>.size
        return sysctlbyname(name, &value, &size, nil, 0) == 0 ? value : nil
    }

    // The outermost .app a program lives in ("/Applications/Google Chrome.app" for every Chrome
    // helper), or nil for programs that aren't part of an app.
    static func appBundle(of path: String) -> String? {
        guard let r = path.range(of: ".app/") else { return nil }
        return String(path[..<r.lowerBound]) + ".app"
    }

    // The app's own name ("Claude Code" rather than its file name "claude").
    static func appName(_ path: String) -> String {
        let info = Bundle(path: path)?.infoDictionary
        let name = info?["CFBundleDisplayName"] as? String ?? info?["CFBundleName"] as? String
        return name.flatMap { $0.isEmpty ? nil : $0 } ?? URL(fileURLWithPath: path).deletingPathExtension().lastPathComponent
    }

    // The apps using the most memory, helpers included, by Activity Monitor's "Memory" figure.
    // Programs of other users (and the system's own) can't be read and are skipped.
    static func topApps(limit: Int = 5, id: (String) -> String?) -> [[String: Any]] {
        let n = proc_listallpids(nil, 0)
        guard n > 0 else { return [] }
        var pids = [pid_t](repeating: 0, count: Int(n) + 64)
        let got = pids.withUnsafeMutableBufferPointer {
            proc_listallpids($0.baseAddress, Int32($0.count * MemoryLayout<pid_t>.size))
        }
        var sizes: [String: UInt64] = [:]
        var buf = [CChar](repeating: 0, count: 4 * Int(MAXPATHLEN))
        for pid in pids.prefix(Int(max(got, 0))) where pid > 0 {
            guard proc_pidpath(pid, &buf, UInt32(buf.count)) > 0,
                  let app = appBundle(of: String(cString: buf)) else { continue }
            var info = rusage_info_v4()
            let ok = withUnsafeMutablePointer(to: &info) {
                $0.withMemoryRebound(to: rusage_info_t?.self, capacity: 1) { proc_pid_rusage(pid, RUSAGE_INFO_V4, $0) }
            }
            if ok == 0 { sizes[app, default: 0] += info.ri_phys_footprint }
        }
        return sizes.sorted { $0.value > $1.value }.prefix(limit).map { path, bytes in
            var item: [String: Any] = ["name": appName(path), "bytes": bytes]
            if let i = id(path) { item["id"] = i }
            return item
        }
    }

    // Everything the Memory window shows. `id` names an app for the phone's icon route.
    static func snapshot(id: (String) -> String? = { _ in nil }) -> [String: Any] {
        var out: [String: Any] = ["total": ProcessInfo.processInfo.physicalMemory, "swapUsed": swapUsed(),
                                  "top": topApps(id: id)]
        if let c = counts() {
            out["used"] = used(c); out["app"] = c.app; out["wired"] = c.wired
            out["compressed"] = c.compressed; out["cached"] = c.cached; out["free"] = c.free
        }
        if let freePercent = sysctlInt("kern.memorystatus_level") { out["pressure"] = max(0, min(100, 100 - Int(freePercent))) }
        let level = sysctlInt("kern.memorystatus_vm_pressure_level") ?? 1   // 1 normal, 2 warn, 4 critical
        out["level"] = level >= 4 ? "critical" : level >= 2 ? "warn" : "normal"
        return out
    }
}
```

- [ ] **Step 4: Run** `sh tests/memory_check.sh` → all PASS.
- [ ] **Step 5: Commit** `git add MemoryInfo.swift tests/memory tests/memory_check.sh && git commit -m "MemoryInfo: memory numbers, pressure and top apps for the Memory window"`

### Task 2: Mac wiring (row formula, route, selftest, live checks)

**Files:** Modify `SystemInfo.swift:156-170`, `Server.swift` (after `/battery-history`), `main.swift` selftest `lines`, `tests/live_checks.sh` (before `exit $fail`)

- [ ] **Step 1:** Replace the body of `SystemInfo.memoryUsed()` with `MemoryInfo.counts().map(MemoryInfo.used)` (keep the comment).
- [ ] **Step 2:** In `Server.swift` add:

```swift
        // The Memory window: read fresh each time (the phone asks every 2 s while it's open).
        case ("GET", "/memory"):
            let snap = MemoryInfo.snapshot { PhoneApps.id($0) }
            reply(c, "200 OK", "application/json", (try? JSONSerialization.data(withJSONObject: snap)) ?? Data("{}".utf8))
```

- [ ] **Step 3:** Selftest line (in the `lines` array, after battery history):

```swift
                 // The Memory window, as the installed app sees it (other programs' sizes need the same user).
                 { let m = MemoryInfo.snapshot(); return "memory: top \((m["top"] as? [Any])?.count ?? 0) apps, used \(((m["used"] as? UInt64) ?? 0) >> 30) of \(((m["total"] as? UInt64) ?? 0) >> 30) GB, pressure \(m["pressure"] ?? "?") \(m["level"] ?? "?")" }(),
```

- [ ] **Step 4:** `tests/live_checks.sh`, before `exit $fail`:

```sh
# Memory window: numbers checked on this Mac, then what the installed app itself can read.
if sh ./memory_check.sh > /tmp/lidawake-mem.$$ 2>&1; then echo "PASS memory numbers ($(grep -c '^PASS' /tmp/lidawake-mem.$$) checks)"
else echo "FAIL memory numbers:"; grep -v '^PASS' /tmp/lidawake-mem.$$; fail=1; fi
rm -f /tmp/lidawake-mem.$$
mm=$(grep "^memory:" "$LOG")
if echo "$mm" | grep -q "top [1-5] apps"; then echo "PASS memory in the app (${mm#memory: })"
else echo "FAIL memory in the app: '$mm'"; fail=1; fi
```

- [ ] **Step 5:** `swiftc -o /tmp/la-check *.swift` compiles; commit "Memory window: /memory route, selftest and live checks".

### Task 3: Page — row, sheet, live graph

**Files:** Modify `Dashboard.swift` (CSS after `.hist-sheet` rules; Memory row ~line 1013; sheet markup after `#histSheet`; JS after the history sheet drag code)

- [ ] **Step 1: CSS**

```css
  /* Memory window: a live pressure graph while it's open, four numbers and the apps using the most. */
  .health-list .row.tappable { cursor: pointer; -webkit-tap-highlight-color: transparent; }
  .health-list .row.tappable:active { opacity: .6; }
  .row-chev { width: 7px; height: 12px; flex: none; margin-left: -4px; fill: none; stroke: var(--faint); stroke-width: 2.2; stroke-linecap: round; stroke-linejoin: round; }
  .mem-graph-card { position: relative; padding: 14px 16px 10px; }
  .mem-graph-head { display: flex; justify-content: space-between; align-items: center; font-size: 15px; color: var(--muted); }
  .mem-pill { display: inline-flex; align-items: center; gap: 6px; padding: 3px 10px; border-radius: 99px; font-size: 13px; font-weight: 600;
    --tint: var(--green); color: var(--tint); background: color-mix(in srgb, var(--tint) 16%, transparent); }
  .mem-pill::before { content: ""; width: 7px; height: 7px; border-radius: 50%; background: currentColor; }
  .mem-pill.warn { --tint: #ffcc00; } .mem-pill.critical { --tint: var(--red); }
  .mem-graph { display: block; width: 100%; height: 150px; margin-top: 10px; overflow: visible; }
  .mem-graph .grid { stroke: var(--line); stroke-width: .5; vector-effect: non-scaling-stroke; }
  #memLine { fill: none; stroke-width: 2; stroke-linejoin: round; vector-effect: non-scaling-stroke; }
  #memArea { opacity: .28; stroke: none; }
  .mem-note { position: absolute; left: 0; right: 0; top: 50%; text-align: center; font-size: 15px; color: var(--muted); }
  .mem-graph-foot { display: flex; justify-content: space-between; font-size: 12px; color: var(--muted); margin-top: 4px; }
  .mem-tile { min-height: 0; padding: 12px 16px 14px; }
  .mem-tile .tile-value { font-size: 28px; margin-top: 2px; }
  .mem-apps .row { gap: 12px; }
  .mem-icon { width: 32px; height: 32px; flex: none; border-radius: 8px; }
  .mem-icon.blank { background: var(--fill); }
  .mem-apps .title > i { display: block; height: 4px; margin-top: 5px; border-radius: 99px; background: var(--blue); transition: width .5s ease; }
```

- [ ] **Step 2: Memory row** becomes
`<div class="row tappable" id="memRow" role="button" tabindex="0" aria-label="Memory, show what's using it"><span class="title">Memory</span><div class="meter"><div id="memBar" style="width:0"></div></div><span class="value" id="memValue">—</span><svg class="row-chev" viewBox="0 0 8 14" aria-hidden="true"><path d="M1.5 1.5 6.5 7l-5 5.5"/></svg></div>`

- [ ] **Step 3: Sheet markup** after `</section>` of `#histSheet`:

```html
<section class="hist-sheet" id="memSheet" role="dialog" aria-label="Memory" aria-hidden="true">
  <div class="sheet-head" id="memHead"><span>Memory</span><button type="button" id="memDone">Done</button></div>
  <div class="hist-body">
    <div class="card mem-graph-card">
      <div class="mem-graph-head"><span>Memory pressure · live</span><span class="mem-pill" id="memPill">—</span></div>
      <svg class="mem-graph" viewBox="0 0 300 120" preserveAspectRatio="none" role="img" aria-label="Memory pressure, last 2 minutes">
        <line class="grid" x1="0" x2="300" y1="30" y2="30"/><line class="grid" x1="0" x2="300" y1="60" y2="60"/><line class="grid" x1="0" x2="300" y1="90" y2="90"/>
        <path id="memArea"/><path id="memLine"/>
      </svg>
      <div class="mem-note" id="memNote">Measuring…</div>
      <div class="mem-graph-foot"><span>2 min ago</span><span>now</span></div>
    </div>
    <div class="tiles">
      <div class="card tile mem-tile"><div class="tile-head">Used</div><div class="tile-value" id="memUsedNum">—</div></div>
      <div class="card tile mem-tile"><div class="tile-head">Free + files</div><div class="tile-value" id="memFreeNum">—</div></div>
      <div class="card tile mem-tile"><div class="tile-head">Apps</div><div class="tile-value" id="memAppNum">—</div></div>
      <div class="card tile mem-tile"><div class="tile-head">Overflow to disk</div><div class="tile-value" id="memSwapNum">—</div></div>
    </div>
    <div class="section" id="memAppsSection" hidden><div class="section-head">Using the most</div><div class="list mem-apps" id="memApps"></div></div>
    <div class="section-foot">Pressure shows how hard your Mac is working to find room. Green is fine even when memory looks full: macOS keeps recent files in spare memory and hands it back the moment an app needs it. Overflow to disk is memory moved to storage when room ran out.</div>
  </div>
</section>
```

- [ ] **Step 4: JS** — shared shade close + drag helper (replace the history-only drag code), then the Memory window:

```js
  // Both sheets share one shade; it goes once neither is open.
  function hideShade() {
    const shade = $("histShade");
    shade.classList.remove("show");
    setTimeout(() => { if (!document.querySelector(".hist-sheet.open")) shade.hidden = true; }, 350);
  }
  function closeSheets() {
    if ($("memSheet").classList.contains("open")) setMemoryOpen(false);
    if ($("histSheet").classList.contains("open")) setHistoryOpen(false);
  }
  // Pull a sheet down by its top to close it.
  function dragToClose(head, sheet, close) {
    let drag = null;
    head.addEventListener("pointerdown", e => {
      if (e.target.closest("button")) return;
      drag = { y: e.clientY, t: performance.now(), dy: 0 };
      head.setPointerCapture(e.pointerId);
      sheet.classList.add("dragging");
    });
    head.addEventListener("pointermove", e => {
      if (!drag) return;
      drag.dy = Math.max(0, e.clientY - drag.y);
      sheet.style.setProperty("--drag", drag.dy + "px");
    });
    for (const ev of ["pointerup", "pointercancel"]) head.addEventListener(ev, () => {
      if (!drag) return;
      const fast = drag.dy / (performance.now() - drag.t) > .5;
      sheet.classList.remove("dragging");
      if (drag.dy > 120 || (fast && drag.dy > 30)) close();
      else sheet.style.removeProperty("--drag");
      drag = null;
    });
  }
  dragToClose($("histHead"), $("histSheet"), () => setHistoryOpen(false));

  // ---------- Memory window ----------
  // Tap the Memory row: where the memory goes right now, plus a pressure graph that draws for as
  // long as the window is open (one reading every 2 s, last 2 minutes; forgotten on close).
  const MEM_SPAN = 120000;
  let memTimer = null, memPoints = [];
  const memRows = new Map();
  const memGB = b => b == null ? "—" : (b / GiB < 10 ? (b / GiB).toFixed(1) : Math.round(b / GiB)) + " GB";
  function setMemoryOpen(open) {
    const sheet = $("memSheet"), shade = $("histShade");
    sheet.style.removeProperty("--drag");
    sheet.setAttribute("aria-hidden", !open);
    clearInterval(memTimer);
    memTimer = null;
    if (open) {
      memPoints = [];
      renderMemGraph("normal");
      $("memNote").textContent = "Measuring…";
      $("memNote").hidden = false;
      shade.hidden = false;
      requestAnimationFrame(() => { shade.classList.add("show"); sheet.classList.add("open"); });
      startMemory();
    } else {
      sheet.classList.remove("open");
      hideShade();
    }
  }
  function startMemory() {
    clearInterval(memTimer);
    memTimer = setInterval(loadMemory, 2000);
    loadMemory();
  }
  async function loadMemory() {
    try {
      const res = await fetchWithin(4000, "memory", { cache: "no-store" });
      if (!res.ok) throw new Error(res.status);
      const m = await res.json();
      if (!memTimer) return;  // closed while asking
      const now = Date.now();
      memPoints.push({ t: now, p: m.pressure ?? 0 });
      memPoints = memPoints.filter(pt => now - pt.t <= MEM_SPAN);
      renderMemory(m);
    } catch (e) {
      if (!memTimer) return;
      $("memNote").textContent = "Can't reach the Mac";
      $("memNote").hidden = false;
    }
  }
  function renderMemGraph(level) {
    const now = Date.now(), W = 300, H = 120;
    const pts = memPoints.map(pt => [W - (now - pt.t) / MEM_SPAN * W, H - pt.p / 100 * H].map(v => v.toFixed(1)));
    const line = pts.map((p, i) => (i ? "L" : "M") + p[0] + " " + p[1]).join(" ");
    const draw = pts.length > 1;
    $("memLine").setAttribute("d", draw ? line : "");
    $("memArea").setAttribute("d", draw ? `${line} L${pts[pts.length - 1][0]} ${H} L${pts[0][0]} ${H} Z` : "");
    const color = { warn: "#ffcc00", critical: "var(--red)" }[level] || "var(--green)";
    $("memLine").style.stroke = color;
    $("memArea").style.fill = color;
  }
  function memAppRow(a) {
    const row = document.createElement("div");
    row.className = "row";
    let icon;
    if (a.id) {
      icon = document.createElement("img");
      icon.src = "app-icon.png?id=" + encodeURIComponent(a.id);
      icon.alt = "";
      icon.addEventListener("error", () => { const blank = document.createElement("span"); blank.className = "mem-icon blank"; icon.replaceWith(blank); });
    } else icon = document.createElement("span");
    icon.className = a.id ? "mem-icon" : "mem-icon blank";
    const title = document.createElement("div");
    title.className = "title";
    title.append(a.name, document.createElement("i"));
    const value = document.createElement("span");
    value.className = "value";
    row.append(icon, title, value);
    return row;
  }
  function renderMemory(m) {
    const level = ["warn", "critical"].includes(m.level) ? m.level : "normal";
    $("memPill").textContent = { normal: "Low", warn: "Medium", critical: "High" }[level];
    $("memPill").className = "mem-pill " + level;
    renderMemGraph(level);
    $("memNote").textContent = "Measuring…";
    $("memNote").hidden = memPoints.length > 1;
    $("memUsedNum").textContent = memGB(m.used);
    $("memFreeNum").textContent = m.free == null ? "—" : memGB(m.free + (m.cached || 0));
    $("memAppNum").textContent = memGB(m.app);
    $("memSwapNum").textContent = memGB(m.swapUsed);
    const top = m.top || [], most = top.length ? top[0].bytes : 1, seen = new Set();
    for (const a of top) {
      let row = memRows.get(a.name);
      if (!row) { row = memAppRow(a); memRows.set(a.name, row); }
      row.querySelector(".title > i").style.width = Math.max(2, a.bytes / most * 100) + "%";
      row.querySelector(".value").textContent = memGB(a.bytes);
      $("memApps").append(row);
      seen.add(a.name);
    }
    for (const [name, row] of memRows) if (!seen.has(name)) { row.remove(); memRows.delete(name); }
    $("memAppsSection").hidden = !top.length;
  }
  $("memRow").addEventListener("click", () => { if (!document.querySelector("main.waiting")) { tick(); setMemoryOpen(true); } });
  $("memRow").addEventListener("keydown", e => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); setMemoryOpen(true); } });
  addTickPad($("memRow"));
  $("memDone").addEventListener("click", () => setMemoryOpen(false));
  dragToClose($("memHead"), $("memSheet"), () => setMemoryOpen(false));
  // No asking while the phone's page is hidden; carry on when it's back.
  document.addEventListener("visibilitychange", () => {
    if (!$("memSheet").classList.contains("open")) return;
    if (document.hidden) { clearInterval(memTimer); memTimer = null; } else startMemory();
  });
```

Also: `setHistoryOpen(false)` branch uses `hideShade()`; `#histShade` click and Escape call `closeSheets()`.

- [ ] **Step 5: Mock preview** — copy the mock server to the scratchpad, add a wiggling `/memory`, run with `SRC=<worktree>/Dashboard.swift`, check sheet in dark + light, phone width, tap/drag/Done, "Measuring…" → graph after 2 readings, icon fallback.
- [ ] **Step 6: Commit** "Memory window on the phone: live pressure graph, numbers, top apps".

### Task 4: Install + full checks

- [ ] `sh build.sh` (only this session builds now), wait ~5 s, `sh tests/live_checks.sh; echo exit=$?` → exit 0 incl. the two memory lines.
- [ ] Hit `/memory` through the running app via selftest log line; tell the user it's installed on branch `memory-sheet`, merge via PR when they say "merge".
