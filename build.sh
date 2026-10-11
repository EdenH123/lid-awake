#!/bin/sh
# Rebuild Lid Awake from the sources next to this script and install it.
# Signs with the "Lid Awake Local Signing" certificate so macOS keeps the app's
# Screen Recording and Accessibility permissions across updates, with the hardened
# runtime on so no other program can load its own code into Lid Awake.
set -e
SRC="$(cd "$(dirname "$0")" && pwd)"
APP="$HOME/Applications/Lid Awake.app"
CERT="Lid Awake Local Signing"
TMP="$(mktemp -d)"

if ! security find-identity -p codesigning | grep -q "\"$CERT\""; then
  echo "No \"$CERT\" certificate on this Mac yet. Run ./install.sh first."; exit 1
fi
swiftc -O -o "$TMP/LidAwake" "$SRC"/*.swift
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$TMP/LidAwake" "$APP/Contents/MacOS/LidAwake"
[ -f "$SRC/Info.plist" ] && cp "$SRC/Info.plist" "$APP/Contents/Info.plist"
# The sources live in this folder only (older builds kept a copy inside the app).
rm -f "$APP/Contents/Resources/"*.swift "$APP/Contents/Resources/build.sh"
# Photos for the phone's opening animation (served at /sky/…).
cp "$SRC"/assets/sky-*.jpg "$APP/Contents/Resources/"
codesign --force --options runtime --entitlements "$SRC/LidAwake.entitlements" --sign "$CERT" --identifier local.lidawake "$APP"
codesign --verify --strict "$APP"
rm -rf "$TMP"

# launchd restarts the app within a few seconds.
pkill -f "Lid Awake.app/Contents/MacOS/LidAwake" || true
echo "Lid Awake rebuilt and restarted."
