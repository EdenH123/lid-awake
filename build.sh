#!/bin/sh
# Rebuild Lid Awake from the sources next to this script and install it.
# Signs with the "Lid Awake Local Signing" certificate so macOS keeps the app's
# Screen Recording and Accessibility permissions across updates.
set -e
SRC="$(cd "$(dirname "$0")" && pwd)"
APP="$HOME/Applications/Lid Awake.app"
CERT="Lid Awake Local Signing"
TMP="$(mktemp -d)"

swiftc -O -o "$TMP/LidAwake" "$SRC"/*.swift
cp "$TMP/LidAwake" "$APP/Contents/MacOS/LidAwake"
if [ "$SRC" != "$APP/Contents/Resources" ]; then
  rm -f "$APP/Contents/Resources/"*.swift
  cp "$SRC"/*.swift "$SRC/build.sh" "$APP/Contents/Resources/"
fi
codesign --force --sign "$CERT" --identifier local.lidawake "$APP"
codesign --verify "$APP"
rm -rf "$TMP"

# launchd restarts the app within a few seconds.
pkill -f "Lid Awake.app/Contents/MacOS/LidAwake" || true
echo "Lid Awake rebuilt and restarted."
