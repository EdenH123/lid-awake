#!/bin/sh
# Lid Awake: hands Claude Code's "finished" and "needs you" moments (its Stop and Notification
# hooks) to Lid Awake, which picks them up from the folder below and alerts your phone.
# It never blocks or fails Claude: it always exits 0, and does nothing if the folder isn't there.
umask 077
dir="$HOME/Library/Application Support/LidAwake/claude-events"
[ -d "$dir" ] || exit 0
f="$dir/$(date +%s)-$$"
if head -c 65536 > "$f.tmp" 2>/dev/null; then
  mv "$f.tmp" "$f.json" 2>/dev/null || rm -f "$f.tmp"
else
  rm -f "$f.tmp"
fi
exit 0
