# Lid Awake

Lid Awake is a small Mac app that keeps your MacBook running with the lid closed, and an iPhone app that lets you use it from anywhere. On the Mac, one switch in the menu bar keeps it awake, with a timer and safety limits that turn it off if the battery runs low or gets too hot. On your iPhone you can see how the Mac is doing (battery, temperature, memory, network), watch its screen live, use the phone as its trackpad and keyboard, and open, switch or quit its apps. Tap the notch in the MacBook drawing to see and hear the room through the Mac's camera, and hold the mic button to talk through its speakers. Everything goes over your own private Tailscale connection, so only you can reach it, protected by a password, Face ID and a log of every time someone views or controls the Mac.

---

## Install it (about 10 minutes)

### Before you start

You need:

- **A MacBook with macOS 14 (Sonoma) or newer.** To check: click the Apple menu (top left) → **About This Mac**.
- **Your iPhone.**
- **Your Mac's login password** (the one you type to unlock the Mac).

You don't need to know anything about code. You'll only open files and click buttons.

---

### Step 1 — Install Tailscale on your Mac and your iPhone

Tailscale is a free app that creates a private, encrypted connection between your own devices. Lid Awake uses it so your iPhone can reach your Mac from anywhere, and nobody else can.

**On your Mac**

1. Go to [tailscale.com/download/mac](https://tailscale.com/download/mac) and download Tailscale.
2. Open it. A small Tailscale icon appears at the top right of your screen (the menu bar).
3. Click that icon → **Log in**, and sign in with your Google, Apple or Microsoft account.

**On your iPhone**

1. Install **Tailscale** from the App Store.
2. Open it and sign in with **the same account** you used on the Mac.
3. When asked, tap **Allow** to add the VPN configuration.

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

A window called **Set up Lid Awake** opens by itself. (You can always open it again: click the cup in the menu bar → **Set up iPhone…**)

1. **Phone password:** type a password (at least 10 characters) twice and click **Save password**. Your iPhone will ask for it.
2. Click **Turn Lid Awake on**. The phone page only opens while Lid Awake is on.

Everything in the window should now have a green ✓, and a **QR code** appears.

---

### Step 5 — Add it to your iPhone

1. Make sure **Tailscale is on** on your iPhone (open the Tailscale app; the switch should be on).
2. Open the iPhone **Camera** and point it at the QR code on your Mac. Tap the link that appears. It opens in Safari.
3. In Safari, tap the **Share** button (the square with an arrow) → **Add to Home Screen** → **Add**.
4. Open **Lid Awake** from your Home Screen and type the phone password from Step 4.
5. Optional: in the app, tap the gear ⚙︎ → turn on **Face ID** (to sign in faster) and **Alerts** (battery, Mac turned off, and more).

That's it.

---

### Step 6 (optional) — See and control the Mac's screen from your iPhone

1. In the **Set up Lid Awake** window, click **Allow screen from phone**.
2. Click **Open Screen Recording settings** and turn on **Lid Awake** in the list.
3. Click **Open Accessibility settings** and turn on **Lid Awake** there too.

Now the **Screen** tab on your iPhone shows the Mac's screen, and you can use your phone as its trackpad and keyboard.

The **camera and microphone** have their own switch: cup in the menu bar → **Allow camera & microphone from phone**. On the iPhone, tap the notch at the top of the MacBook drawing (Face ID every time).

---

## Every day

- **Turn it on:** click the cup in the Mac's menu bar and flip the switch. You can turn it **off** from the iPhone too.
- **Turn it on for a while only:** in the same menu, choose **Turn off after** (for example 2 hours).
- **Close the lid:** while Lid Awake is on, your Mac keeps running with the lid closed.
- **Let it charge** if you leave it running for a long time. Lid Awake turns itself off if the battery gets low or too warm (you can change this in the menu).

## Update to a new version

Download the new version (Step 2) and open **Install Lid Awake** again. Your password, settings and iPhone stay as they are.

## Remove Lid Awake

Open **Uninstall Lid Awake** in the folder (right-click → **Open**). It asks whether to also delete your settings.

---

## If something doesn't work

| What you see | What to do |
|---|---|
| The installer stops at **Tailscale** | Install Tailscale on the Mac, sign in, then open **Install Lid Awake** again. |
| **"Turn on MagicDNS and HTTPS Certificates"** | On the Tailscale page that opened, turn on MagicDNS, and click **Enable** under HTTPS Certificates. Then open the installer again. |
| **"Install Lid Awake" can't be opened** | System Settings → Privacy & Security → scroll down → **Open Anyway**. |
| The iPhone says **"Lid Awake is off"** | Turn it on: cup in the Mac's menu bar, or **Turn Lid Awake on** in the setup window. |
| The iPhone **can't reach the Mac** | Make sure Tailscale is on, on both the Mac and the iPhone, with the same account. |
| The **Screen** tab stays black | Do Step 6 again: Screen Recording must be on for Lid Awake. |
| The camera says **the lid is closed** | macOS switches off the built-in camera and microphone when the lid is closed. Open the lid, or plug in a USB camera. |
| You **forgot the phone password** | Cup in the menu bar → **Change phone password…** |

---

## Privacy and security

- The phone page lives only on **your** private Tailscale network, never on the open internet, and it only answers **your** Tailscale account.
- Your phone password is never stored, only a scrambled fingerprint of it. Face ID keys stay on your iPhone.
- The camera and microphone need their own switch, Face ID every time, and turn off by themselves after 10 minutes.
- The installer gives Lid Awake one admin permission only: turning sleep off and on (`pmset -a disablesleep 0/1`).
- Every sign-in, screen view, control and camera use is written to the activity log (cup in the menu bar → **Show activity log**).
