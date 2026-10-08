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
<link rel="apple-touch-icon" href="/apple-touch-icon.png">
<link rel="icon" type="image/png" href="/apple-touch-icon.png">
<link rel="manifest" href="/manifest.webmanifest">
<script>try { const t = localStorage.getItem("theme"); if (t === "light" || t === "dark") document.documentElement.dataset.theme = t; } catch (e) {}</script>
<style>
  /* iOS system colors and materials (light / dark), as in Settings, Battery and Weather. */
  :root {
    --bg: #f2f2f7; --card: #ffffff; --card2: #e5e5ea; --text: #000000;
    --muted: rgba(60,60,67,.6); --faint: rgba(60,60,67,.3); --line: rgba(60,60,67,.29); --fill: rgba(120,120,128,.16);
    --green: #34c759; --amber: #ff9500; --red: #ff3b30; --blue: #007aff; --gray: #8e8e93;
    --bar: rgba(255,255,255,.78); --bar-edge: rgba(0,0,0,.06); --bar-shadow: 0 6px 24px rgba(0,0,0,.10);
    --kb: #d1d3d9; --key: #ffffff; --key-fn: #abb0bc; --key-shadow: rgba(0,0,0,.35);
    --pad: #e5e5ea; --pad-on: #dcdce1;
    --mac-base: linear-gradient(#e6e6ea, #b8b8be); --mac-groove: #9a9aa1;
  }
  /* Dark: follows the iPhone (Automatic), or chosen in Settings → Appearance. */
  @media (prefers-color-scheme: dark) {
    :root:not([data-theme="light"]) {
      --bg: #000000; --card: #1c1c1e; --card2: #2c2c2e; --text: #ffffff;
      --muted: rgba(235,235,245,.6); --faint: rgba(235,235,245,.3); --line: rgba(84,84,88,.65); --fill: rgba(120,120,128,.32);
      --green: #30d158; --amber: #ff9f0a; --red: #ff453a; --blue: #0a84ff; --gray: #8e8e93;
      --bar: rgba(30,30,32,.72); --bar-edge: rgba(255,255,255,.08); --bar-shadow: 0 6px 24px rgba(0,0,0,.5);
      --kb: #2b2b2d; --key: #6b6b6e; --key-fn: #464649; --key-shadow: rgba(0,0,0,.6);
      --pad: #2c2c2e; --pad-on: #3a3a3c;
      --mac-base: linear-gradient(#8e8e93, #5a5a5f); --mac-groove: #3a3a3c;
    }
  }
  :root[data-theme="dark"] {
      --bg: #000000; --card: #1c1c1e; --card2: #2c2c2e; --text: #ffffff;
      --muted: rgba(235,235,245,.6); --faint: rgba(235,235,245,.3); --line: rgba(84,84,88,.65); --fill: rgba(120,120,128,.32);
      --green: #30d158; --amber: #ff9f0a; --red: #ff453a; --blue: #0a84ff; --gray: #8e8e93;
      --bar: rgba(30,30,32,.72); --bar-edge: rgba(255,255,255,.08); --bar-shadow: 0 6px 24px rgba(0,0,0,.5);
      --kb: #2b2b2d; --key: #6b6b6e; --key-fn: #464649; --key-shadow: rgba(0,0,0,.6);
      --pad: #2c2c2e; --pad-on: #3a3a3c;
      --mac-base: linear-gradient(#8e8e93, #5a5a5f); --mac-groove: #3a3a3c;
    }
  :root { color-scheme: light dark; --seg-on: #ffffff; }
  :root[data-theme="light"] { color-scheme: light; }
  :root[data-theme="dark"] { color-scheme: dark; --seg-on: #636366; }
  @media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) { --seg-on: #636366; } }
  * { box-sizing: border-box; -webkit-tap-highlight-color: transparent; }
  /* The page itself never scrolls (or zooms sideways): the content scrolls inside #scroller,
     and the floating tab bar sits outside it, so it stays at exactly the same spot on every
     page, whatever the page's length or a fast fling. */
  html, body { height: 100%; overflow: hidden; overscroll-behavior: none; }
  body {
    margin: 0; background: var(--bg); color: var(--text);
    font: 17px/1.29 -apple-system, BlinkMacSystemFont, "SF Pro Text", system-ui, sans-serif;
    -webkit-font-smoothing: antialiased; letter-spacing: -0.022em;
  }
  #scroller {
    position: fixed; inset: 0; overflow-x: hidden; overflow-y: auto; overscroll-behavior: none;
    -webkit-overflow-scrolling: touch;
    padding: max(10px, env(safe-area-inset-top)) 16px calc(96px + env(safe-area-inset-bottom));
  }
  body.apps-full #scroller, body.landscape-full #scroller { padding: 0; overflow: hidden; }
  button { font: inherit; color: inherit; letter-spacing: inherit; -webkit-user-select: none; user-select: none; }
  button:focus-visible, [tabindex]:focus-visible { outline: 2px solid var(--blue); outline-offset: 2px; }
  main { max-width: 480px; margin: 0 auto; display: grid; gap: 20px; }
  main.wide { max-width: 1100px; }
  .tab { display: grid; gap: 20px; }
  .tab[hidden], [hidden] { display: none !important; }
  .num { font-variant-numeric: tabular-nums; }

  /* Navigation bar: connection status on the left, a round Lock button on the right, then the large title. */
  .topbar { display: flex; align-items: center; justify-content: space-between; min-height: 44px; margin-bottom: -14px; }
  .live { font-size: 13px; color: var(--muted); display: flex; align-items: center; gap: 6px; }
  .dot { width: 7px; height: 7px; border-radius: 50%; background: var(--green); }
  .live.stale .dot { background: var(--red); }
  .lock-btn {
    width: 44px; height: 44px; border-radius: 50%; border: 0; padding: 0; display: grid; place-items: center;
    color: var(--text); background: var(--bar); box-shadow: var(--bar-shadow), inset 0 0 0 .5px var(--bar-edge);
    -webkit-backdrop-filter: blur(20px) saturate(180%); backdrop-filter: blur(20px) saturate(180%); cursor: pointer;
  }
  .top-actions { display: flex; gap: 10px; margin-left: auto; }
  /* Locking lives in Settings ("Lock this page now"); this button only keeps the sign-in state. */
  #lockBtn { display: none !important; }
  .back-btn { display: flex; align-items: center; gap: 4px; margin-left: -6px; padding: 8px 6px; border: 0; background: none;
    color: var(--blue); font-size: 17px; cursor: pointer; }
  .back-btn svg { width: 12px; height: 20px; fill: none; stroke: currentColor; stroke-width: 2.4; stroke-linecap: round; stroke-linejoin: round; }
  .lock-btn svg { width: 19px; height: 19px; fill: none; stroke: currentColor; stroke-width: 1.9; stroke-linecap: round; stroke-linejoin: round; }
  .titles { margin-bottom: -6px; }
  .titles h1 { font-size: 34px; font-weight: 700; letter-spacing: -0.012em; margin: 0; line-height: 1.2; }
  .titles p { margin: 0; font-size: 15px; color: var(--muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }

  .card { background: var(--card); border-radius: 26px; padding: 16px; }
  .label { font-size: 13px; color: var(--muted); }
  .warn { color: var(--amber) !important; }
  .bad { color: var(--red) !important; }

  /* Inset grouped sections: header, rounded group, footer. */
  .section { display: grid; gap: 7px; }
  .section-head { font-size: 13px; color: var(--muted); text-transform: uppercase; letter-spacing: 0; padding: 0 16px; }
  .section-foot { font-size: 13px; line-height: 1.38; color: var(--muted); padding: 0 16px; text-wrap: pretty; }
  .list { background: var(--card); border-radius: 26px; padding-left: 16px; overflow: hidden; }
  .row { display: flex; align-items: center; gap: 12px; min-height: 52px; padding: 10px 16px 10px 0; position: relative; }
  .row + .row::before { content: ""; position: absolute; top: 0; left: 0; right: 0; height: .5px; background: var(--line); }
  .row .title { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .row .value { color: var(--muted); text-align: right; font-variant-numeric: tabular-nums; white-space: nowrap; }

  /* Status: the main switch, laid out like the account card at the top of Settings, with the timer below it. */
  .hero-group { background: var(--card); border-radius: 26px; padding-left: 16px; overflow: hidden; }
  .hero { display: flex; align-items: center; gap: 14px; padding: 14px 16px 14px 0; }
  .hero + .row::before { content: ""; position: absolute; top: 0; left: 0; right: 0; height: .5px; background: var(--line); }
  /* A pop-up row: the value with up/down chevrons; the iPhone's own picker opens on tap. */
  .picker { cursor: pointer; }
  .picker .value { color: var(--blue); transition: opacity .2s ease; }
  .picker.saving .value { opacity: .4; }
  .picker .chev { width: 9px; height: 15px; margin-left: -6px; fill: none; stroke: var(--blue); stroke-width: 1.7; stroke-linecap: round; stroke-linejoin: round; }
  .picker select { position: absolute; inset: 0; width: 100%; opacity: 0; font-size: 16px; cursor: pointer; -webkit-appearance: none; appearance: none; }
  .hero .app-icon {
    width: 58px; height: 58px; border-radius: 14px; flex: none; display: grid; place-items: center;
    background: var(--gray); color: #fff; transition: background .3s ease;
  }
  .hero.on .app-icon { background: var(--green); }
  .hero .app-icon svg { width: 34px; height: 34px; fill: none; stroke: currentColor; stroke-width: 1.7; stroke-linecap: round; stroke-linejoin: round; }
  .hero .text { flex: 1; min-width: 0; }
  .hero .state { font-size: 22px; font-weight: 600; letter-spacing: -0.02em; line-height: 1.2; }
  .hero .sub { font-size: 15px; color: var(--muted); margin-top: 1px; font-variant-numeric: tabular-nums; }
  .awake-switch {
    position: relative; width: 63px; height: 28px; border-radius: 99px; border: 0; padding: 0; flex: none;
    background: var(--fill); cursor: pointer; transition: background .25s ease;
  }
  .awake-switch::after {
    content: ""; position: absolute; top: 2px; left: 2px; width: 37px; height: 24px; border-radius: 99px;
    background: #fff; box-shadow: 0 2px 6px rgba(0,0,0,.15), 0 0 0 .5px rgba(0,0,0,.04);
    transition: transform .3s cubic-bezier(.32,.72,0,1);
  }
  .awake-switch.on { background: var(--green); }
  .awake-switch.on::after { transform: translateX(22px); }
  .awake-switch:disabled { opacity: .5; }

  /* Status: tiles in the style of the Weather app (small caps header, big value, one line of detail). */
  .tiles { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
  .tile { display: grid; align-content: start; gap: 4px; padding: 14px 16px 16px; min-height: 150px; }
  .tile-head { display: flex; align-items: center; gap: 5px; font-size: 13px; font-weight: 600; color: var(--muted); text-transform: uppercase; letter-spacing: 0; }
  .tile-head svg { width: 14px; height: 14px; fill: none; stroke: currentColor; stroke-width: 2.1; stroke-linecap: round; stroke-linejoin: round; }
  .tile-value { font-size: 34px; font-weight: 500; letter-spacing: -0.02em; line-height: 1.1; margin-top: 6px; font-variant-numeric: tabular-nums; display: flex; align-items: center; gap: 4px; }
  .tile-value.small { font-size: 28px; }
  /* Mac health: one short list, a thin bar per row, like iPhone Storage. */
  .health-list .row .title { flex: 0 0 88px; }
  .health-list .meter { flex: 1; margin: 0; }
  .health-list .value { flex: 0 0 96px; }
  .health-list .row.plain .value { flex: 1; }
  .tile-detail { font-size: 15px; color: var(--text); line-height: 1.3; margin-top: auto; padding-top: 8px; text-wrap: pretty; }
  .tile-detail.muted { color: var(--muted); }
  .bolt { width: 13px; height: 20px; fill: var(--green); }
  .meter { position: relative; height: 6px; border-radius: 99px; background: var(--fill); margin-top: 10px; }
  .meter > div { position: absolute; left: 0; top: 0; bottom: 0; border-radius: 99px; background: var(--green); transition: width .5s ease, background .3s ease; }
  /* Temperature: a cool-to-hot scale with a dot, like the UV index in Weather. */
  .scale { position: relative; height: 6px; border-radius: 99px; margin-top: 10px;
    background: linear-gradient(90deg, var(--green) 0%, #ffcc00 62%, var(--amber) 80%, var(--red) 100%); }
  .scale i { position: absolute; top: 50%; width: 10px; height: 10px; margin: -5px 0 0 -5px; border-radius: 50%;
    background: #fff; box-shadow: 0 0 0 2px var(--card), 0 1px 3px rgba(0,0,0,.3); transition: left .5s ease; }

  /* Status: Claude plan usage, two meters in one card. */
  .usage { display: grid; gap: 16px; padding: 16px; }
  .usage-row { display: grid; gap: 0; }
  .usage-top { display: flex; justify-content: space-between; align-items: baseline; font-size: 17px; }
  .usage-pct { font-size: 22px; font-weight: 600; font-variant-numeric: tabular-nums; letter-spacing: -0.02em; }
  .usage .meter { margin-top: 8px; }
  .usage-sub { font-size: 13px; color: var(--muted); margin-top: 6px; font-variant-numeric: tabular-nums; }

  /* Wi-Fi signal glyph. */
  .bars { width: 22px; height: 16px; flex: none; }
  .bars rect { fill: var(--faint); transition: fill .3s ease; }
  .bars rect.on { fill: var(--text); }

  /* Screen: the picture sits inside a MacBook Pro. */
  .notice { background: var(--card); border-radius: 18px; padding: 14px 16px; font-size: 15px; }
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
  .screen-wrap img, .screen-wrap canvas { display: block; width: 100%; height: auto; -webkit-user-drag: none; pointer-events: none; transform-origin: 0 0; }

  /* Screen: picture-quality slider in the style of iOS Settings (Text Size). */
  .qslider { display: grid; gap: 7px; }
  .qs-card { background: var(--card); border-radius: 26px; padding: 14px 16px 12px; display: grid; gap: 6px; }
  .qs-head { display: flex; justify-content: space-between; align-items: baseline; font-size: 17px; }
  .qs-head span:last-child { color: var(--muted); transition: opacity .25s ease; }
  .qs-row { display: grid; grid-template-columns: 18px 1fr 18px; align-items: center; gap: 14px; }
  .qs-end { width: 16px; height: 16px; fill: none; stroke: var(--muted); stroke-width: 1.6; stroke-linecap: round; stroke-linejoin: round; }
  .qs-end.big { width: 20px; height: 20px; }
  .qs-track { position: relative; height: 36px; touch-action: none; cursor: pointer; outline: none; -webkit-user-select: none; user-select: none; }
  .qs-track::before { content: ""; position: absolute; left: 0; right: 0; top: 50%; height: 6px; margin-top: -3px; border-radius: 99px; background: var(--fill); }
  .qs-fill { position: absolute; left: 0; top: 50%; height: 6px; margin-top: -3px; border-radius: 99px; background: var(--blue);
    transition: width .45s cubic-bezier(.32,.72,0,1); }
  .qs-fill.dragging { transition: none; }
  .qs-stop { position: absolute; top: 50%; width: 1.5px; height: 10px; margin: -5px 0 0 -0.75px; border-radius: 1px; background: var(--muted); opacity: .55; pointer-events: none; }
  .qs-track:focus-visible .qs-knob { box-shadow: 0 0 0 3px rgba(10,132,255,.4), 0 3px 8px rgba(0,0,0,.15), 0 3px 1px rgba(0,0,0,.06); }
  .qs-knob {
    position: absolute; top: 50%; left: 50%; width: 38px; height: 24px; margin: -12px 0 0 -19px; border-radius: 99px;
    background: #fff; pointer-events: none;
    box-shadow: 0 2px 6px rgba(0,0,0,.15), 0 0 0 .5px rgba(0,0,0,.04);
    transition: left .45s cubic-bezier(.32,.72,0,1), transform .2s ease;
  }
  .qs-knob.dragging { transition: transform .2s ease; transform: scale(1.08); }
  /* Labels sit under their stops (ends aligned to the track's ends). */
  .qs-labels { position: relative; height: 18px; margin: 0 32px; }
  .qs-labels button { position: absolute; top: 0; transform: translateX(-50%); white-space: nowrap;
    background: none; border: 0; padding: 0; font-size: 12px; color: var(--muted); cursor: pointer; transition: color .25s ease; }
  .qs-labels button:first-child { transform: none; }
  .qs-labels button:last-child { transform: translateX(-100%); }
  .qs-labels button.active { color: var(--text); }
  .qs-caption { font-size: 13px; color: var(--muted); padding: 0 16px; min-height: 1.35em; transition: opacity .25s ease; }
  .qs-caption.fade, .qs-head .fade { opacity: 0; }
  .zoombar { display: flex; align-items: center; justify-content: center; gap: 10px; font-size: 13px; color: var(--muted); }
  .zoombar button { font-size: 15px; font-weight: 600; color: var(--blue); background: var(--fill); border: 0; border-radius: 99px; padding: 6px 14px; cursor: pointer; }

  /* Invisible field that receives the iPhone keyboard; 16px stops iOS zooming in on focus. */
  .live-input {
    position: fixed; left: 0; bottom: 0; width: 1px; height: 1px; opacity: 0;
    font-size: 16px; border: 0; padding: 0; background: transparent; color: transparent;
  }

  /* Screen: keys styled like the iPhone keyboard, on a keyboard tray. */
  .keyboard { background: var(--kb); border-radius: 26px; padding: 10px 8px; display: grid; gap: 8px; }
  .typebar { display: flex; gap: 6px; }
  .typebar input {
    flex: 1; min-width: 0; font: inherit; font-size: 16px; color: var(--text); background: var(--card);
    border: 0; border-radius: 99px; padding: 9px 14px;
  }
  .keys { display: grid; grid-template-columns: repeat(6, 1fr); gap: 6px; }
  .keys .space { grid-column: span 2; }
  .key, .keys button, .gestures button, .typebar button {
    font-size: 16px; font-weight: 400; color: var(--text); background: var(--key); border: 0;
    border-radius: 8px; padding: 10px 0; cursor: pointer; box-shadow: 0 1px 0 var(--key-shadow);
  }
  .typebar button { padding: 9px 16px; font-weight: 600; border-radius: 99px; color: #fff; background: var(--blue); box-shadow: none; }
  .keys button[data-mod], .keys button[data-key="escape"], .keys button[data-key="tab"],
  .keys button[data-key="delete"], .keys button[data-key="return"], .gestures button { background: var(--key-fn); }
  .keys button:active, .gestures button:active { background: var(--card2); }
  .typebar button:active { opacity: .7; }
  .keys button.on { background: var(--blue); color: #fff; }
  .gestures { display: grid; grid-template-columns: 1fr 1.3fr 1.1fr 1fr; gap: 6px; }
  .gestures button { font-size: 13px; padding: 9px 2px; }

  /* Screen: Magic Trackpad. */
  .trackpad {
    aspect-ratio: 16 / 10.5; max-height: 280px; width: 100%; justify-self: center; border-radius: 22px;
    background: var(--pad); box-shadow: inset 0 0 0 0.5px var(--line), inset 0 1px 2px rgba(0,0,0,.06);
    display: flex; align-items: flex-end; justify-content: center; padding-bottom: 12px;
    color: var(--muted); font-size: 13px;
    touch-action: none; -webkit-user-select: none; user-select: none; -webkit-touch-callout: none;
    transition: background .15s ease;
  }
  .trackpad.active { background: var(--pad-on); }

  /* Apps: a home-screen grid of the Mac apps chosen in the menu. */
  .app-grid { display: grid; grid-template-columns: repeat(4, 1fr); gap: 22px 8px; padding: 8px 0 4px; }
  .app {
    display: grid; justify-items: center; gap: 5px; background: none; border: 0; padding: 0; cursor: pointer; min-width: 0;
    transition: transform .2s cubic-bezier(.32,.72,0,1), opacity .2s ease;
  }
  .app:active { transform: scale(.9); }
  .app { -webkit-touch-callout: none; -webkit-user-select: none; user-select: none; touch-action: manipulation; transition: transform .2s cubic-bezier(.32,.72,0,1), opacity .2s ease, scale .25s cubic-bezier(.32,.72,0,1); }
  /* Hold an icon to rearrange: icons wiggle, the one under your finger lifts and follows it. */
  .app-grid { position: relative; }
  .app-grid.editing { touch-action: none; }
  .app-grid.editing .app { animation: jiggle .25s ease-in-out infinite alternate; }
  .app-grid.editing .app:nth-child(2n) { animation-duration: .23s; animation-delay: -.11s; }
  .app-grid.editing .app:active { transform: none; }
  .app.dragging { animation: none !important; scale: 1.12; z-index: 3; pointer-events: none; filter: drop-shadow(0 10px 18px rgba(0,0,0,.28)); }
  @keyframes jiggle { from { rotate: -1.8deg; } to { rotate: 1.8deg; } }
  /* While rearranging, open apps get a quit badge in the corner. */
  .quit {
    position: absolute; top: -7px; left: calc(50% - var(--icon-size, 68px) / 2 - 7px); z-index: 2;
    width: 24px; height: 24px; border-radius: 50%; border: 0; padding: 0; display: none; place-items: center; cursor: pointer;
    background: rgba(118,118,128,.82); color: #fff; -webkit-backdrop-filter: blur(10px); backdrop-filter: blur(10px);
    box-shadow: 0 1px 3px rgba(0,0,0,.25);
  }
  .quit svg { width: 10px; height: 10px; stroke: currentColor; stroke-width: 2.4; stroke-linecap: round; }
  .app-grid.editing .app.running .quit { display: grid; }
  body:has(.app-grid.editing) .lock-btn { visibility: hidden; }
  .app { position: relative; }
  body.apps-full .app { --icon-size: var(--icon); }
  .done-btn {
    position: fixed; z-index: 12; top: max(10px, env(safe-area-inset-top)); right: max(16px, env(safe-area-inset-right));
    font-size: 15px; font-weight: 600; color: #fff; background: var(--blue); border: 0; border-radius: 99px; padding: 9px 20px; cursor: pointer;
    box-shadow: var(--bar-shadow);
  }
  .app.opening { opacity: .45; }
  .app img { width: 68px; height: 68px; display: block; -webkit-user-drag: none; pointer-events: none; }
  .app .name { font-size: 12px; letter-spacing: 0; max-width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .app .run { width: 4px; height: 4px; border-radius: 50%; background: var(--muted); margin-top: -2px; visibility: hidden; }
  .app.running .run { visibility: visible; }
  .apps-empty { text-align: center; padding: 10vh 16px 0; display: grid; gap: 8px; }
  .apps-empty h2 { margin: 0; font-size: 22px; font-weight: 700; }
  .apps-empty p { margin: 0 auto; max-width: 32ch; color: var(--muted); font-size: 15px; line-height: 1.38; text-wrap: pretty; }
  .opened {
    position: fixed; z-index: 9; left: 50%; transform: translateX(-50%);
    bottom: calc(max(4px, env(safe-area-inset-bottom) - 22px) + 72px);
    display: flex; align-items: center; gap: 12px; padding: 6px 6px 6px 16px; border-radius: 99px; font-size: 15px; white-space: nowrap;
    background: var(--bar); box-shadow: var(--bar-shadow), inset 0 0 0 .5px var(--bar-edge);
    -webkit-backdrop-filter: blur(20px) saturate(180%); backdrop-filter: blur(20px) saturate(180%);
  }
  .opened button { font-size: 15px; font-weight: 600; color: #fff; background: var(--blue); border: 0; border-radius: 99px; padding: 7px 14px; cursor: pointer; }

  /* Apps, phone turned sideways: only the icons, centred on black, in balanced rows. */
  body.apps-full { padding: 0; overflow: hidden; background: #000; color: #fff; }
  body.apps-full main { max-width: none; }
  body.apps-full .topbar, body.apps-full .titles, body.apps-full .tabbar,
  body.apps-full #appsFoot, body.apps-full #openedBar { display: none !important; }
  body.apps-full #tab-apps {
    position: fixed; inset: 0; z-index: 5; background: #000; display: grid; place-items: center;
    padding: env(safe-area-inset-top) max(24px, env(safe-area-inset-right)) env(safe-area-inset-bottom) max(24px, env(safe-area-inset-left));
    --icon: min(110px, 28vh); --col-gap: calc(var(--icon) * .36); --row-gap: calc(var(--icon) * .3);
  }
  body.apps-full .app-grid {
    display: flex; flex-wrap: wrap; justify-content: center; gap: var(--row-gap) var(--col-gap); padding: 0;
    width: calc(var(--cols, 5) * var(--icon) + (var(--cols, 5) - 1) * var(--col-gap)); max-width: 100%;
  }
  body.apps-full .app { width: var(--icon); gap: 0; }
  body.apps-full .app img { width: var(--icon); height: var(--icon); }
  body.apps-full .app .name { display: none; }
  body.apps-full .app .run { background: rgba(255,255,255,.6); margin-top: 9px; }
  body.apps-full .app.opening { opacity: 1; }
  body.apps-full .apps-empty p { color: rgba(255,255,255,.6); }
  /* Opening: a small press, then a spring back. */
  .app.launched { animation: launch .5s cubic-bezier(.32,.72,0,1); }
  @keyframes launch { 0% { transform: scale(.86); } 55% { transform: scale(1.07); } 100% { transform: scale(1); } }

  /* Floating tab bar, as in iOS 26: a capsule above the content, the current tab on a soft pill. */
  .tabbar {
    position: fixed; z-index: 10; left: 50%; transform: translateX(-50%);
    bottom: max(4px, calc(env(safe-area-inset-bottom) - 22px));
    display: flex; gap: 2px; padding: 4px; border-radius: 99px; isolation: isolate;
    background: var(--bar); box-shadow: var(--bar-shadow), inset 0 0 0 .5px var(--bar-edge);
    -webkit-backdrop-filter: blur(20px) saturate(180%); backdrop-filter: blur(20px) saturate(180%);
  }
  .tabbar button {
    width: 84px; height: 54px; background: none; border: 0; border-radius: 99px; display: grid; justify-items: center; align-content: center; gap: 1px;
    font-size: 10px; font-weight: 600; letter-spacing: 0; color: var(--text); cursor: pointer; padding: 0; transition: color .3s ease;
    position: relative; z-index: 1;
  }
  .tabbar button svg { transition: transform .35s cubic-bezier(.32,.72,0,1); }
  .tabbar button:active svg { transform: scale(.86); }
  /* Liquid glass, as in iOS 26: the pill behind the current tab is a drop of glass that
     stretches toward the tab you pick, then settles there with a little wobble (see movePill). */
  .tab-pill {
    position: absolute; z-index: 0; top: 4px; left: 0; width: 84px; height: 54px; border-radius: 99px;
    background: var(--fill); pointer-events: none;
    box-shadow: inset 0 0 0 .5px var(--bar-edge), inset 0 1px 0 rgba(255,255,255,.12);
  }
  .tab-pill.moving {
    background: linear-gradient(180deg, rgba(255,255,255,.24), rgba(255,255,255,.05)), var(--fill);
    box-shadow: inset 0 0 0 .5px rgba(255,255,255,.4), inset 0 1px 1px rgba(255,255,255,.35), 0 4px 14px rgba(0,0,0,.18);
  }
  /* The tab you picked gives a small bounce. */
  .tabbar button.bounce svg { animation: tab-bounce .55s cubic-bezier(.32,.72,0,1); }
  @keyframes tab-bounce { 0% { transform: scale(.8); } 45% { transform: scale(1.15); } 100% { transform: scale(1); } }
  /* The new page slides in from the side of the tab you picked. It moves with `left`, not a
     transform, so the floating bars inside it stay pinned to the screen while it slides. */
  .tab-enter-right, .tab-enter-left { position: relative; }
  .tab-enter-right { animation: tab-from-right .42s cubic-bezier(.32,.72,0,1); }
  .tab-enter-left { animation: tab-from-left .42s cubic-bezier(.32,.72,0,1); }
  @keyframes tab-from-right { from { opacity: 0; left: 32px; } to { opacity: 1; left: 0; } }
  @keyframes tab-from-left { from { opacity: 0; left: -32px; } to { opacity: 1; left: 0; } }
  @media (prefers-reduced-motion: reduce) {
    .tabbar button svg { transition: none; }
    .tabbar button.bounce svg { animation: none; }
    .tab-enter-right, .tab-enter-left { animation: tab-fade .2s ease-out; }
    @keyframes tab-fade { from { opacity: 0; } to { opacity: 1; } }
  }
  .tabbar svg { width: 26px; height: 24px; fill: none; stroke: currentColor; stroke-width: 1.8; stroke-linecap: round; stroke-linejoin: round; }
  .tabbar button.active { color: var(--blue); }
  body:has(#app[hidden]) .tabbar { display: none; }

  /* Screen: the overlay shown while the Mac's screen is off. */
  .screen-off {
    position: absolute; inset: 9px 9px 12px; z-index: 3; display: grid; place-content: center; gap: 12px;
    text-align: center; background: rgba(0,0,0,.85); color: #fff; border-radius: 4px; font-size: 15px;
  }
  .screen-off button { justify-self: center; font-weight: 600; color: #000; background: #fff; border: 0; border-radius: 99px; padding: 8px 18px; cursor: pointer; }
  body.landscape-full .screen-off { inset: 0; border-radius: 0; }

  /* Screen: one row of tools under the Mac screen. */
  .screen-tools { display: flex; gap: 5px; align-items: center; margin-top: -6px; min-width: 0; overflow-x: auto; scrollbar-width: none; }
  .screen-tools::-webkit-scrollbar { display: none; }
  .tool {
    height: 34px; display: inline-flex; align-items: center; gap: 5px; padding: 0 11px; border: 0; border-radius: 99px;
    font-size: 15px; font-weight: 600; color: var(--blue); background: var(--fill); cursor: pointer; flex: none;
    transition: background .2s ease, color .2s ease;
  }
  .tool svg { width: 18px; height: 18px; fill: none; stroke: currentColor; stroke-width: 1.8; stroke-linecap: round; stroke-linejoin: round; }
  .tool.icon { width: 34px; padding: 0; justify-content: center; }
  .tool.help { margin-left: auto; font-size: 16px; }
  .tool.on, .tool[aria-expanded="true"] { background: var(--blue); color: #fff; }
  .tool:active { opacity: .6; }
  .tool .hd { font-size: 11px; font-weight: 800; letter-spacing: .03em; border: 1.5px solid currentColor; border-radius: 4px; padding: 1px 4px; line-height: 1.1; }
  .help-card { background: var(--card); border-radius: 22px; padding: 12px 16px; display: grid; gap: 8px; font-size: 14px; line-height: 1.38; color: var(--muted); }
  .help-card p { margin: 0; }
  .help-card b { color: var(--text); font-weight: 600; margin-right: 4px; }
  .warn-notice { color: var(--amber); font-size: 14px; }
  /* Keys: a sheet that slides up from the bottom; the Mac screen stays in view above it. */
  .key-sheet {
    position: fixed; z-index: 11; left: 0; right: 0; bottom: 0; max-width: 520px; margin: 0 auto;
    background: var(--kb); border-radius: 26px 26px 0 0; padding: 6px 8px calc(10px + env(safe-area-inset-bottom));
    box-shadow: 0 -8px 30px rgba(0,0,0,.18); transform: translateY(105%); visibility: hidden;
    transition: transform .4s cubic-bezier(.32,.72,0,1), visibility 0s linear .4s;
  }
  .key-sheet.open { transform: none; visibility: visible; transition: transform .4s cubic-bezier(.32,.72,0,1), visibility 0s; }
  .key-sheet .keyboard { background: none; padding: 0; border-radius: 0; }
  .sheet-head { display: flex; justify-content: space-between; align-items: center; padding: 4px 8px 10px; font-size: 17px; font-weight: 600; }
  .sheet-head::before { content: ""; position: absolute; left: 50%; top: 6px; width: 36px; height: 5px; margin-left: -18px; border-radius: 3px; background: rgba(0,0,0,.2); }
  .sheet-head button { font-size: 17px; font-weight: 600; color: var(--blue); background: none; border: 0; padding: 4px 6px; cursor: pointer; }
  body:has(.key-sheet.open) .tabbar { visibility: hidden; }

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
  body.landscape-full .screen-wrap img, body.landscape-full .screen-wrap canvas { width: 100%; height: 100%; object-fit: contain; }
  body.landscape-full .float-bar {
    display: flex; position: fixed; z-index: 20; gap: 2px; padding: 4px; border-radius: 99px;
    top: max(8px, env(safe-area-inset-top)); right: max(8px, env(safe-area-inset-right));
    background: rgba(30,30,32,.72); -webkit-backdrop-filter: blur(20px); backdrop-filter: blur(20px);
    box-shadow: inset 0 0 0 .5px rgba(255,255,255,.1);
  }
  .float-bar button {
    min-width: 38px; height: 34px; border: 0; border-radius: 99px; background: transparent;
    color: #fff; font-size: 15px; font-weight: 600; cursor: pointer; display: grid; place-items: center;
  }
  .float-bar button.on { background: var(--blue); }
  .float-bar .q-badge span {
    font-size: 11px; font-weight: 800; letter-spacing: .03em; line-height: 1;
    border: 1.5px solid currentColor; border-radius: 4px; padding: 2px 4px;
  }
  .float-bar svg { width: 20px; height: 16px; fill: none; stroke: currentColor; stroke-width: 1.6; stroke-linecap: round; stroke-linejoin: round; }
  body.landscape-full #zoomBar {
    position: fixed; z-index: 20; left: 50%; bottom: max(10px, env(safe-area-inset-bottom)); transform: translateX(-50%);
    background: rgba(30,30,32,.72); color: #fff; padding: 4px 6px 4px 12px; border-radius: 99px;
    -webkit-backdrop-filter: blur(20px); backdrop-filter: blur(20px);
  }
  body.landscape-full #zoomBar { transition: opacity .6s ease; }
  body.landscape-full #zoomBar.faded { opacity: 0; pointer-events: none; }
  body.landscape-full #screenMsg { position: fixed; z-index: 15; left: 50%; top: 50%; transform: translate(-50%, -50%); max-width: 80vw; }

  /* Locked / off screen: a plain sign-in sheet. */
  .gate { text-align: center; padding: 12vh 4px 0; display: grid; gap: 10px; }
  main:has(#gate:not([hidden])) .titles { display: none; }
  .gate .icon { width: 64px; height: 64px; justify-self: center; display: grid; place-items: center; color: var(--blue); }
  .gate .icon svg { width: 52px; height: 52px; fill: none; stroke: currentColor; stroke-width: 1.5; stroke-linecap: round; stroke-linejoin: round; }
  .gate h2 { margin: 0; font-size: 28px; font-weight: 700; letter-spacing: -0.01em; }
  .gate p { margin: 0 auto; max-width: 34ch; color: var(--muted); font-size: 15px; line-height: 1.38; text-wrap: pretty; }
  .gate form { display: grid; gap: 14px; margin-top: 18px; }
  .gate input {
    font: inherit; color: var(--text); background: var(--card); text-align: left;
    border: 0; border-radius: 26px; padding: 15px 18px;
  }
  .gate button { font-weight: 600; color: #fff; background: var(--blue); border: 0; border-radius: 99px; padding: 15px; cursor: pointer; }
  .gate button:active { opacity: .7; }
  .gate button:disabled { opacity: .5; }
  /* With Face ID offered, it's the main button and the password button steps back. */
  .gate:has(#faceIdBtn:not([hidden])) #unlockBtn { background: var(--fill); color: var(--blue); }
  .faceid-btn { display: flex; align-items: center; justify-content: center; gap: 8px; }
  .faceid-btn svg, .faceid-icon svg { width: 22px; height: 22px; fill: none; stroke: currentColor; stroke-width: 1.7; stroke-linecap: round; stroke-linejoin: round; }
  .faceid-offer { display: grid; grid-template-columns: auto 1fr; gap: 10px 12px; align-items: center; padding: 14px 16px; }
  .faceid-icon { width: 42px; height: 42px; border-radius: 11px; background: var(--blue); color: #fff; display: grid; place-items: center; }
  .faceid-text { display: grid; gap: 2px; font-size: 15px; }
  .faceid-text span { color: var(--muted); font-size: 13px; line-height: 1.35; }
  .faceid-actions { grid-column: 1 / -1; display: flex; justify-content: flex-end; gap: 8px; }
  .faceid-actions button { font-size: 15px; font-weight: 600; border: 0; border-radius: 99px; padding: 8px 16px; cursor: pointer; color: var(--blue); background: var(--fill); }
  .faceid-actions button.primary { color: #fff; background: var(--blue); }
  /* Settings rows with a small colored icon, like iPhone Settings. */
  .set-icon { width: 30px; height: 30px; border-radius: 8px; flex: none; display: grid; place-items: center; color: #fff; }
  .set-icon svg { width: 18px; height: 18px; fill: none; stroke: currentColor; stroke-width: 1.9; stroke-linecap: round; stroke-linejoin: round; }
  .set-icon.red { background: #ff3b30; } .set-icon.indigo { background: #5856d6; } .set-icon.green { background: #34c759; }
  .set-icon.clay { background: #d97757; } .set-icon.orange { background: #ff9500; } .set-icon.blue { background: var(--blue); }
  .set-icon.gray { background: #8e8e93; }
  .row .title small { display: block; font-size: 13px; color: var(--muted); margin-top: 1px; white-space: normal; }
  .icon-list .row + .row::before, .icon-list .alert-rows .row::before { left: 42px; }
  .row-link.danger { color: var(--red); }
  .pw-row input { flex: 1; min-width: 0; font: inherit; font-size: 17px; color: inherit; background: none; border: 0; outline: none; padding: 6px 0; }
  .device-row .title small { display: block; color: var(--muted); font-size: 13px; }
  .activity-list .row { min-height: 44px; align-items: baseline; font-size: 15px; }
  .activity-list .row .when { color: var(--muted); font-size: 13px; white-space: nowrap; font-variant-numeric: tabular-nums; }
  .activity-list .row .what { flex: 1; min-width: 0; }
  /* Alerts: one card with a title, then one row per alert. */
  .alerts-card { background: var(--card); border-radius: 26px; overflow: hidden; }
  .alerts-head { display: flex; gap: 12px; align-items: center; padding: 16px 16px 14px; }
  .alerts-badge { width: 44px; height: 44px; border-radius: 12px; flex: none; display: grid; place-items: center; color: #fff;
    background: linear-gradient(#ff5a4f, #ff3b30); }
  .alerts-badge svg { width: 24px; height: 24px; fill: none; stroke: currentColor; stroke-width: 1.9; stroke-linecap: round; stroke-linejoin: round; }
  .alerts-head h2 { margin: 0; font-size: 20px; font-weight: 700; letter-spacing: -0.01em; }
  .alerts-head p { margin: 2px 0 0; font-size: 13px; line-height: 1.35; color: var(--muted); }
  .alert-row { position: relative; display: flex; align-items: center; gap: 12px; padding: 11px 16px; min-height: 60px; }
  .alert-row::before { content: ""; position: absolute; top: 0; left: 58px; right: 0; height: .5px; background: var(--line); }
  .alert-row:first-of-type::before { left: 0; }
  .alert-row .title { flex: 1; min-width: 0; font-size: 17px; line-height: 1.25; }
  .alert-row .title small { display: block; font-size: 13px; color: var(--muted); margin-top: 2px; }
  .alerts-card.unavailable .alert-row { opacity: .45; }
  .limits-card .alert-row:first-of-type::before { display: none; }
  /* iOS segmented control. */
  .segmented { display: grid; grid-template-columns: repeat(3, 1fr); gap: 2px; padding: 3px; border-radius: 12px; background: var(--fill); }
  .segmented button { border: 0; border-radius: 9px; padding: 8px 0; font-size: 15px; font-weight: 500; color: var(--text);
    background: transparent; cursor: pointer; transition: background .2s ease, box-shadow .2s ease; }
  .segmented button[aria-checked="true"] { background: var(--seg-on); font-weight: 600; box-shadow: 0 2px 6px rgba(0,0,0,.12), 0 0 0 .5px rgba(0,0,0,.04); }
  .alerts-note { margin: 0; padding: 10px 16px 14px; font-size: 13px; line-height: 1.38; color: var(--muted); border-top: .5px solid var(--line); }
  .mini-switch { position: relative; width: 63px; height: 28px; flex: none; border-radius: 99px; border: 0; padding: 0;
    margin-left: auto; background: var(--fill); cursor: pointer; transition: background .25s ease; }
  .mini-switch::after { content: ""; position: absolute; top: 2px; left: 2px; width: 37px; height: 24px; border-radius: 99px;
    background: #fff; box-shadow: 0 2px 6px rgba(0,0,0,.15); transition: transform .3s cubic-bezier(.32,.72,0,1); }
  .mini-switch[aria-checked="true"] { background: var(--green); }
  .mini-switch[aria-checked="true"]::after { transform: translateX(22px); }
  .alert-rows .row::before { content: ""; position: absolute; top: 0; left: 0; right: 0; height: .5px; background: var(--line); }
  .alert-rows .row { position: relative; }
  .row-link.start { margin-left: 0; }
  .row-link { margin-left: auto; font-size: 17px; color: var(--blue); background: none; border: 0; padding: 0; cursor: pointer; }
  .gate .error { color: var(--red); font-size: 15px; min-height: 1.3em; }
</style>
</head>
<body>
<div id="scroller">
<main id="main">
  <div class="topbar">
    <button type="button" class="back-btn" id="settingsBack" hidden><svg viewBox="0 0 12 20" aria-hidden="true"><path d="M10 2 2 10l8 8"/></svg>Status</button>
    <div class="live" id="live"><span class="dot"></span><span id="liveText">Connecting…</span></div>
    <div class="top-actions">
    <button type="button" class="lock-btn" id="settingsBtn" aria-label="Settings" hidden><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12.00 4.60 L13.87 2.58 L15.67 3.13 L16.11 5.85 L17.23 6.77 L19.98 6.67 L20.87 8.33 L19.26 10.56 L19.40 12.00 L21.42 13.87 L20.87 15.67 L18.15 16.11 L17.23 17.23 L17.33 19.98 L15.67 20.87 L13.44 19.26 L12.00 19.40 L10.13 21.42 L8.33 20.87 L7.89 18.15 L6.77 17.23 L4.02 17.33 L3.13 15.67 L4.74 13.44 L4.60 12.00 L2.58 10.13 L3.13 8.33 L5.85 7.89 L6.77 6.77 L6.67 4.02 L8.33 3.13 L10.56 4.74Z"/><circle cx="12" cy="12" r="3"/></svg></button>
    <button class="lock-btn" id="lockBtn" aria-label="Lock this page" hidden>
      <svg viewBox="0 0 24 24" aria-hidden="true"><rect x="5" y="11" width="14" height="10" rx="2.5"/><path d="M8 11V8a4 4 0 0 1 8 0v3"/></svg></button>
    </div>
  </div>
  <div class="titles">
    <h1 id="pageTitle">Lid Awake</h1>
    <p id="host"></p>
  </div>

  <section class="gate" id="gate" hidden>
    <div class="icon"><svg viewBox="0 0 24 24" aria-hidden="true"><rect x="5" y="11" width="14" height="10" rx="2.5"/><path d="M8 11V8a4 4 0 0 1 8 0v3"/></svg></div>
    <h2 id="gateTitle">Locked</h2>
    <p id="gateText"></p>
    <form id="loginForm">
      <button type="button" class="faceid-btn" id="faceIdBtn" hidden><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M7 3H5a2 2 0 0 0-2 2v2M17 3h2a2 2 0 0 1 2 2v2M7 21H5a2 2 0 0 1-2-2v-2M17 21h2a2 2 0 0 0 2-2v-2M9 9v1.5M15 9v1.5M12 9v4.5h-1M9.5 16.5a4 4 0 0 0 5 0"/></svg>Sign in with Face ID</button>
      <input type="password" id="password" placeholder="Password" autocomplete="current-password" required>
      <button id="unlockBtn">Unlock</button>
      <div class="error" id="loginError"></div>
    </form>
  </section>

  <div class="tab" id="app" hidden>

  <div class="tab" id="tab-status">
    <section class="card faceid-offer" id="faceIdOffer" hidden>
      <div class="faceid-icon"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M7 3H5a2 2 0 0 0-2 2v2M17 3h2a2 2 0 0 1 2 2v2M7 21H5a2 2 0 0 1-2-2v-2M17 21h2a2 2 0 0 0 2-2v-2M9 9v1.5M15 9v1.5M12 9v4.5h-1M9.5 16.5a4 4 0 0 0 5 0"/></svg></div>
      <div class="faceid-text"><b>Sign in with Face ID?</b><span>Skip the password next time. Your face never leaves this iPhone.</span></div>
      <div class="faceid-actions"><button type="button" id="faceIdLater">Not now</button><button type="button" class="primary" id="faceIdOn">Turn on</button></div>
    </section>
    <div class="section">
      <div class="hero-group">
      <section class="hero" id="statusCard">
        <div class="app-icon" aria-hidden="true">
          <svg viewBox="0 0 34 34"><rect x="6" y="8" width="22" height="14.5" rx="1.8"/><path d="M3 26h28"/></svg>
        </div>
        <div class="text">
          <div class="state" id="state">—</div>
          <div class="sub" id="stateSub"></div>
        </div>
        <button class="awake-switch" id="awakeSwitch" role="switch" aria-label="Stay awake with lid closed"></button>
      </section>
      <label class="row picker">
        <span class="title">Turn off after</span>
        <span class="value" id="timerRow">—</span><svg class="chev" viewBox="0 0 10 16" aria-hidden="true"><path d="M2 6l3-3 3 3M2 10l3 3 3-3"/></svg>
        <select id="timerSelect" aria-label="Turn off after">
          <option value="0">Never</option><option value="15">15 minutes</option><option value="30">30 minutes</option>
          <option value="60">1 hour</option><option value="120">2 hours</option><option value="240">4 hours</option><option value="480">8 hours</option>
        </select>
      </label>
      </div>
      <div class="section-foot" id="stateLabel"></div>
    </div>

    <div class="tiles">
      <section class="card tile">
        <div class="tile-head"><svg viewBox="0 0 16 16" aria-hidden="true"><rect x="1.5" y="4.5" width="11" height="7" rx="2"/><path d="M14.5 7v2"/></svg>Battery</div>
        <div class="tile-value"><span id="battery">—</span><svg class="bolt" id="bolt" viewBox="0 0 12 16" hidden aria-hidden="true"><path d="M7 0 0 9h5l-1 7 7-9H6z"/></svg></div>
        <div class="meter"><div id="batteryBar" style="width:0"></div></div>
        <div class="tile-detail" id="batterySub"></div>
      </section>
      <section class="card tile">
        <div class="tile-head"><svg viewBox="0 0 16 16" aria-hidden="true"><path d="M8 1.5a2 2 0 0 0-2 2v6.1a3.4 3.4 0 1 0 4 0V3.5a2 2 0 0 0-2-2z"/></svg>Temperature</div>
        <div class="tile-value" id="temp">—</div>
        <div class="scale"><i id="tempDot" style="left:0"></i></div>
        <div class="tile-detail" id="tempSub"></div>
      </section>
    </div>

    <div class="section" id="claudeSection" hidden>
      <div class="section-head">Claude</div>
      <section class="card usage">
        <div class="usage-row">
          <div class="usage-top"><span>Current session</span><span class="usage-pct" id="claude5hPct">—</span></div>
          <div class="meter"><div id="claude5hBar" style="width:0"></div></div>
          <div class="usage-sub" id="claude5hSub"></div>
        </div>
        <div class="usage-row">
          <div class="usage-top"><span>This week</span><span class="usage-pct" id="claudeWeekPct">—</span></div>
          <div class="meter"><div id="claudeWeekBar" style="width:0"></div></div>
          <div class="usage-sub" id="claudeWeekSub"></div>
        </div>
      </section>
      <div class="section-foot" id="claudeFoot"></div>
    </div>

    <div class="section">
      <div class="section-head">Mac</div>
      <div class="list health-list">
        <div class="row"><span class="title">Processor</span><div class="meter"><div id="cpuBar" style="width:0"></div></div><span class="value" id="cpuValue">—</span></div>
        <div class="row"><span class="title">Memory</span><div class="meter"><div id="memBar" style="width:0"></div></div><span class="value" id="memValue">—</span></div>
        <div class="row"><span class="title">Storage</span><div class="meter"><div id="diskBar" style="width:0"></div></div><span class="value" id="diskValue">—</span></div>
        <div class="row plain"><span class="title">Heat</span><span class="value" id="thermal">—</span></div>
        <div class="row plain"><span class="title">Running</span><span class="value" id="uptimeValue">—</span></div>
      </div>
    </div>

    <div class="section">
      <div class="section-head">Network</div>
      <div class="list">
        <div class="row">
          <span class="title" id="netKind">Wi-Fi</span>
          <span class="value" id="wifiName">—</span>
          <svg class="bars" id="wifiBars" viewBox="0 0 22 16" aria-hidden="true">
            <rect x="0" y="11" width="4" height="5" rx="1"/><rect x="6" y="7.5" width="4" height="8.5" rx="1"/>
            <rect x="12" y="4" width="4" height="12" rx="1"/><rect x="18" y="0" width="4" height="16" rx="1"/>
          </svg>
        </div>
        <div class="row"><span class="title">Download</span><span class="value" id="netDown">—</span></div>
        <div class="row"><span class="title">Upload</span><span class="value" id="netUp">—</span></div>
        <div class="row"><span class="title">Response time to this iPhone</span><span class="value" id="netPing">—</span></div>
        <div class="row"><span class="title">Connection to this iPhone</span><span class="value" id="netPath">—</span></div>
      </div>
    </div>
  </div>

  <div class="tab" id="tab-apps" hidden>
    <button type="button" class="done-btn" id="appsDone" hidden>Done</button>
    <div class="app-grid" id="appGrid"></div>
    <div class="apps-empty" id="appsEmpty" hidden>
      <h2>No apps yet</h2>
      <p>On your Mac, click the Lid Awake icon in the menu bar, then choose Apps on phone → Add apps…</p>
    </div>
    <div class="section-foot" id="appsFoot" hidden>Tap an app to open it on your Mac. A dot means it's already open. To change this list, use Apps on phone in the Lid Awake menu on your Mac.</div>
    <div class="opened" id="openedBar" hidden><span id="openedText"></span><button type="button" id="openedShow">Show screen</button></div>
  </div>

  <div class="tab" id="tab-settings" hidden>
    <section class="alerts-card" id="alertsSection">
      <header class="alerts-head">
        <div class="alerts-badge"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6 16v-5a6 6 0 0 1 12 0v5l1.5 2h-15z"/><path d="M10 20.5a2 2 0 0 0 4 0"/></svg></div>
        <div><h2>Alerts</h2><p>Notifications on this iPhone, even when Lid Awake is closed. Sign-in alerts are always on.</p></div>
      </header>
      <div class="alert-row"><span class="set-icon indigo"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M19 14.5A7.5 7.5 0 0 1 9.5 5a7.5 7.5 0 1 0 9.5 9.5z"/></svg></span><span class="title">Lid Awake turns off<small>Timer, low battery or heat</small></span><button type="button" class="mini-switch" data-alert="turnedOff" role="switch" aria-label="Lid Awake turns off"></button></div>
      <div class="alert-row"><span class="set-icon green"><svg viewBox="0 0 24 24" aria-hidden="true"><rect x="3" y="8" width="15" height="8" rx="2"/><path d="M21 11v2M6 11v2"/></svg></span><span class="title">Battery low<small>At 20%, with time left</small></span><button type="button" class="mini-switch" data-alert="lowBattery" role="switch" aria-label="Battery low"></button></div>
      <div class="alert-row"><span class="set-icon clay"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 3v18M3 12h18M5.6 5.6l12.8 12.8M18.4 5.6L5.6 18.4"/></svg></span><span class="title">Claude usage<small>Over 90% of a limit</small></span><button type="button" class="mini-switch" data-alert="claudeUsage" role="switch" aria-label="Claude usage"></button></div>
      <div class="alert-row"><span class="set-icon orange"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 3.5a2.3 2.3 0 0 0-2.3 2.3v7.3a4 4 0 1 0 4.6 0V5.8A2.3 2.3 0 0 0 12 3.5z"/></svg></span><span class="title">Mac warm<small>Battery at 37°C</small></span><button type="button" class="mini-switch" data-alert="warm" role="switch" aria-label="Mac warm"></button></div>
      <p class="alerts-note" id="alertsFoot" hidden></p>
    </section>

    <div class="section">
      <div class="section-head">Sign-in</div>
      <div class="list icon-list">
        <div class="row" id="faceIdRow" hidden><span class="set-icon blue"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M7 3H5a2 2 0 0 0-2 2v2M17 3h2a2 2 0 0 1 2 2v2M7 21H5a2 2 0 0 1-2-2v-2M17 21h2a2 2 0 0 0 2-2v-2M9 9v1.5M15 9v1.5M12 9v4.5h-1M9.5 16.5a4 4 0 0 0 5 0"/></svg></span><span class="title">Face ID</span>
          <span class="value" id="faceIdState" hidden>On</span><button type="button" class="row-link" id="faceIdRowBtn">Set up</button></div>
        <form class="row pw-row" id="faceIdPwRow" hidden><input type="password" id="faceIdPassword" placeholder="Password, to add Face ID" autocomplete="current-password" aria-label="Your Lid Awake password"><button class="row-link" id="faceIdPwGo">Continue</button></form>
        <div class="row"><span class="set-icon gray"><svg viewBox="0 0 24 24" aria-hidden="true"><rect x="5" y="11" width="14" height="10" rx="2.5"/><path d="M8 11V8a4 4 0 0 1 8 0v3"/></svg></span><button type="button" class="row-link start danger" id="lockNowBtn">Lock this page now</button></div>
      </div>
      <div class="section-foot" id="faceIdError" hidden></div>
      <div class="section-foot">You stay signed in on this iPhone for 7 days. Adding Face ID asks for your password. Change the password from the Lid Awake menu on your Mac.</div>
    </div>

    <div class="section" id="devicesSection" hidden>
      <div class="section-head">Signed-in devices</div>
      <div class="list" id="devicesList"></div>
      <div class="section-foot">Sign out anything you don't recognize. Your iPhone gets an alert whenever someone signs in with the password or adds Face ID.</div>
    </div>

    <div class="section" id="activitySection" hidden>
      <div class="section-head">Recent activity</div>
      <div class="list activity-list" id="activityList"></div>
      <div class="section-foot">The full log is on your Mac: Lid Awake menu → Signed-in devices → Show activity log.</div>
    </div>

    <div class="section">
      <div class="section-head">Safety limits</div>
      <section class="alerts-card limits-card">
        <div class="alert-row"><span class="set-icon green"><svg viewBox="0 0 24 24" aria-hidden="true"><rect x="3" y="8" width="15" height="8" rx="2"/><path d="M21 11v2M6 11v2"/></svg></span><span class="title">Low battery<small id="batteryLimitText">Turns off below 10% on battery</small></span><button type="button" class="mini-switch" data-limit="battery" role="switch" aria-label="Turn off at low battery"></button></div>
        <div class="alert-row"><span class="set-icon orange"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 3.5a2.3 2.3 0 0 0-2.3 2.3v7.3a4 4 0 1 0 4.6 0V5.8A2.3 2.3 0 0 0 12 3.5z"/></svg></span><span class="title">Too hot<small id="heatLimitText">Turns off above 40°C or if the Mac overheats</small></span><button type="button" class="mini-switch" data-limit="heat" role="switch" aria-label="Turn off when too hot"></button></div>
      </section>
      <div class="section-foot">Lid Awake turns itself off so your Mac can sleep safely. These are the same switches as in the Mac menu.</div>
    </div>

    <div class="section">
      <div class="section-head">Appearance</div>
      <div class="segmented" role="radiogroup" aria-label="Appearance">
        <button type="button" data-theme-choice="auto" role="radio">Automatic</button>
        <button type="button" data-theme-choice="light" role="radio">Light</button>
        <button type="button" data-theme-choice="dark" role="radio">Dark</button>
      </div>
      <div class="section-foot">Automatic follows your iPhone's light or dark setting.</div>
    </div>
  </div>

  <div class="tab" id="tab-screen" hidden>
    <div class="float-bar" id="floatBar" aria-label="Screen controls">
      <button type="button" id="kbFloat" aria-label="Keyboard"><svg viewBox="0 0 24 24" aria-hidden="true"><rect x="2" y="6" width="20" height="12" rx="2.5"/><path d="M6 10h.01M10 10h.01M14 10h.01M18 10h.01M8 14h8"/></svg></button>
      <button type="button" id="padModeBtn" aria-label="Trackpad mode">
        <svg viewBox="0 0 20 16" aria-hidden="true"><rect x="1.5" y="1.5" width="17" height="13" rx="3"/></svg></button>
      <button type="button" id="qualityFloat" class="q-badge" aria-label="Picture quality: Balanced"><span>SD</span></button>
      <button data-mod="cmd" aria-label="Command">⌘</button>
      <button data-mod="alt" aria-label="Option">⌥</button>
      <button data-key="escape">esc</button>
    </div>
    <div class="notice" id="screenMsg" hidden></div>
    <div class="tab" id="screenControls" hidden>
    <div class="macbook">
      <div class="lid">
        <div class="screen-wrap" id="screenWrap"><img id="screenImg" alt="Mac screen"><canvas id="screenCanvas" hidden aria-label="Mac screen"></canvas></div>
        <div class="screen-off" id="screenOffOverlay" hidden><span>Your Mac's screen is off</span><button type="button" id="wakeBtn">Wake screen</button></div>
        <div class="screen-off" id="pausedOverlay" hidden><span>Paused to save data and battery</span><button type="button" id="resumeBtn">Continue</button></div>
      </div>
      <div class="base"></div>
    </div>
    <div class="screen-tools" role="toolbar" aria-label="Screen tools">
      <button type="button" class="tool" id="qualityBtn" aria-expanded="false" aria-label="Picture quality: Balanced"><span class="hd">SD</span></button>
      <button type="button" class="tool" id="kbBtn"><svg viewBox="0 0 24 24" aria-hidden="true"><rect x="2" y="6" width="20" height="12" rx="2.5"/><path d="M6 10h.01M10 10h.01M14 10h.01M18 10h.01M8 14h8"/></svg><span id="kbLabel">Type</span></button>
      <button type="button" class="tool" id="keysBtn" aria-expanded="false"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M7 4h4v4H7zM13 4h4v4h-4zM4 11h16v4H4zM8 18h8"/></svg>Keys</button>
      <button type="button" class="tool icon" id="lockMacBtn" aria-label="Lock Mac"><svg viewBox="0 0 24 24" aria-hidden="true"><rect x="5" y="11" width="14" height="10" rx="2.5"/><path d="M8 11V8a4 4 0 0 1 8 0v3"/></svg></button>
      <button type="button" class="tool icon" id="screenOffBtn" aria-label="Turn the Mac's screen off"><svg viewBox="0 0 24 24" aria-hidden="true"><rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4M9 8l6 6"/></svg></button>
      <button type="button" class="tool icon help" id="helpBtn" aria-expanded="false" aria-label="How to use">?</button>
    </div>
    <div class="qslider" id="qualityPanel" hidden>
      <div class="qs-card">
        <div class="qs-head"><span>Picture</span><span id="qsValue">Balanced</span></div>
        <div class="qs-row">
          <svg class="qs-end" viewBox="0 0 24 24" aria-hidden="true"><path d="M12 3l1.8 5.2L19 10l-5.2 1.8L12 17l-1.8-5.2L5 10l5.2-1.8z"/></svg>
          <div class="qs-track" id="qsTrack" role="slider" tabindex="0" aria-label="Picture quality" aria-valuemin="0" aria-valuemax="3">
            <div class="qs-fill" id="qsFill"></div>
            <span class="qs-stop" style="left:0%"></span><span class="qs-stop" style="left:33.333%"></span><span class="qs-stop" style="left:66.667%"></span><span class="qs-stop" style="left:100%"></span>
            <div class="qs-knob" id="qsKnob"></div>
          </div>
          <svg class="qs-end big" viewBox="0 0 24 24" aria-hidden="true"><path d="M13 2L4 14h7l-1 8 9-12h-7z"/></svg>
        </div>
        <div class="qs-labels">
          <button type="button" data-q="max" style="left:0%">Max</button><button type="button" data-q="sharp" style="left:33.333%">Sharp</button><button type="button" data-q="balanced" style="left:66.667%">Balanced</button><button type="button" data-q="fast" style="left:100%">Fast</button>
        </div>
      </div>
      <div class="qs-caption" id="qsCaption"></div>
    </div>
    <div class="help-card" id="helpPanel" hidden>
      <p><b>On the Mac screen</b> Tap = click · Double-tap = double-click · Hold = right-click · Drag = scroll · Pinch = zoom (on this phone only)</p>
      <p><b>Trackpad, 1 finger</b> Move · tap = click · tap twice, then hold and slide = drag</p>
      <p><b>2 fingers</b> Slide = scroll · tap = right-click</p>
      <p><b>3 fingers</b> Swipe ← → = switch desktop · ↑ = Mission Control · ↓ = app windows</p>
      <p><b>Type</b> uses the iPhone keyboard. <b>Keys</b> has esc, arrows, ⌘ and more.</p>
    </div>
    <div class="zoombar" id="zoomBar" hidden>Zoomed <span id="zoomLevel">1.0×</span> on this phone only <button id="zoomReset">Reset zoom</button></div>
    <div class="notice warn-notice" id="hint" hidden></div>
    <div class="trackpad" id="trackpad">Trackpad</div>
    <div class="key-sheet" id="keySheet" aria-label="Keys" aria-hidden="true">
      <div class="sheet-head"><span>Keys</span><button type="button" id="keysDone">Done</button></div>
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
        <button data-gesture="spaceLeft">‹ Desktop</button>
        <button data-gesture="mission">Mission Control</button>
        <button data-gesture="appWindows">App windows</button>
        <button data-gesture="spaceRight">Desktop ›</button>
      </div>
      </div>
    </div>
    </div>
  </div>
  </div>
</main>
</div>
  <nav class="tabbar tabs" aria-label="Pages">
    <span class="tab-pill" id="tabPill" aria-hidden="true"></span>
    <button data-tab="status" class="active">
      <svg viewBox="0 0 26 24" aria-hidden="true"><path d="M3 17a10 10 0 1 1 20 0"/><path d="M13 17l5-6"/><circle cx="13" cy="17" r="1.6"/></svg>Status</button>
    <button data-tab="screen">
      <svg viewBox="0 0 26 24" aria-hidden="true"><rect x="4" y="4" width="18" height="12" rx="1.8"/><path d="M1.5 19.5h23"/></svg>Screen</button>
    <button data-tab="apps">
      <svg viewBox="0 0 26 24" aria-hidden="true"><rect x="4" y="2.5" width="8" height="8" rx="2.4"/><rect x="14" y="2.5" width="8" height="8" rx="2.4"/><rect x="4" y="12.5" width="8" height="8" rx="2.4"/><rect x="14" y="12.5" width="8" height="8" rx="2.4"/></svg>Apps</button>
  </nav>
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
    $("pageTitle").textContent = { screen: "Screen", apps: "Apps", settings: "Settings" }[currentTab] || "Lid Awake";
    $("settingsBtn").hidden = currentTab !== "status" || $("lockBtn").hidden;
    if (!data) return;

    $("host").textContent = data.host || "";
    const on = data.lidAwake;
    $("statusCard").classList.toggle("on", on);
    $("awakeSwitch").classList.toggle("on", on);
    $("awakeSwitch").setAttribute("aria-checked", on);
    $("awakeSwitch").disabled = !on;
    $("stateLabel").textContent = on ? "Your Mac keeps running with the lid closed." : "Your Mac sleeps when the lid closes.";
    $("state").textContent = on ? "On" : "Off";
    const left = data.timerRemaining != null ? data.timerRemaining - (Date.now() - fetchedAt) / 1000 : null;
    $("stateSub").textContent = !on ? "" : left != null && left > 0 ? `Turns off in ${fmtCountdown(left)}` : "Stays on until you turn it off";

    if (data.battery != null) {
      const pct = data.battery;
      const color = pct < data.batteryLimit + 5 ? "var(--red)" : pct < 25 ? "var(--amber)" : "var(--green)";
      $("battery").textContent = pct + "%";
      $("batteryBar").style.width = Math.max(3, pct) + "%";
      $("batteryBar").style.background = color;
      $("bolt").toggleAttribute("hidden", !data.charging);
      // macOS needs a minute or two after plugging in / unplugging before it has an estimate.
      const clockIn = min => new Date(fetchedAt + min * 60000).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
      let sub;
      if (data.onBattery) {
        sub = fmtMinutes(data.minutesToEmpty)
          ? `Lasts until ${clockIn(data.minutesToEmpty)}`
          : "On battery · estimating…";
      } else if (data.charging) {
        sub = fmtMinutes(data.minutesToFull) && data.minutesToFull > 0
          ? `Full at ${clockIn(data.minutesToFull)}`
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
      $("temp").className = "tile-value " + level;
      $("tempDot").style.left = Math.min(100, Math.max(0, (t - 20) / (max - 20) * 80)) + "%";
      $("tempSub").textContent = (level === "bad" ? "Too hot" : level === "warn" ? "Getting warm" : "Normal") + ` · limit ${max}°`;
    }

    renderHealth();
    renderApps();
    renderClaude();

    const timerChoice = data.timerMinutes > 0
      ? (data.timerMinutes >= 60 ? `${data.timerMinutes / 60} hour${data.timerMinutes === 60 ? "" : "s"}` : `${data.timerMinutes} minutes`)
      : "Never";
    if (!timerSaving) {
      $("timerRow").textContent = timerChoice;
      $("timerSelect").value = String(data.timerMinutes);
    }
    if (!limitSaving) {
      document.querySelector('[data-limit="battery"]').setAttribute("aria-checked", !!data.batteryGuard);
      document.querySelector('[data-limit="heat"]').setAttribute("aria-checked", !!data.heatGuard);
    }
    $("batteryLimitText").textContent = `Turns off below ${data.batteryLimit}% on battery`;
    $("heatLimitText").textContent = `Turns off above ${data.maxBatteryTemp}°C or if the Mac overheats`;
    const thermalText = { nominal: "Normal", fair: "Warm", serious: "Hot", critical: "Critical" }[data.thermalState] || "—";
    $("thermal").textContent = thermalText;
    $("thermal").className = "value " + (data.thermalState === "serious" || data.thermalState === "critical" ? "bad" : data.thermalState === "fair" ? "warn" : "");

    const noPermission = data.remoteScreen && data.screenPermission && !data.controlPermission;
    const controlOff = data.remoteScreen && data.screenPermission && data.remoteControl === false;
    const viewOnly = currentTab === "screen" && (noPermission || controlOff);
    $("hint").hidden = !viewOnly;
    $("hint").textContent = !viewOnly ? ""
      : controlOff ? "View only. To click and type from here, tick “Allow control from phone” in the Lid Awake menu on your Mac."
      : "View only. To control, allow Lid Awake in System Settings → Privacy & Security → Accessibility on your Mac.";
  }

  // ---------- Mac health and network ----------
  function setBar(id, pct, color) {
    $(id).style.width = Math.min(100, Math.max(3, pct)) + "%";
    $(id).style.background = color;
  }
  // Green is kept for "awake" and the battery; other bars are blue until they need attention.
  const level = (v, warn, bad) => v >= bad ? "var(--red)" : v >= warn ? "var(--amber)" : "var(--blue)";
  const GiB = 1024 ** 3;
  function fmtRate(bps) {
    if (bps == null) return "—";
    if (bps < 1000) return Math.round(bps) + " B/s";
    if (bps < 1e6) return Math.round(bps / 1000) + " KB/s";
    return (bps / 1e6).toFixed(1) + " MB/s";
  }
  function fmtUptime(sec) {
    const d = Math.floor(sec / 86400), h = Math.floor(sec % 86400 / 3600), m = Math.floor(sec % 3600 / 60);
    return d > 0 ? `${d} day${d === 1 ? "" : "s"}` : h > 0 ? `${h} h ${m} m` : `${m} min`;
  }
  let pingMs = null;
  function renderHealth() {
    const mac = data.mac || {}, net = data.net || {};
    if (mac.cpu != null) {
      $("cpuValue").textContent = Math.round(mac.cpu) + "%";
      setBar("cpuBar", mac.cpu, level(mac.cpu, 60, 85));
    }
    if (mac.memUsed != null && mac.memTotal) {
      const used = mac.memUsed / mac.memTotal * 100;
      $("memValue").textContent = `${(mac.memUsed / GiB).toFixed(0)} of ${Math.round(mac.memTotal / GiB)} GB`;
      setBar("memBar", used, level(used, 80, 92));
    }
    if (mac.diskFree != null && mac.diskTotal) {
      const usedPct = 100 - mac.diskFree / mac.diskTotal * 100;
      $("diskValue").textContent = Math.round(mac.diskFree / 1e9) + " GB free";
      setBar("diskBar", usedPct, level(usedPct, 85, 93));
    }
    if (mac.uptime != null) $("uptimeValue").textContent = fmtUptime(mac.uptime);

    // Signal: 4 bars ≥ −55 dBm, 3 ≥ −65, 2 ≥ −75, otherwise 1.
    const rssi = net.rssi;
    const bars = net.wired ? 4 : rssi == null ? 0 : rssi >= -55 ? 4 : rssi >= -65 ? 3 : rssi >= -75 ? 2 : 1;
    [...$("wifiBars").children].forEach((r, i) => r.classList.toggle("on", i < bars));
    $("netKind").textContent = net.wired ? "Ethernet" : "Wi-Fi";
    $("wifiName").textContent = net.wired ? "Connected" : rssi == null ? "Not connected"
      : net.wifi || ["", "Weak", "Fair", "Good", "Excellent"][bars] + " signal";
    $("wifiBars").style.display = net.wired ? "none" : "";
    $("netDown").textContent = fmtRate(net.down);
    $("netUp").textContent = fmtRate(net.up);
    $("netPing").textContent = pingMs == null ? "—" : Math.round(pingMs) + " ms";
    $("netPath").textContent = data.link || "—";
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
    if (auth.loopback) {
      // On the Mac's own 127.0.0.1 address: sign-in only happens at the Tailscale address.
      unlocked = false;
      $("app").hidden = true;
      $("lockBtn").hidden = true;
      $("gate").hidden = false;
      $("loginForm").hidden = true;
      $("gateTitle").textContent = "Open the Tailscale address";
      $("gateText").textContent = "For safety, Lid Awake only signs in at its private Tailscale address. ";
      if (auth.url) {
        const a = document.createElement("a");
        a.href = auth.url;
        a.textContent = "Open it";
        $("gateText").append(a);
      }
      return;
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
      requestAnimationFrame(() => movePill(false));
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
      $("faceIdBtn").hidden = !(auth.faceID && faceIDPossible());
      $("gateText").textContent = $("faceIdBtn").hidden ? "Enter your Lid Awake password." : "Use Face ID, or enter your Lid Awake password.";
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
        rememberPassword($("password").value);
        $("password").value = "";
        await checkAuth();
        offerFaceID();
      } else {
        $("loginError").textContent = await res.text();
        $("password").select();
      }
    } catch (err) {
      $("loginError").textContent = "Can't reach your Mac.";
    }
    $("unlockBtn").disabled = false;
  });

  // ---------- Face ID (passkeys) ----------
  // Works on the Tailscale https address. The iPhone keeps the key; the Mac only checks signatures.
  const b64u = buf => btoa(String.fromCharCode(...new Uint8Array(buf))).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
  const fromB64u = t => Uint8Array.from(atob(t.replace(/-/g, "+").replace(/_/g, "/") + "=".repeat((4 - t.length % 4) % 4)), ch => ch.charCodeAt(0));
  function faceIDPossible() { return location.protocol === "https:" && "PublicKeyCredential" in window; }
  function faceIDState() { try { return localStorage.getItem("faceid") || ""; } catch (e) { return ""; } }
  function setFaceIDState(v) { try { localStorage.setItem("faceid", v); } catch (e) {} }

  async function faceIDLogin() {
    $("loginError").textContent = "";
    try {
      const o = await (await fetch("passkey/login-options", { cache: "no-store" })).json();
      const cred = await navigator.credentials.get({ publicKey: {
        challenge: fromB64u(o.challenge), rpId: o.rpId, userVerification: "required", timeout: 60000,
      } });
      const r = cred.response;
      const res = await fetch("passkey/login", {
        method: "POST",
        headers: { "Content-Type": "application/json", "X-LidAwake": "1" },
        body: JSON.stringify({ id: b64u(cred.rawId), clientData: b64u(r.clientDataJSON),
                               authenticatorData: b64u(r.authenticatorData), signature: b64u(r.signature) }),
      });
      if (res.ok) return checkAuth();
      $("loginError").textContent = await res.text();
    } catch (e) {
      if (e.name !== "NotAllowedError") $("loginError").textContent = "Face ID didn't work. Use your password.";
    }
  }
  $("faceIdBtn").addEventListener("click", faceIDLogin);

  // The password typed at sign-in is kept in this page (never stored) for 3 minutes, so turning
  // on Face ID right after doesn't ask for it twice.
  let recentPassword = "", recentTimer = null;
  function rememberPassword(pw) {
    recentPassword = pw;
    clearTimeout(recentTimer);
    recentTimer = setTimeout(() => { recentPassword = ""; }, 180000);
  }
  function faceIDError(text) {
    $("faceIdError").textContent = text;
    $("faceIdError").hidden = !text;
  }

  async function faceIDSetup() {
    const pw = recentPassword || $("faceIdPassword").value;
    faceIDError("");
    if (!pw) {
      // Ask for the password first (in Settings).
      $("faceIdOffer").hidden = true;
      if (currentTab !== "settings") showTab("settings");
      $("faceIdPwRow").hidden = false;
      $("faceIdPassword").focus();
      return;
    }
    try {
      const optRes = await fetch("passkey/register-options", {
        method: "POST",
        headers: { "Content-Type": "application/json", "X-LidAwake": "1" },
        body: JSON.stringify({ password: pw }),
      });
      if (!optRes.ok) {
        recentPassword = "";
        $("faceIdPassword").value = "";
        throw new Error(await optRes.text());
      }
      const o = await optRes.json();
      const cred = await navigator.credentials.create({ publicKey: {
        challenge: fromB64u(o.challenge), rp: { name: "Lid Awake", id: o.rpId },
        // One account on every phone, so setting up again replaces rather than piles up keys.
        user: { id: new TextEncoder().encode("lid-awake-owner"), name: "Lid Awake", displayName: "Lid Awake" },
        excludeCredentials: (o.ids || []).map(id => ({ type: "public-key", id: fromB64u(id) })),
        pubKeyCredParams: [{ type: "public-key", alg: -7 }], attestation: "none", timeout: 60000,
        authenticatorSelection: { authenticatorAttachment: "platform", residentKey: "preferred", userVerification: "required" },
      } });
      const res = await fetch("passkey/register", {
        method: "POST",
        headers: { "Content-Type": "application/json", "X-LidAwake": "1" },
        body: JSON.stringify({ clientData: b64u(cred.response.clientDataJSON), attestation: b64u(cred.response.attestationObject) }),
      });
      if (!res.ok) throw new Error(await res.text());
      recentPassword = "";
      $("faceIdPassword").value = "";
      $("faceIdPwRow").hidden = true;
      setFaceIDState("on");
      $("faceIdOffer").hidden = true;
      renderFaceIDRow();
    } catch (e) {
      if (e.name === "InvalidStateError") {
        // This iPhone already has Face ID for Lid Awake.
        setFaceIDState("on");
        $("faceIdOffer").hidden = true;
        $("faceIdPwRow").hidden = true;
        return renderFaceIDRow();
      }
      if (e.name !== "NotAllowedError") {
        const msg = "Couldn't turn on Face ID: " + (e.message || e);
        if (currentTab === "settings") faceIDError(msg);
        else {
          $("faceIdOffer").hidden = false;
          $("faceIdOffer").querySelector(".faceid-text span").textContent = msg;
        }
      }
    }
  }
  $("faceIdPwRow").addEventListener("submit", e => { e.preventDefault(); faceIDSetup(); });

  // ---------- Signed-in devices and recent activity (Settings) ----------
  const dayFmt = new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric" });
  const timeFmt = new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" });
  async function loadDevices() {
    try {
      const { sessions } = await (await fetch("sessions", { cache: "no-store" })).json();
      const list = $("devicesList");
      list.replaceChildren();
      for (const d of sessions) {
        const row = document.createElement("div");
        row.className = "row device-row";
        const title = document.createElement("span");
        title.className = "title";
        title.textContent = d.current ? `${d.device} (this one)` : d.device;
        const small = document.createElement("small");
        small.textContent = `Signed in ${dayFmt.format(new Date(d.created * 1000))} with ${d.faceID ? "Face ID" : "the password"}`;
        title.append(small);
        row.append(title);
        if (!d.current) {
          const out = document.createElement("button");
          out.type = "button";
          out.className = "row-link danger";
          out.textContent = "Sign out";
          out.addEventListener("click", () => signOutDevices({ id: d.id }));
          row.append(out);
        }
        list.append(row);
      }
      if (sessions.filter(d => !d.current).length > 1) {
        const row = document.createElement("div");
        row.className = "row";
        const all = document.createElement("button");
        all.type = "button";
        all.className = "row-link start danger";
        all.textContent = "Sign out all other devices";
        all.addEventListener("click", () => signOutDevices({ others: true }));
        row.append(all);
        list.append(row);
      }
      $("devicesSection").hidden = sessions.length === 0;
    } catch (e) {}
  }
  async function signOutDevices(body) {
    await fetch("sessions/signout", {
      method: "POST", headers: { "Content-Type": "application/json", "X-LidAwake": "1" }, body: JSON.stringify(body),
    }).catch(() => {});
    loadDevices();
    loadActivity();
  }
  async function loadActivity() {
    try {
      const { lines } = await (await fetch("activity", { cache: "no-store" })).json();
      const list = $("activityList");
      list.replaceChildren();
      for (const line of lines) {
        const space = line.indexOf("  ");
        const when = new Date(line.slice(0, space));
        const row = document.createElement("div");
        row.className = "row";
        const what = document.createElement("span");
        what.className = "what";
        what.textContent = line.slice(space + 2);
        const time = document.createElement("span");
        time.className = "when";
        time.textContent = isNaN(when) ? "" : timeFmt.format(when);
        row.append(what, time);
        list.append(row);
      }
      $("activitySection").hidden = lines.length === 0;
    } catch (e) {}
  }

  // After a password sign-in, offer it once (and keep a "Set up" row in settings if declined).
  function offerFaceID() {
    renderFaceIDRow();
    if (!faceIDPossible() || faceIDState() === "on") return;
    if (faceIDState() !== "declined") $("faceIdOffer").hidden = false;
  }
  function renderFaceIDRow() {
    const on = faceIDState() === "on";
    $("faceIdRow").hidden = !faceIDPossible();
    $("faceIdState").hidden = !on;
    $("faceIdRowBtn").hidden = on;
  }
  renderFaceIDRow();
  $("lockNowBtn").addEventListener("click", () => $("lockBtn").click());
  $("settingsBtn").addEventListener("click", () => { showTab("settings"); $("scroller").scrollTo(0, 0); loadDevices(); loadActivity(); });
  $("settingsBack").addEventListener("click", () => { showTab("status"); $("scroller").scrollTo(0, 0); });
  $("faceIdOn").addEventListener("click", faceIDSetup);
  $("faceIdRowBtn").addEventListener("click", faceIDSetup);
  $("faceIdLater").addEventListener("click", () => { setFaceIDState("declined"); $("faceIdOffer").hidden = true; });

  $("lockBtn").addEventListener("click", async () => {
    stopLive();
    await fetch("logout", { method: "POST", headers: { "X-LidAwake": "1" } }).catch(() => {});
    unlocked = false;
    checkAuth();
  });

  async function poll() {
    if (!unlocked) return;
    try {
      const sentAt = performance.now();
      const res = await fetch("status", { cache: "no-store" });
      if (res.status === 401 || res.status === 423) return locked();
      if (!res.ok) throw new Error(res.status);
      const rtt = performance.now() - sentAt;
      pingMs = pingMs == null ? rtt : pingMs * 0.7 + rtt * 0.3;
      data = await res.json();
      fetchedAt = Date.now();
      lastOk = fetchedAt;
    } catch (e) { /* keep showing the last data, marked stale */ }
    render();
  }

  // ---------- Tabs ----------
  const tabButtons = document.querySelectorAll(".tabs button");
  // The highlight is a drop of glass: it stretches across from the old tab to the new one, then
  // settles with a little wobble. It just jumps the first time, on resize, and with Reduce Motion.
  const reduceMotion = matchMedia("(prefers-reduced-motion: reduce)");
  let pillAt = null;  // { x, w }: where the pill sits now
  function movePill(animate) {
    const active = document.querySelector(".tabs button.active");
    if (!active) return;
    const pill = $("tabPill");
    const to = { x: active.offsetLeft, w: active.offsetWidth }, from = pillAt;
    pillAt = to;
    pill.style.width = to.w + "px";
    pill.style.transform = `translateX(${to.x}px)`;
    if (!animate || !from || from.x === to.x || reduceMotion.matches || !pill.animate) return;
    const left = Math.min(from.x, to.x), span = Math.abs(to.x - from.x) + Math.max(from.w, to.w);
    pill.getAnimations().forEach(a => a.cancel());
    pill.classList.add("moving");
    const glide = pill.animate([
      { transform: `translateX(${from.x}px) scale(1, 1)`, width: from.w + "px" },
      { transform: `translateX(${left}px) scale(1, .88)`, width: span + "px", offset: .42 },
      { transform: `translateX(${to.x}px) scale(1.07, .95)`, width: to.w + "px", offset: .72 },
      { transform: `translateX(${to.x}px) scale(.98, 1.02)`, width: to.w + "px", offset: .88 },
      { transform: `translateX(${to.x}px) scale(1, 1)`, width: to.w + "px" },
    ], { duration: 600, easing: "cubic-bezier(.32,.72,0,1)" });
    glide.onfinish = glide.oncancel = () => pill.classList.remove("moving");
  }
  let pillPlaced = false;
  addEventListener("resize", () => movePill(false));
  const tabOrder = ["status", "screen", "apps", "settings"];
  function showTab(name) {
    const changed = currentTab !== name;
    const forward = tabOrder.indexOf(name) >= tabOrder.indexOf(currentTab);
    currentTab = name;
    tabButtons.forEach(b => b.classList.toggle("active", b.dataset.tab === (name === "settings" ? "status" : name)));
    requestAnimationFrame(() => { movePill(pillPlaced); pillPlaced = true; });
    const page = $("tab-" + name);
    if (changed && page && pillPlaced) {
      page.classList.remove("tab-enter-left", "tab-enter-right");
      void page.offsetWidth;  // restart the animation
      page.classList.add(forward ? "tab-enter-right" : "tab-enter-left");
      const button = document.querySelector(`.tabs button[data-tab="${name}"]`);
      if (button && !reduceMotion.matches) {
        button.classList.remove("bounce");
        void button.offsetWidth;
        button.classList.add("bounce");
      }
    }
    $("settingsBack").hidden = name !== "settings";
    $("live").hidden = name === "settings";
    $("tab-status").hidden = name !== "status";
    $("tab-screen").hidden = name !== "screen";
    $("tab-apps").hidden = name !== "apps";
    $("tab-settings").hidden = name !== "settings";
    if (name !== "apps" && editingApps) setEditing(false);
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
  const QUALITY_BADGE = { max: "MAX", sharp: "HD", balanced: "SD", fast: "LOW" };
  const QUALITY_NAME = { max: "Max", sharp: "Sharp", balanced: "Balanced", fast: "Fast" };
  // Slider: one footnote per mode.
  const Q_ORDER = ["max", "sharp", "balanced", "fast"];
  const Q_LAST = Q_ORDER.length - 1;
  const Q_CAPTION = {
    max: "The Mac's full sharpness, best when you zoom in. Uses the most data.",
    sharp: "Clearest text, up to 20 frames a second.",
    balanced: "Clear and smooth, for everyday use.",
    fast: "Smoothest and lightest on data, for mobile networks.",
  };
  let shownQuality = null;
  function placeKnob(fraction, animate) {
    const f = Math.min(1, Math.max(0, fraction)) * 100 + "%";
    $("qsKnob").classList.toggle("dragging", !animate);
    $("qsFill").classList.toggle("dragging", !animate);
    $("qsKnob").style.left = f;
    $("qsFill").style.width = f;
  }
  function showSlider(q) {
    placeKnob(Q_ORDER.indexOf(q) / Q_LAST, true);
    document.querySelectorAll("[data-q]").forEach(b => b.classList.toggle("active", b.dataset.q === q));
    $("qsTrack").setAttribute("aria-valuenow", Q_ORDER.indexOf(q));
    $("qsTrack").setAttribute("aria-valuetext", QUALITY_NAME[q]);
    if (shownQuality === q) return;
    // Cross-fade the value and the footnote.
    const first = shownQuality === null;
    $("qsValue").classList.add("fade");
    $("qsCaption").classList.add("fade");
    setTimeout(() => {
      $("qsValue").textContent = QUALITY_NAME[q];
      $("qsCaption").textContent = Q_CAPTION[q];
      $("qsValue").classList.remove("fade");
      $("qsCaption").classList.remove("fade");
    }, first ? 0 : 180);
    shownQuality = q;
  }
  function showQuality() {
    showSlider(quality);
    for (const id of ["qualityFloat", "qualityBtn"]) {
      $(id).firstElementChild.textContent = QUALITY_BADGE[quality];
      $(id).setAttribute("aria-label", "Picture quality: " + QUALITY_NAME[quality]);
    }
  }
  function setQuality(q) {
    quality = q;
    try { localStorage.setItem("quality", quality); } catch (e) {}
    showQuality();
    if (ws && ws.readyState === 1) ws.send(JSON.stringify({ quality }));
  }
  document.querySelectorAll("[data-q]").forEach(b => b.addEventListener("click", () => setQuality(b.dataset.q)));
  // Drag or tap the track: the knob follows the finger, then springs to the nearest stop.
  (() => {
    const track = $("qsTrack");
    let dragging = false;
    const fractionAt = e => { const r = track.getBoundingClientRect(); return (e.clientX - r.left) / r.width; };
    const preview = f => {
      const q = Q_ORDER[Math.round(Math.min(1, Math.max(0, f)) * Q_LAST)];
      document.querySelectorAll("[data-q]").forEach(b => b.classList.toggle("active", b.dataset.q === q));
    };
    track.addEventListener("pointerdown", e => {
      dragging = true;
      track.setPointerCapture(e.pointerId);
      placeKnob(fractionAt(e), false);
      preview(fractionAt(e));
    });
    track.addEventListener("pointermove", e => {
      if (!dragging) return;
      placeKnob(fractionAt(e), false);
      preview(fractionAt(e));
    });
    const release = e => {
      if (!dragging) return;
      dragging = false;
      const q = Q_ORDER[Math.round(Math.min(1, Math.max(0, fractionAt(e))) * Q_LAST)];
      if (q === quality) showSlider(q); else setQuality(q);
    };
    track.addEventListener("pointerup", release);
    track.addEventListener("pointercancel", release);
    track.addEventListener("keydown", e => {
      const i = Q_ORDER.indexOf(quality);
      if (e.key === "ArrowLeft" && i > 0) { e.preventDefault(); setQuality(Q_ORDER[i - 1]); }
      if (e.key === "ArrowRight" && i < Q_LAST) { e.preventDefault(); setQuality(Q_ORDER[i + 1]); }
    });
  })();
  // Landscape toolbar: one badge that steps LOW → SD → HD → LOW.
  $("qualityFloat").addEventListener("click", () => setQuality({ fast: "balanced", balanced: "sharp", sharp: "max", max: "fast" }[quality]));
  showQuality();

  function liveURL() {
    return location.protocol === "https:" ? `wss://${location.host}/live` : `ws://${location.hostname}:8788/live`;
  }
  function showFrame(blob) {
    if (!$("screenCanvas").hidden) { $("screenCanvas").hidden = true; $("screenImg").hidden = false; }
    const url = URL.createObjectURL(blob);
    const img = $("screenImg");
    img.onload = () => { if (frameUrl && frameUrl !== url) URL.revokeObjectURL(frameUrl); frameUrl = url; };
    img.src = url;
    $("screenControls").hidden = false;
    setScreenMsg("");
  }
  // ----- Video: H.264 from the Mac's video encoder, played by the phone's own decoder -----
  // Much smaller than JPEGs while things move. If the phone can't play it, the page asks the
  // Mac for JPEGs again (for the rest of this visit).
  let decoder = null, videoConfigured = false, videoBacklog = [], videoBroken = false;
  let videoNeedsKey = true, videoRestarts = [];
  const canvas = $("screenCanvas");
  const canvasCtx = canvas.getContext("2d", { alpha: false, desynchronized: true });
  function canPlayVideo() { return "VideoDecoder" in window && !videoBroken; }
  let videoProblem = "";
  // For the Mac's stream log: why this phone isn't playing video.
  function videoWhy() {
    return !("VideoDecoder" in window) ? "no VideoDecoder in this browser: " + navigator.userAgent : videoProblem;
  }
  async function setupVideo(v) {
    videoConfigured = false;
    const config = {
      codec: v.codec, codedWidth: v.width, codedHeight: v.height, optimizeForLatency: true,
      description: Uint8Array.from(atob(v.description), c => c.charCodeAt(0)),
    };
    try {
      if (!(await VideoDecoder.isConfigSupported(config)).supported) throw new Error("This phone can't play the video");
      if (!decoder || decoder.state === "closed") decoder = new VideoDecoder({ output: drawVideo, error: videoFailed });
      decoder.configure(config);
      videoConfigured = true;
      const waiting = videoBacklog;
      videoBacklog = [];
      waiting.forEach(decodeVideo);
    } catch (e) { videoFailed(e); }
  }
  function decodeVideo(bytes) {
    if (videoBroken) return;
    // After a (re)start, frames only make sense from a keyframe on.
    if (videoNeedsKey) { if (bytes[0] !== 1) return; videoNeedsKey = false; }
    if (!videoConfigured) { videoBacklog.push(bytes); return; }
    try {
      decoder.decode(new EncodedVideoChunk({
        type: bytes[0] === 1 ? "key" : "delta",
        timestamp: Number(new DataView(bytes.buffer, bytes.byteOffset + 1, 8).getBigInt64(0)),
        data: bytes.subarray(9),
      }));
    } catch (e) { videoFailed(e); }
  }
  function drawVideo(frame) {
    if (canvas.width !== frame.displayWidth || canvas.height !== frame.displayHeight) {
      canvas.width = frame.displayWidth; canvas.height = frame.displayHeight;
    }
    canvasCtx.drawImage(frame, 0, 0);
    frame.close();
    if (canvas.hidden) { canvas.hidden = false; $("screenImg").hidden = true; }
    $("screenControls").hidden = false;
    setScreenMsg("");
  }
  function videoFailed(e) {
    if (videoBroken) return;
    videoProblem = String(e && e.message || e);
    try { decoder && decoder.state !== "closed" && decoder.close(); } catch (err) {}
    decoder = null; videoConfigured = false; videoBacklog = []; videoNeedsKey = true;
    // iPhones sometimes reset the decoder (e.g. after the page was in the background):
    // ask the Mac for a fresh start, up to 3 times a minute, before falling back to JPEGs.
    const now = Date.now();
    videoRestarts = videoRestarts.filter(t => now - t < 60000);
    if (videoRestarts.length < 3 && ws && ws.readyState === 1) {
      videoRestarts.push(now);
      ws.send(JSON.stringify({ videoRestart: true, why: videoProblem }));
      return;
    }
    videoBroken = true;
    try { decoder && decoder.state !== "closed" && decoder.close(); } catch (err) {}
    if (ws && ws.readyState === 1) ws.send(JSON.stringify({ video: false, why: videoProblem }));
  }

  function watching() { return unlocked && currentTab === "screen" && !document.hidden && !streamPaused; }

  // ----- Pause when nobody is using it -----
  // The page keeps the iPhone awake, so an untouched Screen tab would stream forever. After 3
  // minutes without a touch or key, stop the stream and let the iPhone dim; Continue resumes.
  let streamPaused = false, lastScreenUse = Date.now();
  const PAUSE_AFTER = 3 * 60 * 1000;
  for (const type of ["pointerdown", "keydown", "wheel"]) addEventListener(type, () => {
    lastScreenUse = Date.now();
    // Any touch on the Screen tab (picture, trackpad, keys) picks up where it left off.
    if (streamPaused && currentTab === "screen") resumeStream();
  }, true);
  setInterval(() => {
    if (streamPaused || currentTab !== "screen" || !unlocked || document.hidden) return;
    if (Date.now() - lastScreenUse > PAUSE_AFTER) pauseStream();
  }, 10000);
  function pauseStream() {
    streamPaused = true;
    stopLive();
    $("pausedOverlay").hidden = false;
    if (wakeLock) { wakeLock.release().catch(() => {}); wakeLock = null; }
  }
  function resumeStream() {
    if (!streamPaused) return;
    streamPaused = false;
    lastScreenUse = Date.now();
    $("pausedOverlay").hidden = true;
    startFrames();
    keepPhoneAwake();
  }
  $("resumeBtn").addEventListener("click", resumeStream);
  function stopLive() { if (ws) { ws.onclose = null; ws.close(); ws = null; } }

  // Opens the socket; after 3 failed tries falls back to long-polling and retries the socket every 30 s.
  function startFrames() {
    if (!watching()) { stopLive(); return; }
    if (ws || framesRunning) return;
    if (wsFailures >= 3 && Date.now() < wsRetryAt) { startPolling(); return; }
    let opened = false;
    const socket = new WebSocket(liveURL());
    socket.binaryType = "arraybuffer";
    ws = socket;
    socket.onopen = () => {
      opened = true; wsFailures = 0;
      videoConfigured = false; videoBacklog = []; videoNeedsKey = true;
      socket.send(JSON.stringify({ quality, acks: true, video: canPlayVideo(), why: videoWhy() }));
      pumpInput();
    };
    socket.onmessage = e => {
      if (typeof e.data !== "string") {
        // Confirm each frame so the Mac never queues up more than the network can carry.
        if (socket.readyState === 1) socket.send('{"ack":1}');
        const bytes = new Uint8Array(e.data);
        // JPEGs start with 0xFF; video frames with 1 (keyframe) or 2.
        return bytes[0] === 0xFF ? showFrame(new Blob([bytes], { type: "image/jpeg" })) : decodeVideo(bytes);
      }
      let m;
      try { m = JSON.parse(e.data); } catch (err) { return; }
      if (m.video) setupVideo(m.video);
      if (m.error) setScreenMsg(m.error);
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
    const video = !canvas.hidden, img = video ? canvas : $("screenImg"), r = img.getBoundingClientRect();
    const nw = (video ? canvas.width : img.naturalWidth) || r.width, nh = (video ? canvas.height : img.naturalHeight) || r.height;
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
  let shownZoom = 1, zoomFadeTimer = null;
  function applyZoom() {
    const w = wrap.clientWidth, h = wrap.clientHeight;
    zoom = Math.min(5, Math.max(1, zoom));
    // Keep the picture covering the frame.
    panX = Math.min(0, Math.max(w - w * zoom, panX));
    panY = Math.min(0, Math.max(h - h * zoom, panY));
    $("screenImg").style.transform = $("screenCanvas").style.transform =
      zoom === 1 ? "" : `translate(${panX}px, ${panY}px) scale(${zoom})`;
    $("zoomBar").hidden = zoom === 1;
    $("zoomLevel").textContent = zoom.toFixed(1) + "×";
    // Sideways, the zoom bubble fades after 2.5 s without a zoom change (panning doesn't count)
    // and comes back with the next pinch.
    if (Math.abs(zoom - shownZoom) > 0.01) {
      shownZoom = zoom;
      $("zoomBar").classList.remove("faded");
      clearTimeout(zoomFadeTimer);
      zoomFadeTimer = setTimeout(() => $("zoomBar").classList.add("faded"), 2500);
    }
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
    $("kbLabel").textContent = "Typing";
    setKeysOpen(false);
  });
  liveInput.addEventListener("blur", () => {
    $("kbFloat").classList.remove("on");
    $("kbBtn").classList.remove("on");
    $("kbLabel").textContent = "Type";
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
    if (!confirm("Lock your Mac?\n\nTo unlock it from here, tap Type directly, type your Mac password and press Return.")) return;
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
    document.body.classList.toggle("apps-full", landscapeQuery.matches && currentTab === "apps" && unlocked);
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

  // ---------- Screen tools: Keys sheet, picture quality, help ----------
  function setKeysOpen(open) {
    $("keySheet").classList.toggle("open", open);
    $("keySheet").setAttribute("aria-hidden", !open);
    $("keysBtn").setAttribute("aria-expanded", open);
  }
  $("keysBtn").addEventListener("click", () => setKeysOpen(!$("keySheet").classList.contains("open")));
  $("keysDone").addEventListener("click", () => setKeysOpen(false));
  // Quality and help open in place under the tools; one at a time.
  function togglePanel(btn, panel) {
    const open = panel.hidden;
    for (const [b, p] of [["qualityBtn", "qualityPanel"], ["helpBtn", "helpPanel"]]) {
      $(p).hidden = true; $(b).setAttribute("aria-expanded", "false");
    }
    panel.hidden = !open;
    btn.setAttribute("aria-expanded", open);
  }
  $("qualityBtn").addEventListener("click", () => togglePanel($("qualityBtn"), $("qualityPanel")));
  $("helpBtn").addEventListener("click", () => togglePanel($("helpBtn"), $("helpPanel")));

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

  // ---------- Claude plan usage ----------
  // Numbers come from Claude Code after each reply, so show how old they are.
  function renderClaude() {
    const c = data.claude;
    $("claudeSection").hidden = !c || (!c.fiveHour && !c.sevenDay);
    if ($("claudeSection").hidden) return;
    const now = Date.now() / 1000;
    const when = (t, long) => {
      const d = new Date(t * 1000), time = d.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
      return long && d.toDateString() !== new Date().toDateString() ? d.toLocaleDateString([], { weekday: "short" }) + " " + time : time;
    };
    const show = (w, id, long) => {
      if (!w) { $(id + "Pct").textContent = "—"; $(id + "Sub").textContent = ""; setBar(id + "Bar", 0, "var(--green)"); return; }
      const over = w.resetsAt && w.resetsAt <= now;  // the window ended since the last reply: it starts again from 0
      const pct = over ? 0 : Math.round(w.pct);
      $(id + "Pct").textContent = pct + "%";
      $(id + "Pct").className = "usage-pct " + (pct >= 90 ? "bad" : pct >= 75 ? "warn" : "");
      setBar(id + "Bar", pct, pct >= 90 ? "var(--red)" : pct >= 75 ? "var(--amber)" : "var(--blue)");
      $(id + "Sub").textContent = over ? "Reset — fresh limit" : `Resets ${when(w.resetsAt, long)}`;
    };
    show(c.fiveHour, "claude5h", false);
    show(c.sevenDay, "claudeWeek", true);
    const ago = Math.max(0, Math.round((now - c.updated) / 60));
    $("claudeFoot").textContent = "Updated " +
      (ago < 1 ? "just now." : ago < 60 ? `${ago} min ago.` : ago < 1440 ? `${Math.round(ago / 60)} h ago.` : "over a day ago.");
  }

  // ---------- Apps ----------
  // The grid is rebuilt only when the list changes, so icons aren't downloaded again.
  let appsKey = null, openedTimer = null;
  function renderApps() {
    const apps = data.apps || [];
    const key = apps.map(a => a.id).join();
    if (key !== appsKey && !editingApps) {
      appsKey = key;
      const grid = $("appGrid");
      grid.replaceChildren(...apps.map(a => {
        const b = document.createElement("button");
        b.type = "button";
        b.className = "app";
        b.dataset.id = a.id;
        const img = document.createElement("img");
        img.src = "app-icon.png?id=" + encodeURIComponent(a.id);
        img.alt = "";
        const name = document.createElement("span");
        name.className = "name";
        name.textContent = a.name;
        const run = document.createElement("span");
        run.className = "run";
        const quit = document.createElement("span");
        quit.className = "quit";
        quit.setAttribute("role", "button");
        quit.setAttribute("aria-label", "Quit " + a.name);
        quit.innerHTML = '<svg viewBox="0 0 10 10" aria-hidden="true"><path d="M1.5 1.5l7 7M8.5 1.5l-7 7"/></svg>';
        b.append(quit, img, name, run);
        b.setAttribute("aria-label", "Open " + a.name);
        b.addEventListener("click", () => { if (!editingApps) openApp(a, b); });
        return b;
      }));
    }
    for (const a of apps) {
      const b = $("appGrid").querySelector(`[data-id="${a.id}"]`);
      if (b) b.classList.toggle("running", a.running);
    }
    // Sideways: balanced rows (1 row up to 5 apps, 2 up to 12, then 3).
    const rows = apps.length <= 5 ? 1 : apps.length <= 12 ? 2 : 3;
    $("appGrid").style.setProperty("--cols", Math.max(1, Math.ceil(apps.length / rows)));
    sizeSidewaysIcons(apps.length, rows);
    $("appsEmpty").hidden = apps.length > 0;
    $("appsFoot").hidden = apps.length === 0;
  }
  async function openApp(app, button) {
    button.classList.add("opening");
    let message = `Opened ${app.name}`;
    try {
      const res = await fetch("open-app", {
        method: "POST",
        headers: { "Content-Type": "application/json", "X-LidAwake": "1" },
        body: JSON.stringify({ id: app.id }),
      });
      if (res.status === 401 || res.status === 423) return locked();
      if (!res.ok) message = await res.text();
    } catch (e) { message = "Can't reach your Mac."; }
    setTimeout(() => button.classList.remove("opening"), 400);
    if (message.startsWith("Opened")) {
      button.classList.remove("launched");
      void button.offsetWidth;
      button.classList.add("launched");
    }
    $("openedText").textContent = message;
    $("openedBar").hidden = false;
    clearTimeout(openedTimer);
    openedTimer = setTimeout(() => { $("openedBar").hidden = true; }, 5000);
    poll();
  }
  $("openedShow").addEventListener("click", () => { $("openedBar").hidden = true; showTab("screen"); });

  // Sideways: make the icons as big as the black screen allows (up to 130 px) with every app showing.
  // Gaps scale with the icon: 0.36× between columns, 0.3× between rows, plus room for the open dot.
  function sizeSidewaysIcons(count, rows) {
    const box = $("tab-apps");
    if (!document.body.classList.contains("apps-full") || !count) { box.style.removeProperty("--icon"); return; }
    const cs = getComputedStyle(box);
    const w = box.clientWidth - parseFloat(cs.paddingLeft) - parseFloat(cs.paddingRight) - 16;
    const h = box.clientHeight - parseFloat(cs.paddingTop) - parseFloat(cs.paddingBottom) - 24;
    const cols = Math.ceil(count / rows);
    const icon = Math.min(130, w / (cols + (cols - 1) * 0.36), h / (rows + (rows - 1) * 0.3 + 0.15 * rows));
    box.style.setProperty("--icon", Math.floor(icon) + "px");
  }
  addEventListener("resize", () => { if (data) renderApps(); });

  // ---------- Apps: hold an icon to rearrange ----------
  let editingApps = false, appHoldTimer = null, drag = null;
  const appGrid = $("appGrid"), APPS_FOOT = $("appsFoot").textContent;
  const SPRING = "cubic-bezier(.32,.72,0,1)";
  function setEditing(on) {
    editingApps = on;
    appGrid.classList.toggle("editing", on);
    $("appsDone").hidden = !on;
    $("appsFoot").textContent = on ? "Drag an app to move it. Tap × to quit an open app. Tap Done when you're finished." : APPS_FOOT;
    if (!on && data) renderApps();
  }
  $("appsDone").addEventListener("click", () => setEditing(false));
  // Tapping empty space also ends rearranging, as on the iPhone home screen.
  $("tab-apps").addEventListener("click", e => {
    if (editingApps && !e.target.closest(".app, .done-btn")) setEditing(false);
  });
  appGrid.addEventListener("contextmenu", e => e.preventDefault());
  // Keep the page from scrolling while an icon is being dragged.
  document.addEventListener("touchmove", e => { if (drag) e.preventDefault(); }, { passive: false });

  appGrid.addEventListener("pointerdown", e => {
    const app = e.target.closest(".app");
    if (!app) return;
    if (editingApps && e.target.closest(".quit")) return quitApp(app);
    if (editingApps) return startDrag(app, e.pointerId, e.clientX, e.clientY);
    // Not rearranging yet: a hold of about half a second starts it (moving the finger cancels).
    const start = { x: e.clientX, y: e.clientY, id: e.pointerId };
    const stop = () => { clearTimeout(appHoldTimer); ["pointermove", "pointerup", "pointercancel"].forEach(t => removeEventListener(t, watch)); };
    const watch = ev => {
      if (ev.pointerId !== start.id) return;
      if (ev.type === "pointermove") {
        if (Math.hypot(ev.clientX - start.x, ev.clientY - start.y) > 10) stop();
        else { start.x = ev.clientX; start.y = ev.clientY; }
      } else stop();
    };
    ["pointermove", "pointerup", "pointercancel"].forEach(t => addEventListener(t, watch));
    appHoldTimer = setTimeout(() => {
      stop();
      setEditing(true);
      startDrag(app, start.id, start.x, start.y);
    }, 450);
  });

  function startDrag(app, pointerId, x, y) {
    const g = appGrid.getBoundingClientRect();
    drag = { app, id: pointerId, grabX: x - g.left - app.offsetLeft, grabY: y - g.top - app.offsetTop, moved: false };
    app.classList.add("dragging");
    try { appGrid.setPointerCapture(pointerId); } catch (e) {}
  }
  appGrid.addEventListener("pointermove", e => {
    if (!drag || e.pointerId !== drag.id) return;
    drag.moved = true;
    const g = appGrid.getBoundingClientRect();
    const gx = e.clientX - g.left, gy = e.clientY - g.top;
    // Which slot is the finger over? Layout positions ignore animations, so this doesn't flicker.
    const over = [...appGrid.children].find(el => gx >= el.offsetLeft && gx < el.offsetLeft + el.offsetWidth
                                               && gy >= el.offsetTop && gy < el.offsetTop + el.offsetHeight);
    if (over && over !== drag.app) moveApp(drag.app, over);
    drag.app.style.translate = `${gx - drag.grabX - drag.app.offsetLeft}px ${gy - drag.grabY - drag.app.offsetTop}px`;
  });
  // Move `app` into `over`'s place; the others slide to their new spots.
  function moveApp(app, over) {
    const items = [...appGrid.children];
    const before = new Map(items.map(el => [el, { x: el.offsetLeft, y: el.offsetTop }]));
    appGrid.insertBefore(app, items.indexOf(app) < items.indexOf(over) ? over.nextSibling : over);
    for (const el of items) {
      if (el === app) continue;
      const b = before.get(el), dx = b.x - el.offsetLeft, dy = b.y - el.offsetTop;
      if (dx || dy) el.animate([{ transform: `translate(${dx}px, ${dy}px)` }, { transform: "none" }], { duration: 320, easing: SPRING });
    }
  }
  function endDrag(e) {
    if (!drag || e.pointerId !== drag.id) return;
    const { app, moved } = drag;
    drag = null;
    const from = app.style.translate;
    app.classList.remove("dragging");
    app.style.translate = "";
    if (from) app.animate([{ translate: from }, { translate: "0px 0px" }], { duration: 320, easing: SPRING });
    if (moved) saveAppOrder();
  }
  appGrid.addEventListener("pointerup", endDrag);
  appGrid.addEventListener("pointercancel", endDrag);
  async function quitApp(button) {
    const app = (data.apps || []).find(a => a.id === button.dataset.id);
    if (!app) return;
    button.classList.remove("running");
    let message = `Asked ${app.name} to quit`;
    try {
      const res = await fetch("quit-app", {
        method: "POST",
        headers: { "Content-Type": "application/json", "X-LidAwake": "1" },
        body: JSON.stringify({ id: app.id }),
      });
      if (res.status === 401 || res.status === 423) return locked();
      if (!res.ok) message = await res.text();
    } catch (e) { message = "Can't reach your Mac."; }
    $("openedText").textContent = message;
    $("openedBar").hidden = false;
    clearTimeout(openedTimer);
    openedTimer = setTimeout(() => { $("openedBar").hidden = true; }, 5000);
    // Apps can take a moment to close (or stop to ask about saving), so check again shortly.
    setTimeout(poll, 1200);
  }
  async function saveAppOrder() {
    const ids = [...appGrid.children].map(el => el.dataset.id);
    appsKey = ids.join();
    // Match the last status too, so nothing redraws the old order before the next update.
    if (data && data.apps) data.apps.sort((a, b) => ids.indexOf(a.id) - ids.indexOf(b.id));
    try {
      const res = await fetch("apps-order", {
        method: "POST",
        headers: { "Content-Type": "application/json", "X-LidAwake": "1" },
        body: JSON.stringify({ ids }),
      });
      if (res.status === 401 || res.status === 423) return locked();
    } catch (e) {}
  }

  // ---------- iPhone alerts (Web Push) ----------
  // Works from the Home Screen icon. The Mac encrypts each alert for this iPhone only.
  const ALERT_KINDS = ["turnedOff", "lowBattery", "claudeUsage", "warm"];
  const standalone = () => matchMedia("(display-mode: standalone)").matches || navigator.standalone === true;
  const pushPossible = () => "serviceWorker" in navigator && "PushManager" in window && "Notification" in window;
  function alertsOff() { try { return JSON.parse(localStorage.getItem("alertsOff") || "[]"); } catch (e) { return []; } }
  async function currentSubscription() {
    if (!pushPossible()) return null;
    const reg = await navigator.serviceWorker.getRegistration("/");
    return reg ? reg.pushManager.getSubscription() : null;
  }
  let alertsReady = false;  // this iPhone is subscribed and allowed notifications
  async function renderAlerts() {
    const sub = await currentSubscription().catch(() => null);
    alertsReady = !!sub && Notification.permission === "granted";
    const off = alertsOff();
    document.querySelectorAll("[data-alert]").forEach(b =>
      b.setAttribute("aria-checked", alertsReady && !off.includes(b.dataset.alert)));
    const possible = pushPossible() && standalone();
    $("alertsSection").classList.toggle("unavailable", !possible);
    const note = !possible ? "To use alerts, open Lid Awake from its Home Screen icon (in Safari: Share → Add to Home Screen)."
      : Notification.permission === "denied" ? "Notifications are blocked. Allow them in iPhone Settings → Notifications → Lid Awake."
      : "";
    $("alertsFoot").textContent = note;
    $("alertsFoot").hidden = !note;
  }
  async function postJSON(url, body) {
    const res = await fetch(url, { method: "POST", headers: { "Content-Type": "application/json", "X-LidAwake": "1" }, body: JSON.stringify(body) });
    if (!res.ok) throw new Error(await res.text());
  }
  async function subscribeAlerts() {
    if (await Notification.requestPermission() !== "granted") return false;
    const reg = await navigator.serviceWorker.register("/sw.js", { scope: "/" });
    await navigator.serviceWorker.ready;
    const { key } = await (await fetch("push/key", { cache: "no-store" })).json();
    const sub = await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: fromB64u(key) });
    await postJSON("push/subscribe", { ...sub.toJSON(), off: alertsOff() });
    return true;
  }
  function saveAlertsOff(off) { try { localStorage.setItem("alertsOff", JSON.stringify(off)); } catch (e) {} }
  document.querySelectorAll("[data-alert]").forEach(b => b.addEventListener("click", async () => {
    if (!pushPossible() || !standalone()) return renderAlerts();
    const kind = b.dataset.alert, wasOn = b.getAttribute("aria-checked") === "true";
    try {
      if (!alertsReady) {
        // First alert turned on: ask the iPhone for permission and subscribe, with just this one on.
        saveAlertsOff(ALERT_KINDS.filter(k => k !== kind));
        if (!(await subscribeAlerts())) saveAlertsOff([]);
      } else {
        const off = wasOn ? [...new Set([...alertsOff(), kind])] : alertsOff().filter(k => k !== kind);
        saveAlertsOff(off);
        const sub = await currentSubscription();
        if (off.length === ALERT_KINDS.length) {
          // Everything off: stop alerts on this iPhone altogether.
          await postJSON("push/settings", { endpoint: sub.endpoint, enabled: false }).catch(() => {});
          await sub.unsubscribe().catch(() => {});
          saveAlertsOff([]);
        } else {
          await postJSON("push/settings", { endpoint: sub.endpoint, off });
        }
      }
    } catch (e) {
      $("alertsFoot").textContent = "Couldn't change alerts: " + (e.message || e);
      $("alertsFoot").hidden = false;
      return;
    }
    renderAlerts();
  }));
  renderAlerts();

  // ---------- Safety limits (same as the Mac menu) ----------
  let limitSaving = false;
  document.querySelectorAll("[data-limit]").forEach(b => b.addEventListener("click", async () => {
    const on = b.getAttribute("aria-checked") !== "true";
    b.setAttribute("aria-checked", on);
    limitSaving = true;
    try { await postJSON("limits", { name: b.dataset.limit, on }); }
    catch (e) { b.setAttribute("aria-checked", !on); }
    limitSaving = false;
    poll();
  }));

  // ---------- Appearance ----------
  function savedTheme() { try { return localStorage.getItem("theme") || "auto"; } catch (e) { return "auto"; } }
  function applyTheme(choice) {
    if (choice === "light" || choice === "dark") document.documentElement.dataset.theme = choice;
    else delete document.documentElement.dataset.theme;
    // The iPhone's status-bar colour follows the page.
    const dark = choice === "dark" || (choice === "auto" && matchMedia("(prefers-color-scheme: dark)").matches);
    document.querySelectorAll('meta[name="theme-color"]').forEach(m => m.setAttribute("content", dark ? "#000000" : "#f2f2f7"));
    document.querySelectorAll("[data-theme-choice]").forEach(b => b.setAttribute("aria-checked", b.dataset.themeChoice === choice));
  }
  document.querySelectorAll("[data-theme-choice]").forEach(b => b.addEventListener("click", () => {
    try { localStorage.setItem("theme", b.dataset.themeChoice); } catch (e) {}
    applyTheme(b.dataset.themeChoice);
  }));
  matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => applyTheme(savedTheme()));
  applyTheme(savedTheme());

  // ---------- Timer ----------
  // Picking a length restarts the countdown from now, so it also works as "give me more time".
  let timerSaving = false;
  $("timerSelect").addEventListener("change", async () => {
    const select = $("timerSelect");
    const minutes = Number(select.value);
    timerSaving = true;
    $("timerRow").textContent = select.options[select.selectedIndex].text;
    select.closest(".picker").classList.add("saving");
    try {
      const res = await fetch("timer", {
        method: "POST",
        headers: { "Content-Type": "application/json", "X-LidAwake": "1" },
        body: JSON.stringify({ minutes }),
      });
      if (res.status === 401 || res.status === 423) { timerSaving = false; return locked(); }
    } catch (e) {}
    timerSaving = false;
    select.closest(".picker").classList.remove("saving");
    await poll();
  });

  // ---------- Keep the iPhone screen on ----------
  // While this page is open and unlocked, ask the phone not to dim or lock (Screen Wake Lock).
  // iOS drops the lock whenever the page is hidden, so take it again on return.
  let wakeLock = null;
  async function keepPhoneAwake() {
    if (!unlocked && wakeLock) { wakeLock.release().catch(() => {}); wakeLock = null; return; }
    if (!("wakeLock" in navigator) || document.hidden || !unlocked || wakeLock) return;
    if (streamPaused && currentTab === "screen") return;  // paused: let the phone dim
    try {
      wakeLock = await navigator.wakeLock.request("screen");
      wakeLock.addEventListener("release", () => { wakeLock = null; });
    } catch (e) { wakeLock = null; }
  }
  document.addEventListener("visibilitychange", keepPhoneAwake);
  // Safari only grants it after the person has touched the page, so also try on each tap.
  document.addEventListener("pointerup", keepPhoneAwake);
  setInterval(keepPhoneAwake, 5000);

  // ---------- Start ----------
  setInterval(poll, 3000);
  setInterval(() => { if (unlocked) render(); }, 1000);
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) { stopLive(); return; }
    checkAuth();
  });
  try { const saved = localStorage.getItem("tab"); if (["screen", "apps", "settings"].includes(saved)) currentTab = saved; } catch (e) {}
  showTab(currentTab);
  checkAuth();
</script>
</body>
</html>
"""#
