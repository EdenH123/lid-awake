# Lid Awake

Lid Awake is a small Mac app that keeps your MacBook running with the lid closed, and an iPhone app that lets you use it from anywhere. On the Mac, one switch in the menu bar keeps it awake, with a timer and safety limits that turn it off if the battery runs low or gets too hot. On your iPhone you can see how the Mac is doing (battery, temperature, memory, network), watch its screen live, use the phone as its trackpad and keyboard, and open, switch or quit its apps. Tap the notch in the MacBook drawing to see and hear the room through the Mac's camera, and hold the mic button to talk through its speakers. Everything goes over your own private Tailscale connection, so only you can reach it, protected by a password, Face ID and a log of every time someone views or controls the Mac.

## What you need

- A Mac with macOS 14 or newer
- [Tailscale](https://tailscale.com) on the Mac and on your iPhone, signed in to the same account (free for personal use)

## Install

1. Download this folder (green **Code** button → **Download ZIP**, then unzip it), or clone it.
2. Open **Terminal**, type `cd ` (with a space), drag the folder into the window, press Return.
3. Run:

   ```sh
   ./install.sh
   ```

The installer checks everything, tells you exactly what to do if something is missing, and asks for your Mac password once. When it finishes, a **Set up Lid Awake** window opens on the Mac.

## Set up your iPhone

In the **Set up Lid Awake** window (also in the menu: **Set up iPhone…**):

1. Choose a **phone password**.
2. Turn Lid Awake **on**: the phone page only opens while it's on.
3. On your iPhone, point the **Camera** at the code in the window and open the link in Safari.
4. Tap **Share → Add to Home Screen**, open it, and sign in. You can turn on Face ID in its Settings.

To see and control the screen from the phone, tick **Allow screen from phone** and allow Lid Awake in **System Settings → Privacy & Security → Screen Recording** and **Accessibility**.

## Update

Download the new version and run `./install.sh` again. Your settings, password and phone stay as they are.

## Remove

```sh
./uninstall.sh
```

It asks whether to keep your settings, in case you install again later.

## If something doesn't work

| What you see | What to do |
|---|---|
| The installer stops at Tailscale | Install Tailscale, sign in, and run `./install.sh` again. |
| "Turn on MagicDNS and HTTPS Certificates" | In the Tailscale admin page that opens, under **DNS**, turn both on. It's a one-time switch for your account. |
| The phone says "Lid Awake is off" | Turn it on from the menu bar on the Mac (or in the setup window). |
| The phone can't reach the Mac | Make sure Tailscale is on, on both devices, with the same account. |
| The screen stays black on the phone | Allow Screen Recording for Lid Awake in System Settings, then turn **Allow screen from phone** off and on. |
| The camera says the lid is closed | macOS switches off the built-in camera and microphone when the lid is closed. Open the lid, or plug in a USB camera. |

## Privacy and security

- The phone page is only on your Tailscale network, never the open internet, and only answers your own Tailscale account.
- Your phone password is stored as a salted hash; Face ID keys stay on your iPhone.
- The camera and microphone need a separate switch in the menu, Face ID each time, and stop on their own after 10 minutes.
- The only admin permission is running `pmset -a disablesleep 0/1` (sleep off and on).
- Every sign-in, screen view, control and camera use is in the activity log (menu → Show activity log).
