---
name: mx-keypad-skill
description: Build a Logitech MX Keypad (MX Creative Console) plugin on macOS with the Logi Actions SDK in C#, and wire it to AI coding agents such as Claude Code and Codex. Covers project setup on the .NET version the Logi Plugin Service really runs, drawing every key yourself, press feedback, page navigation, showing which agent sessions are working, waiting or finished, opening an exact session, dictation and screenshot capture into the last AI window, reading Claude and Codex usage limits, and the failures that cost the most time. Use this skill whenever someone mentions the MX Keypad, MX Creative Console, Loupedeck, Logi Options+ plugins, Logi Plugin Service, LogiPluginTool, .lplug4 files, or wants physical buttons or a macro pad for Claude Code, Codex or other coding agents, even if they do not name the SDK.
---

# MX Keypad plugins for AI coding agents

Few people have built Logitech Actions SDK plugins, and the official documentation is thin and partly out of date. This skill records what actually worked on a real MX Keypad connected to Claude Code and Codex desktop on macOS in October 2026, including the mistakes. A complete working plugin is in `sample-plugin/` of the repository this skill ships in.

## How to work with this skill

1. Read the section below that matches the task, then open only the reference file it points to.
2. Check facts against the installed software before relying on them. Run `dotnet --list-runtimes`, read the plugin log, and inspect the SDK assembly (see `references/setup-and-build.md`). Logitech changes the service without updating the docs.
3. You cannot press the keypad yourself. Prove changes by rendering keys to PNG files and by running probes inside the plugin at load time (see "Proving changes without the device" below). Report anything that only the owner can confirm, such as a real key press, as unproved.

## The facts that cost the most time

These are the problems that took hours to find. Check each one before debugging anything else.

| Symptom | Cause | Fix |
|---|---|---|
| Build fails: PluginApi needs `System.Runtime` 10 | Logi Plugin Service 6.4 runs .NET 10, though Logitech's docs say .NET 8 | Install the .NET 10 SDK and target `net10.0` |
| `dotnet` killed with SIGKILL after installing the SDK over an old one | macOS code-signature cache | `cp dotnet dotnet.fresh && mv -f dotnet.fresh dotnet` in the SDK folder |
| Options+ shows a caption such as "Home key 3" under every key, and icons look grey | No icon template, or an action-level template | Ship a plugin-level `package/metadata/DefaultIconTemplate.ict` with a full-area image and hidden text |
| Images go blank after adding a template | An action-level template stops the service asking the plugin for images | Use the plugin-level template only |
| Coloured SVG icons render white | The renderer recolours single-colour SVGs | Put the stroke on each element and add a near-invisible dot in a second colour |
| Yellow exclamation marks on keys | A key image threw an exception, or the plugin was reloading | Wrap drawing in try/catch and return a blank image; expect marks briefly during reloads |
| Behaviour from old builds keeps happening, or an API starts returning 429 | `Unload()` left timers running, so every reload added another live copy of the plugin | Dispose every timer, watcher and process in `Unload()`; restart Logi Plugin Service once to clear old copies |
| "Home" does nothing on another page | Options+ page numbers in `ProfileInfo.json` are not the display order | Use the generic action `@FirstTouchPage` instead of counting pages |
| Simulated fn key does nothing | fn and Shift are modifier keys and must be sent as flags-changed events | Set the event type to 12 (`kCGEventFlagsChanged`) |
| A key sent the wrong shortcut and switched the agent to another session | Blind keystroke chains (Command+Space, then Command+4) landed in the agent app when the first step failed | Never chain keystrokes on a guess; check the frontmost app first, or use an app's own action |
| `File.Copy` fails with a sharing violation on an app's database | .NET honours the lock another process holds | Copy with `/bin/cp`, then read the copy |
| The plugin cannot tell which Options+ page is on screen | The keypad's own page buttons never reach the plugin | Options+ asks only for images of the visible page's keys: trigger a redraw and note which keys come back |
| A spinner looks frozen on the device | It was drawn once and nothing redrew the key | Redraw progress states on a timer (about four times a second) and derive the frame from the clock; add a seconds count |
| The user said a dictation "gets stuck on Not sent" although the text arrived | They stopped with Cancel, which skipped Return by design, and the key stayed on Not sent until pressed | Never press Return after dictation; end every capture on a short-lived, plainly named state such as "In box" |
| Codex sessions only ever show Working | Codex runs a hook only after it is approved, the approval is tied to the hook's position in `hooks.json`, and only `PostToolUse` was approved | Ask the user to approve every keypad hook in Codex's hook review; never move entries in `hooks.json` without telling them to approve again |
| A session stays on Working or Waiting after Esc or a refused dialog | An interrupted turn ends without `Stop` | Treat a tool event with `is_interrupt` as idle |
| Dictation "gets stuck on loading", then on Not sent | A one-second recording with no words waited the full 25 s for a transcript, no key could end the wait, and Not sent stayed until pressed | Scale the wait with the recording length (6 s plus half the recording, at most 25 s), let the capture key and Cancel end it, and clear Not sent after a few seconds |
| Press and screen-change animations look slow on the device | Options+ shows key images at only a few frames a second, so a dip, a shake or a flip between screens takes visibly long | Give press feedback as an instant brighten and change screens at once; keep animations that run in place, such as an edge light, turning ticks, a breathing outline and countdown lines |
| A key that opens a new Claude Code session with a request leaves the request unsent | `claude://code/new?q=<text>&folder=<path>` fills the new session's message box but does not send it | Press Return yourself, but only after the check in the next row |
| A Return meant for the new session sent half-typed text in the session that was already open | Claude was already in front, so a timed Return landed before the new session's view appeared | Read the focused field through accessibility (`AXFocusedUIElement`, then `AXValue`; Claude's message box is an `AXTextArea`) and press Return only when it holds your request |
| Codex sessions show "waiting for approval" while Codex keeps working | With `approvals_reviewer` set to a reviewer in `~/.codex/config.toml`, Codex sends approval requests to its own reviewer but still fires `PermissionRequest`, and the wait clears only when the approved command finishes | Read `approvals_reviewer` in the hook; when it is not `user`, treat `PermissionRequest` from Codex as working |

## Architecture that works

Use one `PluginDynamicCommand` with a parameter per physical key (for example `h0`..`h8` for the Home page and `m0`..`m8` for a second page), and keep all page state inside the plugin. Options+ only knows "key N was pressed" and "draw key N". The plugin decides what each key shows on each screen. This gives you lists, More, Back and Home without creating dozens of Options+ actions.

- `GetCommandImage` draws the key with `BitmapBuilder`. `ActionImageChanged()` asks for a redraw.
- `ProcessButtonEvent2` on `DeviceButtonEventType.Press` gives feedback on the downstroke; return `false` so `RunCommand` still runs the action on release.
- Make the plugin universal (`HasNoApplication` and `UsesApplicationApiOnly` both true) so one profile stays on screen whatever app is in front, and ask the owner to turn off Adapt to App in Options+.

Read `references/drawing-keys.md` before writing any rendering code, and `references/navigation.md` before adding a second page.

## Connecting to Claude Code and Codex

The keypad needs to know each session's state without reading prompts. A small hook script, called by both apps' lifecycle hooks, writes one JSON file per session with only ids, folder, state and times. The plugin watches that folder. Session titles come from each app's own records, and deep links open the exact session:

- Claude desktop: `claude://code/continue?session=local_<id>&source=desktop_action`
- Codex desktop: `codex://threads/<id>`

Codex asks the user to trust new hooks before they run. Never approve them on the user's behalf.

Read `references/agent-sessions.md` for the hook events, the state machine, where titles live, and how to read the page open in each app's built-in browser panel. Read `references/usage-limits.md` for the Claude and Codex five-hour and weekly limits.

## Sending voice and screenshots to the last AI window

Track the frontmost app every 500 ms and remember the last AI app. Fix the target when a capture starts. When sending, bring the target forward, paste, and check it is still in front. Do not press Return for dictation or screenshots: the user sends the message, and the key shows "In box" for a few seconds and then returns to its normal face. Wait for the target to be in front before pasting or stopping the recorder, because the recorder pastes into whatever app is in front. If the text did not reach the box, leave it on the clipboard and show "Not sent"; never retry on your own. Give every capture a number and ignore late work from an earlier one, and log every capture outcome with its reason. Details and the macOS calls are in `references/macos-native.md`.

## Design rules for the keys

The owner of the first plugin cared about these, and they hold up on an 80 to 116 pixel key:

- One label size on every key, at most about 13 characters. No subtitles unless they carry a state or a reason.
- Show state with icons and colours, not words. Keep one meaning per colour: red recording, blue choosing or working, amber needs you, green done, grey not sent, violet opening.
- Every press shows feedback within 0.1 s. A press on an unavailable key shows the reason for about a second; it never does nothing.
- Back goes one step back (previous list page, then previous screen). Home always returns to the first page. Keep Back and Home in the same slots on every screen.
- Keys that look alike must do the same thing. A "switch to the other agent" key needs its own icon, not the agent's logo.

## Proving changes without the device

- Gate a debug mode on a marker file (for example `~/.mx-keypad/debug-renders`). In debug mode, save every drawn key as a PNG and draw every screen at load, then look at the PNGs.
- Run probes inside the plugin at load in debug mode when something depends on the service's own permissions (Accessibility, network, Keychain). The service has different permissions from your terminal.
- Log one line per outcome to the plugin log, including every capture result and its reason, at `~/Library/Application Support/Logi/LogiPluginService/Logs/plugin_logs/<Name>.log`. Never log secrets.
- Reload with `open -g "loupedeck:plugin/<Name>/reload"`. Every reload sends a fresh round of network requests unless you cache and back off.

## Reference files

| File | Read when |
|---|---|
| `references/setup-and-build.md` | Creating the project, building, installing, inspecting the SDK |
| `references/drawing-keys.md` | Drawing keys, icons, fonts, press feedback, the icon template |
| `references/navigation.md` | Pages, lists, More, Back, Home, switching Options+ pages |
| `references/agent-sessions.md` | Hooks, session state, titles, deep links, browser panel pages |
| `references/usage-limits.md` | Claude and Codex five-hour and weekly limits |
| `references/macos-native.md` | Frontmost app, clipboard, key events, accessibility, permissions |
