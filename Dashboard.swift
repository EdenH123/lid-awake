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
<!--SPLASH-->
<script>try { const t = localStorage.getItem("theme"); if (t === "light" || t === "dark") document.documentElement.dataset.theme = t; } catch (e) {}
// Opening the home-screen app: decide before anything is drawn, so the closed MacBook from the
// launch screen is the very first frame (no "Connecting…" flash before the animation).
try { if ((matchMedia("(display-mode: standalone)").matches || navigator.standalone) && !sessionStorage.getItem("introShown")
          && !matchMedia("(prefers-reduced-motion: reduce)").matches) document.documentElement.classList.add("intro"); } catch (e) {}</script>
<style>
  /* iOS system colors and materials (light / dark), as in Settings, Battery and Weather. */
  :root {
    --bg: #f2f2f7; --card: #ffffff; --card2: #e5e5ea; --text: #000000;
    --muted: rgba(60,60,67,.6); --faint: rgba(60,60,67,.3); --line: rgba(60,60,67,.29); --fill: rgba(120,120,128,.16);
    --green: #34c759; --amber: #ff9500; --red: #ff3b30; --blue: #007aff; --gray: #8e8e93;
    --bar: rgba(255,255,255,.78); --bar-edge: rgba(0,0,0,.06); --bar-shadow: 0 6px 24px rgba(0,0,0,.10);
    /* Tab bar glass: see-through, a bright rim along the top, a soft sheen. */
    --glass: rgba(255,255,255,.52); --glass-rim: rgba(255,255,255,.9); --glass-edge: rgba(255,255,255,.55);
    --glass-sheen: rgba(255,255,255,.55); --glass-shadow: 0 10px 34px rgba(0,0,0,.14), 0 1px 3px rgba(0,0,0,.06);
    --glass-pill: rgba(0,0,0,.07); --glass-pill-rim: rgba(255,255,255,.85);
    --kb: #d1d3d9; --key: #ffffff; --key-fn: #abb0bc; --key-shadow: rgba(0,0,0,.35);
    --pad: #e5e5ea; --pad-on: #dcdce1;
    --alu-top: #f1f1f4; --alu-bottom: #dfdfe4; --alu-line: rgba(0,0,0,.022); --alu-hi: rgba(255,255,255,.95);
    --alu-rim: rgba(0,0,0,.07); --alu-ink: #6e6e73; --alu-emboss: rgba(255,255,255,.8); --alu-press: rgba(0,0,0,.05);
    --mac-base: linear-gradient(#e6e6ea, #b8b8be); --mac-groove: #9a9aa1;
  }
  /* Dark: follows the iPhone (Automatic), or chosen in Settings → Appearance. */
  @media (prefers-color-scheme: dark) {
    :root:not([data-theme="light"]) {
      --bg: #000000; --card: #1c1c1e; --card2: #2c2c2e; --text: #ffffff;
      --muted: rgba(235,235,245,.6); --faint: rgba(235,235,245,.3); --line: rgba(84,84,88,.65); --fill: rgba(120,120,128,.32);
      --green: #30d158; --amber: #ff9f0a; --red: #ff453a; --blue: #0a84ff; --gray: #8e8e93;
      --bar: rgba(30,30,32,.72); --bar-edge: rgba(255,255,255,.08); --bar-shadow: 0 6px 24px rgba(0,0,0,.5);
      --glass: rgba(44,44,48,.46); --glass-rim: rgba(255,255,255,.22); --glass-edge: rgba(255,255,255,.12);
      --glass-sheen: rgba(255,255,255,.10); --glass-shadow: 0 10px 34px rgba(0,0,0,.55), 0 1px 3px rgba(0,0,0,.3);
      --glass-pill: rgba(255,255,255,.12); --glass-pill-rim: rgba(255,255,255,.25);
      --kb: #2b2b2d; --key: #6b6b6e; --key-fn: #464649; --key-shadow: rgba(0,0,0,.6);
      --pad: #2c2c2e; --pad-on: #3a3a3c;
      --alu-top: #3b3b3f; --alu-bottom: #2d2d31; --alu-line: rgba(255,255,255,.018); --alu-hi: rgba(255,255,255,.14);
      --alu-rim: rgba(0,0,0,.5); --alu-ink: #a1a1a6; --alu-emboss: rgba(0,0,0,.55); --alu-press: rgba(255,255,255,.06);
      --mac-base: linear-gradient(#8e8e93, #5a5a5f); --mac-groove: #3a3a3c;
    }
  }
  :root[data-theme="dark"] {
      --bg: #000000; --card: #1c1c1e; --card2: #2c2c2e; --text: #ffffff;
      --muted: rgba(235,235,245,.6); --faint: rgba(235,235,245,.3); --line: rgba(84,84,88,.65); --fill: rgba(120,120,128,.32);
      --green: #30d158; --amber: #ff9f0a; --red: #ff453a; --blue: #0a84ff; --gray: #8e8e93;
      --bar: rgba(30,30,32,.72); --bar-edge: rgba(255,255,255,.08); --bar-shadow: 0 6px 24px rgba(0,0,0,.5);
      --glass: rgba(44,44,48,.46); --glass-rim: rgba(255,255,255,.22); --glass-edge: rgba(255,255,255,.12);
      --glass-sheen: rgba(255,255,255,.10); --glass-shadow: 0 10px 34px rgba(0,0,0,.55), 0 1px 3px rgba(0,0,0,.3);
      --glass-pill: rgba(255,255,255,.12); --glass-pill-rim: rgba(255,255,255,.25);
      --kb: #2b2b2d; --key: #6b6b6e; --key-fn: #464649; --key-shadow: rgba(0,0,0,.6);
      --pad: #2c2c2e; --pad-on: #3a3a3c;
      --alu-top: #3b3b3f; --alu-bottom: #2d2d31; --alu-line: rgba(255,255,255,.018); --alu-hi: rgba(255,255,255,.14);
      --alu-rim: rgba(0,0,0,.5); --alu-ink: #a1a1a6; --alu-emboss: rgba(0,0,0,.55); --alu-press: rgba(255,255,255,.06);
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
  /* Like a native app: no selecting the interface's text or popping the iOS callout when a finger
     rests on it (holding a dock icon used to select the whole page). Typing fields stay normal. */
  body { -webkit-user-select: none; user-select: none; -webkit-touch-callout: none; }
  input, textarea, [contenteditable] { -webkit-user-select: text; user-select: text; -webkit-touch-callout: default; }
  #scroller {
    position: fixed; inset: 0; overflow-x: hidden; overflow-y: auto; overscroll-behavior: none;
    -webkit-overflow-scrolling: touch;
    padding: max(10px, env(safe-area-inset-top)) 16px calc(96px + env(safe-area-inset-bottom));
  }
  body.apps-full #scroller, body.landscape-full #scroller { padding: 0; overflow: hidden; }
  /* no grey scroll line on the side while scrolling (any scroll area) */
  * { scrollbar-width: none; }
  *::-webkit-scrollbar { display: none; }
  /* The browser only scrolls the page up and down; a sideways swipe moves between tabs. */
  #scroller { touch-action: pan-y; }
  #screenImg:not([src]) { visibility: hidden; }  /* no picture yet (e.g. swiping in for the first time) */
  body.swiping { -webkit-user-select: none; user-select: none; }
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
  /* A drawing of the MacBook above the switch that follows the real one: the lid folds open and
     shut, the screen lights up or goes dark, a MagSafe cable shows when it's plugged in (light
     amber while charging, green when full) and a green glow breathes under it while charging. */
  .mac-pic { position: relative; height: 196px; display: grid; place-items: end center; padding: 20px 16px 18px 0; transition: height .45s cubic-bezier(.32,.72,0,1); }
  /* Shut, the card shrinks to fit (after the lid has folded); opening, it grows first, then the lid rises. */
  .mac-pic.closed { height: 88px; transition-delay: .75s; }
  .mac-pic:not(.closed) .mp-lid { transition-delay: .35s, 0s; }
  .mp-mac { position: relative; width: 236px; display: grid; justify-items: center; }  /* only hinge + base take room; the lid stands above */
  .mp-lid { position: absolute; left: 8px; bottom: 12px; width: 220px; height: 146px; transform-origin: 50% 100%;
    transform: perspective(520px) rotateX(0deg); transition: transform .85s cubic-bezier(.45,.05,.2,1), opacity .15s ease .7s; }
  .mp-front { position: absolute; inset: 0; padding: 1.5px; border-radius: 12px 12px 4px 4px; background: var(--mac-groove); }
  .mp-bezel { position: relative; height: 100%; padding: 6px 6px 8px; border-radius: 10.5px 10.5px 3px 3px; background: #08080a; }
  .mp-bezel::before {  /* the notch, with the camera */
    content: ""; position: absolute; z-index: 2; left: 50%; top: 6px; width: 30px; height: 6px; transform: translateX(-50%);
    background: radial-gradient(circle at 50% 45%, #24304a 0 1.1px, transparent 1.6px), #08080a; border-radius: 0 0 4px 4px;
  }
  .mp-screen { position: relative; width: 100%; height: 100%; border-radius: 5px 5px 1px 1px; overflow: hidden; background: #1f6fd1 center / cover; }
  .mp-screen::before { content: ""; position: absolute; inset: 0; background: #0a0a0c; opacity: 0; transition: opacity .5s ease; }
  .mp-screen::after {  /* glass */
    content: ""; position: absolute; inset: 0; background: linear-gradient(118deg, rgba(255,255,255,.16), rgba(255,255,255,0) 40%);
  }
  .mac-pic.dark .mp-screen::before { opacity: 1; }
  .mp-hinge { width: 178px; height: 3px; background: #1c1c1e; border-radius: 0 0 3px 3px; }
  .mp-base { position: relative; width: 236px; height: 9px; background: var(--mac-base); border-radius: 2px 2px 10px 10px / 2px 2px 8px 8px;
    box-shadow: inset 0 1px 0 rgba(255,255,255,.55); }
  .mp-base::after {  /* the thumb groove */
    content: ""; position: absolute; left: 50%; top: 0; width: 40px; height: 3.5px; transform: translateX(-50%);
    background: var(--mac-groove); border-radius: 0 0 5px 5px;
  }
  .mp-shadow { position: absolute; left: 50%; bottom: -5px; width: 250px; height: 10px; transform: translateX(-50%); border-radius: 50%;
    background: radial-gradient(closest-side, rgba(0,0,0,.35), transparent); filter: blur(2px); }
  /* Closed: the lid folds down toward you, then the shut lid's front edge sits on the base. */
  .mp-shut { position: absolute; left: 4px; right: 4px; bottom: 9px; height: 5px; border-radius: 3px 3px 0 0;
    background: var(--mac-base); box-shadow: inset 0 1px 0 rgba(255,255,255,.55), 0 1px 0 rgba(0,0,0,.25); opacity: 0; transition: opacity .15s ease; }
  .mac-pic.closed .mp-lid { transform: perspective(520px) rotateX(-89deg); opacity: 0; }
  .mac-pic.closed .mp-shut { opacity: 1; transition-delay: .7s; }
  .mac-pic.closed .mp-hinge { opacity: 0; }
  /* MagSafe: cable into the left side while plugged in. */
  .mp-plug { position: absolute; right: 100%; bottom: 2px; width: 44px; height: 5px; opacity: 0; transition: opacity .4s ease; }
  .mp-plug::before { content: ""; position: absolute; right: 0; top: 0; width: 10px; height: 5px; border-radius: 2px 1px 1px 2px; background: var(--mac-base); }
  .mp-plug::after { content: ""; position: absolute; right: 10px; top: 1.5px; width: 34px; height: 2px; background: linear-gradient(90deg, transparent, var(--mac-groove)); }
  .mp-led { position: absolute; right: 3px; top: 1.5px; z-index: 1; width: 2px; height: 2px; border-radius: 50%; background: var(--green); box-shadow: 0 0 3px var(--green); }
  .mac-pic.plugged .mp-plug { opacity: 1; }
  .mac-pic.charging .mp-led { background: var(--amber); box-shadow: 0 0 3px var(--amber); }
  .mp-glow { position: absolute; left: calc(50% - 8px); bottom: 2px; width: 270px; height: 34px; transform: translateX(-50%); border-radius: 50%;
    background: radial-gradient(closest-side, var(--green), transparent); filter: blur(10px); opacity: 0; transition: opacity .6s ease; pointer-events: none; }
  .mac-pic.charging .mp-glow { opacity: .55; animation: mp-breathe 3.2s ease-in-out infinite; }
  @keyframes mp-breathe { 0%, 100% { opacity: .45; } 50% { opacity: .75; } }
  @media (prefers-reduced-motion: reduce) { .mac-pic * { transition: none !important; animation: none !important; } }
  .mac-pic-caption { text-align: center; font-size: 13px; color: var(--muted); padding-right: 16px; margin-top: -6px; }
  .mac-pic-caption b { font-weight: 600; color: var(--green); }
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
  /* Tapping the tile opens the last hours as a graph (a sheet, like a Weather detail). */
  .tile.tappable { position: relative; cursor: pointer; -webkit-tap-highlight-color: transparent; transition: transform .2s ease, opacity .2s ease; }
  .tile.tappable:active { transform: scale(.97); opacity: .85; }
  .tile-chev { width: 7px !important; height: 12px !important; margin-left: auto; stroke-width: 2.2 !important; color: var(--faint); }
  .sheet-shade { position: fixed; inset: 0; z-index: 40; background: rgba(0,0,0,.4); opacity: 0; transition: opacity .35s ease; }
  .sheet-shade.show { opacity: 1; }
  .temp-sheet {
    position: fixed; z-index: 41; left: 0; right: 0; bottom: 0; max-width: 560px; margin: 0 auto;
    height: calc(100% - env(safe-area-inset-top) - 18px); display: flex; flex-direction: column;
    background: var(--bg); border-radius: 26px 26px 0 0; box-shadow: 0 -8px 30px rgba(0,0,0,.2);
    transform: translateY(105%); visibility: hidden;
    transition: transform .45s cubic-bezier(.32,.72,0,1), visibility 0s linear .45s;
  }
  .temp-sheet.open { transform: translateY(var(--drag, 0px)); visibility: visible; transition: transform .45s cubic-bezier(.32,.72,0,1), visibility 0s; }
  .temp-sheet.dragging { transition: none; }
  .temp-sheet .sheet-head { position: relative; padding: 20px 16px 8px 20px; touch-action: none; cursor: grab; }
  .temp-sheet .sheet-head::before { background: var(--faint); }
  .temp-body { flex: 1; overflow-y: auto; overscroll-behavior: contain; display: grid; align-content: start; gap: 16px;
    padding: 6px 16px calc(24px + env(safe-area-inset-bottom)); }
  .temp-readout { padding: 2px 4px 0; }
  .temp-big { font-size: 44px; font-weight: 600; letter-spacing: -0.02em; line-height: 1.05; font-variant-numeric: tabular-nums; }
  .temp-when { font-size: 15px; color: var(--muted); margin-top: 2px; font-variant-numeric: tabular-nums; min-height: 1.3em; }
  .temp-chart-card { position: relative; padding: 14px 6px 8px 14px; }
  .temp-chart { display: block; width: 100%; height: 240px; overflow: visible; touch-action: pan-y; user-select: none; -webkit-user-select: none; }
  .temp-chart text { font-size: 11px; fill: var(--muted); font-variant-numeric: tabular-nums; }
  .temp-chart .grid { stroke: var(--line); stroke-width: .5; }
  .temp-chart .limit { stroke: var(--red); stroke-width: 1.2; stroke-dasharray: 4 4; }
  .temp-chart text.limit-label { fill: var(--red); font-weight: 600; }
  .temp-chart .bar { transition: opacity .15s ease; }
  .temp-chart .bar-off { fill: var(--fill); }
  .temp-chart.picking .bar:not(.picked) { opacity: .3; }
  .temp-story .row { gap: 12px; }
  .temp-story .row i { width: 10px; height: 10px; border-radius: 50%; flex: none; }
  .temp-story .row .title { white-space: normal; }
  .temp-empty { position: absolute; inset: 0; display: grid; place-items: center; padding: 20px; text-align: center; font-size: 15px; color: var(--muted); }
  body:has(.temp-sheet.open) { overflow: hidden; }
  body:has(.temp-sheet.open) .tabbar { visibility: hidden; }
  @media (prefers-reduced-motion: reduce) { .temp-sheet, .temp-sheet.open, .sheet-shade { transition: none; } }

  /* Status: Claude plan usage, two meters in one card. */
  .usage { display: grid; gap: 16px; padding: 16px; }
  /* Claude's spark beside the title. A tap ripples its rays like Claude thinking, then a little pop. */
  .claude-head { display: flex; align-items: center; gap: 6px; }
  /* Apple's logo beside the Mac title: the system font draws it on iPhone, in the title's own color. */
  .mac-head { display: flex; align-items: center; gap: 6px; }
  .apple-logo { font-family: -apple-system, system-ui; font-size: 15px; line-height: 1; text-transform: none; margin-top: -2px; }
  .claude-spark { position: relative; flex: none; width: 36px; height: 36px; margin: -10px -9px -10px -11px; padding: 10px;
    border: 0; background: none; -webkit-tap-highlight-color: transparent; }
  .claude-spark svg { display: block; width: 16px; height: 16px; overflow: visible; }
  .claude-spark line { stroke: #d97757; stroke-linecap: round; transform-box: view-box; transform-origin: 12px 12px; }
  .claude-spark.go line { animation: spark-think .6s ease-in-out calc(var(--i) * 50ms) 2; }
  .claude-spark.go svg { animation: spark-pop .5s cubic-bezier(.3, 1.5, .5, 1) 1.7s; }
  @keyframes spark-think { 50% { transform: scale(.4); } }
  @keyframes spark-pop { 40% { transform: scale(1.35) rotate(20deg); } }
  @media (prefers-reduced-motion: reduce) { .claude-spark.go line, .claude-spark.go svg { animation: none; } }
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
  .trackpad { position: relative; }
  .pad-note {
    position: absolute; top: 12px; left: 50%; transform: translateX(-50%); padding: 4px 12px; border-radius: 99px;
    font-size: 13px; font-weight: 600; color: var(--text); background: var(--card); box-shadow: 0 1px 4px rgba(0,0,0,.12);
    opacity: 0; pointer-events: none;
  }
  .pad-note.show { animation: pad-note 1.1s ease forwards; }
  @keyframes pad-note { 0% { opacity: 0; } 12% { opacity: 1; } 70% { opacity: 1; } 100% { opacity: 0; } }
  /* Holding still on the screen picture: a ring fills under the finger; when it closes, that's a right-click. */
  .hold-ring {
    position: fixed; z-index: 30; width: 46px; height: 46px; margin: -23px 0 0 -23px; border-radius: 50%;
    pointer-events: none; border: 3px solid rgba(255,255,255,.9); box-shadow: 0 0 0 1px rgba(0,0,0,.25), inset 0 0 0 1px rgba(0,0,0,.25);
    opacity: 0; transform: scale(.4);
  }
  .hold-ring.on { animation: hold-ring .8s linear forwards; }
  .hold-ring.done { animation: none; opacity: 1; transform: scale(1); background: rgba(255,255,255,.35); }
  @keyframes hold-ring { 0% { opacity: 0; transform: scale(.4); } 25% { opacity: .9; } 100% { opacity: 1; transform: scale(1); } }
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

  /* Screen: an aluminium Magic Trackpad (space gray in dark mode). The tools are engraved along
     its top edge; the rest of the slab is the trackpad. */
  .mtp {
    border-radius: 24px; overflow: hidden;
    background: repeating-linear-gradient(90deg, var(--alu-line) 0 1px, transparent 1px 3px),
                linear-gradient(180deg, var(--alu-top), var(--alu-bottom));
    box-shadow: inset 0 1px 0 var(--alu-hi), inset 0 0 0 1px var(--alu-rim), 0 1px 2px rgba(0,0,0,.06), 0 12px 28px rgba(0,0,0,.12);
  }
  .mtp .screen-tools { margin: 0; padding: 10px 10px 0; gap: 2px; justify-content: space-between; overflow: visible; }
  .mtp .tool {
    background: transparent; color: var(--alu-ink); height: 38px; padding: 0 10px; border-radius: 12px;
    filter: drop-shadow(0 1px 0 var(--alu-emboss)); transition: color .2s ease, background .2s ease, transform .15s ease;
  }
  .mtp .tool.icon { width: 40px; padding: 0; }
  .mtp .tool svg { width: 21px; height: 21px; stroke-width: 1.7; }
  .mtp .tool .label { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0 0 0 0); white-space: nowrap; }
  .mtp .tool .hd { font-size: 11px; padding: 2px 5px; border-radius: 5px; border-width: 1.6px; }
  .mtp .tool.help { margin-left: 0; font-size: 18px; font-weight: 500; }
  .mtp .tool:active { opacity: 1; transform: scale(.9); background: var(--alu-press); }
  .mtp .tool.on, .mtp .tool[aria-expanded="true"] { color: var(--blue); background: var(--alu-press); filter: none; }
  .mtp .trackpad { background: transparent; box-shadow: none; border-radius: 0; color: var(--alu-ink); opacity: .9; }
  .mtp .trackpad.active { background: var(--alu-press); }

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
    display: flex; align-items: center; gap: 12px; padding: 10px 18px; border-radius: 99px; font-size: 15px; white-space: nowrap;
    background: var(--bar); box-shadow: var(--bar-shadow), inset 0 0 0 .5px var(--bar-edge);
    -webkit-backdrop-filter: blur(20px) saturate(180%); backdrop-filter: blur(20px) saturate(180%);
  }
  /* The note pops in with a little spring, then sinks, shrinks and blurs away. */
  .opened:not([hidden]) { animation: note-in .35s cubic-bezier(.32,.72,0,1); }
  .opened.fade-out { animation: note-out .5s cubic-bezier(.4,0,.6,1) forwards; }
  @keyframes note-in {
    from { opacity: 0; transform: translateX(-50%) translateY(10px) scale(.9); }
    to { opacity: 1; transform: translateX(-50%); }
  }
  @keyframes note-out {
    from { opacity: 1; transform: translateX(-50%); filter: blur(0); }
    to { opacity: 0; transform: translateX(-50%) translateY(14px) scale(.82); filter: blur(6px); }
  }
  .opened button { font-size: 15px; font-weight: 600; color: #fff; background: var(--blue); border: 0; border-radius: 99px; padding: 7px 14px; cursor: pointer; }

  /* Opening the home-screen app: the closed MacBook from the launch screen opens, its screen shows
     the sky of the moment (sunrise, day, sunset or night), and the view dives into it. */
  #intro { display: none; }
  html.intro #intro { position: fixed; inset: 0; z-index: 100; background: #000; display: grid; place-items: center;
    animation: intro-out .4s ease 2.05s forwards; }
  .intro-mac { position: relative; width: min(58vw, 300px); aspect-ratio: 220 / 170; perspective: 700px;
    animation: intro-zoom .75s cubic-bezier(.55,0,.75,.2) 1.45s forwards; }
  .intro-lid { position: absolute; left: 4.5%; right: 4.5%; bottom: 7%; height: 82%; transform-origin: 50% 100%;
    transform: rotateX(88deg); border-radius: 9px 9px 2px 2px; background: #1c1c1e; box-shadow: inset 0 0 0 2px #3a3a3c;
    padding: 3.5%; animation: intro-lid .8s cubic-bezier(.2,.8,.2,1) .12s forwards; }
  .intro-screen { position: relative; width: 100%; height: 100%; border-radius: 3px; overflow: hidden; opacity: 0;
    animation: intro-fade .55s ease .75s forwards; }
  .intro-base { position: absolute; left: 0; right: 0; bottom: 0; height: 7%; border-radius: 2px 2px 10px 10px;
    background: linear-gradient(#d1d1d6, #8e8e93); }
  @keyframes intro-lid { to { transform: rotateX(-8deg); } }
  @keyframes intro-fade { to { opacity: 1; } }
  @keyframes intro-zoom { to { transform: scale(7); } }
  @keyframes intro-out { to { opacity: 0; visibility: hidden; } }
  /* Apps: a Mac-style dock above the tab bar with the other apps open on the Mac. */
  .mac-dock {
    position: fixed; z-index: 9; left: 50%; transform: translateX(-50%);
    bottom: calc(max(4px, env(safe-area-inset-bottom) - 22px) + 72px);
    display: flex; gap: 10px; padding: 8px 10px 5px; border-radius: 20px; max-width: calc(100vw - 32px); overflow-x: auto; scrollbar-width: none;
    background: var(--bar); box-shadow: var(--bar-shadow), inset 0 0 0 .5px var(--bar-edge);
    -webkit-backdrop-filter: blur(20px) saturate(180%); backdrop-filter: blur(20px) saturate(180%);
  }
  .mac-dock::-webkit-scrollbar { display: none; }
  .mac-dock button { flex: none; border: 0; padding: 0; background: none; display: grid; justify-items: center; gap: 3px; cursor: pointer; }
  .mac-dock img { width: 42px; height: 42px; display: block; -webkit-user-drag: none; transition: transform .15s ease; }
  .mac-dock button:active img { transform: scale(.88); }
  .mac-dock .dot { width: 4px; height: 4px; border-radius: 50%; background: var(--text); opacity: .55; }
  .mac-dock button.launched { animation: launch .5s cubic-bezier(.32,.72,0,1); }
  .mac-dock button { touch-action: none; -webkit-touch-callout: none; -webkit-user-select: none; user-select: none; }
  /* Hold an icon in the dock: a small menu above it, like the Mac Dock's. */
  .dock-menu {
    position: fixed; z-index: 30; min-width: 190px; border-radius: 14px; overflow: hidden;
    background: var(--bar); box-shadow: var(--bar-shadow), inset 0 0 0 .5px var(--bar-edge);
    -webkit-backdrop-filter: blur(24px) saturate(180%); backdrop-filter: blur(24px) saturate(180%);
    transform-origin: 50% 100%; animation: dock-menu .22s cubic-bezier(.32,.72,0,1);
  }
  @keyframes dock-menu { from { opacity: 0; transform: scale(.85); } to { opacity: 1; transform: none; } }
  .dock-menu .menu-title { padding: 10px 14px 8px; font-size: 13px; color: var(--muted); font-weight: 600; }
  .dock-menu button { position: relative; display: block; width: 100%; text-align: left; border: 0; background: none; padding: 11px 14px; font-size: 16px; color: var(--text); border-top: .5px solid var(--line); cursor: pointer; }
  .dock-menu button.danger { color: var(--red); }
  .dock-menu button:active { background: var(--fill); }
  .dock-shade { position: fixed; inset: 0; z-index: 29; }
  #tab-apps .app-grid, #macDock { transition: opacity .25s ease; }
  #tab-apps.dock-focusing .app-grid, #tab-apps.dock-focusing #appsFoot, #tab-apps.dock-focusing #openedBar,
  #tab-apps.dock-focusing #macDock { opacity: 0; }
  #tab-apps:has(#macDock:not([hidden])) { padding-bottom: 72px; }
  #tab-apps:has(#macDock:not([hidden])) .opened { bottom: calc(max(4px, env(safe-area-inset-bottom) - 22px) + 144px); }
  body.apps-full #macDock { display: none !important; }

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
  /* Slide up on an icon: it follows the finger, then zooms to fill the screen and the Screen tab appears. */
  .app.lifting img { transition: transform .12s ease; }
  /* The finger's movement on an icon belongs to the icon (slide up to open, hold to rearrange),
     not to page scrolling; the page still scrolls from the spaces between icons. */
  .app-grid .app { touch-action: none; transition: opacity .25s ease; }
  .app-grid.focusing .app:not(.focused) { opacity: 0; }
  #appsFoot, #openedBar { transition: opacity .25s ease; }
  #tab-apps:has(.app-grid.focusing) #appsFoot, #tab-apps:has(.app-grid.focusing) #openedBar { opacity: 0; }
  .app-grid.focusing .app.focused > * { opacity: 0; transition: opacity .2s ease; }  /* the floating copy takes its place */
  .app-zoom {
    position: fixed; z-index: 40; pointer-events: none; transform-origin: center;
    background: transparent center / 100% 100% no-repeat; filter: drop-shadow(0 10px 24px rgba(0,0,0,.25));
  }
  /* Opening: a small press, then a spring back. */
  .app.launched { animation: launch .5s cubic-bezier(.32,.72,0,1); }
  @keyframes launch { 0% { transform: scale(.86); } 55% { transform: scale(1.07); } 100% { transform: scale(1); } }

  /* Floating tab bar, as in iOS 26: a capsule above the content, the current tab on a soft pill. */
  .tabbar {
    position: fixed; z-index: 10; left: 50%; transform: translateX(-50%);
    bottom: max(4px, calc(env(safe-area-inset-bottom) - 22px));
    display: flex; gap: 2px; padding: 4px; border-radius: 99px; isolation: isolate;
    background: var(--glass);
    box-shadow: var(--glass-shadow), inset 0 1px 0 var(--glass-rim), inset 0 0 0 .5px var(--glass-edge),
                inset 0 -1px 1px rgba(0,0,0,.04);
    -webkit-backdrop-filter: blur(24px) saturate(190%) brightness(1.04); backdrop-filter: blur(24px) saturate(190%) brightness(1.04);
  }
  /* The sheen: light catching the upper half of the glass. */
  .tabbar::before {
    content: ""; position: absolute; inset: 0; border-radius: inherit; pointer-events: none; z-index: 0;
    background: linear-gradient(180deg, var(--glass-sheen) 0%, transparent 55%);
    opacity: .55;
  }
  .tabbar button {
    width: 84px; height: 54px; background: none; border: 0; border-radius: 99px; display: grid; justify-items: center; align-content: center; gap: 1px;
    font-size: 10px; font-weight: 600; letter-spacing: 0; color: var(--text); cursor: pointer; padding: 0; transition: color .3s ease;
    position: relative; z-index: 1;
  }
  .tabbar button svg { transition: transform .35s cubic-bezier(.32,.72,0,1); }
  .tabbar button:active svg { transform: scale(.86); }
  /* The glass pill behind the current tab glides to the tab you pick with a soft spring. It
     only ever moves sideways (a GPU transform), so it stays smooth even while the page updates.
     Moved with `translate`, not `transform`: `scale` (the swell while dragging) would otherwise
     multiply the distance too and push the pill out past the end of the bar. */
  .tab-pill {
    position: absolute; z-index: 0; top: 4px; left: 0; width: 84px; height: 54px; border-radius: 99px;
    pointer-events: none; will-change: translate;
    background: linear-gradient(180deg, rgba(255,255,255,.14), rgba(255,255,255,0) 60%), var(--glass-pill);
    box-shadow: inset 0 1px 0 var(--glass-pill-rim), inset 0 0 0 .5px var(--glass-edge), 0 2px 8px rgba(0,0,0,.08);
    transition: translate .42s cubic-bezier(.3,1.3,.5,1), scale .3s cubic-bezier(.3,1.3,.5,1);
  }
  .tabbar.no-anim .tab-pill { transition: scale .3s cubic-bezier(.3,1.3,.5,1); }
  /* Slide a finger along the bar: the pill follows it and swells a little, like a lens. */
  .tabbar { touch-action: none; }
  .tabbar.dragging .tab-pill { scale: 1.12 1.08; }
  /* A new page fades in (opacity only: smooth on iPhone, and the floating bars stay put). */
  .tab-enter { animation: tab-enter .22s ease-out; }
  @keyframes tab-enter { from { opacity: 0; } to { opacity: 1; } }
  @media (prefers-reduced-motion: reduce) {
    .tab-pill, .tabbar button svg { transition: none; }
    .tab-enter { animation: none; }
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
    transition: opacity .5s ease, transform .5s ease;
  }
  /* After a few quiet seconds the bar fades so only the Mac shows; any touch brings it back. */
  body.landscape-full .float-bar.faded { opacity: 0; transform: translateY(-6px); pointer-events: none; }
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
  .set-icon.gray { background: #8e8e93; } .set-icon.pink { background: #ff2d55; }
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
  /* Tiny vibrations: an invisible layer over a button passes the finger's tap to a hidden iOS switch. */
  .tick-pad { position: absolute; inset: 0; z-index: 1; border-radius: inherit; -webkit-tap-highlight-color: transparent; }
  .tick-pad input { position: absolute; top: 0; left: 0; width: 1px; height: 1px; margin: 0; visibility: hidden; pointer-events: none; }
  body.no-haptics .tick-pad { display: none; }
  .mac-dock button { position: relative; }

  /* Loading: until the Mac first answers, the Status page is drawn as soft grey shapes with one
     light sweeping across them all (sweepAlign() starts each shape's sweep by where it sits). */
  :root { --bone: var(--fill); --bone-hi: rgba(255,255,255,.6); }
  @media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) { --bone-hi: rgba(255,255,255,.09); } }
  :root[data-theme="dark"] { --bone-hi: rgba(255,255,255,.09); }
  main.waiting :where(#host, #tab-status :where(#state, #stateSub, #macPicCaption, .row .value, .tile-value > span:first-child, #temp,
    .tile-detail, .usage-pct, .usage-sub, .section-foot)) { position: relative; color: transparent !important; }
  main.waiting :where(#host, #tab-status :where(#state, #stateSub, #macPicCaption, .row .value, .tile-value > span:first-child, #temp,
    .tile-detail, .usage-pct, .usage-sub, .section-foot))::after {
    content: ""; position: absolute; left: 0; right: 0; top: 50%; height: .72em; transform: translateY(-50%); border-radius: 6px;
  }
  main.waiting :where(#host, #tab-status :where(#state, #stateSub, #macPicCaption, .row .value, .tile-value > span:first-child, #temp,
    .tile-detail, .usage-pct, .usage-sub, .section-foot))::after,
  main.waiting #tab-status :is(.meter, .scale, .awake-switch), .screen-skel .bone {
    background: linear-gradient(100deg, transparent 20%, var(--bone-hi) 50%, transparent 80%) -60vw 0 / 60vw 100% no-repeat, var(--bone);
    animation: bone-sweep 1.8s ease-in-out var(--sweep-delay, 0s) infinite;
  }
  @keyframes bone-sweep { 0% { background-position: -60vw 0; } 75%, 100% { background-position: 100vw 0; } }
  main.waiting #tab-status { pointer-events: none; }
  main.waiting #host { width: 9em; min-height: 1.3em; }
  main.waiting #tab-status #state { width: 2.4em; }
  main.waiting #tab-status #stateSub { width: 10em; min-height: 1.25em; margin-top: 3px; }
  main.waiting #tab-status .row .value::after { left: auto; width: 4.5em; }  /* right-aligned, like the numbers */
  main.waiting #tab-status .health-list .value::after { width: 2.6em; }
  main.waiting #tab-status :is(#thermal, #netPing)::after { width: 3.4em; }
  main.waiting #tab-status :is(#uptimeValue, #wifiName)::after { width: 5.5em; }
  main.waiting #tab-status .tile-value > span:first-child, main.waiting #tab-status #temp { width: 2.3em; }
  main.waiting #tab-status .tile-detail { width: 70%; min-height: 1.3em; }
  main.waiting #tab-status .usage-pct { min-width: 2.2em; }
  main.waiting #tab-status .usage-sub { width: 55%; min-height: 1.3em; }
  main.waiting #tab-status .section-foot { margin: 0 16px; padding: 0; width: 62%; min-height: 1.38em; }
  main.waiting #tab-status :is(.meter > div, .scale i, .bars), main.waiting #tab-status .awake-switch::after { opacity: 0; }
  main.waiting #macPic { opacity: .35; }
  main.waiting #tab-status #macPicCaption { width: 12em; min-height: 1.3em; margin-inline: auto; }  /* not yet known: lid, screen and charger come with the first answer */
  /* When the numbers arrive they fade in where the shapes were; the bars grow and the switch slides on. */
  main.revealed :is(#host, #tab-status :is(#state, #stateSub, #macPicCaption, .row .value, .tile-value, .tile-detail, .usage-pct,
    .usage-sub, .section-foot, .scale i, .bars)) { animation: bone-reveal .4s ease both; }
  main.revealed #macPic { animation: pic-reveal .5s ease both; }
  @keyframes pic-reveal { from { opacity: .35; } }
  @keyframes bone-reveal { from { opacity: 0; } }
  /* Screen tab: a grey MacBook and trackpad hold the place until the first picture arrives. */
  .screen-skel { display: grid; gap: 20px; }
  .screen-skel .screen-wrap { aspect-ratio: 1512 / 982; }
  .screen-skel .screen-wrap.bone { --bone: #161618; --bone-hi: rgba(255,255,255,.07); }
  .screen-skel .skel-pad { aspect-ratio: 16 / 12.6; max-height: 330px; border-radius: 24px; }
  body.landscape-full .screen-skel { display: none; }
  @media (prefers-reduced-motion: reduce) {
    main.waiting #tab-status *, main.waiting #host, .screen-skel .bone { animation: none !important; }
    main.waiting #tab-status ::after, main.waiting #host::after { animation: none !important; }
  }
</style>
</head>
<body>
<div id="intro" aria-hidden="true"><div class="intro-mac"><div class="intro-lid"><div class="intro-screen" id="introSky"></div></div><div class="intro-base"></div></div></div>
<div id="scroller">
<main id="main" class="waiting">
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
    <h2 id="gateTitle">Connecting…</h2>
    <p id="gateText">Reaching your Mac.</p>
    <form id="loginForm" hidden>
      <button type="button" class="faceid-btn" id="faceIdBtn" hidden><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M7 3H5a2 2 0 0 0-2 2v2M17 3h2a2 2 0 0 1 2 2v2M7 21H5a2 2 0 0 1-2-2v-2M17 21h2a2 2 0 0 0 2-2v-2M9 9v1.5M15 9v1.5M12 9v4.5h-1M9.5 16.5a4 4 0 0 0 5 0"/></svg>Sign in with Face ID</button>
      <input type="password" id="password" placeholder="Password" autocomplete="current-password" required>
      <button id="unlockBtn">Unlock</button>
      <div class="error" id="loginError"></div>
    </form>
  </section>

  <div class="tab" id="app">

  <div class="tab" id="tab-status">
    <section class="card faceid-offer" id="faceIdOffer" hidden>
      <div class="faceid-icon"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M7 3H5a2 2 0 0 0-2 2v2M17 3h2a2 2 0 0 1 2 2v2M7 21H5a2 2 0 0 1-2-2v-2M17 21h2a2 2 0 0 0 2-2v-2M9 9v1.5M15 9v1.5M12 9v4.5h-1M9.5 16.5a4 4 0 0 0 5 0"/></svg></div>
      <div class="faceid-text"><b>Sign in with Face ID?</b><span>Skip the password next time. Your face never leaves this iPhone.</span></div>
      <div class="faceid-actions"><button type="button" id="faceIdLater">Not now</button><button type="button" class="primary" id="faceIdOn">Turn on</button></div>
    </section>
    <div class="section">
      <div class="hero-group">
      <div class="mac-pic closed dark" id="macPic" aria-hidden="true">
        <div class="mp-glow"></div>
        <div class="mp-mac">
          <div class="mp-lid"><div class="mp-front"><div class="mp-bezel"><div class="mp-screen" id="macPicScreen"></div></div></div></div>
          <div class="mp-hinge"></div>
          <div class="mp-shut"></div>
          <div class="mp-base"><div class="mp-plug"><span class="mp-led"></span></div><div class="mp-shadow"></div></div>
        </div>
      </div>
      <div class="mac-pic-caption" id="macPicCaption"></div>
      <section class="hero" id="statusCard">
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
      <section class="card tile tappable" id="tempTile" role="button" tabindex="0" aria-label="Temperature, show the last hours">
        <div class="tile-head"><svg viewBox="0 0 16 16" aria-hidden="true"><path d="M8 1.5a2 2 0 0 0-2 2v6.1a3.4 3.4 0 1 0 4 0V3.5a2 2 0 0 0-2-2z"/></svg>Temperature<svg class="tile-chev" viewBox="0 0 8 14" aria-hidden="true"><path d="M1.5 1.5 6.5 7l-5 5.5"/></svg></div>
        <div class="tile-value" id="temp">—</div>
        <div class="scale"><i id="tempDot" style="left:0"></i></div>
        <div class="tile-detail" id="tempSub"></div>
      </section>
    </div>

    <div class="section" id="claudeSection" hidden>
      <div class="section-head claude-head"><button type="button" class="claude-spark" id="claudeSpark" aria-label="Claude"><svg viewBox="0 0 24 24" aria-hidden="true"><line style="--i:0" x1="12" y1="12" x2="12.00" y2="2.00" stroke-width="2.8"/><line style="--i:1" x1="12" y1="12" x2="15.75" y2="5.50" stroke-width="2.2"/><line style="--i:2" x1="12" y1="12" x2="20.23" y2="7.25" stroke-width="2.8"/><line style="--i:3" x1="12" y1="12" x2="19.00" y2="12.00" stroke-width="2.2"/><line style="--i:4" x1="12" y1="12" x2="20.66" y2="17.00" stroke-width="2.8"/><line style="--i:5" x1="12" y1="12" x2="16.00" y2="18.93" stroke-width="2.2"/><line style="--i:6" x1="12" y1="12" x2="12.00" y2="21.00" stroke-width="2.8"/><line style="--i:7" x1="12" y1="12" x2="8.25" y2="18.50" stroke-width="2.2"/><line style="--i:8" x1="12" y1="12" x2="3.34" y2="17.00" stroke-width="2.8"/><line style="--i:9" x1="12" y1="12" x2="5.00" y2="12.00" stroke-width="2.2"/><line style="--i:10" x1="12" y1="12" x2="3.77" y2="7.25" stroke-width="2.8"/><line style="--i:11" x1="12" y1="12" x2="8.00" y2="5.07" stroke-width="2.2"/></svg></button>Claude</div>
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
      <div class="section-head mac-head"><span class="apple-logo" aria-hidden="true">&#xF8FF;</span>Mac</div>
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
    <div class="section-foot" id="appsFoot" hidden>Tap an app to open it on your Mac. Slide up on an app to open it and watch it on the screen. A dot means it's already open. To change this list, use Apps on phone in the Lid Awake menu on your Mac.</div>
    <div class="mac-dock" id="macDock" role="toolbar" aria-label="Other apps open on your Mac" hidden></div>
    <div class="opened" id="openedBar" hidden><span id="openedText"></span></div>
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
      <div class="alert-row"><span class="set-icon clay"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 5h16v11H9l-5 4z"/><path d="M8 10.5h8"/></svg></span><span class="title">Claude sessions<small>Finished, or waiting for you, while you're away from the Mac</small></span><button type="button" class="mini-switch" data-alert="claudeSession" role="switch" aria-label="Claude sessions"></button></div>
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
      <div class="section-head">Vibrations</div>
      <section class="alerts-card limits-card">
        <div class="alert-row"><span class="set-icon pink"><svg viewBox="0 0 24 24" aria-hidden="true"><rect x="8" y="3" width="8" height="18" rx="2"/><path d="M4.5 8.5v7M19.5 8.5v7"/></svg></span><span class="title">Tiny vibrations<small>Switches, opening apps, picture quality</small></span><button type="button" class="mini-switch" id="hapticsSwitch" role="switch" aria-label="Tiny vibrations"></button></div>
      </section>
      <div class="section-foot">Only on this iPhone. If you don't feel them, turn on System Haptics in iPhone Settings → Sounds &amp; Haptics.</div>
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
    <div class="screen-skel" id="screenSkel" aria-hidden="true">
      <div class="macbook"><div class="lid"><div class="screen-wrap bone"></div></div><div class="base"></div></div>
      <div class="skel-pad bone"></div>
    </div>
    <div class="tab" id="screenControls" hidden>
    <div class="macbook">
      <div class="lid">
        <div class="screen-wrap" id="screenWrap"><img id="screenImg" alt="Mac screen"><canvas id="screenCanvas" hidden aria-label="Mac screen"></canvas></div>
        <div class="screen-off" id="screenOffOverlay" hidden><span>Your Mac's screen is off</span><button type="button" id="wakeBtn">Wake screen</button></div>
        <div class="screen-off" id="pausedOverlay" hidden><span>Paused to save data and battery</span><button type="button" id="resumeBtn">Continue</button></div>
      </div>
      <div class="base"></div>
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
    <div class="mtp">
      <div class="screen-tools" role="toolbar" aria-label="Screen tools">
        <button type="button" class="tool" id="qualityBtn" aria-expanded="false" aria-label="Picture quality: Balanced"><span class="hd">SD</span></button>
        <button type="button" class="tool" id="kbBtn"><svg viewBox="0 0 24 24" aria-hidden="true"><rect x="2" y="6" width="20" height="12" rx="2.5"/><path d="M6 10h.01M10 10h.01M14 10h.01M18 10h.01M8 14h8"/></svg><span class="label" id="kbLabel">Type</span></button>
        <button type="button" class="tool" id="keysBtn" aria-expanded="false"><svg viewBox="0 0 24 24" aria-hidden="true"><rect x="3.5" y="3.5" width="17" height="17" rx="4"/><text x="12" y="17" text-anchor="middle" font-size="13.5" fill="currentColor" stroke="none" font-family="-apple-system, system-ui, sans-serif">⌘</text></svg><span class="label">Keys</span></button>
        <button type="button" class="tool icon" id="lockMacBtn" aria-label="Lock Mac"><svg viewBox="0 0 24 24" aria-hidden="true"><rect x="5" y="11" width="14" height="10" rx="2.5"/><path d="M8 11V8a4 4 0 0 1 8 0v3"/></svg></button>
        <button type="button" class="tool icon" id="screenOffBtn" aria-label="Turn the Mac's screen off"><svg viewBox="0 0 24 24" aria-hidden="true"><rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4M9 8l6 6"/></svg></button>
        <button type="button" class="tool icon help" id="helpBtn" aria-expanded="false" aria-label="How to use">?</button>
      </div>
      <div class="trackpad" id="trackpad">Trackpad</div>
    </div>
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
<div class="sheet-shade" id="tempShade" hidden></div>
<section class="temp-sheet" id="tempSheet" role="dialog" aria-label="Temperature history" aria-hidden="true">
  <div class="sheet-head" id="tempHead"><span>Temperature</span><button type="button" id="tempDone">Done</button></div>
  <div class="temp-body">
    <div class="segmented" id="tempRange" role="radiogroup" aria-label="Time range">
      <button type="button" role="radio" data-hours="1" aria-checked="false">1H</button>
      <button type="button" role="radio" data-hours="6" aria-checked="true">6H</button>
      <button type="button" role="radio" data-hours="24" aria-checked="false">24H</button>
    </div>
    <div class="temp-readout">
      <div class="temp-big" id="tempBig">—</div>
      <div class="temp-when" id="tempWhen">Now</div>
    </div>
    <div class="card temp-chart-card">
      <svg class="temp-chart" id="tempChart" role="img" aria-label="Battery temperature graph"></svg>
      <div class="temp-empty" id="tempEmpty" hidden></div>
    </div>
    <div class="list temp-story" id="tempStory" hidden></div>
    <div class="section-foot">Each bar runs from the lowest to the highest reading in its slot. Battery temperature, from the log your Mac keeps itself (about one reading a minute).</div>
  </div>
</section>
<input class="live-input" id="liveInput" aria-label="Type directly on your Mac"
       autocapitalize="off" autocorrect="off" autocomplete="off" spellcheck="false" enterkeyhint="go">
<script>
  const $ = id => document.getElementById(id);
  let data = null, fetchedAt = 0, lastOk = 0;
  const TAB_TITLES = { screen: "Screen", apps: "Apps", settings: "Settings" };
  // The Status page greets you by the time of day (the Mac's name stays underneath).
  function greeting() {
    const h = new Date().getHours();
    return h >= 5 && h < 12 ? "Good morning" : h >= 12 && h < 17 ? "Good afternoon" : h >= 17 && h < 21 ? "Good evening" : "Good night";
  }
  // While Lid Awake is off the Mac doesn't answer at all (no error, just silence), so give up
  // after a few seconds instead of waiting on a request that never ends.
  function fetchWithin(ms, url, options = {}) {
    const stop = new AbortController();
    const timer = setTimeout(() => stop.abort(), ms);
    return fetch(url, { ...options, signal: stop.signal }).finally(() => clearTimeout(timer));
  }
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

  // ---------- Loading placeholders ----------
  // One light sweeps across the grey shapes as if they were one surface: each shape starts its
  // sweep a little later the further right (and lower) it sits.
  function sweepAlign(root) {
    const period = 1.8, speed = 1.6 * innerWidth / (0.75 * period);  // px/s while the light moves
    root.querySelectorAll("*").forEach(el => {
      const r = el.getBoundingClientRect();
      el.style.setProperty("--sweep-delay", `${((r.left + r.top * .3) / speed) % period - period}s`);
    });
  }
  // Keep the Claude card's place while loading if it was there last time, so nothing jumps.
  try { if (localStorage.getItem("claudeShown") === "1") $("claudeSection").hidden = false; } catch (e) {}
  sweepAlign($("main"));
  function revealPage() {
    const main = $("main");
    if (!main.classList.contains("waiting")) return;
    main.classList.replace("waiting", "revealed");
    setTimeout(() => main.classList.remove("revealed"), 600);
  }

  function render() {
    updateLandscape();
    showScreenOff();
    const ago = Math.round((Date.now() - lastOk) / 1000);
    const stale = !lastOk || ago > 10;
    const loading = $("main").classList.contains("waiting") && $("gate").hidden && performance.now() < 15000;
    $("live").classList.toggle("stale", stale && !loading);
    $("liveText").textContent = !lastOk ? (loading ? "Connecting…" : "Can't reach Mac") : stale ? `Last seen ${ago}s ago` : "Live";
    $("pageTitle").textContent = TAB_TITLES[currentTab] || greeting();
    $("settingsBtn").hidden = currentTab !== "status" || $("lockBtn").hidden;
    if (!data) return;
    revealPage();

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

    renderMacPic();

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
      auth = await (await fetchWithin(4000, "auth", { cache: "no-store" })).json();
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
      const res = await fetchWithin(8000, "status", { cache: "no-store" });
      if (res.status === 401 || res.status === 423) return locked();
      if (!res.ok) throw new Error(res.status);
      const rtt = performance.now() - sentAt;
      pingMs = pingMs == null ? rtt : pingMs * 0.7 + rtt * 0.3;
      data = await res.json();
      fetchedAt = Date.now();
      lastOk = fetchedAt;
    } catch (e) {
      // Keep showing the last data, marked stale, through a short blip. If the Mac stays
      // silent (Lid Awake turned off, Mac asleep or offline), switch to the "can't reach" screen;
      // it comes back by itself once the Mac answers.
      if (lastOk && Date.now() - lastOk > 8000) return locked();
    }
    render();
  }

  // ---------- Tabs ----------
  const tabButtons = document.querySelectorAll(".tabs button");
  // The highlight glides to the current tab (jumps without animating the first time).
  function movePill(animate) {
    const active = document.querySelector(".tabs button.active");
    if (!active || (swipe && swipe.live)) return;  // mid-swipe the pill follows the finger
    document.querySelector(".tabbar").classList.toggle("no-anim", !animate);
    $("tabPill").style.width = active.offsetWidth + "px";
    $("tabPill").style.translate = `${active.offsetLeft}px 0`;
  }
  let pillPlaced = false;
  addEventListener("resize", () => movePill(false));
  function showTab(name, swiped) {
    if (settleSwipe) settleSwipe();  // a tap on the tab bar while a swipe is still settling
    else if (swipe && swipe.live) { clearSwipePeer(); swipe = null; document.body.classList.remove("swiping"); }
    const changed = currentTab !== name;
    currentTab = name;
    tabButtons.forEach(b => b.classList.toggle("active", b.dataset.tab === (name === "settings" ? "status" : name)));
    // Start the glide right away, before the heavier page work below, so its first frames aren't held up.
    movePill(pillPlaced);
    pillPlaced = true;
    const page = $("tab-" + name);
    if (changed && page && !swiped) {
      page.classList.remove("tab-enter");
      void page.offsetWidth;  // restart the animation
      page.classList.add("tab-enter");
    }
    $("pageTitle").textContent = TAB_TITLES[name] || greeting();
    $("settingsBack").hidden = name !== "settings";
    $("live").hidden = name === "settings";
    $("tab-status").hidden = name !== "status";
    $("tab-screen").hidden = name !== "screen";
    $("tab-apps").hidden = name !== "apps";
    $("tab-settings").hidden = name !== "settings";
    if (name !== "apps" && editingApps) setEditing(false);
    $("main").classList.toggle("wide", name === "screen");
    try { localStorage.setItem("tab", name); } catch (e) {}
    requestAnimationFrame(() => {
      if (currentTab !== name) return;  // tapped another tab meanwhile
      render();
      if (name === "screen") { sweepAlign($("screenSkel")); startFrames(); }
      else stopLive();
    });
  }
  tabButtons.forEach(b => b.addEventListener("click", () => showTab(b.dataset.tab)));

  // ---------- Swipe between tabs ----------
  // Swipe sideways to go Status ↔ Screen ↔ Apps: the page follows the finger with the next page
  // sliding in beside it, and the tab-bar pill follows too. Up and down stays page scrolling.
  // What already uses the finger keeps it: sliders, the dock, the tool row, and on the Screen tab
  // the picture, trackpad and tools (there, swipe from the phone's edge or the title instead).
  const TAB_ORDER = ["status", "screen", "apps"];
  const SWIPE_EASE = "cubic-bezier(.25,1,.5,1)";
  let swipe = null, settleSwipe = null;
  function swipeAllowed(e) {
    if (!unlocked || $("app").hidden || !TAB_ORDER.includes(currentTab)) return false;  // Settings has its Back button
    const body = document.body.classList;
    if (body.contains("landscape-full") || body.contains("apps-full") || editingApps || drag) return false;
    if (document.querySelector(".key-sheet.open, .temp-sheet.open, .dock-shade, .app-zoom")) return false;
    const t = e.target;
    if (t.closest("input, select, textarea, .qs-track, .screen-tools, .mac-dock")) return false;
    if (currentTab === "screen" && t.closest("#screenControls")) {
      if (t.closest("#screenWrap, .trackpad")) return false;
      return e.clientX < 22 || e.clientX > innerWidth - 22;
    }
    return true;
  }
  // Moving everything sideways uses each element's `translate` (not the page's `transform`), so the
  // Apps dock and notes, which are pinned to the screen, travel with their page instead of jumping.
  function placeSwipe(dx) {
    for (const [el, base] of swipe.movers) el.style.translate = `${base + dx}px 0`;
  }
  function setSwipePeer(dir) {
    const s = swipe;
    if (s.dir === dir) return;
    clearSwipePeer();
    s.dir = dir;
    const titles = document.querySelector(".titles"), cur = $("tab-" + currentTab);
    s.movers = [[titles, 0], ...[...cur.children].map(el => [el, 0])];
    s.peer = TAB_ORDER[TAB_ORDER.indexOf(currentTab) + dir];
    if (!s.peer) return;  // nothing further this way: the page just stretches
    const sc = $("scroller"), page = $("tab-" + s.peer);
    const tallNow = sc.scrollHeight, curHeight = cur.offsetHeight;
    $("app").style.position = "relative";
    page.style.cssText = `position:absolute;left:0;top:0;width:${$("app").clientWidth}px`;
    page.hidden = false;
    // Before its first picture the Screen page is empty; show the (still dark) MacBook coming in.
    if (s.peer === "screen" && $("screenControls").hidden && $("screenMsg").hidden) {
      $("screenControls").hidden = false;
      s.revealed = true;
    }
    // Show the new page where it will be once it's the current one: scrolled as close to where we
    // are now as its length allows, so letting go doesn't make anything jump.
    const most = Math.max(0, tallNow - curHeight + page.offsetHeight - sc.clientHeight);
    s.scrollAfter = Math.min(sc.scrollTop, most);
    const shift = sc.scrollTop - s.scrollAfter;
    page.style.top = shift + "px";
    const title = titles.cloneNode(true);
    title.querySelectorAll("[id]").forEach(n => n.removeAttribute("id"));
    title.querySelector("h1").textContent = TAB_TITLES[s.peer] || greeting();
    title.setAttribute("aria-hidden", "true");
    title.style.cssText = `position:absolute;left:0;right:0;top:${shift}px;margin:0;translate:${dir * s.width}px 0`;
    titles.style.position = "relative";
    titles.append(title);
    s.title = title;
    s.movers.push(...[...page.children].map(el => [el, dir * s.width]));
  }
  function clearSwipePeer(landed) {
    const s = swipe;
    for (const [el] of s.movers || []) { el.style.translate = ""; el.style.transition = ""; }
    if (s.title) s.title.remove();
    if (s.peer) {
      const page = $("tab-" + s.peer);
      page.style.cssText = "";
      page.hidden = !landed;
    }
    // Landing on the Screen tab keeps the MacBook up while its picture arrives (if it can).
    if (s.revealed && !(landed && data && data.remoteScreen && data.screenPermission)) {
      $("screenControls").hidden = true;
    }
    s.revealed = false;
    document.querySelector(".titles").style.position = "";
    $("app").style.position = "";
    s.movers = s.title = s.peer = null;
  }
  // The pill sits between the two tabs' buttons as far as the page has come.
  function swipePill(progress) {
    const from = document.querySelector(`.tabs button[data-tab="${currentTab}"]`);
    const to = swipe.peer && document.querySelector(`.tabs button[data-tab="${swipe.peer}"]`);
    const x = from.offsetLeft + (to ? (to.offsetLeft - from.offsetLeft) * progress : 0);
    $("tabPill").style.translate = `${x}px 0`;
    tabButtons.forEach(b => b.classList.toggle("active", b === (to && progress > 0.5 ? to : from)));
  }

  $("scroller").addEventListener("pointerdown", e => {
    if ((swipe && swipe.live) || settleSwipe || !e.isPrimary || e.button > 0 || !swipeAllowed(e)) return;
    swipe = { id: e.pointerId, x0: e.clientX, y0: e.clientY, live: false, track: [[e.timeStamp, e.clientX]] };
  });
  addEventListener("pointermove", e => {
    const s = swipe;
    if (!s || s.settling || e.pointerId !== s.id) return;
    const dx = e.clientX - s.x0, dy = e.clientY - s.y0;
    s.track.push([e.timeStamp, e.clientX]);
    if (s.track.length > 6) s.track.shift();
    if (!s.live) {
      // Mostly up or down: it's a scroll (or the finger is busy with something else).
      if (Math.abs(dy) > 10 && Math.abs(dy) >= Math.abs(dx)) { swipe = null; return; }
      if (Math.abs(dx) < 10 || Math.abs(dx) < Math.abs(dy) * 1.3) return;
      s.live = true;
      s.x0 = e.clientX - Math.sign(dx) * 10;  // no jump as the page picks up the finger
      s.width = innerWidth;
      document.body.classList.add("swiping");
      document.querySelector(".tabbar").classList.add("no-anim");
    }
    const off = e.clientX - s.x0;
    setSwipePeer(off < 0 ? 1 : -1);
    // With a page that way it follows the finger; at either end the page stretches and resists.
    const moved = s.peer
      ? Math.max(-s.width, Math.min(s.width, off))
      : Math.sign(off) * (1 - 1 / (Math.abs(off) * 0.55 / s.width + 1)) * s.width;
    s.off = moved;
    placeSwipe(moved);
    swipePill(Math.abs(moved) / s.width);
  });
  // While a swipe is under way, the browser mustn't start scrolling the page as well.
  document.addEventListener("touchmove", e => { if (swipe && swipe.live) e.preventDefault(); }, { passive: false });

  function endSwipe(e) {
    const s = swipe;
    if (!s || s.settling || e.pointerId !== s.id) return;
    if (!s.live) { swipe = null; return; }
    s.settling = true;  // kept until the page has settled
    document.body.classList.remove("swiping");
    // Speed over the last moments: a quick flick turns the page even if it only moved a little.
    const [t0, x0] = s.track[0], [t1, x1] = s.track[s.track.length - 1];
    const speed = t1 > t0 && e.timeStamp - t1 < 100 ? (x1 - x0) / (t1 - t0) : 0;  // px per ms, + = rightward
    const off = s.off || 0, toward = -s.dir;  // the direction the finger moves to bring the new page in
    const go = e.type === "pointerup" && s.peer &&
      (Math.abs(off) > s.width * 0.3 ? speed * toward > -0.3 : speed * toward > 0.3 && Math.abs(off) > 16);
    const target = go ? toward * s.width : 0;
    const ms = Math.round(Math.max(220, Math.min(420, Math.abs(target - off) / Math.max(Math.abs(speed), 1.4) * 1.6)));
    for (const [el] of s.movers) el.style.transition = `translate ${ms}ms ${SWIPE_EASE}`;
    placeSwipe(target);
    document.querySelector(".tabbar").classList.remove("no-anim");
    swipePill(go ? 1 : 0);
    // A swipe that started on a button or an app icon isn't a tap on it.
    const swallow = ev => { ev.stopPropagation(); ev.preventDefault(); };
    addEventListener("click", swallow, true);
    setTimeout(() => removeEventListener("click", swallow, true), 400);

    const timer = setTimeout(() => settleSwipe(), ms + 20);
    settleSwipe = () => {
      clearTimeout(timer);
      settleSwipe = null;
      const peer = s.peer, scrollAfter = s.scrollAfter;
      clearSwipePeer(go);
      swipe = null;
      if (go) {
        showTab(peer, true);
        $("scroller").scrollTop = scrollAfter;
      } else movePill(true);
      tabButtons.forEach(b => b.classList.toggle("active", b.dataset.tab === (currentTab === "settings" ? "status" : currentTab)));
    };
  }
  addEventListener("pointerup", endSwipe);
  addEventListener("pointercancel", endSwipe);

  // Slide a finger along the tab bar: the pill follows it and lands on the nearest tab.
  {
    const bar = document.querySelector(".tabbar");
    let slide = null;
    const nearest = x => [...tabButtons].reduce((a, b) =>
      Math.abs(b.offsetLeft - x) < Math.abs(a.offsetLeft - x) ? b : a);
    bar.addEventListener("pointerdown", e => {
      if (!e.isPrimary || e.button > 0) return;
      const active = document.querySelector(".tabs button.active");
      slide = { id: e.pointerId, x0: e.clientX, from: active ? active.offsetLeft : 0, live: false };
    });
    addEventListener("pointermove", e => {
      if (!slide || e.pointerId !== slide.id) return;
      const dx = e.clientX - slide.x0;
      if (!slide.live && Math.abs(dx) < 8) return;
      if (!slide.live) { slide.live = true; bar.classList.add("dragging", "no-anim"); }
      const first = tabButtons[0].offsetLeft, last = tabButtons[tabButtons.length - 1].offsetLeft;
      // Past either end the pill only gives a little, more slowly the further the finger goes.
      const want = slide.from + dx, past = want < first ? want - first : want > last ? want - last : 0;
      const x = slide.x = Math.max(first, Math.min(last, want)) + Math.sign(past) * 8 * (1 - 1 / (1 + Math.abs(past) / 40));
      $("tabPill").style.translate = `${x}px 0`;
      const near = nearest(x);
      tabButtons.forEach(b => b.classList.toggle("active", b === near));
    });
    const end = e => {
      if (!slide || e.pointerId !== slide.id) return;
      const s = slide;
      slide = null;
      if (!s.live) return;
      bar.classList.remove("dragging", "no-anim");
      const near = nearest(s.x);
      if (e.type === "pointerup" && near.dataset.tab !== currentTab) showTab(near.dataset.tab);
      else { tabButtons.forEach(b => b.classList.toggle("active", b.dataset.tab === (currentTab === "settings" ? "status" : currentTab))); movePill(true); }
      // The finger lifting over a button isn't a tap on it.
      const swallow = ev => { ev.stopPropagation(); ev.preventDefault(); };
      addEventListener("click", swallow, true);
      setTimeout(() => removeEventListener("click", swallow, true), 400);
    };
    addEventListener("pointerup", end);
    addEventListener("pointercancel", end);
  }

  // ---------- Screen frames ----------
  let framesRunning = false, frameUrl = null, frameId = 0;
  function setScreenMsg(text) {
    $("screenMsg").textContent = text;
    $("screenMsg").hidden = !text;
    updateScreenSkel();
  }
  // Grey MacBook while the first picture is on its way (also while the Mac wakes its screen),
  // but not over a problem the owner needs to read, like screen access being turned off.
  function updateScreenSkel() {
    const msg = $("screenMsg").hidden ? "" : $("screenMsg").textContent;
    const wasShowing = !$("screenSkel").hidden;
    $("screenSkel").hidden = !$("screenControls").hidden || (!!msg && !msg.startsWith("Waking"));
    if (wasShowing && !$("screenControls").hidden) {  // the picture fades in
      $("screenControls").classList.add("tab-enter");
      $("screenControls").addEventListener("animationend", () => $("screenControls").classList.remove("tab-enter"), { once: true });
    }
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
  function setQuality(q, silent = false) {
    if (q !== quality && !silent) tick();
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
    let shown = null;  // the stop lit up under the finger; each new one ticks
    const preview = f => {
      const q = Q_ORDER[Math.round(Math.min(1, Math.max(0, f)) * Q_LAST)];
      if (shown !== null && q !== shown) tick();
      shown = q;
      document.querySelectorAll("[data-q]").forEach(b => b.classList.toggle("active", b.dataset.q === q));
    };
    track.addEventListener("pointerdown", e => {
      dragging = true;
      shown = quality;
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
      if (q !== shown) tick();  // landed on a stop the finger hadn't lit up yet
      if (q === quality) showSlider(q); else setQuality(q, true);  // the drag already ticked
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
          // Access turned off or permission missing: hide the old picture and controls.
          if (res.status === 403) $("screenControls").hidden = true;
          setScreenMsg(await res.text());
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
  // A tap is a click; holding still for 0.8 s is a right-click, shown by a ring that fills
  // under the finger (lift before it closes and it stays a normal click).
  const holdRing = document.createElement("div");
  holdRing.className = "hold-ring";
  document.body.append(holdRing);
  function showHoldRing(x, y) {
    holdRing.className = "hold-ring";
    holdRing.style.left = x + "px";
    holdRing.style.top = y + "px";
    void holdRing.offsetWidth;  // restart the fill
    holdRing.classList.add("on");
  }
  function hideHoldRing() { holdRing.className = "hold-ring"; }
  function holdRingDone() {
    holdRing.className = "hold-ring done";
    setTimeout(() => { if (holdRing.classList.contains("done")) hideHoldRing(); }, 350);
  }
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
      hideHoldRing();
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
    showHoldRing(e.clientX, e.clientY);
    holdTimer = setTimeout(() => {
      held = true;
      holdRingDone();
      send({ type: "click", button: "right", ...p });
      tick();
    }, 800);
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
      hideHoldRing();
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
    if (!held) hideHoldRing();
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
  // When the second finger landed (ms after the first); a right-click needs both together.
  let secondFingerAt = null;
  // A short label on the trackpad saying what a tap did ("Right-click"), so nothing is a surprise.
  const padNote = document.createElement("span");
  padNote.className = "pad-note";
  function flashPadNote(text) {
    padNote.textContent = text;
    pad.append(padNote);
    padNote.classList.remove("show");
    void padNote.offsetWidth;
    padNote.classList.add("show");
  }
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
      padFingers = 0; padMoved = false; gestureDone = false; threeStart = null; secondFingerAt = null;
      // A quick second tap that then slides becomes a drag.
      pendingDrag = e.touches.length === 1 && Date.now() - lastPadTap < 300;
    }
    if (e.touches.length === 2 && secondFingerAt == null) secondFingerAt = Date.now() - padStart.time;
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
    } else if (!padMoved && quick && (padFingers === 1 || (padFingers === 2 && !(secondFingerAt < 150)))) {
      // One finger, or a tap that another finger brushed a moment later: a normal click.
      send({ type: "click", button: "left", count: pendingDrag ? 2 : 1 });
      lastPadTap = pendingDrag ? 0 : Date.now();
    } else if (!padMoved && quick && padFingers === 2 && secondFingerAt != null && secondFingerAt < 150) {
      // Both fingers came down together: a deliberate two-finger tap, not a finger brushing the pad.
      send({ type: "click", button: "right" });
      flashPadNote("Right-click");
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
    if (full) wakeFloatBar(); else clearTimeout(barTimer);
    // The picture changes size, so start the zoom fresh.
    zoom = 1; panX = panY = 0; applyZoom();
  }
  landscapeQuery.addEventListener("change", updateLandscape);
  $("padModeBtn").addEventListener("click", () => {
    trackpadMode = !trackpadMode;
    $("padModeBtn").classList.toggle("on", trackpadMode);
    if (trackpadMode) { zoom = 1; panX = panY = 0; applyZoom(); }
  });
  // Just watching: the button bar fades after 3 quiet seconds. Any touch brings it back
  // (a tap on the picture still clicks; the black strips beside it don't). It stays up
  // while typing or while ⌘/⌥ is on, so those never disappear mid-use.
  let barTimer = null;
  function wakeFloatBar() {
    $("floatBar").classList.remove("faded");
    clearTimeout(barTimer);
    barTimer = setTimeout(fadeFloatBar, 3000);
  }
  function fadeFloatBar() {
    if (document.activeElement === liveInput || latched.size) { barTimer = setTimeout(fadeFloatBar, 3000); return; }
    $("floatBar").classList.add("faded");
  }
  document.addEventListener("pointerdown", () => {
    if (document.body.classList.contains("landscape-full")) wakeFloatBar();
  }, { capture: true, passive: true });

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
    tick();
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
  $("claudeSpark").addEventListener("click", () => {
    const spark = $("claudeSpark");
    spark.classList.remove("go");
    void spark.offsetWidth;  // restart the animation on every tap
    spark.classList.add("go");
  });
  $("claudeSpark").addEventListener("animationend", e => { if (e.target.tagName === "svg") $("claudeSpark").classList.remove("go"); });
  // Numbers come from Claude Code after each reply, so show how old they are.
  function renderClaude() {
    const c = data.claude;
    $("claudeSection").hidden = !c || (!c.fiveHour && !c.sevenDay);
    try { localStorage.setItem("claudeShown", $("claudeSection").hidden ? "0" : "1"); } catch (e) {}
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
        addTickPad(b);
        b.setAttribute("aria-label", "Open " + a.name);
        b.addEventListener("click", () => { if (!editingApps && !slideOpened) openApp(a, b); });
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
    renderDock();
    $("appsEmpty").hidden = apps.length > 0;
    $("appsFoot").hidden = apps.length === 0;
  }
  // A short note above the dock ("Opened Notes", "Hid Finder"): shown 1.5 s, then it fades away.
  function showOpened(message) {
    const bar = $("openedBar");
    $("openedText").textContent = message;
    bar.classList.remove("fade-out");
    bar.hidden = true;
    void bar.offsetWidth;  // restart the pop-in when a new note replaces one on screen
    bar.hidden = false;
    clearTimeout(openedTimer);
    openedTimer = setTimeout(() => {
      bar.classList.add("fade-out");
      openedTimer = setTimeout(() => { bar.hidden = true; bar.classList.remove("fade-out"); }, 500);
    }, 1500);
  }

  // The dock: other apps open on the Mac. A tap brings one to the front, like the Mac's Dock.
  let dockKey = "";
  function renderDock() {
    const others = data.otherApps || [];
    const key = others.map(a => a.id).join();
    if (key !== dockKey) {
      dockKey = key;
      $("macDock").replaceChildren(...others.map(a => {
        const b = document.createElement("button");
        b.type = "button";
        b.setAttribute("aria-label", "Bring " + a.name + " to the front");
        const img = document.createElement("img");
        img.src = "app-icon.png?id=" + encodeURIComponent(a.id);
        img.alt = "";
        const dot = document.createElement("span");
        dot.className = "dot";
        b.append(img, dot);
        addTickPad(b);
        b.addEventListener("click", () => { if (dockHeld) { dockHeld = false; return; } focusApp(a, b); });
        // Slide up: bring it to the front and zoom into the Screen tab. Hold: Hide / Quit menu.
        let holdTimer = null;
        b.addEventListener("pointerdown", e => {
          dockHeld = false;
          const start = { x: e.clientX, y: e.clientY };
          const cancel = ev => {
            const up = start.y - ev.clientY;
            if (up > 44 && up > Math.abs(ev.clientX - start.x)) {
              end();
              dockHeld = true;  // the click that follows isn't a plain tap
              dockSlideOpen(a, b);
            } else if (up < 8 && Math.hypot(ev.clientX - start.x, ev.clientY - start.y) > 10) end();
            else if (up >= 8) clearTimeout(holdTimer);
          };
          const end = () => { clearTimeout(holdTimer); removeEventListener("pointermove", cancel); removeEventListener("pointerup", end); removeEventListener("pointercancel", end); };
          addEventListener("pointermove", cancel);
          addEventListener("pointerup", end);
          addEventListener("pointercancel", end);
          holdTimer = setTimeout(() => { end(); dockHeld = true; showDockMenu(a, b); }, 450);
        });
        b.addEventListener("contextmenu", e => e.preventDefault());
        return b;
      }));
    }
    $("macDock").hidden = others.length === 0;
  }
  let dockHeld = false;  // the hold opened a menu, so the tap that ends it isn't a click
  function showDockMenu(app, button) {
    closeDockMenu();
    tick();
    const shade = document.createElement("div");
    shade.className = "dock-shade";
    shade.addEventListener("pointerdown", e => { e.preventDefault(); closeDockMenu(); });
    const menu = document.createElement("div");
    menu.className = "dock-menu";
    menu.setAttribute("role", "menu");
    const title = document.createElement("div");
    title.className = "menu-title";
    title.textContent = app.name;
    menu.append(title);
    for (const [label, action, danger] of [["Hide", "hide"], ["Quit", "quit", true]]) {
      const item = document.createElement("button");
      item.type = "button";
      item.setAttribute("role", "menuitem");
      item.textContent = label;
      if (danger) item.className = "danger";
      addTickPad(item);
      item.addEventListener("click", async () => {
        closeDockMenu();
        focusApp(app, button, action);
      });
      menu.append(item);
    }
    document.body.append(shade, menu);
    const r = button.getBoundingClientRect(), w = menu.offsetWidth;
    menu.style.left = Math.min(Math.max(12, r.left + r.width / 2 - w / 2), innerWidth - w - 12) + "px";
    menu.style.top = (r.top - menu.offsetHeight - 10) + "px";
  }
  function dockSlideOpen(app, button) {
    focusApp(app, button);
    if (matchMedia("(prefers-reduced-motion: reduce)").matches) return showTab("screen");
    const tab = $("tab-apps");
    tab.classList.add("dock-focusing");
    button.classList.add("focused");
    zoomIntoScreen(button.querySelector("img"), "app-icon.png?size=512&id=" + encodeURIComponent(app.id), () => {
      tab.classList.remove("dock-focusing");
      button.classList.remove("focused");
    });
  }
  function closeDockMenu() { document.querySelectorAll(".dock-menu, .dock-shade").forEach(n => n.remove()); }

  async function focusApp(app, button, action = "focus") {
    if (action === "focus") tick();
    let message = { focus: `Switched to ${app.name}`, hide: `Hid ${app.name}`, quit: `Asked ${app.name} to quit` }[action];
    try {
      const res = await fetch("focus-app", {
        method: "POST",
        headers: { "Content-Type": "application/json", "X-LidAwake": "1" },
        body: JSON.stringify({ id: app.id, action }),
      });
      if (res.status === 401 || res.status === 423) return locked();
      if (!res.ok) message = await res.text();
    } catch (e) { message = "Can't reach your Mac."; }
    if (action === "focus" && message.startsWith("Switched")) {
      button.classList.remove("launched");
      void button.offsetWidth;
      button.classList.add("launched");
    }
    showOpened(message);
    poll();
  }

  async function openApp(app, button) {
    tick();
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
    showOpened(message);
    poll();
  }

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
    // Sliding up instead opens the app and zooms into the Screen tab to watch it.
    const start = { x: e.clientX, y: e.clientY, id: e.pointerId };
    const origin = { x: e.clientX, y: e.clientY };
    const img = app.querySelector("img");
    const stop = () => {
      clearTimeout(appHoldTimer);
      ["pointermove", "pointerup", "pointercancel"].forEach(t => removeEventListener(t, watch));
      app.classList.remove("lifting");
      img.style.transform = "";
    };
    const watch = ev => {
      if (ev.pointerId !== start.id) return;
      if (ev.type === "pointermove") {
        const up = origin.y - ev.clientY, side = Math.abs(ev.clientX - origin.x);
        if (up > 8 && up > side) {
          // Following the finger upward: the icon lifts a little and grows.
          clearTimeout(appHoldTimer);
          if (!app.classList.contains("lifting")) new Image().src = bigIcon(app);
          app.classList.add("lifting");
          img.style.transform = `translateY(${-Math.min(up, 60) * 0.5}px) scale(${1 + Math.min(up, 60) / 300})`;
          if (up > 44) { stop(); slideOpen(app); }
          return;
        }
        if (Math.hypot(ev.clientX - start.x, ev.clientY - start.y) > 10) stop();
        else { start.x = ev.clientX; start.y = ev.clientY; }
      } else stop();
    };
    ["pointermove", "pointerup", "pointercancel"].forEach(t => addEventListener(t, watch));
    appHoldTimer = setTimeout(() => {
      stop();
      tick();
      setEditing(true);
      startDrag(app, start.id, start.x, start.y);
    }, 450);
  });

  // Slide up on an icon: open the app, zoom the icon to fill the screen, land on the Screen tab.
  let slideOpened = false;
  // The sharper icon for the big zoom, fetched as soon as the finger starts to slide.
  const bigIcon = button => "app-icon.png?size=512&id=" + encodeURIComponent(button.dataset.id);
  function slideOpen(button) {
    const app = (data.apps || []).find(a => a.id === button.dataset.id);
    if (!app) return;
    slideOpened = true;
    setTimeout(() => { slideOpened = false; }, 600);
    openApp(app, button);
    if (matchMedia("(prefers-reduced-motion: reduce)").matches) return showTab("screen");
    appGrid.classList.add("focusing");
    button.classList.add("focused");
    zoomIntoScreen(button.querySelector("img"), bigIcon(button), () => {
      appGrid.classList.remove("focusing");
      button.classList.remove("focused");
    });
  }

  // The zoom shared by the grid and the dock: the icon floats to the middle and grows (drawn at its
  // final size from the sharper icon), then the Screen tab appears and the icon fades.
  function zoomIntoScreen(img, big, done) {
    const r = img.getBoundingClientRect();
    const size = Math.min(innerWidth * 0.86, 360);
    const zoom = document.createElement("div");
    zoom.className = "app-zoom";
    zoom.style.cssText = `left:${(innerWidth - size) / 2}px;top:${(innerHeight - size) / 2}px;width:${size}px;height:${size}px;`
      + `background-image:url("${big}"),url("${img.src}")`;
    document.body.append(zoom);
    const dx = r.left + r.width / 2 - innerWidth / 2, dy = r.top + r.height / 2 - innerHeight / 2;
    zoom.animate([
      { transform: `translate(${dx}px,${dy}px) scale(${r.width / size})` },
      { transform: "translate(0,0) scale(1)" },
    ], { duration: 440, easing: SPRING, fill: "forwards" });
    // Timers rather than animation events, so the page always moves on (and the layer always goes).
    setTimeout(() => {
      showTab("screen");
      zoom.animate([{ opacity: 1 }, { opacity: 0 }], { duration: 260, easing: "ease-out", fill: "forwards" });
    }, 420);
    setTimeout(() => {
      zoom.remove();
      $("openedBar").hidden = true;  // already watching it; no "Show screen" offer needed
      done();
    }, 750);
  }

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
    showOpened(message);
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
  const ALERT_KINDS = ["turnedOff", "lowBattery", "claudeUsage", "claudeSession", "warm"];
  const standalone = () => matchMedia("(display-mode: standalone)").matches || navigator.standalone === true;

  // ---------- The Mac drawing on Status ----------
  // Lid closed/open and screen on/off come from the Mac; the lit screen shows the sky of the moment.
  let macPicSky = "";
  function renderMacPic() {
    const closed = !!data.lidClosed, dark = closed || !!data.displayAsleep || !!data.screenOff;
    const pic = $("macPic");
    pic.classList.toggle("closed", closed);
    pic.classList.toggle("dark", dark);
    pic.classList.toggle("charging", !!data.charging);
    pic.classList.toggle("plugged", data.onBattery === false);
    const kind = skyKind(new Date().getHours());
    if (kind !== macPicSky) { macPicSky = kind; $("macPicScreen").style.backgroundImage = `url(/sky/${kind}.jpg)`; }
    const cap = $("macPicCaption");
    cap.textContent = (closed ? "Lid closed" : dark ? "Lid open · Screen off" : "Lid open · Screen on")
      + (data.onBattery == null ? "" : " · ");
    if (data.charging) cap.appendChild(document.createElement("b")).textContent = "Charging";
    else if (data.onBattery != null) cap.append(data.onBattery ? "On battery" : "Plugged in");
  }

  // ---------- Opening animation (home-screen app only, once per launch) ----------
  // The sky of the moment as a small landscape in the style of macOS wallpapers: layered ridges
  // fading into haze, a glowing sun or moon, soft clouds by day and a field of stars at night.
  function skyKind(h) {
    return h >= 5 && h < 8 ? "sunrise" : h >= 8 && h < 17 ? "day" : h >= 17 && h < 20 ? "sunset" : "night";
  }
  function skyScene(h) {
    const kind = skyKind(h);
    const P = {
      sunrise: { sky: ["#2b3a6b", "#8a6aa3", "#f2a0a1", "#ffd3a1"], orb: [168, 92, 18, "#fff1d6", "#ffb88a"],
                 ridges: ["#8e7aa8", "#6b5a8c", "#4c3f6e", "#2e2647"], haze: "#ffc7a8" },
      day:     { sky: ["#1f6fd1", "#4b97e6", "#8fc3f2", "#d6ecfb"], orb: [230, 36, 16, "#ffffff", "#fff2c2"],
                 ridges: ["#9cc0de", "#6f9cc4", "#4a7aa6", "#2f5a82"], haze: "#e8f4fd" },
      sunset:  { sky: ["#1e1b4b", "#5b2b6e", "#d35d6e", "#f8b26a"], orb: [96, 104, 20, "#fff0c9", "#ff9e5e"],
                 ridges: ["#a4506f", "#7a3a62", "#4f2750", "#2a1636"], haze: "#ffb07a" },
      night:   { sky: ["#03071a", "#0a1638", "#16244f", "#25305e"], orb: [214, 40, 13, "#f4f6ff", "#b9c6ff"],
                 ridges: ["#26305a", "#1b2347", "#121935", "#0a0f24"], haze: "#3a4a85" },
    }[kind];
    const [ox, oy, r, core, glow] = P.orb;
    const id = "s" + kind;
    let stars = "";
    if (kind === "night" || kind === "sunset") {
      const n = kind === "night" ? 70 : 14;
      for (let i = 0; i < n; i++) {
        const x = (i * 97.3 + 13) % 300, y = (i * 53.7 + 7) % (kind === "night" ? 120 : 60);
        const rr = (i % 7 === 0 ? 1.1 : i % 3 === 0 ? 0.7 : 0.45);
        stars += `<circle cx="${x.toFixed(1)}" cy="${y.toFixed(1)}" r="${rr}" fill="#fff" opacity="${(0.35 + (i % 5) * 0.13).toFixed(2)}"/>`;
      }
    }
    const clouds = kind === "day" ? `<g filter="url(#${id}b2)" fill="#fff" opacity=".9">
        <ellipse cx="70" cy="62" rx="34" ry="9"/><ellipse cx="88" cy="56" rx="20" ry="9"/><ellipse cx="58" cy="58" rx="14" ry="7"/>
        <ellipse cx="190" cy="80" rx="26" ry="6" opacity=".7"/><ellipse cx="204" cy="76" rx="14" ry="6" opacity=".7"/></g>` : "";
    const ridge = (d, fill) => `<path d="${d}" fill="${fill}"/>`;
    return `<svg viewBox="0 0 300 190" preserveAspectRatio="xMidYMid slice" style="position:absolute;inset:0;width:100%;height:100%">
      <defs>
        <linearGradient id="${id}g" x1="0" y1="0" x2="0" y2="1">${P.sky.map((c, i) => `<stop offset="${i / 3}" stop-color="${c}"/>`).join("")}</linearGradient>
        <radialGradient id="${id}o"><stop offset="0" stop-color="${core}"/><stop offset=".55" stop-color="${core}"/><stop offset="1" stop-color="${glow}" stop-opacity="0"/></radialGradient>
        <radialGradient id="${id}h" cx=".5" cy="1" r=".8"><stop offset="0" stop-color="${P.haze}" stop-opacity=".55"/><stop offset="1" stop-color="${P.haze}" stop-opacity="0"/></radialGradient>
        <filter id="${id}b" x="-100%" y="-100%" width="300%" height="300%"><feGaussianBlur stdDeviation="9"/></filter>
        <filter id="${id}b2" x="-50%" y="-100%" width="200%" height="300%"><feGaussianBlur stdDeviation="2.4"/></filter>
      </defs>
      <rect width="300" height="190" fill="url(#${id}g)"/>
      ${stars}
      <circle cx="${ox}" cy="${oy}" r="${r * (kind === "day" ? 2 : 2.6)}" fill="${glow}" opacity="${kind === "day" ? .28 : .32}" filter="url(#${id}b)"/>
      <circle cx="${ox}" cy="${oy}" r="${r}" fill="url(#${id}o)"/>
      ${clouds}
      <rect y="80" width="300" height="110" fill="url(#${id}h)"/>
      ${ridge("M0 128 C40 110 70 104 110 116 S180 96 220 110 S280 104 300 112 V190 H0Z", P.ridges[0])}
      ${ridge("M0 142 C30 130 60 122 96 134 S150 120 196 136 S262 124 300 132 V190 H0Z", P.ridges[1])}
      ${ridge("M0 156 C46 144 80 146 120 154 S190 140 236 152 S280 148 300 150 V190 H0Z", P.ridges[2])}
      ${ridge("M0 172 C50 162 92 166 140 172 S220 160 300 168 V190 H0Z", P.ridges[3])}
    </svg>`
      // A real photo of the same moment on top (the drawing shows until it has loaded).
      + `<img src="/sky/${kind}.jpg" alt="" style="position:absolute;inset:0;width:100%;height:100%;object-fit:cover">`;
  }

  (function playIntro() {
    const intro = $("intro");
    if (!document.documentElement.classList.contains("intro")) return intro.remove();
    try { sessionStorage.setItem("introShown", "1"); } catch (e) {}
    $("introSky").innerHTML = skyScene(new Date().getHours());
    const done = () => { intro.remove(); document.documentElement.classList.remove("intro"); };
    intro.addEventListener("click", done);       // tap to skip
    setTimeout(done, 2600);
  })();
  // Install the service worker on every visit (not only with alerts), so the home-screen app
  // keeps a copy of this page and still opens while the Mac is off.
  if ("serviceWorker" in navigator && location.protocol === "https:") {
    navigator.serviceWorker.register("/sw.js", { scope: "/" }).catch(() => {});
  }
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
    tick();
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
    tick();
    limitSaving = true;
    try { await postJSON("limits", { name: b.dataset.limit, on }); }
    catch (e) { b.setAttribute("aria-checked", !on); }
    limitSaving = false;
    poll();
  }));

  // ---------- Tiny vibrations ----------
  // An iOS switch (<input type=checkbox switch>) plays the system's light tick when it flips, but
  // only for a real finger tap (a script flipping it is silent, and navigator.vibrate does nothing
  // on iPhone). So tappable things get an invisible layer that hands the tap to a hidden switch;
  // the tap still reaches the button as usual. Holds and drags can't tick on iPhone.
  // Other phones get navigator.vibrate from tick(). On/off is kept on this phone only.
  function hapticsOn() { try { return localStorage.getItem("haptics") !== "off"; } catch (e) { return true; } }
  function addTickPad(el) {
    const pad = document.createElement("label");
    pad.className = "tick-pad";
    pad.setAttribute("aria-hidden", "true");
    const input = document.createElement("input");
    input.type = "checkbox";
    input.setAttribute("switch", "");
    input.tabIndex = -1;
    // The label passes its click on to the switch; that copy mustn't reach the button a second time.
    input.addEventListener("click", e => e.stopPropagation());
    pad.append(input);
    el.append(pad);
  }
  function tick() {
    if (hapticsOn() && navigator.vibrate) navigator.vibrate(8);
  }
  function showHaptics() {
    $("hapticsSwitch").setAttribute("aria-checked", hapticsOn());
    document.body.classList.toggle("no-haptics", !hapticsOn());
  }
  document.querySelectorAll(".mini-switch, #awakeSwitch, [data-q], #claudeSpark").forEach(addTickPad);
  $("hapticsSwitch").addEventListener("click", () => {
    try { localStorage.setItem("haptics", hapticsOn() ? "off" : "on"); } catch (e) {}
    showHaptics();
  });
  showHaptics();

  // ---------- Temperature history: tap the Temperature tile ----------
  // The Mac reads its own power log (about one battery reading a minute), so there's history
  // from the first tap. Drag a finger along the graph to read any moment, like in Weather.
  let tempPoints = null, tempFetchedAt = 0, tempFailed = false, tempHours = 6, tempGraph = null, tempTimer = 0, scrubbing = false;
  try { tempHours = +localStorage.getItem("tempHours") || 6; } catch (e) {}
  const SVG_NS = "http://www.w3.org/2000/svg";
  const clock = (sec, withMinutes = true) =>
    new Date(sec * 1000).toLocaleTimeString([], withMinutes ? { hour: "numeric", minute: "2-digit" } : { hour: "numeric" });
  const tempLimit = () => (data && data.maxBatteryTemp) || 40;
  const tempWord = t => t >= tempLimit() ? "Too hot" : t >= tempLimit() - 4 ? "Getting warm" : "Normal";
  function svgEl(name, attrs, parent) {
    const el = document.createElementNS(SVG_NS, name);
    for (const k in attrs) el.setAttribute(k, attrs[k]);
    if (parent) parent.append(el);
    return el;
  }
  async function loadTempHistory() {
    try {
      const res = await fetchWithin(8000, "temp-history?hours=24", { cache: "no-store" });
      if (!res.ok) throw new Error(res.status);
      tempPoints = (await res.json()).points || [];
      tempFailed = false;
    } catch (e) { tempFailed = !tempPoints; }
    tempFetchedAt = Date.now();
    if (!scrubbing) renderTempGraph();  // a finger on the graph keeps its reading
  }
  function setTempOpen(open) {
    const sheet = $("tempSheet"), shade = $("tempShade");
    sheet.style.removeProperty("--drag");
    sheet.setAttribute("aria-hidden", !open);
    clearInterval(tempTimer);
    if (open) {
      shade.hidden = false;
      requestAnimationFrame(() => { shade.classList.add("show"); sheet.classList.add("open"); });
      renderTempGraph();
      if (Date.now() - tempFetchedAt > 30000) loadTempHistory();
      tempTimer = setInterval(loadTempHistory, 60000);
    } else {
      shade.classList.remove("show");
      sheet.classList.remove("open");
      setTimeout(() => { if (!sheet.classList.contains("open")) shade.hidden = true; }, 350);
    }
  }
  function showTempRange() {
    document.querySelectorAll("#tempRange button").forEach(b => b.setAttribute("aria-checked", +b.dataset.hours === tempHours));
  }
  // The big number: now, or the bar under the finger (its low–high).
  function showTempReading(bin) {
    if (!bin) {
      const t = data && data.batteryTemp != null ? data.batteryTemp : tempPoints && tempPoints.length ? tempPoints[tempPoints.length - 1][1] : null;
      $("tempBig").textContent = t == null ? "—" : t.toFixed(1) + "°";
      $("tempWhen").textContent = t == null ? "" : "Now · " + tempWord(t);
    } else {
      $("tempBig").textContent = bin.lo.toFixed(1) === bin.hi.toFixed(1) ? bin.hi.toFixed(1) + "°" : `${bin.lo.toFixed(1)}–${bin.hi.toFixed(1)}°`;
      const end = Math.min(bin.s + bin.size, Date.now() / 1000);
      $("tempWhen").textContent = `${clock(bin.s).replace(/\s?[AP]\.?M\.?$/i, "")}–${clock(end)}`;
    }
  }
  const spokenMinutes = sec => {
    const m = Math.round(sec / 60);
    return m < 60 ? `${m} min` : m % 60 ? `${Math.floor(m / 60)} h ${m % 60} min` : `${m / 60} h`;
  };
  // Event times: "8:21 PM", or "Yesterday 8:21 PM" on the 24-hour view.
  function eventTime(sec) {
    const d = new Date(sec * 1000), today = new Date();
    return (d.toDateString() === today.toDateString() ? "" : "Yesterday ") + clock(sec);
  }
  // The timeline under the graph, newest first: now, sleeps, the peak and the coolest moment.
  function renderTempStory(pts, hi, lo) {
    const box = $("tempStory");
    box.replaceChildren();
    if (pts.length < 2) { box.hidden = true; return; }
    box.hidden = false;
    const last = pts[pts.length - 1], limit = tempLimit();
    const before = pts.find(p => p[0] >= last[0] - 900) || pts[0];
    const change = (data && data.batteryTemp != null ? data.batteryTemp : last[1]) - before[1];
    const trend = change > .5 ? "rising" : change < -.5 ? "cooling down" : "steady";
    const color = t => t >= limit ? "var(--red)" : t >= limit - 4 ? "var(--amber)" : t >= limit - 7 ? "#ffcc00" : "var(--green)";
    // "Now" is the live reading, same as the big number above (the log can lag a minute).
    const nowT = data && data.batteryTemp != null ? data.batteryTemp : last[1];
    const events = [[last[0] + 1, "Now", `${nowT.toFixed(1)}°, ${trend}`, color(nowT)]];
    for (let i = 1; i < pts.length; i++) {
      const gap = pts[i][0] - pts[i - 1][0];
      if (gap <= 300) continue;
      events.push([pts[i][0], eventTime(pts[i][0]), "Woke up", "var(--blue)"]);
      events.push([pts[i - 1][0], eventTime(pts[i - 1][0]), `Mac slept for ${spokenMinutes(gap)}`, "var(--gray)"]);
    }
    if (hi[1] - lo[1] >= .3) {
      events.push([hi[0], eventTime(hi[0]), `Peaked at ${hi[1].toFixed(1)}°`, color(hi[1])]);
      events.push([lo[0], eventTime(lo[0]), `Coolest, ${lo[1].toFixed(1)}°`, color(lo[1])]);
    }
    events.sort((a, b) => b[0] - a[0]);
    for (const [, when, text, dot] of events.slice(0, 8)) {
      const row = document.createElement("div");
      row.className = "row";
      const d = document.createElement("i"); d.style.background = dot;
      const t = document.createElement("span"); t.className = "title"; t.textContent = text;
      const v = document.createElement("span"); v.className = "value"; v.textContent = when;
      row.append(d, t, v);
      box.append(row);
    }
  }
  // Range bars: the readings grouped into bins (5 min for 1H, 15 min for 6H, 1 hour for 24H),
  // each bar running from that bin's lowest to highest reading, coloured by height.
  function renderTempGraph() {
    showTempRange();
    showTempReading(null);
    const svg = $("tempChart");
    svg.replaceChildren();
    tempGraph = null;
    const size = { 1: 300, 6: 900, 24: 3600 }[tempHours];
    const now = Date.now() / 1000, offset = new Date().getTimezoneOffset() * 60;  // bins on round local times
    const from = Math.floor((now - tempHours * 3600 - offset) / size) * size + offset;
    const n = Math.ceil((now - from) / size), to = from + n * size;
    const pts = (tempPoints || []).filter(p => p[0] >= from);
    const empty = $("tempEmpty");
    empty.hidden = pts.length > 1;
    empty.textContent = tempFailed ? "Couldn't load the history from your Mac." : !tempPoints ? "Loading…" : "No readings for this time yet.";
    let hi = pts[0], lo = pts[0];
    for (const p of pts) { if (p[1] > hi[1]) hi = p; if (p[1] < lo[1]) lo = p; }
    renderTempStory(pts, hi, lo);
    if (pts.length < 2) return;

    const bins = [];
    for (let i = 0; i < n; i++) bins.push({ s: from + i * size, size, lo: Infinity, hi: -Infinity, count: 0 });
    for (const p of pts) {
      const b = bins[Math.min(n - 1, Math.floor((p[0] - from) / size))];
      b.lo = Math.min(b.lo, p[1]); b.hi = Math.max(b.hi, p[1]); b.count++;
    }

    // Scale: the readings with a little room; the turn-off limit joins in once it's near.
    const limit = tempLimit();
    let yMin = Math.floor(lo[1] - .5), yMax = Math.ceil(hi[1] + .5);
    if (yMax - yMin < 4) { const mid = (yMax + yMin) / 2; yMin = Math.floor(mid - 2); yMax = yMin + 4; }
    const showLimit = hi[1] >= limit - 4;
    if (showLimit) yMax = Math.max(yMax, limit + 1);
    const step = [1, 2, 5, 10].find(s => (yMax - yMin) / s <= 4);
    yMin = Math.floor(yMin / step) * step; yMax = Math.ceil(yMax / step) * step;

    const W = svg.getBoundingClientRect().width || 320, H = 240;
    const L = 0, R = W - 34, T = 6, B = H - 24;
    svg.setAttribute("viewBox", `0 0 ${W} ${H}`);
    const x = s => L + (s - from) / (to - from) * (R - L);
    const y = t => B - (t - yMin) / (yMax - yMin) * (B - T);

    // Grid: degrees on the right, times along the bottom.
    for (let t = yMin; t <= yMax + 1e-9; t += step) {
      svgEl("line", { class: "grid", x1: L, x2: R, y1: y(t), y2: y(t) }, svg);
      svgEl("text", { x: R + 8, y: y(t) + 4 }, svg).textContent = t + "°";
    }
    const tickEvery = { 1: 900, 6: 3600, 24: 6 * 3600 }[tempHours];
    for (let s = Math.ceil((from - offset) / tickEvery) * tickEvery + offset; s < to; s += tickEvery) {
      if (x(s) < 14 || x(s) > R - 14) continue;
      const label = tempHours === 1 ? clock(s).replace(/\s?[AP]\.?M\.?$/i, "") : clock(s, false);
      svgEl("text", { x: x(s), y: H - 6, "text-anchor": "middle" }, svg).textContent = label;
    }
    if (showLimit) {
      svgEl("line", { class: "limit", x1: L, x2: R, y1: y(limit), y2: y(limit) }, svg);
      svgEl("text", { class: "limit-label", x: L + 4, y: y(limit) - 6 }, svg).textContent = `Turns off at ${limit}°`;
    }

    // One colour scale for every bar, set by height, like the tile's scale (green, yellow, orange, red).
    const defs = svgEl("defs", {}, svg);
    const grad = svgEl("linearGradient", { id: "tempGrad", gradientUnits: "userSpaceOnUse", x1: 0, x2: 0,
                                           y1: y(20), y2: y(20 + (limit - 20) * 1.25) }, defs);
    for (const [o, c] of [[0, "var(--green)"], [.62, "#ffcc00"], [.8, "var(--amber)"], [1, "var(--red)"]])
      svgEl("stop", { offset: o, style: `stop-color:${c}` }, grad);

    const slot = (R - L) / n, bw = Math.min(14, slot * .62);
    for (const b of bins) {
      const cx = x(b.s) + slot / 2;
      if (!b.count) {
        // Asleep (or not reached yet): a small grey dash on the floor.
        if (b.s < now) b.el = svgEl("rect", { class: "bar-off", x: cx - bw / 2, y: B - 4, width: bw, height: 4, rx: 2 }, svg);
        continue;
      }
      // Steady bins still show as a round dot.
      let top = y(b.hi), bottom = y(b.lo);
      if (bottom - top < bw) { const mid = (top + bottom) / 2; top = mid - bw / 2; bottom = mid + bw / 2; }
      b.el = svgEl("rect", { class: "bar", x: cx - bw / 2, y: top, width: bw, height: bottom - top, rx: bw / 2, fill: "url(#tempGrad)" }, svg);
    }
    tempGraph = { bins: bins.filter(b => b.count), x, slot, svg, left: () => svg.getBoundingClientRect().left, scale: () => W / svg.getBoundingClientRect().width };
  }
  // Scrubbing: the bar nearest the finger stands out, the rest fade.
  function scrubTemp(e) {
    const g = tempGraph;
    if (!g || !g.bins.length) return;
    const px = (e.clientX - g.left()) * g.scale();
    let best = g.bins[0];
    for (const b of g.bins) if (Math.abs(g.x(b.s) + g.slot / 2 - px) < Math.abs(g.x(best.s) + g.slot / 2 - px)) best = b;
    if (g.picked !== best) {
      g.svg.classList.add("picking");
      if (g.picked) g.picked.el.classList.remove("picked");
      best.el.classList.add("picked");
      if (g.picked) tick();
      g.picked = best;
    }
    showTempReading(best);
  }
  function endScrub() {
    scrubbing = false;
    const g = tempGraph;
    if (!g) return;
    g.svg.classList.remove("picking");
    if (g.picked) g.picked.el.classList.remove("picked");
    g.picked = null;
    showTempReading(null);
  }
  const chart = $("tempChart");
  chart.addEventListener("pointerdown", e => { try { chart.setPointerCapture(e.pointerId); } catch (err) {} scrubbing = true; scrubTemp(e); });
  chart.addEventListener("pointermove", e => { if (chart.hasPointerCapture(e.pointerId)) scrubTemp(e); });
  for (const ev of ["pointerup", "pointercancel"]) chart.addEventListener(ev, endScrub);

  $("tempTile").addEventListener("click", () => { if (!document.querySelector("main.waiting")) { tick(); setTempOpen(true); } });
  $("tempTile").addEventListener("keydown", e => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); setTempOpen(true); } });
  addTickPad($("tempTile"));
  $("tempDone").addEventListener("click", () => setTempOpen(false));
  $("tempShade").addEventListener("click", () => setTempOpen(false));
  document.addEventListener("keydown", e => { if (e.key === "Escape" && $("tempSheet").classList.contains("open")) setTempOpen(false); });
  document.querySelectorAll("#tempRange button").forEach(b => b.addEventListener("click", () => {
    tempHours = +b.dataset.hours;
    try { localStorage.setItem("tempHours", tempHours); } catch (e) {}
    tick();
    renderTempGraph();
  }));
  addEventListener("resize", () => { if ($("tempSheet").classList.contains("open")) renderTempGraph(); });
  // Pull the sheet down by its top to close it.
  let sheetDrag = null;
  $("tempHead").addEventListener("pointerdown", e => {
    if (e.target.closest("button")) return;
    sheetDrag = { y: e.clientY, t: performance.now(), dy: 0 };
    $("tempHead").setPointerCapture(e.pointerId);
    $("tempSheet").classList.add("dragging");
  });
  $("tempHead").addEventListener("pointermove", e => {
    if (!sheetDrag) return;
    sheetDrag.dy = Math.max(0, e.clientY - sheetDrag.y);
    $("tempSheet").style.setProperty("--drag", sheetDrag.dy + "px");
  });
  for (const ev of ["pointerup", "pointercancel"]) $("tempHead").addEventListener(ev, () => {
    if (!sheetDrag) return;
    const fast = sheetDrag.dy / (performance.now() - sheetDrag.t) > .5;
    $("tempSheet").classList.remove("dragging");
    if (sheetDrag.dy > 120 || (fast && sheetDrag.dy > 30)) setTempOpen(false);
    else $("tempSheet").style.removeProperty("--drag");
    sheetDrag = null;
  });

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
