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
sleep 30; launchctl remove local.lidawake.selftest 2>/dev/null
cat "$LOG"
for p in max sharp balanced fast; do
  line=$(grep "^live $p:" "$LOG")
  n=$(echo "$line" | sed -n 's/.* \([0-9][0-9]*\) frames.*/\1/p')
  if [ -n "$n" ] && [ "$n" -ge 1 ]; then echo "PASS live $p ($line)"; else echo "FAIL live $p: '$line'"; fail=1; fi
done
# A still screen sends few frames, so check the presets by size: Sharp > Fast while moving,
# and Fast sends a clearer (bigger) frame once the screen is still.
sharp=$(grep "^live sharp:" "$LOG" | sed -n 's/.*last \([0-9]*\) bytes.*/\1/p')
fastFirst=$(grep "^live fast:" "$LOG" | sed -n 's/.*first \([0-9]*\) bytes.*/\1/p')
fastLast=$(grep "^live fast:" "$LOG" | sed -n 's/.*last \([0-9]*\) bytes.*/\1/p')
if [ -n "$sharp" ] && [ -n "$fastFirst" ] && [ "$sharp" -gt "$fastFirst" ]; then echo "PASS presets differ (sharp $sharp > fast $fastFirst)"
else echo "FAIL presets: sharp '$sharp' vs fast '$fastFirst'"; fail=1; fi
# Max sends the Mac's full Retina picture: bigger frames than Sharp.
max=$(grep "^live max:" "$LOG" | sed -n 's/.*last \([0-9]*\) bytes.*/\1/p')
if [ -n "$max" ] && [ -n "$sharp" ] && [ "$max" -gt "$sharp" ]; then echo "PASS max is sharper (max $max > sharp $sharp bytes)"
else echo "FAIL max: max '$max' vs sharp '$sharp'"; fail=1; fi
# The still-frame check needs a calm screen. If the selftest saw real motion (>3% of the
# picture changing) it can't judge, so it's skipped rather than failed.
moving=$(grep "^live fast changed areas:" "$LOG" | tr ' ' '\n' | awk '$1+0 > 0.03 {n++} END {print n+0}')
# (The clear frame is 1280 px at 0.6 quality, so it's at least ~1.2× a moving frame.)
if [ -n "$fastLast" ] && [ "$fastLast" -gt $(( fastFirst * 6 / 5 )) ]; then echo "PASS fast sharpens when still ($fastFirst -> $fastLast bytes)"
elif [ "$moving" -gt 0 ]; then echo "SKIP fast still frame: the screen was moving during the test ($moving busy frames)"
else echo "FAIL fast still frame: first '$fastFirst', last '$fastLast'"; fail=1; fi
grep -q "^live input echo: ok" "$LOG" && echo "PASS live input" || { echo "FAIL live input"; fail=1; }
# Flow control: a page that confirms frames never has more than 2 waiting on the way.
held=$(grep "^live flow:" "$LOG" | sed -n 's/^live flow: \([0-9]*\) frames.*/\1/p')
if [ -n "$held" ] && [ "$held" -le 2 ]; then echo "PASS flow control ($(grep '^live flow:' "$LOG" | cut -c12-))"
else echo "FAIL flow control: '$(grep '^live flow:' "$LOG")'"; fail=1; fi
# Video: setup arrives, then a keyframe first.
video=$(grep "^live video:" "$LOG")
if echo "$video" | grep -q "setup avc1" && echo "$video" | grep -q "first is keyframe"; then echo "PASS live video (${video#live video: })"
else echo "FAIL live video: '$video'"; fail=1; fi
# Face ID sign-in checks (no phone needed).
if sh ./passkeys_check.sh > /tmp/lidawake-passkeys.$$ 2>&1; then echo "PASS face id ($(grep -c '^PASS' /tmp/lidawake-passkeys.$$) checks)"
else echo "FAIL face id:"; grep -v '^PASS' /tmp/lidawake-passkeys.$$; fail=1; fi
rm -f /tmp/lidawake-passkeys.$$
exit $fail
