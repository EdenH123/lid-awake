#!/bin/sh
# The Memory window's numbers (MemoryInfo.swift), read from this Mac.
cd "$(dirname "$0")" || exit 1
OUT="$(mktemp -d)"
swiftc -o "$OUT/memory" memory/main.swift ../MemoryInfo.swift || { rm -rf "$OUT"; exit 1; }
result="$("$OUT/memory")"
rm -rf "$OUT"
echo "$result"
echo "$result" | grep -q "^FAIL" && exit 1
exit 0
