#!/bin/sh
# Removes Lid Awake from this Mac. Run it from this folder:  ./uninstall.sh
cd "$(dirname "$0")"

APP="$HOME/Applications/Lid Awake.app"
AGENT="$HOME/Library/LaunchAgents/local.lidawake.plist"
TS="/Applications/Tailscale.app/Contents/MacOS/Tailscale"

ok() { printf '  \033[32m✓\033[0m %s\n' "$1"; }

printf '\n\033[1mRemoving Lid Awake\033[0m\n'
launchctl bootout "gui/$(id -u)/local.lidawake" 2>/dev/null
rm -f "$AGENT"
pkill -f "Lid Awake.app/Contents/MacOS/LidAwake" 2>/dev/null
ok "Stopped, and won't start with the Mac"

# Let the Mac sleep normally again and take the phone page off Tailscale.
sudo -n /usr/bin/pmset -a disablesleep 0 2>/dev/null
[ -x "$TS" ] && TAILSCALE_BE_CLI=1 "$TS" serve --https=443 off >/dev/null 2>&1
ok "Sleep back to normal, phone page closed"

rm -rf "$APP"
ok "App removed"

# Lid Awake's part of Claude Code, if the installer added it: out of its settings, scripts removed.
if grep -qs -e "Lid Awake" -e "Application Support/LidAwake" \
     "$HOME/.claude/hooks/lidawake-claude-event.sh" "$HOME/.claude/statusline-lidawake.py" &&
   xcode-select -p >/dev/null 2>&1; then
  if out=$(/usr/bin/python3 tools/claude/setup.py off --quiet 2>&1); then
    ok "Taken out of Claude Code's settings"
  else
    printf '  \033[31m✗\033[0m %s\n' "$out"
  fi
fi

if [ -f /etc/sudoers.d/lidawake ]; then
  printf '    Removing the keep-awake permission needs your Mac password:\n'
  sudo rm -f /etc/sudoers.d/lidawake && ok "Keep-awake permission removed"
fi

printf '\n    Also delete its settings (phone password, Face ID sign-ins, alerts, history)\n'
printf '    and its signing certificate? [y/N] '
read -r answer
case "$answer" in
  y|Y|yes)
    defaults delete local.lidawake 2>/dev/null
    rm -rf "$HOME/Library/Application Support/LidAwake"
    rm -f "$HOME"/Library/Logs/LidAwake-*.log
    security delete-identity -c "Lid Awake Local Signing" "$HOME/Library/Keychains/login.keychain-db" >/dev/null 2>&1
    ok "Settings and certificate deleted"
    ;;
  *) ok "Settings kept (installing again picks up where you left off)" ;;
esac
printf '\n'
