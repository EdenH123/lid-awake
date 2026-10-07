let dashboardHTML = #"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="apple-mobile-web-app-capable" content="yes">
<meta name="apple-mobile-web-app-status-bar-style" content="black-translucent">
<meta name="apple-mobile-web-app-title" content="Lid Awake">
<meta name="theme-color" content="#f2f2f7" media="(prefers-color-scheme: light)">
<meta name="theme-color" content="#000000" media="(prefers-color-scheme: dark)">
<title>Lid Awake</title>
<style>
  /* Apple system colors (iOS light / dark). */
  :root {
    --bg: #f2f2f7; --card: #ffffff; --card2: #f2f2f7; --text: #000000;
    --muted: rgba(60,60,67,.6); --line: rgba(60,60,67,.18); --fill: rgba(118,118,128,.12);
    --green: #34c759; --green-text: #248a3d; --green-soft: #e8f8ec; --amber: #ff9500; --red: #ff3b30; --blue: #007aff;
    --tabbar: rgba(249,249,249,.88); --seg-on: #ffffff;
    --kb: #d1d3d9; --key: #ffffff; --key-fn: #abb0bc; --key-shadow: rgba(0,0,0,.35);
    --pad: #e5e5ea; --pad-on: #dcdce1;
    --mac-base: linear-gradient(#e6e6ea, #b8b8be); --mac-groove: #9a9aa1;
  }
  @media (prefers-color-scheme: dark) {
    :root {
      --bg: #000000; --card: #1c1c1e; --card2: #2c2c2e; --text: #ffffff;
      --muted: rgba(235,235,245,.6); --line: rgba(84,84,88,.6); --fill: rgba(118,118,128,.24);
      --green: #30d158; --green-text: #30d158; --green-soft: #0e2a17; --amber: #ff9f0a; --red: #ff453a; --blue: #0a84ff;
      --tabbar: rgba(22,22,24,.86); --seg-on: #636366;
      --kb: #2b2b2d; --key: #6b6b6e; --key-fn: #464649; --key-shadow: rgba(0,0,0,.6);
      --pad: #2c2c2e; --pad-on: #3a3a3c;
      --mac-base: linear-gradient(#8e8e93, #5a5a5f); --mac-groove: #3a3a3c;
    }
  }
  * { box-sizing: border-box; -webkit-tap-highlight-color: transparent; }
  body {
    margin: 0; background: var(--bg); color: var(--text);
    font: 17px/1.3 -apple-system, BlinkMacSystemFont, "SF Pro Text", system-ui, sans-serif;
    -webkit-font-smoothing: antialiased; letter-spacing: -0.01em;
    padding: max(12px, env(safe-area-inset-top)) 16px calc(80px + env(safe-area-inset-bottom));
  }
  button { font: inherit; color: inherit; -webkit-user-select: none; user-select: none; }
  main { max-width: 480px; margin: 0 auto; display: grid; gap: 14px; }
  main.wide { max-width: 1100px; }
  .tab { display: grid; gap: 14px; }
  .tab[hidden], [hidden] { display: none !important; }

  /* Top: status pill row, then an iOS large title. */
  .topbar { display: flex; align-items: center; justify-content: space-between; min-height: 30px; }
  .live { font-size: 13px; color: var(--muted); display: flex; align-items: center; gap: 6px; }
  .dot { width: 8px; height: 8px; border-radius: 50%; background: var(--green); }
  .live.stale .dot { background: var(--red); }
  .lock-btn {
    font-size: 15px; font-weight: 600; color: var(--blue); background: var(--fill);
    border: 0; border-radius: 99px; padding: 5px 14px; cursor: pointer;
  }
  .titles h1 { font-size: 34px; font-weight: 700; letter-spacing: -0.03em; margin: 2px 0 0; line-height: 1.1; }
  .titles p { margin: 2px 0 0; font-size: 15px; color: var(--muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }

  .card { background: var(--card); border-radius: 22px; padding: 16px; }
  .label { font-size: 13px; color: var(--muted); }
  .warn { color: var(--amber) !important; }
  .bad { color: var(--red) !important; }

  /* Status: hero card with a little MacBook that lights up. */
  .hero { display: grid; grid-template-columns: auto 1fr auto; align-items: center; gap: 14px; transition: background .3s ease; }
  .hero.on { background: var(--green-soft); }
  .hero .mac-icon { width: 64px; height: 44px; }
  .hero .mac-icon .glass { fill: var(--fill); transition: fill .3s ease; }
  .hero.on .mac-icon .glass { fill: var(--green); }
  .hero .state { font-size: 30px; font-weight: 700; letter-spacing: -0.02em; line-height: 1.1; }
  .hero.on .state, .hero.on .label { color: var(--green-text); }
  .hero .sub { font-size: 14px; color: var(--muted); margin-top: 2px; font-variant-numeric: tabular-nums; }
  .hero.on .sub { color: var(--green-text); }
  .awake-switch {
    position: relative; width: 51px; height: 31px; border-radius: 99px; border: 0; padding: 0; flex: none;
    background: var(--fill); cursor: pointer; transition: background .25s ease;
  }
  .awake-switch::after {
    content: ""; position: absolute; top: 2px; left: 2px; width: 27px; height: 27px; border-radius: 50%;
    background: #fff; box-shadow: 0 3px 8px rgba(0,0,0,.15), 0 1px 1px rgba(0,0,0,.16); transition: transform .25s ease;
  }
  .awake-switch.on { background: var(--green); }
  .awake-switch.on::after { transform: translateX(20px); }
  .awake-switch:disabled { opacity: .5; }

  /* Status: battery ring and temperature gauge widgets. */
  .widgets { display: grid; grid-template-columns: 1fr 1fr; gap: 14px; }
  .widget { display: grid; gap: 6px; align-content: start; min-height: 168px; }
  .ring { position: relative; width: 96px; height: 96px; justify-self: center; margin: 2px 0; }
  .ring svg { width: 96px; height: 96px; transform: rotate(-90deg); }
  .ring .track { stroke: var(--fill); }
  .ring .arc { stroke: var(--green); transition: stroke-dashoffset .5s ease, stroke .3s ease; }
  .ring .pct { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; gap: 1px;
    font-size: 22px; font-weight: 700; font-variant-numeric: tabular-nums; }
  .ring .bolt { width: 12px; height: 16px; fill: var(--green); }
  .widget .cap { font-size: 13px; color: var(--muted); text-align: center; line-height: 1.25; }
  .temp-value { font-size: 34px; font-weight: 700; letter-spacing: -0.02em; font-variant-numeric: tabular-nums; margin: 6px 0 2px; }
  .gauge { height: 8px; border-radius: 99px; background: var(--fill); overflow: hidden; }
  .gauge div { height: 100%; border-radius: 99px; background: var(--green); transition: width .5s ease, background .3s ease; }

  /* Status: iOS Settings-style grouped list. */
  .group-title { font-size: 13px; color: var(--muted); text-transform: uppercase; letter-spacing: .02em; margin: 6px 16px -6px; }
  .list { background: var(--card); border-radius: 12px; padding-left: 16px; }
  .list .row { display: flex; justify-content: space-between; align-items: center; gap: 12px; padding: 12px 16px 12px 0; font-size: 17px; }
  .list .row + .row { border-top: 0.5px solid var(--line); }
  .list .row span:last-child { color: var(--muted); text-align: right; }

  /* Screen: the picture sits inside a MacBook Pro. */
  .notice { background: var(--card); border-radius: 14px; padding: 14px 16px; font-size: 15px; }
  .macbook { display: grid; justify-items: center; }
  .macbook .lid {
    position: relative; width: 100%; background: #0d0d0f; border-radius: 16px 16px 6px 6px;
    padding: 9px 9px 12px; box-shadow: inset 0 0 0 1.5px #3a3a3e;
  }
  .macbook .lid::before {  /* the notch */
    content: ""; position: absolute; left: 50%; top: 9px; transform: translateX(-50%); z-index: 2;
    width: 13%; max-width: 90px; height: 9px; background: #0d0d0f; border-radius: 0 0 6px 6px; pointer-events: none;
  }
  .macbook .base {
    position: relative; width: 108%; height: 12px; margin-top: -1px; background: var(--mac-base);
    border-radius: 2px 2px 14px 14px / 2px 2px 10px 10px;
  }
  .macbook .base::after {  /* the thumb groove */
    content: ""; position: absolute; left: 50%; top: 0; transform: translateX(-50%);
    width: 16%; height: 5px; background: var(--mac-groove); border-radius: 0 0 6px 6px;
  }
  .screen-wrap {
    position: relative; background: #000; border-radius: 4px; overflow: hidden; min-height: 120px;
    touch-action: none; -webkit-user-select: none; user-select: none; -webkit-touch-callout: none;
  }
  .screen-wrap img { display: block; width: 100%; height: auto; -webkit-user-drag: none; pointer-events: none; transform-origin: 0 0; }

  /* Screen: iOS segmented control for picture quality. */
  .quality { display: grid; grid-template-columns: repeat(3, 1fr); background: var(--fill); border-radius: 9px; padding: 2px; justify-self: center; width: min(100%, 320px); }
  .quality button { font-size: 13px; font-weight: 500; background: none; border: 0; padding: 6px 0; border-radius: 7px; cursor: pointer; }
  .quality button.active { background: var(--seg-on); font-weight: 600; box-shadow: 0 3px 8px rgba(0,0,0,.12), 0 3px 1px rgba(0,0,0,.04); }
  .zoombar { display: flex; align-items: center; justify-content: center; gap: 10px; font-size: 13px; color: var(--muted); }
  .zoombar button { font-size: 13px; font-weight: 600; color: var(--blue); background: var(--fill); border: 0; border-radius: 99px; padding: 4px 12px; cursor: pointer; }
  .hint { font-size: 13px; color: var(--muted); text-align: center; line-height: 1.4; }

  .panel-row { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
  .kb-btn, .panel-toggle {
    font-size: 15px; font-weight: 600; color: var(--blue); background: var(--fill);
    border: 0; border-radius: 99px; padding: 8px 14px; cursor: pointer;
  }
  .kb-btn.on { background: var(--green); color: #fff; }
  .panel-toggle { font-size: 13px; color: var(--muted); }
  /* Invisible field that receives the iPhone keyboard; 16px stops iOS zooming in on focus. */
  .live-input {
    position: fixed; left: 0; bottom: 0; width: 1px; height: 1px; opacity: 0;
    font-size: 16px; border: 0; padding: 0; background: transparent; color: transparent;
  }

  /* Screen: keys styled like the iPhone keyboard, on a keyboard tray. */
  .keyboard { background: var(--kb); border-radius: 16px; padding: 8px 6px; display: grid; gap: 8px; }
  .typebar { display: flex; gap: 6px; }
  .typebar input {
    flex: 1; min-width: 0; font: inherit; font-size: 16px; color: var(--text); background: var(--card);
    border: 0; border-radius: 10px; padding: 9px 12px;
  }
  .keys { display: grid; grid-template-columns: repeat(6, 1fr); gap: 6px; }
  .keys .space { grid-column: span 2; }
  .key, .keys button, .gestures button, .typebar button {
    font-size: 16px; font-weight: 500; color: var(--text); background: var(--key); border: 0;
    border-radius: 6px; padding: 10px 0; cursor: pointer; box-shadow: 0 1px 0 var(--key-shadow);
  }
  .typebar button { padding: 9px 14px; font-weight: 600; }
  .keys button[data-mod], .keys button[data-key="escape"], .keys button[data-key="tab"],
  .keys button[data-key="delete"], .keys button[data-key="return"], .gestures button { background: var(--key-fn); }
  .keys button:active, .gestures button:active, .typebar button:active { background: var(--card2); }
  .keys button.on { background: var(--green); color: #fff; }
  .gestures { display: grid; grid-template-columns: 1fr 1.3fr 1.1fr 1fr; gap: 6px; }
  .gestures button { font-size: 13px; padding: 9px 2px; }

  /* Screen: Magic Trackpad. */
  .trackpad {
    aspect-ratio: 16 / 10.5; max-height: 280px; width: 100%; justify-self: center; border-radius: 18px;
    background: var(--pad); box-shadow: inset 0 0 0 0.5px var(--line), inset 0 1px 2px rgba(0,0,0,.06);
    display: flex; align-items: flex-end; justify-content: center; padding-bottom: 12px;
    color: var(--muted); font-size: 12px; letter-spacing: .04em;
    touch-action: none; -webkit-user-select: none; user-select: none; -webkit-touch-callout: none;
    transition: background .15s ease;
  }
  .trackpad.active { background: var(--pad-on); }

  /* Bottom tab bar, like iPhone apps. */
  .tabbar {
    position: fixed; left: 0; right: 0; bottom: 0; z-index: 10;
    display: grid; grid-template-columns: 1fr 1fr; padding: 7px 0 max(8px, env(safe-area-inset-bottom));
    background: var(--tabbar); -webkit-backdrop-filter: blur(20px) saturate(180%); backdrop-filter: blur(20px) saturate(180%);
    border-top: 0.5px solid var(--line);
  }
  .tabbar button { background: none; border: 0; display: grid; justify-items: center; gap: 2px; font-size: 10px; font-weight: 500; color: var(--muted); cursor: pointer; padding: 0; }
  .tabbar svg { width: 28px; height: 26px; fill: none; stroke: currentColor; stroke-width: 1.8; stroke-linecap: round; stroke-linejoin: round; }
  .tabbar button.active { color: var(--green); }

  /* Screen: Lock Mac / Screen off, and the overlay shown while the screen is off. */
  .power-row { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; }
  .power-row button {
    display: flex; align-items: center; justify-content: center; gap: 7px; font-size: 15px; font-weight: 600;
    color: var(--text); background: var(--card); border: 0; border-radius: 12px; padding: 12px; cursor: pointer;
  }
  .power-row button:active { background: var(--card2); }
  .power-row svg { width: 18px; height: 18px; fill: none; stroke: currentColor; stroke-width: 1.8; stroke-linecap: round; stroke-linejoin: round; }
  .screen-off {
    position: absolute; inset: 9px 9px 12px; z-index: 3; display: grid; place-content: center; gap: 12px;
    text-align: center; background: rgba(0,0,0,.85); color: #fff; border-radius: 4px; font-size: 15px;
  }
  .screen-off button { justify-self: center; font-weight: 600; color: #000; background: #fff; border: 0; border-radius: 99px; padding: 8px 18px; cursor: pointer; }
  body.landscape-full .screen-off { inset: 0; border-radius: 0; }

  /* Full-screen landscape: the Mac screen fills the phone, with a small floating toolbar. */
  .float-bar { display: none; }
  body.landscape-full { padding: 0; overflow: hidden; background: #000; }
  body.landscape-full main { max-width: none; }
  body.landscape-full .topbar, body.landscape-full .titles, body.landscape-full .tabbar,
  body.landscape-full #screenControls > :not(.macbook):not(#zoomBar) { display: none !important; }
  body.landscape-full .macbook .lid {
    position: fixed; inset: 0; z-index: 5; border-radius: 0; background: #000; box-shadow: none;
    padding: env(safe-area-inset-top) env(safe-area-inset-right) env(safe-area-inset-bottom) env(safe-area-inset-left);
  }
  body.landscape-full .macbook .lid::before, body.landscape-full .macbook .base { display: none; }
  body.landscape-full .screen-wrap { width: 100%; height: 100%; border-radius: 0; }
  body.landscape-full .screen-wrap img { width: 100%; height: 100%; object-fit: contain; }
  body.landscape-full .float-bar {
    display: flex; position: fixed; z-index: 20; gap: 2px; padding: 4px; border-radius: 14px;
    top: max(8px, env(safe-area-inset-top)); right: max(8px, env(safe-area-inset-right));
    background: rgba(30,30,32,.72); -webkit-backdrop-filter: blur(20px); backdrop-filter: blur(20px);
  }
  .float-bar button {
    min-width: 38px; height: 34px; border: 0; border-radius: 10px; background: transparent;
    color: #fff; font-size: 15px; font-weight: 600; cursor: pointer; display: grid; place-items: center;
  }
  .float-bar button.on { background: var(--green); }
  .float-bar svg { width: 20px; height: 16px; fill: none; stroke: currentColor; stroke-width: 1.6; }
  body.landscape-full #zoomBar {
    position: fixed; z-index: 20; left: 50%; bottom: max(10px, env(safe-area-inset-bottom)); transform: translateX(-50%);
    background: rgba(30,30,32,.72); color: #fff; padding: 4px 6px 4px 12px; border-radius: 99px;
    -webkit-backdrop-filter: blur(20px); backdrop-filter: blur(20px);
  }
  body.landscape-full #screenMsg { position: fixed; z-index: 15; left: 50%; top: 50%; transform: translate(-50%, -50%); max-width: 80vw; }

  /* Locked / off screen. */
  .gate { text-align: center; padding: 30px 22px; display: grid; gap: 12px; margin-top: 24px; }
  .gate .icon { width: 56px; height: 56px; justify-self: center; border-radius: 16px; background: var(--fill); display: grid; place-items: center; }
  .gate .icon svg { width: 28px; height: 28px; fill: none; stroke: var(--muted); stroke-width: 1.8; stroke-linecap: round; stroke-linejoin: round; }
  .gate h2 { margin: 0; font-size: 22px; font-weight: 700; }
  .gate p { margin: 0; color: var(--muted); font-size: 15px; }
  .gate form { display: grid; gap: 10px; margin-top: 6px; }
  .gate input {
    font: inherit; color: var(--text); background: var(--fill); text-align: center;
    border: 0; border-radius: 12px; padding: 13px;
  }
  .gate button { font-weight: 600; color: #fff; background: var(--green); border: 0; border-radius: 12px; padding: 13px; cursor: pointer; }
  .gate button:disabled { opacity: .6; }
  .gate .error { color: var(--red); font-size: 14px; min-height: 1.2em; }
</style>
</head>
<body>
<main id="main">
  <div class="topbar">
    <div class="live" id="live"><span class="dot"></span><span id="liveText">Connecting…</span></div>
    <button class="lock-btn" id="lockBtn" hidden>Lock</button>
  </div>
  <div class="titles">
    <h1 id="pageTitle">Lid Awake</h1>
    <p id="host"></p>
  </div>

  <section class="card gate" id="gate" hidden>
    <div class="icon"><svg viewBox="0 0 24 24" aria-hidden="true"><rect x="5" y="11" width="14" height="10" rx="2.5"/><path d="M8 11V8a4 4 0 0 1 8 0v3"/></svg></div>
    <h2 id="gateTitle">Locked</h2>
    <p id="gateText"></p>
    <form id="loginForm">
      <input type="password" id="password" placeholder="Password" autocomplete="current-password" required>
      <button id="unlockBtn">Unlock</button>
      <div class="error" id="loginError"></div>
    </form>
  </section>

  <div class="tab" id="app" hidden>
  <nav class="tabbar tabs" aria-label="Pages">
    <button data-tab="status" class="active">
      <svg viewBox="0 0 28 26" aria-hidden="true"><path d="M4 18a10 10 0 1 1 20 0"/><path d="M14 18l5-6"/><circle cx="14" cy="18" r="1.6"/></svg>Status</button>
    <button data-tab="screen">
      <svg viewBox="0 0 28 26" aria-hidden="true"><rect x="5" y="5" width="18" height="12" rx="1.8"/><path d="M2.5 20.5h23"/></svg>Screen</button>
  </nav>

  <div class="tab" id="tab-status">
    <section class="card hero" id="statusCard">
      <svg class="mac-icon" viewBox="0 0 64 44" aria-hidden="true">
        <rect x="8" y="2" width="48" height="32" rx="3.5" fill="#1d1d1f"/>
        <rect class="glass" x="11" y="5" width="42" height="26" rx="1.5"/>
        <rect x="28" y="2" width="8" height="2.2" rx="1.1" fill="#1d1d1f"/>
        <path d="M2 36h60l-2.5 4.5a3 3 0 0 1-2.6 1.5H7.1a3 3 0 0 1-2.6-1.5z" fill="#a1a1a6"/>
        <rect x="27" y="36" width="10" height="1.8" rx=".9" fill="#7c7c80"/>
      </svg>
      <div>
        <div class="label" id="stateLabel">Lid-closed awake</div>
        <div class="state" id="state">—</div>
        <div class="sub" id="stateSub"></div>
      </div>
      <button class="awake-switch" id="awakeSwitch" role="switch" aria-label="Stay awake with lid closed"></button>
    </section>

    <div class="widgets">
      <section class="card widget">
        <div class="label">Battery</div>
        <div class="ring">
          <svg viewBox="0 0 96 96" aria-hidden="true">
            <circle class="track" cx="48" cy="48" r="40" fill="none" stroke-width="10"/>
            <circle class="arc" id="batteryArc" cx="48" cy="48" r="40" fill="none" stroke-width="10" stroke-linecap="round"
                    stroke-dasharray="251.3" stroke-dashoffset="251.3"/>
          </svg>
          <div class="pct"><svg class="bolt" id="bolt" viewBox="0 0 12 16" hidden aria-hidden="true"><path d="M7 0 0 9h5l-1 7 7-9H6z"/></svg><span id="battery">—</span></div>
        </div>
        <div class="cap" id="batterySub"></div>
      </section>
      <section class="card widget">
        <div class="label">Battery temperature</div>
        <div class="temp-value" id="temp">—</div>
        <div class="gauge"><div id="tempFill" style="width:0"></div></div>
        <div class="cap" id="tempSub" style="text-align:left"></div>
      </section>
    </div>

    <div class="group-title">Mac</div>
    <div class="list">
      <div class="row"><span>Turn off after</span><span id="timerRow">—</span></div>
      <div class="row"><span>Low-battery limit</span><span id="batteryGuardRow">—</span></div>
      <div class="row"><span>Heat limit</span><span id="heatGuardRow">—</span></div>
      <div class="row"><span>Mac heat</span><span id="thermal">—</span></div>
    </div>
  </div>

  <div class="tab" id="tab-screen" hidden>
    <div class="float-bar" id="floatBar" aria-label="Screen controls">
      <button type="button" id="kbFloat" aria-label="Keyboard">⌨</button>
      <button type="button" id="padModeBtn" aria-label="Trackpad mode">
        <svg viewBox="0 0 20 16" aria-hidden="true"><rect x="1.5" y="1.5" width="17" height="13" rx="3"/></svg></button>
      <button data-mod="cmd" aria-label="Command">⌘</button>
      <button data-mod="alt" aria-label="Option">⌥</button>
      <button data-key="escape">esc</button>
    </div>
    <div class="notice" id="screenMsg" hidden></div>
    <div class="tab" id="screenControls" hidden>
    <div class="macbook">
      <div class="lid">
        <div class="screen-wrap" id="screenWrap"><img id="screenImg" alt="Mac screen"></div>
        <div class="screen-off" id="screenOffOverlay" hidden><span>Your Mac's screen is off</span><button type="button" id="wakeBtn">Wake screen</button></div>
      </div>
      <div class="base"></div>
    </div>
    <nav class="quality" id="quality" aria-label="Picture quality">
      <button data-quality="sharp">Sharp</button>
      <button data-quality="balanced">Balanced</button>
      <button data-quality="fast">Fast</button>
    </nav>
    <div class="zoombar" id="zoomBar" hidden>Zoomed <span id="zoomLevel">1.0×</span> on this phone only <button id="zoomReset">Reset zoom</button></div>
    <div class="hint" id="hint">Tap = click · Double-tap = double-click · Hold = right-click · Drag = scroll · Pinch = zoom</div>
    <div class="panel-row">
      <button type="button" class="kb-btn" id="kbBtn">⌨ Type directly</button>
      <button class="panel-toggle" id="keysToggle" aria-expanded="true">Hide buttons ▴</button>
    </div>
    <div class="keyboard" id="keyPanel">
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
    <div class="power-row">
      <button type="button" id="lockMacBtn"><svg viewBox="0 0 24 24" aria-hidden="true"><rect x="5" y="11" width="14" height="10" rx="2.5"/><path d="M8 11V8a4 4 0 0 1 8 0v3"/></svg>Lock Mac</button>
      <button type="button" id="screenOffBtn"><svg viewBox="0 0 24 24" aria-hidden="true"><rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4M9 8l6 6"/></svg>Screen off</button>
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
    updateLandscape();
    showScreenOff();
    const ago = Math.round((Date.now() - lastOk) / 1000);
    const stale = !lastOk || ago > 10;
    $("live").classList.toggle("stale", stale);
    $("liveText").textContent = !lastOk ? "Can't reach Mac" : stale ? `Last seen ${ago}s ago` : "Live";
    $("pageTitle").textContent = currentTab === "screen" ? "Screen" : "Lid Awake";
    if (!data) return;

    $("host").textContent = data.host || "";
    const on = data.lidAwake;
    $("statusCard").classList.toggle("on", on);
    $("awakeSwitch").classList.toggle("on", on);
    $("awakeSwitch").setAttribute("aria-checked", on);
    $("awakeSwitch").disabled = !on;
    $("stateLabel").textContent = on ? "Stays awake with lid closed" : "Sleeps when the lid closes";
    $("state").textContent = on ? "On" : "Off";
    const left = data.timerRemaining != null ? data.timerRemaining - (Date.now() - fetchedAt) / 1000 : null;
    $("stateSub").textContent = !on ? "" : left != null && left > 0 ? `Turns off in ${fmtCountdown(left)}` : "No timer";

    if (data.battery != null) {
      const pct = data.battery;
      const color = pct < data.batteryLimit + 5 ? "var(--red)" : pct < 25 ? "var(--amber)" : "var(--green)";
      $("battery").textContent = pct + "%";
      $("batteryArc").style.strokeDashoffset = (251.3 * (1 - pct / 100)).toFixed(1);
      $("batteryArc").style.stroke = color;
      $("bolt").toggleAttribute("hidden", !data.charging);
      // macOS needs a minute or two after plugging in / unplugging before it has an estimate.
      const clockIn = min => new Date(fetchedAt + min * 60000).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
      let sub;
      if (data.onBattery) {
        sub = fmtMinutes(data.minutesToEmpty)
          ? `${fmtMinutes(data.minutesToEmpty)} left · until ${clockIn(data.minutesToEmpty)}`
          : "On battery · estimating…";
      } else if (data.charging) {
        sub = fmtMinutes(data.minutesToFull) && data.minutesToFull > 0
          ? `Full in ${fmtMinutes(data.minutesToFull)} · at ${clockIn(data.minutesToFull)}`
          : "Charging · estimating…";
      } else {
        sub = data.charged || pct >= 99 ? "Plugged in · full"
          : pct >= 79 ? "Plugged in · holding charge"
          : "Plugged in · not charging";
      }
      $("batterySub").textContent = sub;
    }

    if (data.batteryTemp != null) {
      const t = data.batteryTemp, max = data.maxBatteryTemp;
      const level = t >= max ? "bad" : t >= max - 4 ? "warn" : "";
      $("temp").textContent = t.toFixed(1) + "°";
      $("temp").className = "temp-value " + level;
      $("tempFill").style.width = Math.min(100, Math.max(4, t / max * 100)) + "%";
      $("tempFill").style.background = level === "bad" ? "var(--red)" : level === "warn" ? "var(--amber)" : "var(--green)";
      $("tempSub").textContent = `Limit ${max}°C`;
    }

    const timerChoice = data.timerMinutes > 0
      ? (data.timerMinutes >= 60 ? `${data.timerMinutes / 60} hour${data.timerMinutes === 60 ? "" : "s"}` : `${data.timerMinutes} minutes`)
      : "Never";
    $("timerRow").textContent = timerChoice;
    $("batteryGuardRow").textContent = data.batteryGuard ? `Below ${data.batteryLimit}%` : "Off";
    $("heatGuardRow").textContent = data.heatGuard ? `Above ${data.maxBatteryTemp}°C` : "Off";
    const thermalText = { nominal: "Normal", fair: "Warm", serious: "Hot", critical: "Critical" }[data.thermalState] || "—";
    $("thermal").textContent = thermalText;
    $("thermal").className = data.thermalState === "serious" || data.thermalState === "critical" ? "bad" : data.thermalState === "fair" ? "warn" : "";

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
      updateLandscape();
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
    updateLandscape();
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
    // Any input wakes the Mac's screen again (the Mac does the same).
    if (!$("screenOffOverlay").hidden) { screenOffAt = 0; wakeAt = Date.now(); showScreenOff(); }
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
  // Where the picture is actually drawn inside the <img> box (letterboxed in landscape).
  function drawnRect() {
    const img = $("screenImg"), r = img.getBoundingClientRect();
    const nw = img.naturalWidth || r.width, nh = img.naturalHeight || r.height;
    const fit = Math.min(r.width / nw, r.height / nh);
    const w = nw * fit, h = nh * fit;
    return { left: r.left + (r.width - w) / 2, top: r.top + (r.height - h) / 2, width: w, height: h };
  }
  function norm(e) {
    const r = drawnRect();
    return {
      x: Math.min(1, Math.max(0, (e.clientX - r.left) / r.width)),
      y: Math.min(1, Math.max(0, (e.clientY - r.top) / r.height)),
    };
  }
  // Phone pixels → Mac points, so a drag scrolls about the same distance on the Mac.
  function scale() {
    const r = drawnRect();
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
    if (padMode()) return;
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
    // Taps on the black bars beside the picture (landscape) do nothing.
    const d = drawnRect();
    if (e.clientX < d.left || e.clientX > d.left + d.width || e.clientY < d.top || e.clientY > d.top + d.height) return;
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
    if (padMode() || !pointers.has(e.pointerId)) return;
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
    if (padMode()) return;
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

  function padTouchStart(e) {
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
    if (e.currentTarget === pad) pad.classList.add("active");
  }

  function padTouchMove(e) {
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
  }

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
  // The bottom trackpad always uses these; the screen picture does in landscape trackpad mode.
  for (const surface of [pad, wrap]) {
    const only = f => e => { if (surface === pad || padMode()) f(e); };
    surface.addEventListener("touchstart", only(padTouchStart), { passive: false });
    surface.addEventListener("touchmove", only(padTouchMove), { passive: false });
    surface.addEventListener("touchend", only(padEnd), { passive: false });
    surface.addEventListener("touchcancel", only(padEnd), { passive: false });
  }

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
  function toggleKeyboard() {
    if (document.activeElement === liveInput) { liveInput.blur(); return; }
    resetLiveInput();
    liveInput.focus();  // must happen inside the tap for iOS to show the keyboard
  }
  $("kbBtn").addEventListener("click", toggleKeyboard);
  $("kbFloat").addEventListener("click", toggleKeyboard);
  liveInput.addEventListener("focus", () => {
    $("kbFloat").classList.add("on");
    $("kbBtn").classList.add("on");
    $("kbBtn").textContent = "⌨ Typing on your Mac… tap to stop";
  });
  liveInput.addEventListener("blur", () => {
    $("kbFloat").classList.remove("on");
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

  // ---------- Lock / Screen off ----------
  let screenOffAt = 0, wakeAt = 0;
  function showScreenOff() {
    const recentlyOff = Date.now() - screenOffAt < 4000;
    const recentlyWoken = Date.now() - wakeAt < 4000;
    $("screenOffOverlay").hidden = !(recentlyOff || (data && data.screenOff && !recentlyWoken));
  }
  function power(action) {
    return fetch("power", {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-LidAwake": "1" },
      body: JSON.stringify({ action }),
    }).then(async res => {
      if (res.status === 401 || res.status === 423) return locked();
      if (!res.ok && res.status !== 204) setScreenMsg(await res.text());
    }).catch(() => setScreenMsg("Can't reach your Mac."));
  }
  $("lockMacBtn").addEventListener("click", () => {
    if (!confirm("Lock your Mac?\n\nTo unlock it from here, tap ⌨ Type directly, type your Mac password and press Return.")) return;
    power("lock");
  });
  $("screenOffBtn").addEventListener("click", () => {
    if (!confirm("Turn off your Mac's screen?\n\nYour Mac may lock too. Tap “Wake screen” to turn it back on.")) return;
    screenOffAt = Date.now(); wakeAt = 0;
    showScreenOff();
    power("displayoff");
  });
  $("wakeBtn").addEventListener("click", () => {
    screenOffAt = 0; wakeAt = Date.now();
    showScreenOff();
    power("wake");
  });

  // ---------- Full-screen landscape ----------
  // Sideways phone on the Screen tab: the Mac screen fills the phone. Phones only
  // (max-height), so a desktop browser window never switches.
  const landscapeQuery = matchMedia("(orientation: landscape) and (max-height: 500px)");
  let trackpadMode = false;
  function padMode() { return trackpadMode && document.body.classList.contains("landscape-full"); }
  function updateLandscape() {
    const full = landscapeQuery.matches && currentTab === "screen" && unlocked;
    if (full === document.body.classList.contains("landscape-full")) return;
    document.body.classList.toggle("landscape-full", full);
    // The picture changes size, so start the zoom fresh.
    zoom = 1; panX = panY = 0; applyZoom();
  }
  landscapeQuery.addEventListener("change", updateLandscape);
  $("padModeBtn").addEventListener("click", () => {
    trackpadMode = !trackpadMode;
    $("padModeBtn").classList.toggle("on", trackpadMode);
    if (trackpadMode) { zoom = 1; panX = panY = 0; applyZoom(); }
  });

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
