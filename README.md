# Lid Awake

Lid Awake is a small app that keeps your laptop running with the lid closed, on a MacBook or on a Windows laptop (see [On a Windows laptop](#on-a-windows-laptop)), and a phone app (iPhone or Android, such as Samsung) that lets you use it from anywhere. On the Mac, one switch in the menu bar keeps it awake, with a timer and safety limits that turn it off if the battery runs low or gets too hot. On your phone you can see how the Mac is doing (battery, temperature, memory, network), watch its screen live, use the phone as its trackpad and keyboard, and open, switch or quit its apps. Tap the notch in the MacBook drawing to see and hear the room through the Mac's camera, and hold the mic button to talk through its speakers. Everything goes over your own private Tailscale connection, so only you can reach it, protected by a password, Face ID or fingerprint and a log of every time someone views or controls the Mac.

---

## Install it (about 10 minutes)

### Before you start

You need:

- **A MacBook with macOS 14 (Sonoma) or newer.** To check: click the Apple menu (top left) → **About This Mac**.
- **Your phone** (an iPhone, or an Android phone such as a Samsung).
- **Your Mac's login password** (the one you type to unlock the Mac).

You don't need to know anything about code. You'll only open files and click buttons.

---

### Step 1 — Install Tailscale on your Mac and your phone

Tailscale is a free app that creates a private, encrypted connection between your own devices. Lid Awake uses it so your phone can reach your Mac from anywhere, and nobody else can.

**On your Mac**

1. Go to [tailscale.com/download/mac](https://tailscale.com/download/mac) and download Tailscale.
2. Open it. A small Tailscale icon appears at the top right of your screen (the menu bar).
3. Click that icon → **Log in**, and sign in with your Google, Apple or Microsoft account.

**On your iPhone**

1. Install **Tailscale** from the App Store.
2. Open it and sign in with **the same account** you used on the Mac.
3. When asked, tap **Allow** to add the VPN configuration.

**On an Android phone (Samsung or similar)**

1. Install **Tailscale** from Google Play.
2. Open it and sign in with **the same account** you used on the Mac.
3. When asked, allow the VPN.

**One switch for your account (only once)**

1. On your Mac, open [login.tailscale.com/admin/dns](https://login.tailscale.com/admin/dns).
2. Make sure **MagicDNS** is on.
3. Under **HTTPS Certificates**, click **Enable**.

> Don't worry if you forget this: the installer checks it and opens this page for you if it's needed.

---

### Step 2 — Download Lid Awake

1. On this page, click the green **Code** button → **Download ZIP**.
2. Open your **Downloads** folder and double-click the ZIP file. A folder appears.
3. Move that folder somewhere you'll keep it, for example **Documents**. You'll use it again to update or remove Lid Awake.

---

### Step 3 — Run the installer

1. Open the Lid Awake folder.
2. **Right-click** (or Control-click) the file **Install Lid Awake** → choose **Open**.
3. If macOS asks *"Are you sure you want to open it?"*, click **Open**.
   - If it says it *can't be opened*: go to **System Settings → Privacy & Security**, scroll down, and click **Open Anyway** next to "Install Lid Awake". Then try again.
4. A Terminal window opens and the installer starts. It checks your Mac step by step and shows a green ✓ for each step.
5. When you see **Password:**, type your Mac login password and press **Return**.
   *Nothing appears while you type. That's normal.*
6. If you use **Claude Code**, the last step asks about **Claude alerts on your phone (optional)**. Press **Return** to turn it on: your phone then shows your Claude usage after each reply and alerts you when Claude finishes or needs you. It adds Lid Awake to Claude Code's settings and backs them up first (to `~/.claude/settings.json.lidawake-backup`). Type **n** to skip it. To undo it later, run `python3 tools/claude/setup.py off` in the Lid Awake folder, or use the uninstaller.

**If a step shows a red ✗**, it tells you exactly what to do (for example "Sign in to Tailscale"). Do it, then open **Install Lid Awake** again. It continues where it stopped.

**If macOS offers to install "command line developer tools"**, click **Install**, wait until it finishes (a few minutes), then open **Install Lid Awake** again.

When it's done, you'll see **"Almost done"** and a **cup icon** appears in your Mac's menu bar. You can close the Terminal window.

---

### Step 4 — Finish on the Mac

A window called **Set up Lid Awake** opens by itself. (You can always open it again: click the cup in the menu bar → **Set up phone…**)

1. **Phone password:** type a password (at least 10 characters) twice and click **Save password**. Your phone will ask for it.
2. Click **Turn Lid Awake on**. The phone page only opens while Lid Awake is on.

Everything in the window should now have a green ✓, and a **QR code** appears.

---

### Step 5 — Add it to your phone

Make sure **Tailscale is on** on your phone first (open the Tailscale app; the switch should be on).

**On an iPhone**

1. Open the iPhone **Camera** and point it at the QR code on your Mac. Tap the link that appears. It opens in Safari.
2. In Safari, tap the **Share** button (the square with an arrow) → **Add to Home Screen** → **Add**.
3. Open **Lid Awake** from your Home Screen and type the phone password from Step 4.
4. Optional: in the app, tap the gear ⚙︎ → turn on **Face ID** (to sign in faster) and **Alerts** (battery, Mac turned off, and more).

**On an Android phone (Samsung or similar)**

1. Point the **Camera** at the QR code on your Mac and tap the link (it opens in your browser, such as Chrome or Samsung Internet).
2. Open the browser menu (**⋮** in Chrome, **≡** in Samsung Internet) → **Add to Home screen** (or **Install app**).
3. Open **Lid Awake** from your Home screen and type the phone password from Step 4.
4. Optional: in the app, tap the gear ⚙︎ → turn on **Fingerprint** (to sign in faster) and **Alerts** (battery, Mac turned off, and more).

That's it.

---

### Step 6 (optional) — See and control the Mac's screen from your phone

1. In the **Set up Lid Awake** window, click **Allow screen from phone**.
2. Click **Open Screen Recording settings** and turn on **Lid Awake** in the list.
3. Click **Open Accessibility settings** and turn on **Lid Awake** there too.

Now the **Screen** tab on your phone shows the Mac's screen, and you can use your phone as its trackpad and keyboard.

The **camera and microphone** have their own switch: cup in the menu bar → **Allow camera & microphone from phone**. On your phone, tap the notch at the top of the MacBook drawing (Face ID or fingerprint every time).

---

## On a Windows laptop

Lid Awake also runs on a Windows laptop, with the same phone app (iPhone, or an Android phone such as a Samsung). Windows does **less** than the Mac so far: see the two lists below before you rely on it.

### Before you start

- **A Windows laptop** with Tailscale (free).
- **Your phone**, with Tailscale installed and signed in. Do Step 1 above for the phone, and for the account switch **MagicDNS** and **HTTPS Certificates** (the "One switch for your account" part of [Step 1](#step-1--install-tailscale-on-your-mac-and-your-phone)).
- **Tailscale on the laptop:** download it from [tailscale.com/download/windows](https://tailscale.com/download/windows) and sign in with **the same account** as on your phone.

You don't need to install any programming tools.

### Install it

1. Download the ZIP. On this page (the GitHub page for Lid Awake), open **Releases** on the right side, then the latest release, and download **LidAwake-Windows.zip**. Or use the direct link: [LidAwake-Windows.zip](https://github.com/EdenH123/lid-awake/releases/latest/download/LidAwake-Windows.zip).
2. To test a branch that has no release yet, use Actions instead: open **Actions** → **Windows** → **Run workflow**, choose the branch, and when the run has finished, download **LidAwake-Windows** from **Artifacts** at the bottom of the run. It's kept for 3 days.
3. Unpack the **whole** ZIP (right-click it → **Extract All**). Inside, **README-FIRST.txt** repeats these steps.
4. Double-click **Install Lid Awake (Windows)**.
   - If Windows says **"Windows protected your PC"**, click **More info** → **Run anyway**.
   - If it asks **"Do you want to run this file?"**, click **Run**.
5. A window shows each step with **[OK]**. If a line shows **[X]**, it tells you what to do (for example "Tailscale is not signed in"). Do it, then open **Install Lid Awake (Windows)** again.

> Already have the source ZIP and the .NET 8 SDK installed? The same installer builds the app for you.

### Finish and add it to your phone

1. The **Set up Lid Awake** window opens by itself the first time (until a phone password is set). Later, open it from the tray: right-click the **Lid Awake** icon by the clock → **Set up phone…**.
   - Lid Awake lives by the clock. If you don't see its icon, click **^** next to the clock and drag it onto the taskbar, or turn it on in **Settings → Personalization → Taskbar → Other system tray icons** (the window has a button that opens that page).
2. The window ticks off what the laptop needs: Tailscale installed and signed in, and the private web address (MagicDNS and HTTPS Certificates). Anything missing has a button next to it that fixes it or opens the right page.
3. Type a **phone password** in the two boxes and click **Save password**. (Later you change it from the tray: **Set phone password…**.)
4. Click **Turn Lid Awake on**. The phone page only opens while Lid Awake is on.
5. On your phone, make sure Tailscale is on, then point the **Camera** at the QR code in the window and open the link (Safari on iPhone, your browser on Android).
   - No camera handy? Type the address shown under the code (**Copy address** copies it), `https://<pc-name>.<tailnet>.ts.net/`, into the phone's browser. You'll also find your laptop's exact name in the Tailscale app on the phone (the list of your machines), or at [login.tailscale.com/admin/machines](https://login.tailscale.com/admin/machines).
6. Add it to your Home screen:
   - **iPhone:** in Safari tap **Share** → **Add to Home Screen** → **Add**.
   - **Android (Samsung or similar):** open the browser menu (**⋮** in Chrome, **≡** in Samsung Internet) → **Add to Home screen** (or **Install app**).
7. Open **Lid Awake** from your Home screen and type your phone password.
8. Optional: to watch the screen from the phone, click **Allow screen from phone** at the bottom of the window.

Each time a new phone signs in, the laptop shows a small alert at the tray: **New sign-in to Lid Awake**.

### Every day on Windows

- **Turn it on:** right-click the tray icon → **Lid Awake on**. You can turn it **off** from your phone too; the laptop then says **Turned off from your phone** at the tray.
- **See how it is at a glance:** the tray icon is a moon while Lid Awake is off and a cup while it's on. It changes within a second when your phone uses the laptop: an **eye** while the screen is viewed (also when the phone falls back to still pictures), a **hand** while the phone clicks or types, a **video camera** while the camera is watched. Point at the icon for the same in words, and for the time left on the timer. The menu's first (grey) line says it too, for example **Turns off in 1:23:45**, **On, no timer** or **Screen being viewed from your phone**, and the grey line below the safety limits shows the battery, for example **Battery 80% · full in 1h 5m · 31.2°C** (or **charging** while the battery can't say how long).
- **Turn it on for a while only:** tray icon → **Turn off after** (for example 2 hours). When the time is up, Lid Awake turns itself off.
- **Open the phone page on the laptop:** tray icon → **Open live page** (it needs Tailscale's name for the laptop; until Tailscale has one, a message says so).
- **Signed-in devices:** tray icon → **Signed-in devices** lists each phone or browser signed in, with the day and how (password, or Face ID or fingerprint). Click one to sign it out, or **Sign out all devices**. After too many wrong passwords, sign-in is locked for a while: **Allow password sign-in again** ends that at once. **Show activity log** is there too.
- **Close the lid:** while Lid Awake is on, the laptop keeps running with the lid closed. See **The screen with the lid closed** below.
- **See what happened:** tray icon → **Show activity log**.
- **Change the phone password:** tray icon → **Change phone password…** (it says **Set phone password…** until there is one). A new password signs out every device.
- **Safety limits:** Lid Awake turns itself off when the laptop is on battery below 10%, and won't turn on then (tray icon → **Turn off below 10% battery**). If the laptop reports its temperature, Lid Awake also turns off when the battery gets hotter than 40°C, or the laptop itself (its processor or board) hotter than 90°C (tray icon → **Turn off if battery is hotter than 40°C**). Many laptops don't report either: then that item is greyed out with **(no temperature on this PC)** and nothing watches the heat, so **don't leave the laptop closed in a bag or a tight space**. Both limits are on to start with. You can also change them on your phone (the gear → **Safety limits**).
- **After Windows restarts** (for example for updates), Lid Awake starts off. Turn it on again from the tray.
- **Wi-Fi signal bars need Location.** On Windows, the bars on your phone need Location to be on for Lid Awake. Windows asks once, when you turn Lid Awake on. If you say no, or Location is off, your phone shows "Connected" without bars. You can change it in **Settings → Privacy & security → Location**.
- **After you choose Quit Lid Awake**, start it again from the Start menu (**Lid Awake**).

### What works on Windows so far

- See how the laptop is doing on your phone: battery, memory, processor, network (with Wi-Fi signal bars and the network name) and uptime.
- Keep it running with the lid closed, and tell your phone when the lid is closed or the screen is off.
- Turn it off from your phone.
- A **Turn off after** timer.
- Sign in with your phone password, with an alert at the tray for each new sign-in.
- See your signed-in devices and sign one or all of them out, on the phone or from the tray (**Signed-in devices**), and allow password sign-in again after a lockout.
- A tray icon that shows what is going on: off, on, screen watched, controlled from the phone, camera watched, with the status, the timer's countdown and the battery in the menu and the icon's tooltip.
- Messages at the tray when the phone turns Lid Awake off, when every device is signed out, and when Tailscale Funnel was on (it would put the phone page on the open internet) and Lid Awake turned it off. Each is in the activity log too, like turning **Allow control from phone** on or off.
- The activity log.
- The low-battery safety limit, and the too-hot one on laptops that report a temperature.
- Battery, temperature and processor history graphs. Windows has no power log to read, so Lid Awake records its own, once a minute, while it runs (the last 48 hours). The graphs only cover the time Lid Awake has been running on this PC, with gaps while the PC slept, and the processor graph shows overall use, not use per app.
- Face ID or fingerprint sign-in. On your phone: the gear ⚙︎ → turn on **Face ID** (or fingerprint), then type your password once more. The laptop shows **Face ID or fingerprint added to Lid Awake** at the tray. To remove one, use the tray icon → **Face ID and fingerprint** → click the device. Setting a new phone password removes them all.
- Open, switch to and quit programs from your phone. Right-click the Lid Awake icon, choose **Apps on phone…**, and tick the programs from the Start menu to show on the phone's Apps page. Programs that are open but not ticked show in the phone's dock.
- Alerts on your phone: the laptop turned off (timer or safety limit), battery low, getting warm (on laptops that report a battery temperature), and each new sign-in. Turn them on in the app: gear ⚙︎ → **Alerts**. On an iPhone, the app must be on the Home Screen first.
- Watching the screen live. Tick **Allow screen from phone** in the tray menu first (it starts off). Turning it off, or turning Lid Awake off, closes the picture on your phone at once. If the graphics card's video encoder fails, Lid Awake uses Windows' own instead, for the screen and the camera alike, until it starts again.
- Using the phone as trackpad and keyboard, and locking the PC or turning its screen off from the phone. Turning on **Allow screen from phone** also allows control, until you change **Allow control from phone** in the tray menu yourself (as on the Mac): if you only want to watch, untick it. **Allow control from phone** is greyed out while screen viewing is off. Windows doesn't let any program control a window that runs as administrator, the "Do you want to allow…" prompt, or the lock screen, so do those at the PC.
- On your phone, as on the Mac: how your phone reaches the laptop (**Connection**: direct on the same Wi-Fi, over the internet, or through a relay), how warm the laptop is (Normal, Warm, Hot, Critical, on laptops with a thermal zone), when the battery will be full while charging, the laptop's own name, and the launch screen when you open the app from your iPhone's Home Screen. After you turn Lid Awake off, your phone still shows **Lid Awake is off** for a few seconds before the address goes away.
- Seeing and hearing the room through the laptop's camera and microphone, and talking through its speakers. Tick **Allow camera & microphone from phone** in the tray menu (it needs **Allow screen from phone**, and starts off), then tap the notch in the drawing on your phone (Face ID or fingerprint every time, so turn that on first). A plugged-in USB camera is used before the built-in one, and a phone connected to Windows as a camera (Phone Link) is never used. It closes by itself after 10 minutes. Many laptops switch the built-in camera off with the lid closed: open the lid, or plug in a USB camera. If Windows says no, turn on Settings → Privacy & security → Camera → **Let desktop apps access your camera** (and the same for the microphone).

### The screen with the lid closed

Many laptops switch the built-in screen off when you close the lid, and Windows doesn't let an app stop that. Lid Awake keeps the laptop running, but the screen can still go dark. Your phone then shows the PC as dark, and if you are watching the screen, the picture may stop. To keep the picture, either:

- open the lid a little (a small gap is enough on most laptops), or
- plug in an external monitor, which stays on with the lid closed.

What Lid Awake does for you: while someone watches the screen from the phone, it stops Windows from turning the display off after a while. With the lid closed it also turns the built-in screen's brightness down to the lowest level, and puts it back when you open the lid, turn Lid Awake off, quit it, or sign out. If Lid Awake is stopped by a crash with the lid closed, it puts the brightness back the next time it starts. Some laptops don't let Windows change the brightness; then Lid Awake leaves it alone and says so once in the activity log.

### Not on Windows yet

Don't rely on these on Windows. They work on the Mac only, for now.

- Claude alerts on your phone (usage and sessions).
- The memory window (tapping the memory reading to see what uses it).

### Update or remove it on Windows

- **Update:** download the new LidAwake-Windows.zip (Install it, steps 1–3) and open **Install Lid Awake (Windows)** again. Your password and settings stay as they are. If Lid Awake is on while you update, the update turns it off and puts the lid setting back. Turn it on again afterwards.
- **Remove:** double-click **Uninstall Lid Awake (Windows)**. It asks whether to also delete your settings, and puts your laptop's lid setting back the way it was (and the screen brightness, if a crash left it lowered).

### Make a new release (for the maintainer)

1. Merge the changes to `master`.
2. On GitHub, open **Releases** → **Draft a new release**, create a new tag such as `v1.1.0` on `master`, and click **Publish release**.
3. After about 10 minutes, **LidAwake-Windows.zip** appears on the release. The **Windows release** workflow builds it, runs the tests and the installer test, and attaches it. If a test fails, nothing is attached. To build it again for a release that already exists, open **Actions** → **Windows release** → **Run workflow** and type its tag.

While the repository is private, only people with access to it can download the ZIP.

---

## Every day

- **Turn it on:** click the cup in the Mac's menu bar and flip the switch. You can turn it **off** from your phone too.
- **Turn it on for a while only:** in the same menu, choose **Turn off after** (for example 2 hours).
- **Close the lid:** while Lid Awake is on, your Mac keeps running with the lid closed.
- **Let it charge** if you leave it running for a long time. Lid Awake turns itself off if the battery gets low or too warm (you can change this in the menu).

## Update to a new version

Download the new version (Step 2) and open **Install Lid Awake** again. Your password, settings and phone stay as they are.

## Remove Lid Awake

Open **Uninstall Lid Awake** in the folder (right-click → **Open**). It asks whether to also delete your settings.

If you turned on **Claude alerts on your phone**, the uninstaller also takes Lid Awake out of Claude Code's settings and removes its two scripts, leaving the rest of your Claude Code settings as they are. To undo only that part and keep Lid Awake, open Terminal in the Lid Awake folder and run `python3 tools/claude/setup.py off` (`on` turns it back on, `status` shows whether it's on).

---

## If something doesn't work

| What you see | What to do |
|---|---|
| The installer stops at **Tailscale** | Install Tailscale on the Mac, sign in, then open **Install Lid Awake** again. |
| **"Turn on MagicDNS and HTTPS Certificates"** | On the Tailscale page that opened, turn on MagicDNS, and click **Enable** under HTTPS Certificates. Then open the installer again. |
| **"Install Lid Awake" can't be opened** | System Settings → Privacy & Security → scroll down → **Open Anyway**. |
| The phone says **"Lid Awake is off"** | Turn it on: cup in the Mac's menu bar, or **Turn Lid Awake on** in the setup window. |
| The phone **can't reach the Mac** | Make sure Tailscale is on, on both the Mac and the phone, with the same account. |
| The **Screen** tab stays black | Do Step 6 again: Screen Recording must be on for Lid Awake. |
| The camera says **the lid is closed** | macOS switches off the built-in camera and microphone when the lid is closed. Open the lid, or plug in a USB camera. |
| You **forgot the phone password** | Cup in the menu bar → **Change phone password…** |
| **Windows:** the Lid Awake icon is missing from the tray | Look under the **^** arrow by the clock. If it isn't there, open **Install Lid Awake (Windows)** again. |
| **Windows:** a message says **"Phone page not available"** | Another program is using the port Lid Awake needs (8787). Restart the PC, then turn **Lid Awake on** again. |
| **Windows:** the phone **can't reach the PC** | Make sure Tailscale is on, on both the PC and the phone, with the same account. |
| **Windows:** a message says **"The phone page isn't published yet"** | Tailscale needs your OK first. Tray icon → **Show activity log**: the line **Phone page not published by Tailscale** has a link from Tailscale. Open it and allow what it asks. Lid Awake tries again within a minute. |
| **Windows:** the laptop still **sleeps with the lid closed**, or a message says **"Couldn't change what closing the lid does"** | Keep the lid open until it's fixed. 1) Make sure **Lid Awake on** is ticked in the tray menu. 2) Some laptops have their own power app from the maker that switches power plans or overrides the lid setting: check it there. 3) If you ever change **Choose what closing the lid does** in Control Panel by hand, do it only while Lid Awake is on, so turning it off puts your setting back. |

---

## Privacy and security

- The phone page lives only on **your** private Tailscale network, never on the open internet, and it only answers **your** Tailscale account.
- Your phone password is never stored, only a scrambled fingerprint of it. Face ID and fingerprint keys stay on your phone.
- The camera and microphone need their own switch, Face ID or fingerprint every time, and turn off by themselves after 10 minutes.
- The installer gives Lid Awake one admin permission only: turning sleep off and on (`pmset -a disablesleep 0/1`).
- Every sign-in, screen view, control and camera use is written to the activity log (cup in the menu bar → **Show activity log**).
- Other programs already running on the laptop could try to reach Lid Awake, but they still need your phone password to see or change anything.
