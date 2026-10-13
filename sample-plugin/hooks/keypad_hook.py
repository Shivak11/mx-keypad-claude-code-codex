#!/usr/bin/env python3
"""Record Claude Code and Codex session status for the MX Keypad plugin.

Called by each app's lifecycle hooks with the hook's JSON on stdin. Writes one small
file per session to ~/.mx-keypad/sessions/. Stores only ids, folder, state and times;
never prompt or reply text. Always exits 0 and prints nothing, so it can never block
or alter the agent.

Usage: keypad_hook.py <claude|codex> [EventName]
"""
import fcntl
import json
import os
import sys
import tempfile
import time

ROOT = os.environ.get("MX_KEYPAD_DIR") or os.path.expanduser("~/.mx-keypad")
SESSIONS = os.path.join(ROOT, "sessions")


def write_atomic(path, record):
    fd, tmp = tempfile.mkstemp(dir=SESSIONS, prefix=".tmp-")
    try:
        with os.fdopen(fd, "w") as f:
            json.dump(record, f)
        os.replace(tmp, path)
    finally:
        if os.path.exists(tmp):
            os.remove(tmp)


def main():
    if len(sys.argv) < 2 or sys.argv[1] not in ("claude", "codex"):
        return
    app = sys.argv[1]
    try:
        data = json.load(sys.stdin)
    except Exception:
        return
    event = data.get("hook_event_name") or (sys.argv[2] if len(sys.argv) > 2 else "")
    sid = str(data.get("session_id") or "").strip()
    if not sid or len(sid) > 128 or not all(c.isalnum() or c in "-_" for c in sid):
        return

    os.makedirs(SESSIONS, exist_ok=True)
    path = os.path.join(SESSIONS, f"{app}-{sid}.json")

    if event == "SessionEnd":
        try:
            os.remove(path)
        except FileNotFoundError:
            pass
        return

    # One writer at a time per session, so parallel tool calls cannot overwrite a permission wait.
    lock = open(os.path.join(SESSIONS, ".lock"), "w")
    fcntl.flock(lock, fcntl.LOCK_EX)
    try:
        update(app, sid, path, event, data)
    finally:
        fcntl.flock(lock, fcntl.LOCK_UN)
        lock.close()


def update(app, sid, path, event, data):
    try:
        with open(path) as f:
            rec = json.load(f)
    except Exception:
        rec = {"state": "idle", "ready": False}

    now = time.time()
    rec.update({"app": app, "id": sid, "updated": now})
    if data.get("cwd"):
        rec["cwd"] = data["cwd"]

    tool = data.get("tool_name") or ""
    if event == "UserPromptSubmit":
        rec.update(state="working", wait=None, waitTool=None, ready=False)
    elif event == "PermissionRequest":
        rec.update(state="waiting", wait="Approval" if app == "codex" else "Permission", waitTool=tool, waitSince=now)
    elif event == "PreToolUse" and tool == "AskUserQuestion":
        rec.update(state="waiting", wait="Question", waitTool=tool, waitSince=now)
    elif event in ("PostToolUse", "PostToolUseFailure", "PermissionDenied"):
        # Only the tool that was waiting clears the wait; other tools finishing in parallel do not.
        if rec.get("state") == "waiting":
            if rec.get("waitTool") in (None, "", tool):
                rec.update(state="working", wait=None, waitTool=None)
        else:
            # A tool ran after Stop (another Stop hook made the agent continue): it is working again.
            rec.update(state="working", ready=False)
    elif event in ("Stop", "StopFailure"):
        rec.update(state="idle", wait=None, waitTool=None, ready=True, readyAt=now)
    elif event == "SessionStart":
        rec.update(state="idle", wait=None, waitTool=None)
    else:
        return

    write_atomic(path, rec)


if __name__ == "__main__":
    try:
        main()
    except Exception:
        pass
    sys.exit(0)
