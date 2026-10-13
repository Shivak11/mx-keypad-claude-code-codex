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

## Reading the profile (read only)

Profiles live under `~/Library/Application Support/Logi/LogiPluginService/Applications/Loupedeck70/<app>/Profiles/<id>/ProfileInfo.json`. `layout.layoutModes[0].workspaces[0].pressPages` lists the pages in display order, and each control's `pressAction` names the action, for example `KeySlotCommand___h0`. Never write to it.
