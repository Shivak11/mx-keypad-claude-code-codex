namespace Loupedeck.AgentKeypadPlugin
{
    using System;

    // Draws a KeyFace into the image the keypad shows. Each kind uses fixed rows so text aligns across keys.
    internal static class KeyRenderer
    {
        // When set, each drawn key is also saved as a PNG for inspection.
        public static String DebugDir;

        // 0..1, advanced by the controller about four times a second while a working session is on screen.
        public static Double Pulse;

        [ThreadStatic]
        public static String DebugName;

        private static BitmapColor Hex(String hex, Int32 alpha = 255)
        {
            var v = Convert.ToUInt32(hex.TrimStart('#'), 16);
            return new BitmapColor((Int32)((v >> 16) & 0xff), (Int32)((v >> 8) & 0xff), (Int32)(v & 0xff), alpha);
        }

        private static (String bg, String border) ToneColors(KeyTone tone) => tone switch
        {
            KeyTone.Recording => ("#3a1414", Colors.Red),
            KeyTone.Selecting => ("#132439", Colors.Blue),
            KeyTone.Sending => ("#33270f", Colors.Amber),
            KeyTone.Sent or KeyTone.Done => ("#103020", Colors.Green),
            KeyTone.NotSent => ("#26272c", Colors.Amber),
            KeyTone.Opening => ("#221c38", Colors.Violet),
            KeyTone.Empty => ("#000000", null),
            _ => ("#111114", null),
        };

        // Largest font size, up to the preferred size, at which the text fits on one line (about 0.55 em per character).
        private static Int32 FitFont(String text, Int32 width, Double preferred) =>
            (Int32)Math.Max(width * 0.09, Math.Min(preferred, (width - 8) / Math.Max(1.0, text.Length * 0.55)));

        private static void Icon(BitmapBuilder b, String name, String color, Int32 size, Int32 x, Int32 y)
        {
            var svg = Icons.Svg(name, color, size);
            if (svg != null && BitmapImage.TryCreateFromSvg(svg, out var icon))
            {
                b.DrawImage(icon, x, y, size, size);
            }
        }

        public static BitmapImage Draw(KeyFace face, PluginImageSize size, Boolean pressed, Boolean denied)
        {
            using var b = new BitmapBuilder(size);
            var w = b.Width;
            var h = b.Height;
            var (bg, border) = ToneColors(face.Tone);
            if (face.Kind == KeyKind.Nav && face.Tone == KeyTone.Normal)
            {
                bg = "#202026";   // navigation keys sit on a lighter, flatter ground than content keys
            }

            if (denied)
            {
                bg = "#3a3a40";
            }

            b.Clear(Hex(bg));
            if (face.Kind == KeyKind.Session && face.Status == "Working")
            {
                var alpha = (Int32)(110 + 145 * (0.5 + 0.5 * Math.Sin(Pulse * 2 * Math.PI)));
                var bc = Hex(Colors.Blue, alpha);
                for (var i = 1; i <= 3; i++)
                {
                    b.DrawRectangle(i, i, w - 1 - 2 * i, h - 1 - 2 * i, bc);
                }
            }

            if (border != null)
            {
                var bc = Hex(border);
                b.DrawRectangle(1, 1, w - 3, h - 3, bc);
                b.DrawRectangle(2, 2, w - 5, h - 5, bc);
            }

            if (face.Tone != KeyTone.Empty)
            {
                switch (face.Kind)
                {
                    case KeyKind.Session:
                        DrawSession(b, face, w, h, denied);
                        break;
                    case KeyKind.Stat:
                        DrawStat(b, face, w, h);
                        break;
                    case KeyKind.Note:
                        b.DrawText(face.Label ?? "", 4, (Int32)(h * 0.25), w - 8, (Int32)(h * 0.5), Hex(Colors.Muted), (Int32)(h * 0.15), 0, 0);
                        break;
                    default:
                        DrawAction(b, face, w, h, pressed, denied);
                        break;
                }

                if (face.Badge > 0)
                {
                    var r = h * 0.12f;
                    var cx = w - (r + h * 0.07f);
                    var cy = r + h * 0.07f;
                    b.FillCircle(cx, cy, r, Hex(face.BadgeColor ?? Colors.Amber));
                    b.DrawText(face.Badge > 99 ? "99" : face.Badge.ToString(), (Int32)(cx - r), (Int32)(cy - r), (Int32)(r * 2), (Int32)(r * 2), Hex("#111111"), (Int32)(h * 0.14), 0, 0);
                }
            }

            // Press feedback: brighten the whole key on the downstroke.
            if (pressed)
            {
                b.FillRectangle(0, 0, w, h, new BitmapColor(255, 255, 255, 60));
            }

            if (DebugDir != null && DebugName != null)
            {
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(DebugDir, $"{DebugName}-{size}-{w}x{h}.png"), b.ToArray(BitmapImageFormat.Png));
            }

            return b.ToImage();
        }

        // Draws one key of every kind and state into the debug folder, for checking layouts without the device.
        public static void DrawSamples()
        {
            var samples = new (String name, KeyFace face)[]
            {
                ("sample-working", new KeyFace { Kind = KeyKind.Session, App = "claude", Label = "Keypad plugin review", Status = "Working" }),
                ("sample-permission", new KeyFace { Kind = KeyKind.Session, App = "claude", Label = "Payments retry bug", Status = "Permission" }),
                ("sample-question", new KeyFace { Kind = KeyKind.Session, App = "claude", Label = "Docs site rewrite", Status = "Question" }),
                ("sample-approval", new KeyFace { Kind = KeyKind.Session, App = "codex", Label = "Mobile release", Status = "Approval" }),
                ("sample-ready", new KeyFace { Kind = KeyKind.Session, App = "codex", Label = "API tests", Status = "Ready" }),
                ("sample-open", new KeyFace { Kind = KeyKind.Session, App = "claude", Label = "Migrate the billing service to the new queue", Status = "Open" }),
                ("sample-note", new KeyFace { Kind = KeyKind.Note, Label = "None ready" }),
                ("sample-stat", new KeyFace { Kind = KeyKind.Stat, App = "codex", Label = "This week", Value = "13%", Percent = 13, Small = "resets Tue" }),
                ("sample-nav", new KeyFace { Kind = KeyKind.Nav, Icon = "back", Label = "Back" }),
                ("sample-more", new KeyFace { Kind = KeyKind.Nav, Icon = "next", Label = "More", Small = "1 of 2" }),
            };
            Pulse = 0.25;
            foreach (var (name, face) in samples)
            {
                DebugName = name;
                Draw(face, PluginImageSize.Width116, false, false);
            }
        }

        private static void DrawAction(BitmapBuilder b, KeyFace face, Int32 w, Int32 h, Boolean pressed, Boolean denied)
        {
            var dim = face.Tone == KeyTone.Off && !denied;
            var small = denied ? (face.Why ?? "unavailable") : face.Small;
            var hasSmall = !String.IsNullOrEmpty(small);
            var nav = face.Kind == KeyKind.Nav;

            // Without a subtitle, the icon and label move down so the pair stays centred.
            var full = nav ? 0.34 : 0.40;
            var iconSize = (Int32)(w * (pressed ? full - 0.04 : full));
            var iconTop = (Int32)(h * (hasSmall ? 0.09 : 0.15)) + ((Int32)(w * full) - iconSize) / 2;
            Icon(b, face.Icon, nav ? Colors.Ink : face.IconColor, iconSize, (w - iconSize) / 2, iconTop);

            var label = face.Label ?? "";
            var labelTop = (Int32)(h * (hasSmall ? 0.53 : 0.60));
            var labelColor = dim ? Hex("#a0a0a8") : Hex(nav ? Colors.Ink : "#ffffff");
            // One label size on every key, sized so the longest label fits.
            b.DrawText(label, 1, labelTop, w - 2, (Int32)(h * 0.22), labelColor, FitFont(label, w, h * 0.145), 0, 0);

            if (hasSmall)
            {
                b.DrawText(small, 1, (Int32)(h * 0.75), w - 2, (Int32)(h * 0.2), Hex(denied ? "#ffffff" : "#c8c8d0"), FitFont(small, w, h * 0.135), 0, 0);
            }
        }

        // Session key: the app's icon top left, a state icon top right, and the title below.
        // State is shown by icon and outline colour only, never by a word.
        private static void DrawSession(BitmapBuilder b, KeyFace face, Int32 w, Int32 h, Boolean denied)
        {
            var appColor = face.App == "claude" ? Colors.Orange : Colors.Teal;
            var glyph = (Int32)(h * 0.2);
            var pad = (Int32)(w * 0.08);
            Icon(b, face.App == "claude" ? "claude" : "codex", appColor, glyph, pad, pad);

            var (stateIcon, stateColor) = face.Status switch
            {
                "Working" => ("spin", Colors.Blue),
                "Permission" or "Approval" => ("shield", Colors.Amber),
                "Question" => ("question", Colors.Amber),
                "Ready" => ("checkcircle", Colors.Green),
                _ => ((String)null, (String)null),
            };
            if (stateIcon != null)
            {
                Icon(b, stateIcon, stateColor, glyph, w - pad - glyph, pad);
            }

            // A coloured outline repeats the state for Waiting and Ready; Working pulses separately.
            if (stateColor != null && face.Status != "Working")
            {
                var bc = Hex(stateColor, 200);
                b.DrawRectangle(1, 1, w - 3, h - 3, bc);
                b.DrawRectangle(2, 2, w - 5, h - 5, bc);
            }

            var title = denied ? (face.Why ?? "") : face.Label ?? "";
            var font = (Int32)(h * (title.Length > 22 ? 0.13 : title.Length > 11 ? 0.145 : 0.165));
            b.DrawText(title, 3, (Int32)(h * 0.34), w - 6, (Int32)(h * 0.6), Hex(denied ? "#ffffff" : "#f1f1f3"), font, 0, 0);
        }

        private static void DrawStat(BitmapBuilder b, KeyFace face, Int32 w, Int32 h)
        {
            var appColor = face.App == "claude" ? Colors.Orange : Colors.Teal;
            var glyph = (Int32)(h * 0.15);
            var pad = (Int32)(w * 0.08);
            Icon(b, face.App == "claude" ? "claude" : "codex", appColor, glyph, pad, (Int32)(h * 0.06));
            b.DrawText(face.Label ?? "", pad + glyph, (Int32)(h * 0.06), w - pad * 2 - glyph, glyph, Hex(Colors.Ink), FitFont(face.Label ?? "", w - glyph, h * 0.12), 0, 0);

            var value = face.Value ?? "–";
            b.DrawText(value, 2, (Int32)(h * 0.27), w - 4, (Int32)(h * 0.3), Hex(face.Value == null ? Colors.Muted : "#ffffff"), (Int32)(h * 0.24), 0, 0);

            if (face.Percent is Double p)
            {
                var bw = w - pad * 2;
                var by = (Int32)(h * 0.62);
                var bh = Math.Max(4, (Int32)(h * 0.05));
                b.FillRectangle(pad, by, bw, bh, Hex("#2a2a2f"));
                var fill = (Int32)(bw * Math.Clamp(p, 0, 100) / 100.0);
                var barColor = p >= 90 ? Colors.Red : p >= 70 ? Colors.Amber : appColor;
                if (fill > 0)
                {
                    b.FillRectangle(pad, by, fill, bh, Hex(barColor));
                }
            }

            if (!String.IsNullOrEmpty(face.Small))
            {
                b.DrawText(face.Small, 1, (Int32)(h * 0.74), w - 2, (Int32)(h * 0.2), Hex("#c8c8d0"), FitFont(face.Small, w, h * 0.125), 0, 0);
            }
        }
    }
}
