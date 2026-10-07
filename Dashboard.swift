let dashboardHTML = #"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="apple-mobile-web-app-capable" content="yes">
<meta name="apple-mobile-web-app-title" content="Lid Awake">
<meta name="theme-color" content="#f4f4f1" media="(prefers-color-scheme: light)">
<meta name="theme-color" content="#121212" media="(prefers-color-scheme: dark)">
<title>Lid Awake</title>
<style>
  :root {
    --bg: #f4f4f1; --card: #ffffff; --text: #1b1b1a; --muted: #6b6b66; --line: #e6e5e0;
    --green: #1f9d55; --green-soft: #e3f4ea; --amber: #b7791f; --red: #d23f31; --track: #ecebe6;
    --seg: #e8e7e2; --seg-on: #ffffff;
  }
  @media (prefers-color-scheme: dark) {
    :root {
      --bg: #121212; --card: #1d1d1c; --text: #f1f0ec; --muted: #9a9993; --line: #2c2c2a;
      --green: #3ccf7e; --green-soft: #17301f; --amber: #e0a43c; --red: #f06b5d; --track: #2c2c2a;
      --seg: #242423; --seg-on: #3a3a38;
    }
  }
  * { box-sizing: border-box; }
  body {
    margin: 0; background: var(--bg); color: var(--text);
    font: 16px/1.4 -apple-system, BlinkMacSystemFont, "SF Pro Text", system-ui, sans-serif;
    -webkit-font-smoothing: antialiased;
    padding: max(20px, env(safe-area-inset-top)) 16px max(24px, env(safe-area-inset-bottom));
  }
  main { max-width: 460px; margin: 0 auto; display: grid; gap: 12px; }
  main.wide { max-width: 1200px; }
  header { display: flex; align-items: baseline; justify-content: space-between; gap: 12px; padding: 4px 4px 0; }
  h1 { font-size: 22px; margin: 0; letter-spacing: -0.01em; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .live { font-size: 13px; color: var(--muted); display: flex; align-items: center; gap: 6px; flex: none; }
  .dot { width: 8px; height: 8px; border-radius: 50%; background: var(--green); }
  .live.stale .dot { background: var(--red); }

  .tabs { display: grid; grid-template-columns: 1fr 1fr; background: var(--seg); border-radius: 10px; padding: 2px; }
  .tabs button {
    font: inherit; font-size: 14px; font-weight: 600; color: var(--muted); background: none; border: 0;
    padding: 7px 0; border-radius: 8px; cursor: pointer;
  }
  .tabs button.active { background: var(--seg-on); color: var(--text); box-shadow: 0 1px 2px rgba(0,0,0,.12); }
  .tab { display: grid; gap: 12px; }
  .tab[hidden] { display: none; }

  .card { background: var(--card); border: 1px solid var(--line); border-radius: 18px; padding: 18px; }
  .label { font-size: 13px; color: var(--muted); margin-bottom: 6px; }
  .big { font-size: 40px; font-weight: 650; letter-spacing: -0.02em; line-height: 1.1; font-variant-numeric: tabular-nums; }
  .sub { color: var(--muted); margin-top: 4px; font-size: 15px; }
  .status.on { background: var(--green-soft); border-color: transparent; }
  .status.on .big { color: var(--green); }
  .countdown { margin-top: 14px; padding-top: 14px; border-top: 1px solid var(--line); display: none; }
  .status.on .countdown { border-color: color-mix(in srgb, var(--green) 25%, transparent); }
  .countdown .big { font-size: 34px; }
  .bar { height: 10px; background: var(--track); border-radius: 99px; margin-top: 12px; overflow: hidden; position: relative; }
  .fill { height: 100%; border-radius: 99px; background: var(--green); transition: width .4s ease; }
  .limit { position: absolute; top: -3px; bottom: -3px; width: 2px; background: var(--muted); opacity: .6; }
  .row { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
  .warn { color: var(--amber) !important; }
  .bad { color: var(--red) !important; }
  footer { font-size: 13px; color: var(--muted); text-align: center; padding: 6px 8px; }

  .notice { background: var(--card); border: 1px solid var(--line); border-radius: 14px; padding: 14px 16px; font-size: 15px; }
  .notice[hidden] { display: none; }
  .screen-wrap {
    position: relative; background: #000; border-radius: 12px; overflow: hidden; border: 1px solid var(--line);
    touch-action: none; -webkit-user-select: none; user-select: none; -webkit-touch-callout: none;
    min-height: 120px;
  }
  .screen-wrap img { display: block; width: 100%; height: auto; -webkit-user-drag: none; pointer-events: none;  transform-origin: 0 0; }
  .zoombar { display: flex; align-items: center; justify-content: center; gap: 10px; font-size: 13px; color: var(--muted); }
  .zoombar[hidden] { display: none; }
  .quality { display: flex; justify-content: center; gap: 8px; flex-wrap: wrap; }
  .quality button {
    font: inherit; font-size: 13px; font-weight: 600; color: var(--text); background: var(--card);
    border: 1px solid var(--line); border-radius: 99px; padding: 6px 12px; cursor: pointer;
    transition: background .15s ease, color .15s ease;
  }
  .quality button.active { background: var(--green); border-color: var(--green); color: #fff; }
  .zoombar button { font: inherit; font-weight: 600; color: var(--text); background: var(--card); border: 1px solid var(--line); border-radius: 99px; padding: 4px 12px; cursor: pointer; }
  .hint { font-size: 13px; color: var(--muted); text-align: center; }
  .typebar { display: flex; gap: 8px; }
  .panel-row { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
  .panel-row .panel-toggle { justify-self: auto; }
  .kb-btn {
    font: inherit; font-size: 15px; font-weight: 600; color: var(--text); background: var(--card);
    border: 1px solid var(--line); border-radius: 10px; padding: 10px 14px; cursor: pointer;
  }
  .kb-btn.on { background: var(--green); border-color: var(--green); color: #fff; }
  /* Invisible field that receives the iPhone keyboard; 16px stops iOS zooming in on focus. */
  .live-input {
    position: fixed; left: 0; bottom: 0; width: 1px; height: 1px; opacity: 0;
    font-size: 16px; border: 0; padding: 0; background: transparent; color: transparent;
  }
  .typebar input {
    flex: 1; min-width: 0; font: inherit; color: var(--text); background: var(--card);
    border: 1px solid var(--line); border-radius: 10px; padding: 10px 12px;
  }
  .typebar button, .keys button {
    font: inherit; font-size: 15px; font-weight: 600; color: var(--text); background: var(--card);
    border: 1px solid var(--line); border-radius: 10px; padding: 10px 14px; cursor: pointer;
  }
  .keys { display: grid; grid-template-columns: repeat(6, 1fr); gap: 6px; }
  .keys.arrows { grid-template-columns: repeat(6, 1fr); }
  .keys .space { grid-column: span 2; }
  .panel-toggle {
    justify-self: center; font: inherit; font-size: 13px; font-weight: 600; color: var(--muted);
    background: var(--seg); border: 0; border-radius: 99px; padding: 6px 14px; cursor: pointer;
  }
  .keys button.on { background: var(--green); border-color: var(--green); color: #fff; }
  .status-head { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
  .status-head .label { margin: 0; }
  .awake-switch {
    position: relative; width: 51px; height: 31px; border-radius: 99px; border: 0; padding: 0; flex: none;
    background: var(--track); cursor: pointer; transition: background .2s ease;
  }
  .awake-switch::after {
    content: ""; position: absolute; top: 2px; left: 2px; width: 27px; height: 27px; border-radius: 50%;
    background: #fff; box-shadow: 0 1px 3px rgba(0,0,0,.3); transition: transform .2s ease;
  }
  .awake-switch.on { background: var(--green); }
  .awake-switch.on::after { transform: translateX(20px); }
  .awake-switch:disabled { opacity: .5; }
  .keys button { padding: 10px 0; }
  .gestures { display: grid; grid-template-columns: 1fr 1.3fr 1.1fr 1fr; gap: 6px; }
  .gestures button {
    font: inherit; font-size: 13px; font-weight: 600; color: var(--text); background: var(--card);
    border: 1px solid var(--line); border-radius: 10px; padding: 10px 2px; cursor: pointer;
  }
  .gestures button:active { background: var(--seg); }
  .trackpad {
    height: 230px; border-radius: 18px; background: var(--seg); border: 1px solid var(--line);
    display: flex; align-items: flex-end; justify-content: center; padding-bottom: 12px;
    color: var(--muted); font-size: 12px; letter-spacing: .04em;
    touch-action: none; -webkit-user-select: none; user-select: none; -webkit-touch-callout: none;
    transition: background .15s ease;
  }
  .trackpad.active { background: var(--seg-on); }
  .typebar button:active, .keys button:active { background: var(--seg); }

  .head-right { display: flex; align-items: center; gap: 12px; flex: none; }
  .lock-btn {
    font: inherit; font-size: 13px; font-weight: 600; color: var(--muted); background: var(--seg);
    border: 0; border-radius: 99px; padding: 4px 10px; cursor: pointer;
  }
  .lock-btn[hidden] { display: none; }
  .gate { text-align: center; padding: 28px 20px; display: grid; gap: 14px; }
  .gate[hidden] { display: none; }
  .gate .icon { font-size: 34px; }
  .gate h2 { margin: 0; font-size: 20px; }
  .gate p { margin: 0; color: var(--muted); font-size: 15px; }
  .gate form { display: grid; gap: 10px; margin-top: 4px; }
  .gate form[hidden] { display: none; }
  .gate input {
    font: inherit; font-size: 17px; color: var(--text); background: var(--bg); text-align: center;
    border: 1px solid var(--line); border-radius: 12px; padding: 12px;
  }
  .gate button {
    font: inherit; font-size: 17px; font-weight: 600; color: #fff; background: var(--green);
    border: 0; border-radius: 12px; padding: 12px; cursor: pointer;
  }
  .gate button:disabled { opacity: .6; }
  .gate .error { color: var(--red); font-size: 14px; min-height: 1.2em; }
</style>
</head>
<body>
<main id="main">
  <header>
    <h1 id="host">Lid Awake</h1>
    <div class="head-right">
      <div class="live" id="live"><span class="dot"></span><span id="liveText">Connecting…</span></div>
      <button class="lock-btn" id="lockBtn" hidden>Lock</button>
    </div>
  </header>

  <section class="card gate" id="gate" hidden>
    <div class="icon">🔒</div>
    <h2 id="gateTitle">Locked</h2>
    <p id="gateText"></p>
    <form id="loginForm">
      <input type="password" id="password" placeholder="Password" autocomplete="current-password" required>
      <button id="unlockBtn">Unlock</button>
      <div class="error" id="loginError"></div>
    </form>
  </section>

  <div class="tab" id="app" hidden>
  <nav class="tabs">
    <button data-tab="status" class="active">Status</button>
    <button data-tab="screen">Screen</button>
  </nav>

  <div class="tab" id="tab-status">
    <section class="card status" id="statusCard">
      <div class="status-head">
        <div class="label">Lid-closed awake</div>
        <button class="awake-switch" id="awakeSwitch" role="switch" aria-label="Stay awake with lid closed"></button>
      </div>
      <div class="big" id="state">—</div>
      <div class="sub" id="stateSub"></div>
      <div class="countdown" id="countdown">
        <div class="label">Turns off in</div>
        <div class="big" id="remaining">—</div>
      </div>
    </section>

    <section class="card">
      <div class="label">Battery</div>
      <div class="big" id="battery">—</div>
      <div class="sub" id="batterySub"></div>
      <div class="bar"><div class="fill" id="batteryFill" style="width:0"></div><div class="limit" id="batteryLimit"></div></div>
    </section>

    <div class="row">
      <section class="card">
        <div class="label">Battery temp</div>
        <div class="big" id="temp">—</div>
        <div class="sub" id="tempSub"></div>
      </section>
      <section class="card">
        <div class="label">Mac heat</div>
        <div class="big" id="thermal" style="font-size:28px">—</div>
        <div class="sub">macOS thermal state</div>
      </section>
    </div>

    <footer id="guards"></footer>
  </div>

  <div class="tab" id="tab-screen" hidden>
    <div class="notice" id="screenMsg" hidden></div>
    <div class="tab" id="screenControls" hidden>
    <div class="screen-wrap" id="screenWrap"><img id="screenImg" alt="Mac screen"></div>
    <nav class="quality" id="quality" aria-label="Picture quality">
      <button data-quality="sharp">🔍 Sharp</button>
      <button data-quality="balanced">⚖️ Balanced</button>
      <button data-quality="fast">⚡ Fast</button>
    </nav>
    <div class="zoombar" id="zoomBar" hidden>Zoomed <span id="zoomLevel">1.0×</span> on this phone only <button id="zoomReset">Reset zoom</button></div>
    <div class="hint" id="hint">Tap = click · Double-tap = double-click · Hold = right-click · Drag = scroll · Pinch = zoom</div>
    <div class="panel-row">
      <button type="button" class="kb-btn" id="kbBtn">⌨ Type directly</button>
      <button class="panel-toggle" id="keysToggle" aria-expanded="true">Hide buttons ▴</button>
    </div>
    <div class="tab" id="keyPanel">
    <form class="typebar" id="typeForm">
      <input id="typeInput" placeholder="Type on your Mac…" autocapitalize="off" autocorrect="off" autocomplete="off" spellcheck="false">
      <button>Send</button>
    </form>
    <div class="keys">
      <button data-key="escape">esc</button>
      <button data-key="tab">⇥</button>
      <button data-key="delete">⌫</button>
      <button data-mod="cmd" aria-label="Command">⌘</button>
      <button data-mod="alt" aria-label="Option">⌥</button>
      <button data-key="return">⏎</button>
    </div>
    <div class="keys arrows">
      <button data-key="space" class="space">space</button>
      <button data-key="left">←</button>
      <button data-key="up">↑</button>
      <button data-key="down">↓</button>
      <button data-key="right">→</button>
    </div>
    <div class="gestures">
      <button data-gesture="spaceLeft">◀ Desktop</button>
      <button data-gesture="mission">Mission Control</button>
      <button data-gesture="appWindows">App windows</button>
      <button data-gesture="spaceRight">Desktop ▶</button>
    </div>
    </div>
    <div class="trackpad" id="trackpad">Trackpad</div>
    <div class="hint">1 finger: move · tap = click · tap twice, then hold &amp; slide = drag<br>
      2 fingers: slide = scroll · tap = right-click<br>
      3 fingers: swipe ← → = switch desktop · ↑ = Mission Control · ↓ = app windows</div>
    </div>
  </div>
  </div>
</main>
<input class="live-input" id="liveInput" aria-label="Type directly on your Mac"
       autocapitalize="off" autocorrect="off" autocomplete="off" spellcheck="false" enterkeyhint="go">
<script>
  const $ = id => document.getElementById(id);
  let data = null, fetchedAt = 0, lastOk = 0;
  let currentTab = "status";

  // ---------- Status ----------
  function fmtCountdown(sec) {
    sec = Math.max(0, Math.ceil(sec));
    const h = Math.floor(sec / 3600), m = Math.floor(sec % 3600 / 60), s = sec % 60;
    const pad = n => String(n).padStart(2, "0");
    return h > 0 ? `${h}:${pad(m)}:${pad(s)}` : `${m}:${pad(s)}`;
  }
  function fmtMinutes(min) {
    if (min == null || min < 0) return null;
    const h = Math.floor(min / 60), m = min % 60;
    return h > 0 ? `${h}h ${m}m` : `${m}m`;
  }

  function render() {
    const ago = Math.round((Date.now() - lastOk) / 1000);
    const stale = !lastOk || ago > 10;
    $("live").classList.toggle("stale", stale);
    $("liveText").textContent = !lastOk ? "Can't reach Mac" : stale ? `Last seen ${ago}s ago` : "Live";
    if (!data) return;

    $("host").textContent = data.host || "Lid Awake";
    const on = data.lidAwake;
    $("statusCard").classList.toggle("on", on);
    $("awakeSwitch").classList.toggle("on", on);
    $("awakeSwitch").setAttribute("aria-checked", on);
    $("awakeSwitch").disabled = !on;
    $("state").textContent = on ? "ON" : "OFF";
    $("stateSub").textContent = on ? "Stays awake with the lid closed" : "Sleeps normally when the lid closes";

    const left = data.timerRemaining != null ? data.timerRemaining - (Date.now() - fetchedAt) / 1000 : null;
    $("countdown").style.display = on && left != null && left > 0 ? "block" : "none";
    if (left != null) $("remaining").textContent = fmtCountdown(left);
    if (on && data.timerRemaining == null) $("stateSub").textContent += " · no timer";

    if (data.battery != null) {
      const pct = data.battery;
      $("battery").textContent = pct + "%";
      $("battery").className = "big" + (pct < data.batteryLimit + 5 ? " bad" : pct < 25 ? " warn" : "");
      $("batteryFill").style.width = pct + "%";
      $("batteryFill").style.background = pct < data.batteryLimit + 5 ? "var(--red)" : pct < 25 ? "var(--amber)" : "var(--green)";
      $("batteryLimit").style.left = data.batteryLimit + "%";
      let sub;
      // macOS needs a minute or two after plugging in / unplugging before it has an estimate.
      const clockIn = min => new Date(fetchedAt + min * 60000).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
      if (data.onBattery) {
        sub = fmtMinutes(data.minutesToEmpty)
          ? `On battery · ${fmtMinutes(data.minutesToEmpty)} left (until around ${clockIn(data.minutesToEmpty)})`
          : "On battery · estimating time left…";
      } else if (data.charging) {
        sub = fmtMinutes(data.minutesToFull) && data.minutesToFull > 0
          ? `Charging · full in ${fmtMinutes(data.minutesToFull)} (around ${clockIn(data.minutesToFull)})`
          : "Charging · estimating time to full…";
      } else {
        sub = data.charged || pct >= 99 ? "Plugged in · fully charged"
          : pct >= 79 ? "Plugged in · holding charge to protect the battery"
          : "Plugged in · not charging right now";
      }
      $("batterySub").textContent = sub;
    }

    if (data.batteryTemp != null) {
      const t = data.batteryTemp, max = data.maxBatteryTemp;
      $("temp").textContent = t.toFixed(1) + "°C";
      $("temp").className = "big" + (t >= max ? " bad" : t >= max - 4 ? " warn" : "");
      $("tempSub").textContent = `Limit ${max}°C`;
    }

    const thermalText = { nominal: "Normal", fair: "Warm", serious: "Hot", critical: "Critical" }[data.thermalState] || "—";
    $("thermal").textContent = thermalText;
    $("thermal").className = "big" + (data.thermalState === "serious" || data.thermalState === "critical" ? " bad" : data.thermalState === "fair" ? " warn" : "");

    const guards = [];
    if (data.batteryGuard) guards.push(`below ${data.batteryLimit}% battery`);
    if (data.heatGuard) guards.push(`above ${data.maxBatteryTemp}°C`);
    $("guards").textContent = guards.length ? "Auto turns off " + guards.join(" or ") : "Safety limits are off";

    if (currentTab === "screen" && data.remoteScreen && data.screenPermission && !data.controlPermission) {
      $("hint").textContent = "View only. To control, allow Lid Awake in System Settings → Privacy & Security → Accessibility on your Mac.";
    } else {
      $("hint").textContent = "Tap = click · Double-tap = double-click · Hold = right-click · Drag = scroll · Pinch = zoom";
    }
  }

  // ---------- Lock ----------
  let unlocked = false, gateTimer = null;
  async function checkAuth() {
    clearTimeout(gateTimer);
    let auth;
    try {
      auth = await (await fetch("auth", { cache: "no-store" })).json();
    } catch (e) {
      // Over Tailscale the link disappears while lid-awake is off, so this is the usual "off" view.
      auth = { unreachable: true };
    }
    if (auth.off || auth.unreachable) {
      unlocked = false;
      $("app").hidden = true;
      $("lockBtn").hidden = true;
      $("gate").hidden = false;
      $("loginForm").hidden = true;
      $("live").classList.add("stale");
      $("liveText").textContent = auth.off ? "Off" : "Can't reach Mac";
      $("gateTitle").textContent = auth.off ? "Lid Awake is off" : "Can't reach your Mac";
      $("gateText").textContent = auth.off
        ? "This page only works while “Stay awake with lid closed” is on. Turn it on from the Lid Awake menu on your Mac."
        : "Lid Awake is off, or your Mac is asleep or offline. This page only works while “Stay awake with lid closed” is on.";
      gateTimer = setTimeout(checkAuth, 3000);
      return;
    }
    if (auth.loggedIn) {
      unlocked = true;
      $("gate").hidden = true;
      $("app").hidden = false;
      $("lockBtn").hidden = false;
      poll();
      if (currentTab === "screen") startFrames();
      return;
    }
    unlocked = false;
    $("app").hidden = true;
    $("lockBtn").hidden = true;
    $("gate").hidden = false;
    $("live").classList.remove("stale");
    $("liveText").textContent = "Locked";
    if (auth.hasPassword) {
      $("gateTitle").textContent = "Locked";
      $("gateText").textContent = "Enter your Lid Awake password.";
      $("loginForm").hidden = false;
    } else {
      $("gateTitle").textContent = "Set a password first";
      $("gateText").textContent = "On your Mac, click the Lid Awake icon in the menu bar and choose “Set phone password…”. This page will continue by itself.";
      $("loginForm").hidden = true;
      gateTimer = setTimeout(checkAuth, 3000);
    }
  }
  function locked() {
    stopLive();
    if (unlocked) { unlocked = false; checkAuth(); }
  }

  $("loginForm").addEventListener("submit", async e => {
    e.preventDefault();
    $("unlockBtn").disabled = true;
    $("loginError").textContent = "";
    try {
      const res = await fetch("login", {
        method: "POST",
        headers: { "Content-Type": "application/json", "X-LidAwake": "1" },
        body: JSON.stringify({ password: $("password").value }),
      });
      if (res.ok) {
        $("password").value = "";
        await checkAuth();
      } else {
        $("loginError").textContent = await res.text();
        $("password").select();
      }
    } catch (err) {
      $("loginError").textContent = "Can't reach your Mac.";
    }
    $("unlockBtn").disabled = false;
  });

  $("lockBtn").addEventListener("click", async () => {
    stopLive();
    await fetch("logout", { method: "POST", headers: { "X-LidAwake": "1" } }).catch(() => {});
    unlocked = false;
    checkAuth();
  });

  async function poll() {
    if (!unlocked) return;
    try {
      const res = await fetch("status", { cache: "no-store" });
      if (res.status === 401 || res.status === 423) return locked();
      if (!res.ok) throw new Error(res.status);
      data = await res.json();
      fetchedAt = Date.now();
      lastOk = fetchedAt;
    } catch (e) { /* keep showing the last data, marked stale */ }
    render();
  }

  // ---------- Tabs ----------
  const tabButtons = document.querySelectorAll(".tabs button");
  function showTab(name) {
    currentTab = name;
    tabButtons.forEach(b => b.classList.toggle("active", b.dataset.tab === name));
    $("tab-status").hidden = name !== "status";
    $("tab-screen").hidden = name !== "screen";
    $("main").classList.toggle("wide", name === "screen");
    try { localStorage.setItem("tab", name); } catch (e) {}
    render();
    if (name === "screen") startFrames();
    else stopLive();
  }
  tabButtons.forEach(b => b.addEventListener("click", () => showTab(b.dataset.tab)));

  // ---------- Screen frames ----------
  let framesRunning = false, frameUrl = null, frameId = 0;
  function setScreenMsg(text) {
    $("screenMsg").textContent = text;
    $("screenMsg").hidden = !text;
  }
  // ----- Live stream: one WebSocket, frames pushed by the Mac -----
  let ws = null, wsFailures = 0, wsRetryAt = 0;
  let quality = "balanced";
  try { quality = localStorage.getItem("quality") || "balanced"; } catch (e) {}
  function showQuality() {
    document.querySelectorAll("[data-quality]").forEach(b => b.classList.toggle("active", b.dataset.quality === quality));
  }
  document.querySelectorAll("[data-quality]").forEach(b => b.addEventListener("click", () => {
    quality = b.dataset.quality;
    try { localStorage.setItem("quality", quality); } catch (e) {}
    showQuality();
    if (ws && ws.readyState === 1) ws.send(JSON.stringify({ quality }));
  }));
  showQuality();

  function liveURL() {
    return location.protocol === "https:" ? `wss://${location.host}/live` : `ws://${location.hostname}:8788/live`;
  }
  function showFrame(blob) {
    const url = URL.createObjectURL(blob);
    const img = $("screenImg");
    img.onload = () => { if (frameUrl && frameUrl !== url) URL.revokeObjectURL(frameUrl); frameUrl = url; };
    img.src = url;
    $("screenControls").hidden = false;
    setScreenMsg("");
  }
  function watching() { return unlocked && currentTab === "screen" && !document.hidden; }
  function stopLive() { if (ws) { ws.onclose = null; ws.close(); ws = null; } }

  // Opens the socket; after 3 failed tries falls back to long-polling and retries the socket every 30 s.
  function startFrames() {
    if (!watching()) { stopLive(); return; }
    if (ws || framesRunning) return;
    if (wsFailures >= 3 && Date.now() < wsRetryAt) { startPolling(); return; }
    let opened = false;
    const socket = new WebSocket(liveURL());
    socket.binaryType = "blob";
    ws = socket;
    socket.onopen = () => {
      opened = true; wsFailures = 0;
      socket.send(JSON.stringify({ quality }));
      pumpInput();
    };
    socket.onmessage = e => {
      if (typeof e.data !== "string") return showFrame(e.data);
      try { const m = JSON.parse(e.data); if (m.error) setScreenMsg(m.error); } catch (err) {}
    };
    socket.onclose = () => {
      ws = null;
      if (!opened) { wsFailures++; if (wsFailures >= 3) wsRetryAt = Date.now() + 30000; }
      if (!watching()) return;
      // A refused connection can mean "logged out" or "lid-awake off": let checkAuth sort it out.
      if (!opened) checkAuth();
      const delay = opened ? 500 : Math.min(5000, 500 * 2 ** wsFailures);
      setTimeout(startFrames, delay);
    };
  }

  // Fallback: the Mac holds each request until the screen changes (up to 2 s).
  async function startPolling() {
    if (framesRunning) return;
    framesRunning = true;
    while (watching() && !ws) {
      if (Date.now() >= wsRetryAt) { framesRunning = false; startFrames(); return; }
      let wait = 0;
      try {
        const res = await fetch("screen.jpg?after=" + frameId, { cache: "no-store" });
        if (res.status === 401 || res.status === 423) { locked(); break; }
        if (res.status === 204) continue;
        if (res.ok) {
          frameId = Number(res.headers.get("X-Frame-Id")) || 0;
          showFrame(await res.blob());
        } else {
          setScreenMsg(await res.text());
          // Access turned off or permission missing: hide the old picture and controls.
          if (res.status === 403) $("screenControls").hidden = true;
          frameId = 0;
          wait = 2000;
        }
      } catch (e) {
        setScreenMsg("Can't reach your Mac.");
        frameId = 0;
        wait = 2000;
      }
      if (wait) await new Promise(r => setTimeout(r, wait));
    }
    framesRunning = false;
  }

  // ---------- Remote input ----------
  // Commands go to the Mac one at a time, in order (so a click lands after the
  // pointer has moved). Pointer moves and scrolls waiting in line are merged.
  const inputQueue = [];
  let inputBusy = false;
  function send(cmd) {
    // A latched ⌘/⌥ also applies to the next click (e.g. ⌘-click).
    if (cmd.type === "click" && latched.size) cmd = { ...cmd, mods: takeMods() };
    const last = inputQueue[inputQueue.length - 1];
    if (last && (cmd.type === "move" || cmd.type === "scroll") && last.type === cmd.type) {
      last.dx += cmd.dx;
      last.dy += cmd.dy;
      if (cmd.x != null) { last.x = cmd.x; last.y = cmd.y; }
    } else {
      inputQueue.push({ ...cmd });
    }
    pumpInput();
  }
  async function pumpInput() {
    if (inputBusy) return;
    inputBusy = true;
    while (inputQueue.length) {
      const cmd = inputQueue.shift();
      if (cmd.dx != null) { cmd.dx = Math.round(cmd.dx); cmd.dy = Math.round(cmd.dy); }
      if ((cmd.type === "move" || cmd.type === "scroll") && !cmd.dx && !cmd.dy) continue;
      if (ws && ws.readyState === 1) { ws.send(JSON.stringify(cmd)); continue; }
      try {
        const res = await fetch("input", {
          method: "POST",
          headers: { "Content-Type": "application/json", "X-LidAwake": "1" },
          body: JSON.stringify(cmd),
        });
        if (res.status === 401 || res.status === 423) { inputQueue.length = 0; locked(); break; }
        if (!res.ok && res.status !== 204) setScreenMsg(await res.text());
      } catch (e) { /* dropped; the next command will try again */ }
    }
    inputBusy = false;
  }

  const wrap = $("screenWrap");
  function norm(e) {
    const r = $("screenImg").getBoundingClientRect();
    return {
      x: Math.min(1, Math.max(0, (e.clientX - r.left) / r.width)),
      y: Math.min(1, Math.max(0, (e.clientY - r.top) / r.height)),
    };
  }
  // Phone pixels → Mac points, so a drag scrolls about the same distance on the Mac.
  function scale() {
    const r = $("screenImg").getBoundingClientRect();
    return data && data.screenWidth && r.width ? data.screenWidth / r.width : 2;
  }

  // ----- Zoom (phone only: the Mac's screen isn't changed) -----
  let zoom = 1, panX = 0, panY = 0;
  function applyZoom() {
    const w = wrap.clientWidth, h = wrap.clientHeight;
    zoom = Math.min(5, Math.max(1, zoom));
    // Keep the picture covering the frame.
    panX = Math.min(0, Math.max(w - w * zoom, panX));
    panY = Math.min(0, Math.max(h - h * zoom, panY));
    $("screenImg").style.transform = zoom === 1 ? "" : `translate(${panX}px, ${panY}px) scale(${zoom})`;
    $("zoomBar").hidden = zoom === 1;
    $("zoomLevel").textContent = zoom.toFixed(1) + "×";
  }
  $("zoomReset").addEventListener("click", () => { zoom = 1; panX = panY = 0; applyZoom(); });

  // ----- Touches on the screen picture -----
  const pointers = new Map();
  let start = null, last = null, moved = false, held = false, holdTimer = null, pinch = null;
  let lastTap = 0, lastTapPos = null;
  let scrollAcc = { dx: 0, dy: 0 }, scrollTimer = null, scrollPos = null;

  function flushScroll() {
    scrollTimer = null;
    const s = scale();
    const dx = scrollAcc.dx * s, dy = scrollAcc.dy * s;
    scrollAcc = { dx: 0, dy: 0 };
    if (dx || dy) send({ type: "scroll", dx, dy, ...scrollPos });
  }
  function pinchInfo() {
    const [a, b] = [...pointers.values()];
    const r = wrap.getBoundingClientRect();
    return { dist: Math.hypot(a.x - b.x, a.y - b.y), mx: (a.x + b.x) / 2 - r.left, my: (a.y + b.y) / 2 - r.top };
  }

  wrap.addEventListener("pointerdown", e => {
    e.preventDefault();
    wrap.setPointerCapture(e.pointerId);
    pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
    if (pointers.size === 2) {
      // Second finger: this is a pinch, not a click.
      clearTimeout(holdTimer);
      const p = pinchInfo();
      pinch = { ...p, zoom, panX, panY };
      return;
    }
    if (pointers.size > 2 || pinch) return;
    start = last = { x: e.clientX, y: e.clientY };
    moved = false; held = false;
    const p = norm(e);
    holdTimer = setTimeout(() => {
      held = true;
      send({ type: "click", button: "right", ...p });
      if (navigator.vibrate) navigator.vibrate(10);
    }, 550);
  });
  wrap.addEventListener("pointermove", e => {
    if (!pointers.has(e.pointerId)) return;
    e.preventDefault();
    pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });

    if (pinch && pointers.size >= 2) {
      // Zoom around the fingers and follow them as they move.
      const p = pinchInfo();
      const contentX = (pinch.mx - pinch.panX) / pinch.zoom, contentY = (pinch.my - pinch.panY) / pinch.zoom;
      zoom = Math.min(5, Math.max(1, pinch.zoom * p.dist / pinch.dist));
      panX = p.mx - contentX * zoom;
      panY = p.my - contentY * zoom;
      applyZoom();
      return;
    }
    if (!start || pinch) return;
    if (!moved && Math.hypot(e.clientX - start.x, e.clientY - start.y) > 8) {
      moved = true;
      clearTimeout(holdTimer);
    }
    if (moved && !held) {
      const dx = e.clientX - last.x, dy = e.clientY - last.y;
      if (zoom > 1) {
        // Zoomed in: one finger moves around the picture.
        panX += dx; panY += dy;
        applyZoom();
      } else {
        // Not zoomed: one finger scrolls on the Mac.
        scrollAcc.dx += dx;
        scrollAcc.dy += dy;
        scrollPos = norm(e);
        if (!scrollTimer) scrollTimer = setTimeout(flushScroll, 40);
      }
    }
    last = { x: e.clientX, y: e.clientY };
  });
  function endPointer(e) {
    pointers.delete(e.pointerId);
    if (pinch) {
      if (pointers.size === 0) { pinch = null; start = null; }
      return;
    }
    clearTimeout(holdTimer);
    if (!start) return;
    if (e.type === "pointerup" && !moved && !held) {
      const p = norm(e);
      const now = Date.now();
      const double = now - lastTap < 350 && lastTapPos && Math.hypot(p.x - lastTapPos.x, p.y - lastTapPos.y) < 0.03;
      send({ type: "click", button: "left", count: double ? 2 : 1, ...p });
      lastTap = double ? 0 : now;
      lastTapPos = p;
    }
    start = null;
  }
  wrap.addEventListener("pointerup", endPointer);
  wrap.addEventListener("pointercancel", endPointer);
  wrap.addEventListener("contextmenu", e => e.preventDefault());

  // ----- Trackpad -----
  // 1 finger moves the pointer (tap = click, tap then hold & slide = drag),
  // 2 fingers scroll (tap = right-click), 3-finger swipes trigger the macOS
  // Mission Control shortcuts (Control + arrow keys).
  const pad = $("trackpad");
  let padStart = null, padLast = null, padFingers = 0, padMoved = false;
  let threeStart = null, gestureDone = false, pendingDrag = false, dragging = false, lastPadTap = 0;

  function centroid(touches) {
    let x = 0, y = 0;
    for (const t of touches) { x += t.clientX; y += t.clientY; }
    return { x: x / touches.length, y: y / touches.length };
  }
  const shortcut = key => send({ type: "key", key, mods: ["ctrl"] });

  pad.addEventListener("touchstart", e => {
    e.preventDefault();
    const c = centroid(e.touches);
    if (!padStart) {
      padStart = { time: Date.now(), x: c.x, y: c.y };
      padFingers = 0; padMoved = false; gestureDone = false; threeStart = null;
      // A quick second tap that then slides becomes a drag.
      pendingDrag = e.touches.length === 1 && Date.now() - lastPadTap < 300;
    }
    padFingers = Math.max(padFingers, e.touches.length);
    if (e.touches.length >= 3 && !threeStart) threeStart = c;
    padLast = c;  // re-anchor when the finger count changes, so nothing jumps
    pad.classList.add("active");
  }, { passive: false });

  pad.addEventListener("touchmove", e => {
    e.preventDefault();
    if (!padStart) return;
    const c = centroid(e.touches);
    const dx = c.x - padLast.x, dy = c.y - padLast.y;
    padLast = c;
    if (Math.hypot(c.x - padStart.x, c.y - padStart.y) > 6) padMoved = true;
    const n = e.touches.length;

    if (n === 1 && padFingers === 1) {
      if (pendingDrag && padMoved && !dragging) { dragging = true; send({ type: "mousedown" }); }
      // Faster finger = bigger jump, like a real trackpad.
      const gain = 1.6 + Math.min(Math.hypot(dx, dy) * 0.25, 3);
      send({ type: "move", dx: dx * gain, dy: dy * gain });
    } else if (n === 2 && padFingers === 2) {
      send({ type: "scroll", dx: dx * 2, dy: dy * 2 });
    } else if (n >= 3 && threeStart && !gestureDone) {
      const tx = c.x - threeStart.x, ty = c.y - threeStart.y;
      if (Math.max(Math.abs(tx), Math.abs(ty)) > 45) {
        gestureDone = true;
        if (Math.abs(tx) > Math.abs(ty)) shortcut(tx < 0 ? "right" : "left");  // swipe left = next desktop
        else shortcut(ty < 0 ? "up" : "down");                               // up = Mission Control
        if (navigator.vibrate) navigator.vibrate(10);
      }
    }
  }, { passive: false });

  function padEnd(e) {
    e.preventDefault();
    if (!padStart) return;
    if (e.touches.length > 0) { padLast = centroid(e.touches); return; }
    pad.classList.remove("active");
    const quick = Date.now() - padStart.time < 300;
    if (dragging) {
      send({ type: "mouseup" });
    } else if (!padMoved && quick && padFingers === 1) {
      send({ type: "click", button: "left", count: pendingDrag ? 2 : 1 });
      lastPadTap = pendingDrag ? 0 : Date.now();
    } else if (!padMoved && quick && padFingers === 2) {
      send({ type: "click", button: "right" });
    }
    dragging = false; pendingDrag = false; padStart = null;
  }
  pad.addEventListener("touchend", padEnd, { passive: false });
  pad.addEventListener("touchcancel", padEnd, { passive: false });

  document.querySelectorAll("[data-gesture]").forEach(b => b.addEventListener("click", () => {
    shortcut({ spaceLeft: "left", spaceRight: "right", mission: "up", appWindows: "down" }[b.dataset.gesture]);
  }));

  $("typeForm").addEventListener("submit", e => {
    e.preventDefault();
    const text = $("typeInput").value;
    if (text && latched.size) {
      // With ⌘/⌥ on, send real key presses so shortcuts like ⌘C work.
      const mods = takeMods();
      for (const char of text) send({ type: "key", char, mods });
    } else if (text) {
      send({ type: "text", text });
    }
    $("typeInput").value = "";
  });
  document.querySelectorAll("[data-key]").forEach(b =>
    b.addEventListener("click", () => send({ type: "key", key: b.dataset.key, mods: takeMods() })));

  // ⌘ and ⌥ are "sticky": tap to turn on, and the next key, typed text or click
  // is sent with it held. Tap again to cancel.
  const latched = new Set();
  function updateModButtons() {
    document.querySelectorAll("[data-mod]").forEach(b => b.classList.toggle("on", latched.has(b.dataset.mod)));
  }
  function takeMods() {
    const mods = [...latched];
    latched.clear();
    updateModButtons();
    return mods;
  }
  document.querySelectorAll("[data-mod]").forEach(b => b.addEventListener("click", () => {
    const mod = b.dataset.mod;
    latched.has(mod) ? latched.delete(mod) : latched.add(mod);
    updateModButtons();
  }));

  // ---------- Type directly ----------
  // An invisible field holds the iPhone keyboard; every keystroke goes straight to the
  // Mac and the field never keeps the text, so iOS has nothing to autocorrect.
  const liveInput = $("liveInput");
  const SENTINEL = "  ";  // lets Backspace fire even when "empty"
  function resetLiveInput() {
    liveInput.value = SENTINEL;
    try { liveInput.setSelectionRange(SENTINEL.length, SENTINEL.length); } catch (e) {}
  }
  function sendTyped(text) {
    if (!text) return;
    if (latched.size) {
      const mods = takeMods();
      for (const char of text) send({ type: "key", char, mods });
    } else {
      send({ type: "text", text });
    }
  }
  $("kbBtn").addEventListener("click", () => {
    if (document.activeElement === liveInput) { liveInput.blur(); return; }
    resetLiveInput();
    liveInput.focus();  // must happen inside the tap for iOS to show the keyboard
  });
  liveInput.addEventListener("focus", () => {
    $("kbBtn").classList.add("on");
    $("kbBtn").textContent = "⌨ Typing on your Mac… tap to stop";
  });
  liveInput.addEventListener("blur", () => {
    $("kbBtn").classList.remove("on");
    $("kbBtn").textContent = "⌨ Type directly";
  });
  liveInput.addEventListener("keydown", e => {
    if (e.isComposing) return;
    if (e.key === "Enter") { e.preventDefault(); send({ type: "key", key: "return", mods: takeMods() }); }
    else if (e.key === "Backspace") { e.preventDefault(); send({ type: "key", key: "delete", mods: takeMods() }); }
  });
  liveInput.addEventListener("beforeinput", e => {
    if (e.isComposing || e.inputType === "insertCompositionText") return;  // finished in compositionend
    e.preventDefault();
    if (e.inputType === "insertText") sendTyped(e.data);
    else if (e.inputType === "deleteContentBackward") send({ type: "key", key: "delete", mods: takeMods() });
    else if (e.inputType === "insertLineBreak" || e.inputType === "insertParagraph") send({ type: "key", key: "return", mods: takeMods() });
  });
  liveInput.addEventListener("compositionend", e => { sendTyped(e.data); resetLiveInput(); });
  liveInput.addEventListener("input", resetLiveInput);

  // ---------- Hide / show the key buttons ----------
  function setKeysHidden(hidden) {
    $("keyPanel").hidden = hidden;
    $("keysToggle").textContent = hidden ? "Show buttons ▾" : "Hide buttons ▴";
    $("keysToggle").setAttribute("aria-expanded", !hidden);
    try { localStorage.setItem("keysHidden", hidden ? "1" : ""); } catch (e) {}
  }
  $("keysToggle").addEventListener("click", () => setKeysHidden(!$("keyPanel").hidden));
  try { if (localStorage.getItem("keysHidden") === "1") setKeysHidden(true); } catch (e) {}

  // ---------- Lid-awake switch ----------
  // Only "off" is possible from here: while lid-awake is off this page is closed.
  $("awakeSwitch").addEventListener("click", async () => {
    if (!data || !data.lidAwake) return;
    const ok = confirm("Turn off Lid Awake?\n\nYour Mac will sleep normally, and this page will close until you turn Lid Awake on again on the Mac.");
    if (!ok) return;
    $("awakeSwitch").disabled = true;
    try {
      await fetch("awake", {
        method: "POST",
        headers: { "Content-Type": "application/json", "X-LidAwake": "1" },
        body: JSON.stringify({ on: false }),
      });
    } catch (e) {}
    setTimeout(checkAuth, 1500);
  });

  // ---------- Start ----------
  setInterval(poll, 3000);
  setInterval(() => { if (unlocked) render(); }, 1000);
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) { stopLive(); return; }
    checkAuth();
  });
  try { if (localStorage.getItem("tab") === "screen") currentTab = "screen"; } catch (e) {}
  showTab(currentTab);
  checkAuth();
</script>
</body>
</html>
"""#
