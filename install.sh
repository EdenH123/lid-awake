#!/bin/sh
# Installs Lid Awake on this Mac. Open "Install Lid Awake" in this folder (or run ./install.sh).
# Safe to run again (e.g. to update): finished steps are skipped.
set -e
cd "$(dirname "$0")"

CERT="Lid Awake Local Signing"
APP="$HOME/Applications/Lid Awake.app"
AGENT="$HOME/Library/LaunchAgents/local.lidawake.plist"
TS="/Applications/Tailscale.app/Contents/MacOS/Tailscale"
KEYCHAIN="$HOME/Library/Keychains/login.keychain-db"

step() { printf '\n\033[1m%s\033[0m\n' "$1"; }
ok()   { printf '  \033[32m✓\033[0m %s\n' "$1"; }
note() { printf '    %s\n' "$1"; }
stop() { printf '  \033[31m✗\033[0m %s\n\n' "$1"; exit 1; }
bad()  { printf '  \033[31m✗\033[0m %s\n' "$1"; }  # for an optional step: say it, carry on

step "1/7  This Mac"
v=$(sw_vers -productVersion)
[ "${v%%.*}" -ge 14 ] || stop "Lid Awake needs macOS 14 or newer (this Mac has $v)."
if ! xcode-select -p >/dev/null 2>&1; then
  xcode-select --install 2>/dev/null || true
  stop "Apple's developer tools are needed to build the app. Finish the window that opened, then open the installer again."
fi
ok "macOS $v, developer tools ready"

step "2/7  Tailscale (the private connection between your iPhone and this Mac)"
if [ ! -x "$TS" ]; then
  open "https://tailscale.com/download/mac"
  stop "Install Tailscale from the page that opened and sign in, then open the installer again."
fi
status=$(TAILSCALE_BE_CLI=1 "$TS" status --json 2>/dev/null || true)
if ! printf '%s' "$status" | grep -q '"BackendState": *"Running"'; then
  open -a Tailscale || true
  stop "Sign in to Tailscale (its icon is in the menu bar), then open the installer again."
fi
# The phone page uses a private https address, which needs MagicDNS and HTTPS certificates
# turned on once for the whole Tailscale account.
if printf '%s' "$status" | grep -q '"CertDomains": *null' || ! printf '%s' "$status" | grep -q '"CertDomains"'; then
  open "https://login.tailscale.com/admin/dns"
  stop "On the Tailscale page that opened, turn on MagicDNS and HTTPS Certificates, then open the installer again."
fi
host=$(printf '%s' "$status" | grep -o '"DNSName": *"[^"]*"' | head -1 | sed 's/.*"\([^"]*\)\.".*/\1/')
ok "Signed in · this Mac will be https://$host"

step "3/7  Signing certificate"
# macOS keeps the app's Screen Recording and Accessibility permissions across updates only when
# every build is signed by the same certificate. It's made here, stays in this Mac's keychain,
# and is used for nothing else.
if security find-identity -p codesigning "$KEYCHAIN" | grep -q "\"$CERT\""; then
  ok "Already there"
else
  tmp=$(mktemp -d)
  printf '[req]\ndistinguished_name = dn\nx509_extensions = ext\nprompt = no\n[dn]\nCN = %s\n[ext]\nbasicConstraints = critical, CA:false\nkeyUsage = critical, digitalSignature\nextendedKeyUsage = critical, codeSigning\n' "$CERT" > "$tmp/cert.cnf"
  /usr/bin/openssl req -x509 -newkey rsa:2048 -nodes -days 3650 -config "$tmp/cert.cnf" \
    -keyout "$tmp/key.pem" -out "$tmp/cert.pem" 2>/dev/null
  pw=$(LC_ALL=C tr -dc A-Za-z0-9 </dev/urandom | head -c 24)
  /usr/bin/openssl pkcs12 -export -inkey "$tmp/key.pem" -in "$tmp/cert.pem" -out "$tmp/id.p12" \
    -passout "pass:$pw" -keypbe PBE-SHA1-3DES -certpbe PBE-SHA1-3DES -macalg sha1
  security import "$tmp/id.p12" -k "$KEYCHAIN" -P "$pw" -T /usr/bin/codesign >/dev/null
  rm -rf "$tmp"
  ok "Made \"$CERT\" in your keychain"
fi

step "4/7  Building the app"
note "This takes a minute or two."
log=$(mktemp)
if ! ./build.sh >"$log" 2>&1; then
  tail -20 "$log"; rm -f "$log"
  stop "Building failed (details above)."
fi
rm -f "$log"
ok "Installed in ~/Applications"

step "5/7  Permission to keep the Mac awake"
# The app runs exactly one command as admin: pmset -a disablesleep 1 (or 0), which turns sleep
# off (or back on). This rule allows only that, without asking for a password each time.
if sudo -n -l /usr/bin/pmset -a disablesleep 1 >/dev/null 2>&1 && sudo -n -l /usr/bin/pmset -a disablesleep 0 >/dev/null 2>&1; then
  ok "Already allowed"
else
  note "Lid Awake may only run: pmset -a disablesleep 0/1 (sleep on or off). Nothing else."
  note "Type your Mac password once (nothing shows while you type):"
  rule=$(mktemp)
  printf '%s ALL=(root) NOPASSWD: /usr/bin/pmset -a disablesleep 0, /usr/bin/pmset -a disablesleep 1\n' "$(id -un)" > "$rule"
  sudo /usr/sbin/visudo -cf "$rule" >/dev/null
  sudo install -m 0440 -o root -g wheel "$rule" /etc/sudoers.d/lidawake
  rm -f "$rule"
  ok "Allowed"
fi

step "6/7  Start with your Mac"
mkdir -p "$HOME/Library/LaunchAgents"
old=$(cat "$AGENT" 2>/dev/null || true)
cat > "$AGENT" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>local.lidawake</string>
  <key>ProgramArguments</key>
  <array><string>$APP/Contents/MacOS/LidAwake</string></array>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
  <key>ThrottleInterval</key><integer>5</integer>
  <key>LimitLoadToSessionType</key><string>Aqua</string>
</dict>
</plist>
EOF
service="gui/$(id -u)/local.lidawake"
if launchctl print "$service" >/dev/null 2>&1 && [ "$old" = "$(cat "$AGENT")" ]; then
  : # already set up; the build above restarted the app
else
  # Stopping takes a moment: wait until it's gone before starting it with the new settings.
  launchctl bootout "$service" 2>/dev/null || true
  i=0
  while launchctl print "$service" >/dev/null 2>&1 && [ $i -lt 20 ]; do sleep 0.5; i=$((i + 1)); done
  launchctl bootstrap "gui/$(id -u)" "$AGENT" 2>/dev/null || { sleep 2; launchctl bootstrap "gui/$(id -u)" "$AGENT"; }
fi
i=0
while ! pgrep -f "Lid Awake.app/Contents/MacOS/LidAwake" >/dev/null && [ $i -lt 20 ]; do sleep 0.5; i=$((i + 1)); done
pgrep -f "Lid Awake.app/Contents/MacOS/LidAwake" >/dev/null || stop "Lid Awake didn't start. Open the installer again."
ok "Running · look for the cup in the menu bar"

# Only on a Mac with Claude Code: its settings folder, or the app where Lid Awake looks for it.
if [ -d "$HOME/.claude" ] || [ -x /opt/homebrew/bin/claude ] || [ -x /usr/local/bin/claude ] ||
   [ -x "$HOME/.local/bin/claude" ] || [ -x "$HOME/.claude/local/claude" ]; then
  step "7/7  Claude alerts on your phone (optional)"
  claude_setup() { /usr/bin/python3 tools/claude/setup.py "$@" 2>&1; }
  state=0; out=$(claude_setup status) || state=$?
  case $state in
    0) ok "Already set up" ;;
    3) if out=$(claude_setup on --quiet); then ok "Already set up (its scripts updated)"
       else bad "$out"; fi ;;
    2) bad "$out" ;;
    *)
      note "Lets your phone show your Claude usage after each reply, and alert you when Claude"
      note "finishes or needs you. It adds Lid Awake to Claude Code's settings (backed up first)."
      if [ -t 0 ]; then
        printf '    Turn it on? [Y/n] '
        read -r answer || answer=n
        case "$answer" in
          n|N|no|No|NO) note "Skipped. To turn it on later: python3 tools/claude/setup.py on" ;;
          *)
            if out=$(claude_setup on); then
              printf '%s\n' "$out" | grep -v '^On:' | while IFS= read -r line; do note "$line"; done
              ok "On · to undo: python3 tools/claude/setup.py off (or the uninstaller)"
            else
              bad "$out"
            fi ;;
        esac
      else
        note "Skipped (nobody here to answer). To turn it on later: python3 tools/claude/setup.py on"
      fi ;;
  esac
fi

printf '\n\033[1mAlmost done.\033[0m The "Set up Lid Awake" window opens on this Mac: choose a phone\n'
printf 'password there and scan its code with your iPhone. (Later: Lid Awake menu → Set up iPhone…)\n\n'
