# macOS calls the SDK does not offer

All of these are P/Invoke calls from C#; see `sample-plugin/plugin/AgentKeypadPlugin/src/Keypad/MacNative.cs` and `BrowserPanel.cs`.

## Frontmost app and clipboard

- Frontmost app: `NSWorkspace.sharedWorkspace.frontmostApplication.bundleIdentifier` through `objc_msgSend`. Bundle ids: Claude desktop `com.anthropic.claudefordesktop`, Codex desktop `com.openai.codex`.
- Clipboard change count: `NSPasteboard.generalPasteboard.changeCount`. It rises each time anything is copied, which is how to detect that a dictation app has produced its transcript.
- Activate an app with `/usr/bin/open -b <bundle id>`.

## Key events

Create events with `CGEventCreateKeyboardEvent`, set flags with `CGEventSetFlags`, post with `CGEventPost(0, event)` and release with `CFRelease`.

Modifier keys (fn = key code 63, Shift = 56) must be sent as flags-changed events: call `CGEventSetType(event, 12)`. Sent as ordinary key events they do nothing. The dictation app Doing starts hands-free recording on fn down, Shift tap, fn up, and stops on a single fn press.

Never chain keystrokes on a guess. A Clipboard key that pressed Command+Space and then Command+4 switched the agent to another session when Spotlight failed to open. Check the frontmost app between steps, or use the target app's own action.

## Sending text to the agent

Copy the text with `pbcopy`, activate the target app, check it is in front, paste with Command+V, check again, and press Return. If any check fails, leave the text on the clipboard and show "Not sent". Add a watchdog (about 30 s) so the key always ends on Sent or Not sent.

## Screenshots

`screencapture -i -c` lets the user drag an area and copies it to the clipboard. Logi Plugin Service needs Screen Recording permission.

## Permissions

Logi Plugin Service, not your terminal, needs Accessibility (key events, reading other apps' windows) and Screen Recording. Test permission-dependent code inside the plugin, because a probe run from a terminal has the terminal's permissions. Log `AXIsProcessTrusted()` at load.
