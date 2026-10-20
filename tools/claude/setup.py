#!/usr/bin/env python3
"""Lid Awake's part of Claude Code on this Mac: alerts on your phone when Claude finishes or
needs you, and your Claude usage after each reply.

  python3 tools/claude/setup.py on       add it (backs up ~/.claude/settings.json first)
  python3 tools/claude/setup.py off      take it out again
  python3 tools/claude/setup.py status   say whether it's set up

"on" copies two small scripts into ~/.claude and adds three things to ~/.claude/settings.json:
the status line (only if you don't have your own) and a Stop and a Notification hook. "off"
removes exactly those. Nothing else in your settings is ever changed.

status exits 0 when set up, 1 when not, 2 when the settings can't be read, and 3 when set up
but the scripts in ~/.claude are older than these (running "on" again updates them).
"""
import json
import os
import shlex
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
HOME = os.path.expanduser("~")
CLAUDE = os.path.join(HOME, ".claude")
SETTINGS = os.path.join(CLAUDE, "settings.json")
EVENTS_DIR = os.path.join(HOME, "Library", "Application Support", "LidAwake", "claude-events")

EVENT_NAME = "lidawake-claude-event.sh"
STATUS_NAME = "statusline-lidawake.py"
EVENT_SCRIPT = os.path.join(CLAUDE, "hooks", EVENT_NAME)
STATUS_SCRIPT = os.path.join(CLAUDE, STATUS_NAME)
SCRIPTS = ((os.path.join(HERE, EVENT_NAME), EVENT_SCRIPT), (os.path.join(HERE, STATUS_NAME), STATUS_SCRIPT))
HOOK_EVENTS = ("Stop", "Notification")
HOOK_COMMAND = '"$HOME/.claude/hooks/%s"' % EVENT_NAME

quiet = False


class Refuse(Exception):
    pass


def say(text):
    if not quiet:
        print(text)


def short(path):
    return "~" + path[len(HOME):] if path.startswith(HOME + os.sep) else path


def read(path):
    try:
        with open(path, "rb") as f:
            return f.read()
    except FileNotFoundError:
        return None


def ours(data):
    # Every version of these scripts names Lid Awake or writes into its folder.
    return b"Lid Awake" in data or b"Application Support/LidAwake" in data


def write_atomic(path, data, mode):
    # Into the real file when it's a link (dotfile setups), never half-written.
    path = os.path.realpath(path)
    fd, tmp = tempfile.mkstemp(dir=os.path.dirname(path), prefix="." + os.path.basename(path) + ".")
    try:
        with os.fdopen(fd, "wb") as f:
            f.write(data)
            f.flush()
            os.fsync(f.fileno())
        os.chmod(tmp, mode)
        os.replace(tmp, path)
    except BaseException:
        try:
            os.unlink(tmp)
        except OSError:
            pass
        raise


# MARK: settings.json

def load_settings():
    raw = read(SETTINGS)
    if raw is None:
        return {}, None
    try:
        settings = json.loads(raw.decode("utf-8"))
    except (ValueError, UnicodeDecodeError) as e:
        raise Refuse("%s isn't valid JSON (%s), so nothing was changed. Fix it and run this again."
                      % (short(SETTINGS), e))
    if not isinstance(settings, dict):
        raise Refuse("%s isn't a settings object, so nothing was changed." % short(SETTINGS))
    hooks = settings.get("hooks")
    if hooks is not None and not isinstance(hooks, dict):
        raise Refuse("\"hooks\" in %s isn't the usual shape, so nothing was changed." % short(SETTINGS))
    for event, groups in (hooks or {}).items():
        if not isinstance(groups, list):
            raise Refuse("\"hooks.%s\" in %s isn't a list, so nothing was changed." % (event, short(SETTINGS)))
    return settings, raw


def save_settings(settings, raw):
    data = (json.dumps(settings, indent=2, ensure_ascii=False) + "\n").encode("utf-8")
    mode = os.stat(SETTINGS).st_mode & 0o7777 if raw is not None else 0o600
    os.makedirs(CLAUDE, exist_ok=True)
    write_atomic(SETTINGS, data, mode)


def backup(raw):
    if raw is None:
        return None
    path = SETTINGS + ".lidawake-backup"
    if os.path.lexists(path):
        path += time.strftime("-%Y%m%d-%H%M%S")
    mode = os.stat(SETTINGS).st_mode & 0o777
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, mode)
    with os.fdopen(fd, "wb") as f:
        f.write(raw)
    os.chmod(path, mode)
    return path


def is_our_hook(hook):
    return isinstance(hook, dict) and EVENT_NAME in str(hook.get("command", ""))


def has_our_hook(groups):
    return any(isinstance(g, dict) and isinstance(g.get("hooks"), list) and any(is_our_hook(h) for h in g["hooks"])
               for g in groups or [])


def status_line(settings):
    line = settings.get("statusLine")
    if line is None:
        return "none"
    if isinstance(line, dict) and STATUS_NAME in str(line.get("command", "")):
        return "ours"
    return "theirs"


# MARK: subcommands

def on():
    settings, raw = load_settings()
    # Check every file before writing any, so a refusal leaves everything as it was.
    plan = []
    for src, dest in SCRIPTS:
        new = read(src)
        if new is None:
            raise Refuse("%s is missing from this folder." % src)
        if os.path.isdir(dest):
            raise Refuse("%s is a folder, so nothing was changed." % short(dest))
        old = read(dest)
        if old is not None and old != new and not ours(old):
            raise Refuse("%s already exists and isn't Lid Awake's, so nothing was changed. "
                         "Move it somewhere else and run this again." % short(dest))
        plan.append((dest, new, old))

    changed = False
    line = status_line(settings)
    if line == "none":
        settings["statusLine"] = {"type": "command", "command": shlex.quote(STATUS_SCRIPT)}
        changed = True
    hooks = settings.get("hooks") or {}
    for event in HOOK_EVENTS:
        if not has_our_hook(hooks.get(event)):
            hooks.setdefault(event, []).append(
                {"hooks": [{"type": "command", "command": HOOK_COMMAND, "timeout": 5}]})
            changed = True
    if hooks:
        settings["hooks"] = hooks

    updated = False
    for dest, new, old in plan:
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        if old != new:
            write_atomic(dest, new, 0o755)
            updated = updated or old is not None
        elif os.stat(dest).st_mode & 0o777 != 0o755:
            os.chmod(dest, 0o755)

    if changed:
        saved = backup(raw)
        save_settings(settings, raw)
        if saved:
            say("Your Claude Code settings were backed up to %s." % short(saved))
    # The folder the hook drops events into (the app makes it too, the same way).
    os.makedirs(os.path.dirname(EVENTS_DIR), mode=0o700, exist_ok=True)
    os.makedirs(EVENTS_DIR, mode=0o700, exist_ok=True)
    os.chmod(EVENTS_DIR, 0o700)

    if line == "theirs":
        say("You have your own status line, so it stays as it is.")
        say("Your phone still gets your Claude usage from Lid Awake's own check every few minutes.")
    if changed:
        say("On: Claude Code now tells Lid Awake when it finishes or needs you.")
    elif updated:
        say("Already set up; Lid Awake's scripts in ~/.claude were updated.")
    else:
        say("Already set up; nothing changed.")
    return 0


def off():
    settings, raw = load_settings()
    changed = False
    if status_line(settings) == "ours":
        del settings["statusLine"]
        changed = True
    hooks = settings.get("hooks")
    if isinstance(hooks, dict):
        for event in list(hooks):
            groups = hooks[event]
            if not has_our_hook(groups):
                continue
            kept = []
            for g in groups:
                if isinstance(g, dict) and isinstance(g.get("hooks"), list) and any(is_our_hook(h) for h in g["hooks"]):
                    rest = [h for h in g["hooks"] if not is_our_hook(h)]
                    if not rest:
                        continue
                    g = dict(g, hooks=rest)
                kept.append(g)
            if kept:
                hooks[event] = kept
            else:
                del hooks[event]
            changed = True
        if not hooks:
            del settings["hooks"]
    if changed:
        save_settings(settings, raw)

    removed = False
    for _, dest in SCRIPTS:
        old = read(dest)
        if old is None:
            continue
        if ours(old):
            os.unlink(dest)
            removed = True
        else:
            say("%s isn't Lid Awake's, so it was left." % short(dest))
    if changed or removed:
        say("Off: Lid Awake is out of Claude Code's settings, and its scripts are removed.")
    else:
        say("It wasn't set up; nothing to undo.")
    return 0


def check():
    try:
        settings, _ = load_settings()
    except Refuse as e:
        print(str(e))
        return 2
    hooks = settings.get("hooks") or {}
    hooked = [event for event in HOOK_EVENTS if has_our_hook(hooks.get(event))]
    line = status_line(settings)
    if len(hooked) == len(HOOK_EVENTS) and line != "none":
        current = all(read(dest) == read(src) for src, dest in SCRIPTS)
        print("Claude alerts on your phone: on" + ("" if current else " (its scripts need updating: run \"on\" again)"))
        print("Status line: " + ("Lid Awake's" if line == "ours" else
                                 "your own (your usage comes from Lid Awake's own check)"))
        return 0 if current else 3
    if hooked or line == "ours":
        print("Claude alerts on your phone: partly set up (run \"on\" to finish, or \"off\" to undo)")
    else:
        print("Claude alerts on your phone: off")
    return 1


def main(argv):
    global quiet
    args = [a for a in argv if a not in ("-q", "--quiet")]
    quiet = len(args) != len(argv)
    command = {"on": on, "off": off, "status": check}.get(args[0] if len(args) == 1 else "")
    if command is None:
        sys.stderr.write("Usage: python3 %s on|off|status [--quiet]\n" % os.path.relpath(__file__))
        return 64
    try:
        return command()
    except Refuse as e:
        print(str(e))
        return 1
    except OSError as e:
        print("Couldn't finish: %s" % e)
        return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
