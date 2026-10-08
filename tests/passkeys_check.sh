#!/bin/sh
# Face ID sign-in (Passkeys.swift) against a software "phone": registration, sign-in, and the
# cases that must be refused (reused challenge, other site, wrong key, no Face ID, http).
cd "$(dirname "$0")" || exit 1
OUT="$(mktemp -d)"
swiftc -o "$OUT/passkeys" passkeys/main.swift ../Passkeys.swift || exit 1
result="$("$OUT/passkeys")"
rm -rf "$OUT"
echo "$result"
echo "$result" | grep -q "^FAIL" && exit 1
exit 0
