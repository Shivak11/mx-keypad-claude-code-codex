# Adapting the keypad to the user's own tools

The sample plugin was built around one set of tools: Claude Code and Codex desktop, the Doing dictation app, the Dia browser and the Xnapper screenshot shortcut. A different user has different tools. Before changing any code, learn what the user has, then change only the places that depend on it.

## 1. Detect, then ask

Run the detection script first. It lists the names of installed coding agents, dictation apps, screenshot tools, browsers and terminals, and the build tools, and it opens no settings file, session file or credential:

```bash
python3 -I "<this skill's folder>/scripts/detect_setup.py"
```

A tool that shows only a settings folder may be a leftover, so confirm it with the user. Apple Dictation is part of macOS and is never found by the script, so ask about it.

Ask the questions below in rounds of at most five. Each question carries a recommended answer taken from what was detected, and a reply of "yes" accepts the whole round. Ask a question only when its answer changes what you build. Do not ask the user to choose a mechanism, such as a hook or a file format.

| Round | Question | Recommended answer |
|---|---|---|
| Agents | Which of these coding agents do you use every week? | The ones found as an app or a command |
| Agents | Does each agent run in its own app window, in a terminal, or inside an editor? | As the detection shows |
| Agents | Should Waiting and Ready combine every agent, with one list per agent behind them? | Yes |
| Dictation | How do you start dictation today: hold a key, tap a key to start and again to stop, or click a button? | Tap to start and again to stop |
| Dictation | Which key or shortcut starts it? | The one in the dictation app's settings, which you read from the user and not from files |
| Dictation | Should Discard throw a recording away before it is transcribed? | Yes where the app can cancel, and otherwise stop it and remove the text |
| Screenshots | Which tool takes your area screenshots? | The macOS screenshot tool, unless another was found |
| Browser | Which browser should open the page shown inside an agent's panel? | The default browser |
| Clipboard | Do you use a clipboard manager, or macOS's own clipboard history? | macOS's own |
| Keys | Which three actions do you repeat most each day? | Switch to a session, dictate and send |
| Limits | Do you want usage limits on the keypad? | Only for agents that report them |

## 2. Choose the modules

Each module is a group of keys with one dependency on the user's setup. Build the modules whose dependency the user has, and leave out the rest instead of leaving a key that does nothing.

| Module | Keys | What it needs from the user's setup | If the user lacks it |
|---|---|---|---|
| Session state | Waiting, Ready, one list per agent | A way to learn each session's state: lifecycle hooks, or the agent's own session files | Show only whether the agent is running, or leave the module out |
| Open a session | A session key | A link or command that opens an exact session | Bring the agent's window to the front |
| Dictate and Discard | Dictate, Discard | The dictation app's trigger, and whether it can cancel | Use macOS Dictation, or leave the module out |
| Select Area and Send | Select Area, Send | A screenshot shortcut, and a message box whose text can be read through accessibility | In a terminal agent, send Return without reading the text |
| Next thread | Next thread | A list of the sessions in use with the time each was last focused | Use the app's own next-tab shortcut |
| Usage | Usage screen | An agent that reports its limits | Leave the module out |
| Open in browser | Open in browser | An agent with a built-in browser panel, and the user's browser | Leave the module out |
| Clipboard | Clipboard | Spotlight clipboard history, or the shortcut of the user's clipboard manager. The sample plugin still has the old disabled key, so apply the Spotlight lesson in the failure table | Leave the module out |
| Reflect | Reflect page | A skill that reads the user's recent work, and an agent that can start a new session with a request. It is not in the sample plugin | Leave the module out |

## 3. What every agent adapter must answer

For each agent the user runs, answer these six questions. Where a question has no answer, drop the dependent module for that agent and say so to the user.

1. How does the plugin recognise the agent's window when it is in front? (A bundle id for an app. A terminal's process or window title for a command-line agent.)
2. How does the plugin learn each session's state? (Lifecycle hooks that write one small file per session, or the agent's own session records.)
3. How does the plugin open one exact session?
4. How does the plugin start a new session with a request typed in?
5. How does the plugin put text into the agent's message box, and read what is there?
6. Does the agent report usage limits, and where?

What has been confirmed on a real keypad:

| Agent | Window | Session state | Open a session | New session with a request | Status |
|---|---|---|---|---|---|
| Claude Code, desktop app | Bundle id | Hooks | `claude://code/continue?session=...` | `claude://code/new?q=...&folder=...`, then Return | Tested on the keypad |
| Codex, desktop app | Bundle id | Hooks (approved by the user) | `codex://threads/<id>` | `codex://threads/new?prompt=...`, then Return | Tested on the keypad |
| Cursor | Bundle id | None found | Bring the app forward | `cursor://anysphere.cursor-deeplink/prompt?text=...`, which asks the user to confirm | Link opens a confirmation, not tested end to end |
| Hermes | Terminal | None found | None | `hermes chat --in <folder> -q <request>` in a terminal | Command form only, not tested end to end |
| Any other agent, such as OpenCode, pi or Gemini CLI | Not tested | Not tested | Not tested | Not tested | Read the agent's current documentation for its hooks or extensions and its session storage, then answer the six questions above |

For an agent that is not in the table, write what you found back to the user in plain words before building, and build the hook script to write the same small state file the sample uses (session id, folder, state, times), so the rest of the plugin does not change.

## 4. Where each assumption lives in the sample plugin

The paths are inside `sample-plugin/plugin/AgentKeypadPlugin/src/`, except the hook files, which are in `sample-plugin/hooks/`. Change these places and no others. Where an assumption appears in several places, turn it into one named setting in the user's copy first, so the next change touches one place.

| Assumption | Where it is |
|---|---|
| Agent app bundle ids | `Keypad/CaptureController.cs` (`ClaudeBundle`, `CodexBundle`), `Keypad/SessionStore.cs` |
| Session links and the agents' session sources | `Keypad/SessionStore.cs` |
| Hooks written into each agent | `hooks/keypad_hook.py` and `hooks/install_hooks.py` |
| Dictation trigger and cancel | `Keypad/CaptureController.cs` and `Keypad/MacNative.cs` (the fn sequence) |
| Screenshot shortcut and browser | `Keypad/KeypadController.cs` |
| Usage limits | `Keypad/UsageStore.cs` |

## 5. Before and after building

Show the user the list of modules you will build and the ones you will leave out, with the reason for each, and wait for their reply before you write code. After building, ask the user to press each key once, and report what is proved and what is not. Do not install hooks into an agent's settings without the user's approval, and never approve an agent's hook trust prompt on their behalf.
