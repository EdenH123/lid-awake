#!/bin/sh
# iPhone alerts: the Mac encrypts an alert (WebPush.swift) and an independent RFC 8291
# receiver (decrypt.js, Node) must read it back exactly. No network needed.
cd "$(dirname "$0")" || exit 1
OUT="$(mktemp -d)"
swiftc -o "$OUT/enc" webpush/main.swift ../WebPush.swift ../Passkeys.swift ../StreamStats.swift || exit 1
result="$("$OUT/enc" | node webpush/decrypt.js)"
rm -rf "$OUT"
echo "$result"
echo "$result" | grep -q '"matches":true' && echo "$result" | grep -q '"lastRecordMarker":true'
