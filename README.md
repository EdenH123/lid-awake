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

1. On this page (the GitHub page for Lid Awake), open **Actions** → **Windows** → **Run workflow**. (Until this version is on the main branch, ask whoever maintains your copy for the ZIP.)
2. When the run has finished, open it and download **LidAwake-Windows** from **Artifacts** at the bottom. It's kept for 3 days.
3. Unpack the **whole** ZIP (right-click it → **Extract All**). Inside, **README-FIRST.txt** repeats these steps.
4. Double-click **Install Lid Awake (Windows)**.
   - If Windows says **"Windows protected your PC"**, click **More info** → **Run anyway**.
   - If it asks **"Do you want to run this file?"**, click **Run**.
5. A window shows each step with **[OK]**. If a line shows **[X]**, it tells you what to do (for example "Tailscale is not signed in"). Do it, then open **Install Lid Awake (Windows)** again.

> Already have the source ZIP and the .NET 8 SDK installed? The same installer builds the app for you.

### Finish and add it to your phone

1. Right-click the **Lid Awake** icon in the tray by the clock (it may be under the **^** arrow).
2. Choose **Set phone password…** and pick a password.
3. Click **Lid Awake on** so it shows a tick. The phone page only opens while Lid Awake is on.
4. On your phone, make sure Tailscale is on, then open `https://<pc-name>.<tailnet>.ts.net/` in the browser. You'll find your laptop's exact name in the Tailscale app on the phone (the list of your machines), or at [login.tailscale.com/admin/machines](https://login.tailscale.com/admin/machines).
5. Add it to your Home screen:
   - **iPhone:** in Safari tap **Share** → **Add to Home Screen** → **Add**.
   - **Android (Samsung or similar):** open the browser menu (**⋮** in Chrome, **≡** in Samsung Internet) → **Add to Home screen** (or **Install app**).
6. Open **Lid Awake** from your Home screen and type your phone password.

Each time a new phone signs in, the laptop shows a small alert at the tray: **New sign-in to Lid Awake**.

### Every day on Windows

- **Turn it on:** right-click the tray icon → **Lid Awake on**. You can turn it **off** from your phone too.
- **Turn it on for a while only:** tray icon → **Turn off after** (for example 2 hours). When the time is up, Lid Awake turns itself off.
- **Close the lid:** while Lid Awake is on, the laptop keeps running with the lid closed.
- **See what happened:** tray icon → **Show activity log**.
- **Keep it plugged in.** On Windows, Lid Awake does **not** turn itself off when the battery is low or the laptop gets hot yet.

### What works on Windows so far

- See how the laptop is doing on your phone: battery, memory, processor, network and uptime.
- Keep it running with the lid closed.
- Turn it off from your phone.
- A **Turn off after** timer.
- Sign in with your phone password, with an alert at the tray for each new sign-in.
- See your signed-in devices and sign one out.
- The activity log.

### Not on Windows yet

Don't rely on these on Windows. They work on the Mac only, for now.

- Watching the screen and using the phone as trackpad and keyboard.
- Opening, switching and quitting apps.
- Alerts on your phone.
- Face ID or fingerprint sign-in (you use your password).
- The camera and microphone.
- Battery and temperature history graphs.
- **The low-battery and too-hot safety switches.** Lid Awake will **not** turn itself off if the battery runs low or the laptop gets hot, so keep it plugged in.
- Wi-Fi signal bars.

### Update or remove it on Windows

- **Update:** download the new LidAwake-Windows ZIP (Install it, steps 1–3) and open **Install Lid Awake (Windows)** again. Your password and settings stay as they are.
- **Remove:** double-click **Uninstall Lid Awake (Windows)**. It asks whether to also delete your settings, and puts your laptop's lid setting back the way it was.

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
| **Windows:** the laptop still **sleeps with the lid closed** | First make sure **Lid Awake on** is ticked in the tray menu. If it still sleeps, that laptop's own power app or a company policy is overriding the lid setting, and Lid Awake can't change it there. Open Control Panel → **Power Options** → **Choose what closing the lid does** and set it to **Do nothing** for both **On battery** and **Plugged in**. Or look in the laptop maker's power app. |

---

## Privacy and security

- The phone page lives only on **your** private Tailscale network, never on the open internet, and it only answers **your** Tailscale account.
- Your phone password is never stored, only a scrambled fingerprint of it. Face ID and fingerprint keys stay on your phone.
- The camera and microphone need their own switch, Face ID or fingerprint every time, and turn off by themselves after 10 minutes.
- The installer gives Lid Awake one admin permission only: turning sleep off and on (`pmset -a disablesleep 0/1`).
- Every sign-in, screen view, control and camera use is written to the activity log (cup in the menu bar → **Show activity log**).
- Other programs already running on the laptop could try to reach Lid Awake, but they still need your phone password to see or change anything.
