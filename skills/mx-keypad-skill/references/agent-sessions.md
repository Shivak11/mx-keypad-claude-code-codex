# Agent sessions

## Session state from hooks

Both Claude Code and Codex run lifecycle hooks. Register one script for both apps; it writes `~/.mx-keypad/sessions/<app>-<session id>.json` with only ids, folder, state and times. Never store prompt or reply text, always exit 0 and print nothing, so the hook can never block or change the agent.

| Event | State |
|---|---|
| `UserPromptSubmit` | working |
| `PermissionRequest` | waiting (remember which tool asked) |
| `PreToolUse` with tool `AskUserQuestion` | waiting for an answer |
| `PostToolUse`, `PostToolUseFailure`, `PermissionDenied` | clear the wait only when the waiting tool finishes; after Stop, set working again |
| `Stop`, `StopFailure` | idle and ready (finished, not yet opened) |
| `SessionStart` | idle |
| `SessionEnd` | delete the file |

Practical details:

- Validate the session id (letters, digits, `-` and `_`, at most 128 characters) before using it in a file name.
- Take a file lock around read-modify-write. Parallel tool events otherwise overwrite a permission wait.
- Write atomically (temp file, then rename) and remove temp files on failure.
- Delete records older than 48 hours.
- Claude Code: add entries to `~/.claude/settings.json` under `hooks`. Codex: `~/.codex/hooks.json` with `SessionStart`, `UserPromptSubmit`, `PermissionRequest`, `PostToolUse`, `Stop`, `SessionEnd`. Back up both files and keep the user's other hooks.
- Codex asks the user to trust new hooks after a restart. Only the user approves them.

The sample installer is `sample-plugin/hooks/install_hooks.py` (`--remove` takes the entries out).

## Titles and the session list

- **Claude desktop** keeps one JSON record per session under `~/Library/Application Support/Claude/claude-code-sessions/**/local_*.json` with `cliSessionId`, `sessionId`, `title`, `isArchived`, `lastActivityAt` and `cwd`. The hook's session id is `cliSessionId`; the deep link needs `sessionId` (`local_...`). Cache by file modification time.
- **Codex** keeps threads in `~/.codex/state_5.sqlite`, table `threads`. Read it read-only with `/usr/bin/sqlite3 -readonly -json`. Filter `thread_source = 'user'`: on one machine there were 311 guardian review threads and 144 subagent threads that do not belong in a list.
- Sessions active in the last 48 hours can appear before any hook fires; show them without a state icon.

## Opening the exact session

```text
claude://code/continue?session=local_<sessionId>&source=desktop_action
codex://threads/<thread id>
```

Open with `/usr/bin/open <url>`. Opening a "ready" session should mark it read.

## Approving from the keypad

Not built in the sample. Claude's `PermissionRequest` hook can return allow or deny, but it runs before the dialog appears, so a keypad answer would have to press keys in the visible dialog. Observe each app's dialog first, open the session, and only then show that app's own options. Never approve on a guess.

## The page open in the app's browser panel

Both apps have a built-in browser panel, and "open this page in my real browser" is a useful key.

- **Codex** stores the panel's history in `~/Library/Application Support/Codex/Default/Partitions/codex-browser-app/History` (Chromium format). Codex holds it locked, so copy it with `/bin/cp` (not .NET `File.Copy`, which fails with a sharing violation) and read `select url from urls where url like 'http%' order by last_visit_time desc limit 1`. If the panel was closed, this is still the last page it showed.
- **Claude desktop** stores nothing about its panel on disk. Read it through macOS accessibility: get Claude's pid from `NSRunningApplication`, set `AXManualAccessibility` to true on the app element so Electron builds its tree, then walk `AXWindows` and `AXChildren` for an `AXWebArea` whose `AXURL` is not Claude's own interface (`claude.ai`, `anthropic.com`). It took 531 elements on a real window. Logi Plugin Service needs Accessibility permission; log `AXIsProcessTrusted()` at load.
