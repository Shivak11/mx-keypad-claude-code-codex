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

For a fixed instruction (not dictation): save the clipboard, copy the text with `pbcopy` and check the change count rose, activate the target app and wait until it is in front plus about 450 ms for the message box to take focus, paste with Command+V, check again, press Return, then restore the clipboard. If any check fails, show "Not sent". Add a watchdog so a key always ends.

## Saving and restoring the clipboard

To paste your own text without losing what the user copied, save every item on the pasteboard with every type (`pasteboardItems`, then `types` and `dataForType:` for each), paste, wait about 400 ms for the app to read it, and write the items back as new `NSPasteboardItem`s with `writeObjects:`. Skip the restore if `changeCount` moved since your copy, because the user copied something newer. macOS can add derived types when content is written (rich text gained a UTF-16 plain-text type), so compare a restore against what you saved, not against what you first wrote.

Test it on a named pasteboard (`pasteboardWithName:`), never the user's clipboard. Check every Objective-C object for nil before adding it to an array: in a test program without AppKit loaded, `NSPasteboardItem` was nil and `addObject:` raised an Objective-C exception, which ends the whole process. Inside Logi Plugin Service that would stop every plugin. Load AppKit explicitly.

## Screenshots

`screencapture -i -c` lets the user drag an area and copies it to the clipboard. Logi Plugin Service needs Screen Recording permission.

## Permissions

Logi Plugin Service, not your terminal, needs Accessibility (key events, reading other apps' windows) and Screen Recording. Test permission-dependent code inside the plugin, because a probe run from a terminal has the terminal's permissions. Log `AXIsProcessTrusted()` at load.
