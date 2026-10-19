#!/bin/sh
# Platform.swift: what this computer tells the phone page about itself.
cd "$(dirname "$0")" || exit 1
OUT="$(mktemp -d)"
swiftc -o "$OUT/platform" platform/main.swift ../Platform.swift || { rm -rf "$OUT"; exit 1; }
"$OUT/platform"; status=$?
rm -rf "$OUT"
exit $status
