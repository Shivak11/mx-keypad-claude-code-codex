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
    # Headless judge runs that other plugins start with `claude --print` are not sessions the user opened.
    if os.environ.get("CLAUDE_HOOK_JUDGE_MODE"):
        return
    app = sys.argv[1]
    try:
        data = json.load(sys.stdin)
    except Exception:
        return
    if not isinstance(data, dict):
        return
    event = data.get("hook_event_name") or (sys.argv[2] if len(sys.argv) > 2 else "")
    sid = str(data.get("session_id") or "").strip()
    if not sid or len(sid) > 128 or not all(c.isalnum() or c in "-_" for c in sid):
        return

    os.makedirs(SESSIONS, exist_ok=True)
    path = os.path.join(SESSIONS, f"{app}-{sid}.json")

    # One writer at a time, so parallel tool calls cannot overwrite a permission wait.
    # Give up after 2 s rather than hold up the agent.
    lock = open(os.path.join(SESSIONS, ".lock"), "w")
    try:
        deadline = time.monotonic() + 2
        while True:
            try:
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
                break
            except BlockingIOError:
                if time.monotonic() > deadline:
                    return
                time.sleep(0.02)
        try:
            update(app, sid, path, event, data)
        finally:
            fcntl.flock(lock, fcntl.LOCK_UN)
    finally:
        lock.close()


def matches(entry, tool_use_id, tool):
    if tool_use_id and entry.get("id"):
        return entry["id"] == tool_use_id
    return entry.get("tool") in (None, "", tool)


def update(app, sid, path, event, data):
    try:
        with open(path) as f:
            rec = json.load(f)
        if not isinstance(rec, dict):
            raise ValueError
    except Exception:
        rec = {"state": "idle", "ready": False}

    now = time.time()
    tool = data.get("tool_name") or ""
    tool_use_id = data.get("tool_use_id") or ""
    pending = [p for p in rec.get("pending") or [] if isinstance(p, dict)]

    if event == "SessionEnd":
        # A marker instead of a delete: a Stop still waiting for the lock cannot bring the session back.
        write_atomic(path, {"state": "ended", "app": app, "id": sid, "updated": now})
        return

    if rec.get("state") == "ended":
        if event not in ("SessionStart", "UserPromptSubmit"):
            return
        rec = {"state": "idle", "ready": False}

    def set_waits(items):
        rec["pending"] = items
        if items:
            rec.update(state="waiting", wait=items[-1]["wait"], waitTool=items[-1]["tool"])
            rec.setdefault("waitSince", now)
        else:
            rec.update(wait=None, waitTool=None)
            rec.pop("waitSince", None)

    if event == "UserPromptSubmit":
        rec.update(state="working", ready=False)
        set_waits([])
    elif event == "PermissionRequest" or (event == "PreToolUse" and tool == "AskUserQuestion"):
        label = "Question" if tool == "AskUserQuestion" else "Approval" if app == "codex" else "Permission"
        if not pending:
            rec["waitSince"] = now
        set_waits(pending + [{"id": tool_use_id, "tool": tool, "wait": label}])
    elif event in ("PostToolUse", "PostToolUseFailure", "PermissionDenied"):
        if data.get("is_interrupt"):
            # Esc or a refused dialog ends the turn without Stop.
            rec["state"] = "idle"
            set_waits([])
        elif rec.get("state") == "waiting":
            # Only the tool that asked clears its wait; other tools finishing in parallel do not.
            if pending:
                left = [p for p in pending if not matches(p, tool_use_id, tool)]
            else:
                # A record written before the pending list existed: match on the tool name.
                left = [] if rec.get("waitTool") in (None, "", tool) else None
            if left is not None:
                set_waits(left)
                if not left:
                    rec["state"] = "working"
        elif data.get("agent_id") and rec.get("state") == "idle":
            # A background subagent still running after the main turn ended; the session stays Ready.
            return
        else:
            # A tool ran after Stop (another Stop hook made the agent continue): it is working again.
            rec.update(state="working", ready=False)
    elif event in ("Stop", "StopFailure"):
        rec.update(state="idle", ready=True, readyAt=now)
        set_waits([])
    elif event == "SessionStart":
        if data.get("source") == "compact":
            return   # compaction in the middle of a turn: the agent carries on working
        rec["state"] = "idle"
        set_waits([])
    else:
        return

    rec.update({"app": app, "id": sid, "updated": now})
    if data.get("cwd"):
        rec["cwd"] = data["cwd"]
    write_atomic(path, rec)


if __name__ == "__main__":
    try:
        main()
    except Exception:
        pass
    sys.exit(0)
