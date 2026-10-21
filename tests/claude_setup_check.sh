#!/bin/sh
# tools/claude: the Claude Code setup the Mac installer offers (setup.py on/off/status), the hook
# that hands Claude's events to Lid Awake, and the status line. Runs in a throwaway HOME only.
cd "$(dirname "$0")/.." || exit 1
REPO="$(pwd)"
SETUP="$REPO/tools/claude/setup.py"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
failed=0

check() {  # check "what" command...
  what="$1"; shift
  if "$@"; then echo "ok    $what"; else echo "FAIL  $what"; failed=1; fi
}

# A new, empty home folder for each case.
fresh() { HOME="$(mktemp -d "$WORK/home.XXXXXX")"; export HOME; S="$HOME/.claude/settings.json"; }
setup() { python3 "$SETUP" "$@" >"$WORK/out" 2>&1; }
# Runs a Python expression against settings.json as `s`; true when it's true.
is() { python3 -c 'import json,sys; s=json.load(open(sys.argv[1])); sys.exit(0 if eval(sys.argv[2]) else 1)' "$S" "$1"; }
ours_count() {  # how many of our hooks an event has
  python3 -c 'import json,sys; s=json.load(open(sys.argv[1])); print(sum("lidawake-claude-event.sh" in h.get("command","") for g in s.get("hooks",{}).get(sys.argv[2],[]) for h in g.get("hooks",[])))' "$S" "$1"
}
mode() { stat -f %Lp "$1"; }
same() { cmp -s "$1" "$2"; }
says() { grep -q "$1" "$WORK/out"; }
nothing_there() { [ ! -e "$1" ]; }

EVENTS_REL="Library/Application Support/LidAwake/claude-events"

echo "-- fresh setup"
fresh
setup status; check "status says off before (exit 1)" [ $? -eq 1 ]
setup on; check "on succeeds" [ $? -eq 0 ]
check "event script copied, 755" [ "$(mode "$HOME/.claude/hooks/lidawake-claude-event.sh")" = 755 ]
check "status line script copied, 755" [ "$(mode "$HOME/.claude/statusline-lidawake.py")" = 755 ]
check "scripts are the repo's" same "$HOME/.claude/hooks/lidawake-claude-event.sh" "$REPO/tools/claude/lidawake-claude-event.sh"
check "status line points to our script" is "s['statusLine']=={'type':'command','command':'$HOME/.claude/statusline-lidawake.py'}"
check "one Stop hook" [ "$(ours_count Stop)" = 1 ]
check "one Notification hook" [ "$(ours_count Notification)" = 1 ]
check "hook entry exactly as Claude Code expects" is "s['hooks']['Stop']==[{'hooks':[{'type':'command','command':'\"\$HOME/.claude/hooks/lidawake-claude-event.sh\"','timeout':5}]}]"
check "events folder made, 700" [ "$(mode "$HOME/$EVENTS_REL")" = 700 ]
check "no backup when there were no settings" nothing_there "$S.lidawake-backup"
setup status; check "status says on (exit 0)" [ $? -eq 0 ]
cp "$S" "$WORK/first"
setup on; check "on again succeeds" [ $? -eq 0 ]
check "on again changes nothing" same "$S" "$WORK/first"
check "on again says so" says "nothing changed"

echo "-- the owner's current settings: already set up, nothing changes"
fresh
mkdir -p "$HOME/.claude/hooks" "$HOME/$EVENTS_REL"
cat > "$S" <<'EOF'
{
  "statusLine": {"type": "command", "command": "/Users/edenheiser/.claude/statusline-lidawake.py"},
  "hooks": {
    "SessionStart": [{"hooks": [{"type": "command", "command": "~/.claude/hooks/session-start.sh"}]}],
    "Stop": [{"hooks": [{"type": "command", "command": "\"$HOME/.claude/hooks/lidawake-claude-event.sh\"", "timeout": 5}]}],
    "Notification": [{"hooks": [{"type": "command", "command": "\"$HOME/.claude/hooks/lidawake-claude-event.sh\"", "timeout": 5}]}]
  },
  "model": "opus"
}
EOF
chmod 644 "$S"
cp "$S" "$WORK/owner"
cp "$REPO/tools/claude/lidawake-claude-event.sh" "$HOME/.claude/hooks/"
cp "$REPO/tools/claude/statusline-lidawake.py" "$HOME/.claude/"
setup status; check "status says on (exit 0)" [ $? -eq 0 ]
setup on; check "on succeeds" [ $? -eq 0 ]
check "settings byte for byte the same" same "$S" "$WORK/owner"
check "no backup made" nothing_there "$S.lidawake-backup"
check "says nothing changed" says "nothing changed"
check "still one Stop hook" [ "$(ours_count Stop)" = 1 ]
check "still one Notification hook" [ "$(ours_count Notification)" = 1 ]
# The owner's hand-made scripts (older text, still Lid Awake's) are recognised and updated.
printf '#!/bin/sh\n# Hands Claude Code'"'"'s moments to Lid Awake.\nexit 0\n' > "$HOME/.claude/hooks/lidawake-claude-event.sh"
setup status; check "older scripts: status exit 3" [ $? -eq 3 ]
setup on; check "on updates them" same "$HOME/.claude/hooks/lidawake-claude-event.sh" "$REPO/tools/claude/lidawake-claude-event.sh"
check "settings still untouched" same "$S" "$WORK/owner"

echo "-- other settings kept, backed up first, permissions kept"
fresh
mkdir -p "$HOME/.claude"
printf '{"model": "opus", "hooks": {"Stop": [{"matcher": "", "hooks": [{"type": "command", "command": "say done"}]}]}, "env": {"A": "1"}}\n' > "$S"
chmod 600 "$S"
cp "$S" "$WORK/before"
setup on; check "on succeeds" [ $? -eq 0 ]
check "backup is the old file" same "$S.lidawake-backup" "$WORK/before"
check "backup keeps the file's permissions" [ "$(mode "$S.lidawake-backup")" = 600 ]
check "settings keep their permissions" [ "$(mode "$S")" = 600 ]
check "other keys kept" is "s['model']=='opus' and s['env']=={'A':'1'}"
check "their Stop hook kept, ours added after it" is "s['hooks']['Stop'][0]=={'matcher':'','hooks':[{'type':'command','command':'say done'}]} and len(s['hooks']['Stop'])==2"

echo "-- the user's own status line is kept"
fresh
mkdir -p "$HOME/.claude"
printf '{"statusLine": {"type": "command", "command": "~/bin/my-status"}}\n' > "$S"
setup on; check "on succeeds" [ $? -eq 0 ]
check "their status line kept" is "s['statusLine']=={'type':'command','command':'~/bin/my-status'}"
check "says usage still comes from Lid Awake's own check" says "Lid Awake's own check"
check "hooks added" [ "$(ours_count Stop)$(ours_count Notification)" = 11 ]
setup status; check "counts as set up" [ $? -eq 0 ]
setup off; check "off succeeds" [ $? -eq 0 ]
check "off keeps their status line" is "s=={'statusLine':{'type':'command','command':'~/bin/my-status'}}"

echo "-- invalid JSON is refused, nothing changes"
fresh
mkdir -p "$HOME/.claude"
printf '{"model": "opus",}\n' > "$S"
cp "$S" "$WORK/bad"
setup on; check "on refuses (exit 1)" [ $? -eq 1 ]
check "says it isn't valid JSON" says "isn't valid JSON"
check "settings unchanged" same "$S" "$WORK/bad"
check "no backup" nothing_there "$S.lidawake-backup"
check "no scripts copied" nothing_there "$HOME/.claude/statusline-lidawake.py"
setup status; check "status exit 2" [ $? -eq 2 ]
setup off; check "off refuses too" [ $? -eq 1 ]
check "settings still unchanged" same "$S" "$WORK/bad"

echo "-- a script that isn't ours is never overwritten"
fresh
mkdir -p "$HOME/.claude"
printf '#!/bin/sh\necho mine\n' > "$HOME/.claude/statusline-lidawake.py"
setup on; check "on refuses" [ $? -eq 1 ]
check "says it isn't Lid Awake's" says "isn't Lid Awake's"
check "their file unchanged" [ "$(cat "$HOME/.claude/statusline-lidawake.py")" = "#!/bin/sh
echo mine" ]
check "no settings written" nothing_there "$S"
check "the other script not copied either" nothing_there "$HOME/.claude/hooks/lidawake-claude-event.sh"

echo "-- off removes only ours"
fresh
mkdir -p "$HOME/.claude"
cat > "$S" <<'EOF'
{
  "statusLine": {"type": "command", "command": "/Users/edenheiser/.claude/statusline-lidawake.py"},
  "hooks": {
    "SessionStart": [{"hooks": [{"type": "command", "command": "~/.claude/hooks/session-start.sh"}]}],
    "Stop": [{"hooks": [{"type": "command", "command": "say done"}]},
             {"hooks": [{"type": "command", "command": "\"$HOME/.claude/hooks/lidawake-claude-event.sh\"", "timeout": 5}]}],
    "Notification": [{"hooks": [{"type": "command", "command": "\"$HOME/.claude/hooks/lidawake-claude-event.sh\"", "timeout": 5},
                                {"type": "command", "command": "afplay ping.aiff"}]}]
  },
  "model": "opus"
}
EOF
chmod 644 "$S"
setup on >/dev/null
setup off; check "off succeeds" [ $? -eq 0 ]
check "status line removed" is "'statusLine' not in s"
check "SessionStart untouched" is "s['hooks']['SessionStart']==[{'hooks':[{'type':'command','command':'~/.claude/hooks/session-start.sh'}]}]"
check "their Stop entry kept, ours gone" is "s['hooks']['Stop']==[{'hooks':[{'type':'command','command':'say done'}]}]"
check "their hook in a shared Notification entry kept" is "s['hooks']['Notification']==[{'hooks':[{'type':'command','command':'afplay ping.aiff'}]}]"
check "other keys kept" is "s['model']=='opus'"
check "permissions kept" [ "$(mode "$S")" = 644 ]
check "event script removed" nothing_there "$HOME/.claude/hooks/lidawake-claude-event.sh"
check "status line script removed" nothing_there "$HOME/.claude/statusline-lidawake.py"
setup status; check "status says off" [ $? -eq 1 ]
fresh
mkdir -p "$HOME/.claude"
printf '{"hooks": {"Stop": [{"hooks": [{"type": "command", "command": "\\"$HOME/.claude/hooks/lidawake-claude-event.sh\\""}]}]}}\n' > "$S"
setup off
check "an event left empty is removed" is "s=={}"
setup off; check "off again: nothing to undo" says "nothing to undo"

echo "-- on, off, on"
fresh
mkdir -p "$HOME/.claude"
printf '{"model": "opus", "hooks": {"SessionStart": [{"hooks": [{"type": "command", "command": "x"}]}]}}\n' > "$S"
cp "$S" "$WORK/start"
setup on; cp "$S" "$WORK/on1"
setup off
check "off puts the settings back as they were" python3 -c 'import json,sys; sys.exit(json.load(open(sys.argv[1]))!=json.load(open(sys.argv[2])))' "$S" "$WORK/start"
setup on
check "on again gives the same as the first time" same "$S" "$WORK/on1"
check "first backup kept" same "$S.lidawake-backup" "$WORK/start"
check "second backup is timestamped" [ "$(ls "$HOME/.claude" | grep -c '^settings.json.lidawake-backup-')" = 1 ]
check "no duplicates" [ "$(ours_count Stop)$(ours_count Notification)" = 11 ]

echo "-- the event hook"
fresh
EV="$REPO/tools/claude/lidawake-claude-event.sh"
mkdir -p "$HOME/$EVENTS_REL"
printf '{"hook_event_name":"Stop","session_id":"abc"}' | sh "$EV"; check "exits 0" [ $? -eq 0 ]
f="$(ls "$HOME/$EVENTS_REL")"
check "writes one .json and nothing else" [ "$(ls -A "$HOME/$EVENTS_REL")" = "$f" ]
check "named <time>-<pid>.json" sh -c 'echo "$1" | grep -Eq "^[0-9]+-[0-9]+\.json$"' - "$f"
check "with the event in it" [ "$(cat "$HOME/$EVENTS_REL/$f")" = '{"hook_event_name":"Stop","session_id":"abc"}' ]
check "readable by you only" [ "$(mode "$HOME/$EVENTS_REL/$f")" = 600 ]
rm -rf "$HOME/Library"
printf '{}' | sh "$EV"; check "exits 0 when the folder is missing" [ $? -eq 0 ]
check "and makes nothing" nothing_there "$HOME/Library"
check "parses as plain sh" sh -n "$EV"
if [ -x /bin/dash ]; then
  mkdir -p "$HOME/$EVENTS_REL"
  printf '{}' | /bin/dash "$EV"; check "runs under dash (strict POSIX sh), exit 0" [ $? -eq 0 ]
  check "and writes the .json" [ "$(ls "$HOME/$EVENTS_REL" | grep -c '\.json$')" = 1 ]
fi

echo "-- the status line"
fresh
SL="$REPO/tools/claude/statusline-lidawake.py"
mkdir -p "$HOME/Library/Application Support/LidAwake"
IN='{"model":{"display_name":"Opus"},"rate_limits":{"five_hour":{"used_percentage":42.4,"resets_at":1760000000},"seven_day":{"used_percentage":7,"resets_at":1760400000}}}'
out="$(printf '%s' "$IN" | TZ=UTC python3 "$SL")"; check "exits 0" [ $? -eq 0 ]
check "prints model and both limits" [ "$out" = "Opus · 5h 42% (resets 08:53) · week 7% (resets Tue 00:00)" ]
U="$HOME/Library/Application Support/LidAwake/claude-usage.json"
check "saves the limits for the app" python3 -c 'import json,sys,time; d=json.load(open(sys.argv[1])); sys.exit(not (d["rate_limits"]==json.loads(sys.argv[2])["rate_limits"] and abs(d["updated"]-time.time())<60))' "$U" "$IN"
check "no temp file left" [ "$(ls -A "$HOME/Library/Application Support/LidAwake" | grep -c tmp)" = 0 ]
out="$(printf '{"model":{"display_name":"Sonnet"},"rate_limits":{"seven_day":{"used_percentage":12.6}}}' | python3 "$SL")"
check "only the parts it has" [ "$out" = "Sonnet · week 13%" ]
rm -f "$U"
out="$(printf '{"model":{"display_name":"Opus"}}' | python3 "$SL")"
check "no limits: model only" [ "$out" = "Opus" ]
check "no limits: no file" nothing_there "$U"
out="$(printf 'not json' | python3 "$SL")"; check "bad input still exits 0" [ $? -eq 0 ]
rm -rf "$HOME/Library"
out="$(printf '%s' "$IN" | TZ=UTC python3 "$SL")"
check "without Lid Awake's folder: still prints" [ "$out" = "Opus · 5h 42% (resets 08:53) · week 7% (resets Tue 00:00)" ]
check "and writes nothing" nothing_there "$HOME/Library"

[ $failed -eq 0 ] && echo "All Claude setup checks passed."
exit $failed
