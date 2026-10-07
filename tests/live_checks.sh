#!/bin/sh
# Checks the live socket refuses anything it shouldn't, then runs the in-app selftest.
cd "$(dirname "$0")"
APP="$HOME/Applications/Lid Awake.app"
fail=0
expect() {  # expect <label> <expected-substring> <actual>
  case "$3" in *"$2"*) echo "PASS $1 ($3)";; *) echo "FAIL $1: expected '$2', got '$3'"; fail=1;; esac
}
expect "no cookie"   "400" "$(node ws_handshake.js 8788 127.0.0.1:8788 http://127.0.0.1:8787)"
expect "fake cookie" "400" "$(node ws_handshake.js 8788 127.0.0.1:8788 http://127.0.0.1:8787 la_session=deadbeef)"
expect "bad host"    "400" "$(node ws_handshake.js 8788 evil.example.com http://127.0.0.1:8787 la_session=deadbeef)"
expect "bad origin"  "400" "$(node ws_handshake.js 8788 127.0.0.1:8788 https://evil.example.com la_session=deadbeef)"

LOG="$HOME/Library/Logs/LidAwake-selftest.log"; rm -f "$LOG"
launchctl submit -l local.lidawake.selftest -- "$APP/Contents/MacOS/LidAwake" --selftest
sleep 16; launchctl remove local.lidawake.selftest 2>/dev/null
cat "$LOG"
for p in sharp balanced fast; do
  line=$(grep "^live $p:" "$LOG")
  n=$(echo "$line" | sed -n 's/.* \([0-9][0-9]*\) frames.*/\1/p')
  if [ -n "$n" ] && [ "$n" -ge 1 ]; then echo "PASS live $p ($line)"; else echo "FAIL live $p: '$line'"; fail=1; fi
done
# A still screen sends few frames, so check the presets by size: Sharp > Fast.
sharp=$(grep "^live sharp:" "$LOG" | sed -n 's/.*last \([0-9]*\) bytes.*/\1/p')
fast=$(grep "^live fast:" "$LOG" | sed -n 's/.*last \([0-9]*\) bytes.*/\1/p')
if [ -n "$sharp" ] && [ -n "$fast" ] && [ "$sharp" -gt "$fast" ]; then echo "PASS presets differ (sharp $sharp > fast $fast)"
else echo "FAIL presets: sharp '$sharp' vs fast '$fast'"; fail=1; fi
grep -q "^live input echo: ok" "$LOG" && echo "PASS live input" || { echo "FAIL live input"; fail=1; }
exit $fail
