#!/usr/bin/env python3
# Lid Awake: Claude Code's status line. Shows the model and your plan usage, for example
#   Opus · 5h 42% (resets 15:10) · week 18% (resets Thu 22:59)
# and saves the usage for Lid Awake's phone page (~/Library/Application Support/LidAwake/
# claude-usage.json). It never fails Claude: anything odd is simply left out.
import json
import math
import os
import sys
import tempfile
import time
from datetime import datetime

FOLDER = os.path.join(os.path.expanduser("~"), "Library", "Application Support", "LidAwake")


def save(limits):
    # Only once Lid Awake is installed (its folder exists); written whole or not at all.
    if not os.path.isdir(FOLDER):
        return
    fd, tmp = tempfile.mkstemp(dir=FOLDER, prefix=".claude-usage.", suffix=".tmp")
    try:
        with os.fdopen(fd, "w") as f:
            json.dump({"rate_limits": limits, "updated": int(time.time())}, f)
        os.replace(tmp, os.path.join(FOLDER, "claude-usage.json"))
    except BaseException:
        try:
            os.unlink(tmp)
        except OSError:
            pass
        raise


def resets(value, fmt):
    try:
        if isinstance(value, (int, float)) and not isinstance(value, bool):
            when = datetime.fromtimestamp(value)
        elif isinstance(value, str):
            when = datetime.fromisoformat(value.replace("Z", "+00:00")).astimezone()
        else:
            return ""
        return " (resets %s)" % when.strftime(fmt)
    except (ValueError, OverflowError, OSError):
        return ""


def window(limits, key, label, fmt):
    w = limits.get(key) if isinstance(limits, dict) else None
    pct = w.get("used_percentage") if isinstance(w, dict) else None
    if not isinstance(pct, (int, float)) or isinstance(pct, bool) or not math.isfinite(pct):
        return None
    return "%s %d%%%s" % (label, round(pct), resets(w.get("resets_at"), fmt))


def main():
    try:
        data = json.load(sys.stdin)
    except (ValueError, UnicodeDecodeError):
        data = None
    if not isinstance(data, dict):
        data = {}
    limits = data.get("rate_limits")
    if isinstance(limits, dict) and limits:
        try:
            save(limits)
        except Exception:
            pass
    model = data.get("model")
    name = model.get("display_name") if isinstance(model, dict) else None
    parts = [str(name)] if name else []
    for key, label, fmt in (("five_hour", "5h", "%H:%M"), ("seven_day", "week", "%a %H:%M")):
        part = window(limits, key, label, fmt)
        if part:
            parts.append(part)
    sys.stdout.buffer.write((" · ".join(parts) + "\n").encode("utf-8"))


if __name__ == "__main__":
    try:
        main()
    except Exception:
        sys.stdout.write("\n")
