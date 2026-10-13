#!/usr/bin/env python3
"""Install or remove the MX Keypad session hooks for Claude Code and Codex.

Copies keypad_hook.py to ~/.mx-keypad/bin/, backs up each settings file, removes any
earlier keypad entries and adds the current ones. Other hooks are left untouched.
Running it twice gives the same result. `--remove` takes the keypad entries out again.
"""
import json
import os
import shutil
import sys
import time

HOME = os.path.expanduser("~")
BIN = os.path.join(HOME, ".mx-keypad", "bin")
HOOK = os.path.join(BIN, "keypad_hook.py")
PY = "/opt/homebrew/bin/python3" if os.path.exists("/opt/homebrew/bin/python3") else "/usr/bin/python3"
MARK = "keypad_hook.py"

CLAUDE = os.path.join(HOME, ".claude", "settings.json")
CODEX = os.path.join(HOME, ".codex", "hooks.json")

CLAUDE_EVENTS = [
    ("SessionStart", None), ("UserPromptSubmit", None), ("PermissionRequest", None),
    ("PreToolUse", "AskUserQuestion"), ("PostToolUse", "*"), ("PostToolUseFailure", "*"),
    ("PermissionDenied", None), ("Stop", None), ("StopFailure", None), ("SessionEnd", None),
]
CODEX_EVENTS = ["SessionStart", "UserPromptSubmit", "PermissionRequest", "PostToolUse", "Stop", "SessionEnd"]


def command(app, event):
    return f'{PY} -I -S "{HOOK}" {app} {event}'


def strip_keypad(hooks):
    for event in list(hooks):
        groups = []
        for g in hooks[event]:
            inner = [h for h in g.get("hooks", []) if MARK not in h.get("command", "")]
            if inner:
                g["hooks"] = inner
                groups.append(g)
            elif not g.get("hooks"):
                groups.append(g)
        if groups:
            hooks[event] = groups
        else:
            del hooks[event]


def backup(path):
    stamp = time.strftime("%Y%m%d-%H%M%S")
    dest = f"{path}.mx-keypad-backup-{stamp}"
    shutil.copy2(path, dest)
    return dest


def save(path, data):
    # Keeps the file's permissions and writes non-ASCII text as is.
    mode = os.stat(path).st_mode & 0o777
    tmp = path + ".mx-keypad-tmp"
    with open(tmp, "w") as f:
        json.dump(data, f, indent=2, ensure_ascii=False)
        f.write("\n")
    os.chmod(tmp, mode)
    os.replace(tmp, path)


def update(path, add):
    with open(path) as f:
        data = json.load(f)
    print("backup:", backup(path))
    hooks = data.setdefault("hooks", {})
    strip_keypad(hooks)
    if add:
        add(hooks)
    save(path, data)


def add_claude(hooks):
    for event, matcher in CLAUDE_EVENTS:
        group = {"hooks": [{"type": "command", "command": command("claude", event), "timeout": 5}]}
        if matcher:
            group["matcher"] = matcher
        hooks.setdefault(event, []).append(group)


def add_codex(hooks):
    for event in CODEX_EVENTS:
        hooks.setdefault(event, []).append({"hooks": [{"type": "command", "command": command("codex", event), "timeout": 5}]})


def main():
    remove = "--remove" in sys.argv
    if not remove:
        os.makedirs(BIN, exist_ok=True)
        os.makedirs(os.path.join(HOME, ".mx-keypad", "sessions"), exist_ok=True)
        shutil.copy2(os.path.join(os.path.dirname(os.path.abspath(__file__)), "keypad_hook.py"), HOOK)
    # Read both files first, so a broken file stops the run before anything is written.
    for path in (CLAUDE, CODEX):
        with open(path) as f:
            json.load(f)
    update(CLAUDE, None if remove else add_claude)
    update(CODEX, None if remove else add_codex)
    print("removed" if remove else "installed")


if __name__ == "__main__":
    main()
