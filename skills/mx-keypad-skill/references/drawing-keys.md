# Drawing keys

## Let the plugin draw the whole key

Options+ adds its own caption under each key (for example "Home key 3") and greys out images unless the plugin supplies an icon template. Two things matter:

1. Use a **plugin-level** template at `package/metadata/DefaultIconTemplate.ict`. An action-level template made the service stop asking the plugin for images, and every key went blank.
2. The template gives the image the full key area and hides the text item:

```json
{
  "backgroundColor": 4278190080,
  "items": [
    { "image": "", "imageFileName": null, "imageColor": 4294967295, "imageRotation": "None",
      "isVisible": true, "itemType": "Image", "area": { "x": 0, "y": 0, "width": 100, "height": 100 } },
    { "text": "", "textColor": 4294967295, "fontSize": 5, "fontName": "Brown Logitech Pan Light",
      "isVisible": false, "itemType": "Text", "area": { "x": 0, "y": 70, "width": 100, "height": 30 } }
  ]
}
```

Return `null` from `GetCommandDisplayName` so no text is added.

## Key sizes

The service asks for several sizes. Seen in practice: `Width90` (80 x 80), `Width116` (116 x 116) and a 143 x 143 size in the Options+ editor. Lay out in fractions of width and height, never fixed pixels.

## Colour icons

`BitmapImage.TryCreateFromSvg` recolours an SVG white when it has a single colour. Two changes keep the colour:

- put `stroke="<colour>"` (and `fill="none"` for line icons) on every `path`, `rect` and `circle`, not on the root element;
- add a near-invisible element in a second colour, for example `<circle cx="0.2" cy="0.2" r="0.15" fill="#010203" fill-opacity="0.02"/>`.

## Text

- Use one label size on every key. Fit it to the longest label once; do not shrink labels per key, because uneven sizes look broken.
- Keep labels to about 13 characters at 80 pixels. "No results to read" became unreadable.
- Use fixed rows (icon row, label row, optional small row) so text lines up across keys.
- For long titles (session names), allow three or four wrapped lines in a box under a small icon row.

## Feedback

- On `DeviceButtonEventType.Press` in `ProcessButtonEvent2`, mark the key pressed, call `ActionImageChanged()`, and clear the mark about 160 ms later. Draw a white overlay at about 25% opacity and shrink the icon slightly. Return `false` so `RunCommand` still runs.
- A press on an unavailable key shows a grey key with the reason for about 1.2 s.
- Instant actions flash a green tick with "Opened" or "Done" for about a second.
- Long operations keep their own colour until they end, and must always end: add a watchdog so a key can never stay on "Sending".
- Animation such as a pulsing border needs a timer that redraws about four times a second. Run it only while an animated key is on screen.

## Never let a key fail to draw

Wrap the whole drawing call in try/catch. On an exception, log it and return a blank image. An exception shows Logitech's yellow warning sign on the key.

## Render to files for review

Gate a debug mode on a marker file. In that mode, write every drawn image with `builder.ToArray(BitmapImageFormat.Png)` to the plugin data folder, and at load draw one sample of every key kind and every screen. Look at the PNGs before asking the owner to look at the device.
