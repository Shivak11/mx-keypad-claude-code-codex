# Navigation

## Two kinds of page

- **Options+ pages** are the physical pages of the keypad. The user drags your keys onto them. Your plugin can switch them only through generic actions.
- **Screens** are what your plugin shows on one Options+ page: Home, a list of sessions, a Usage screen. You control these completely.

Put each group of slots on its own Options+ page (for example Home keys `h0`..`h8` on the first page and a second group `m0`..`m8` on the next) and keep a stack of screens per group.

## Rules that felt right on the device

- Lists show six items, then More, Back and Home in the last three slots.
- More moves forward one list page and disappears on the last page.
- Back goes one step back: the previous list page first, then the previous screen. It never jumps further than one step.
- Home always resets both groups to their first screen and shows the first Options+ page.
- Put Back and Home in the same slots on every screen.

## Switching Options+ pages

Use generic actions:

```csharp
this.Plugin.ExecuteGenericAction("@FirstTouchPage", null, 0);     // Home
this.Plugin.ExecuteGenericAction("@PreviousTouchPage", null, 0);  // Back from the second page
```

Do not compute page offsets from `ProfileInfo.json`. On one profile the pages were stored as "Page (2)", "Page (3)" and "Page (1)", in that display order, so the numbers did not match what the user saw. `@FirstTouchPage` follows the display order.

A third Options+ page that holds only Logitech's default keys has no key of yours, so nothing there can go Home. Offer a separate "Go to Home page" slot the user can drag onto any page.

## Knowing which page is on screen

The keypad's own page buttons never reach the plugin, and the SDK has no page-changed event. Options+ does ask for images only of the keys on the page it is showing: after load, every redraw asked for the nine Home keys and none from the second page. So trigger a redraw every few seconds and note which group's keys were drawn since the last check. No keys at all means a page without your keys. If two groups appear in one window, keep the previous reading.

## Returning Home after a quiet spell

`ExecuteGenericAction` only queues a request; the service carries it out with no link to a key press, so the same call works from a timer. An idle return built on that:

- Restart a countdown (60 seconds worked) on every press, on every page change seen through redraws, and while a capture is running, so a recording or a send is never interrupted.
- When it runs out on any screen other than Home, do exactly what the Home key does: reset both stacks and run `@FirstTouchPage`. Fire once per quiet period, then log whether the next check shows the Home page.
- Keep the delay in one named constant and dispose the timer in `Unload()`.
- It only sees presses on your keys. On a page of Logitech's own keys it returns Home even while those keys are in use.

## Reading the profile (read only)

Profiles live under `~/Library/Application Support/Logi/LogiPluginService/Applications/Loupedeck70/<app>/Profiles/<id>/ProfileInfo.json`. `layout.layoutModes[0].workspaces[0].pressPages` lists the pages in display order, and each control's `pressAction` names the action, for example `KeySlotCommand___h0`. Never write to it.
