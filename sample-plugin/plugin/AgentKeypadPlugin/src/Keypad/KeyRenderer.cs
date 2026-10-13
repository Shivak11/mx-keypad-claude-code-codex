namespace Loupedeck.AgentKeypadPlugin
{
    using System;

    // Draws a KeyFace into the image the keypad shows. Each kind uses fixed rows so text aligns across keys.
    internal static class KeyRenderer
    {
        // When set, each drawn key is also saved as a PNG for inspection.
        public static String DebugDir;

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

        // ---------- drawn shapes used by the animations ----------

        // Eight ticks round a circle with a bright head that moves one tick every 125 ms (one frame of the
        // animation timer), so it reads as turning at 8 frames a second.
        private static void Ticks(BitmapBuilder b, String color, Single cx, Single cy, Single size, Double now)
        {
            var head = (Int32)(now / Motion.FrameMs) % 8;
            Single r1 = size * 0.22f, r2 = size * 0.42f, width = Math.Max(2f, size * 0.085f);
            for (var i = 0; i < 8; i++)
            {
                var behind = (head - i + 8) % 8;
                var alpha = (Int32)(255 * Math.Max(0.16, 1 - behind * 0.13));
                var a = i / 8.0 * Math.PI * 2 - Math.PI / 2;
                b.DrawLine(cx + (Single)Math.Cos(a) * r1, cy + (Single)Math.Sin(a) * r1, cx + (Single)Math.Cos(a) * r2, cy + (Single)Math.Sin(a) * r2, Hex(color, alpha), width);
            }
        }

        // The check mark from Icons.cs ("m5 12 5 5 9-10" on a 24 grid), drawn up to the given progress.
        private static void Check(BitmapBuilder b, String color, Int32 size, Int32 x, Int32 y, Double progress)
        {
            Single S(Double v) => (Single)(v * size / 24.0);
            var (x0, y0, x1, y1, x2, y2) = (x + S(5), y + S(12), x + S(10), y + S(17), x + S(19), y + S(7));
            var l1 = Math.Sqrt(50); var l2 = Math.Sqrt(181); var total = l1 + l2;
            var d = Motion.Clamp01(progress) * total;
            var col = Hex(color);
            var width = Math.Max(2f, size / 12f);
            if (d <= 0)
            {
                return;
            }

            var f1 = Math.Min(1, d / l1);
            b.DrawLine(x0, y0, x0 + (x1 - x0) * (Single)f1, y0 + (y1 - y0) * (Single)f1, col, width);
            if (d > l1)
            {
                var f2 = (d - l1) / l2;
                b.DrawLine(x1, y1, x1 + (x2 - x1) * (Single)f2, y1 + (y2 - y1) * (Single)f2, col, width);
            }
        }

        private static void Outline(BitmapBuilder b, Int32 w, Int32 h, String color, Int32 alpha = 255)
        {
            var bc = Hex(color, alpha);
            b.DrawRectangle(1, 1, w - 3, h - 3, bc);
            b.DrawRectangle(2, 2, w - 5, h - 5, bc);
        }

        // A point on the key's edge, u from 0 to 1 going clockwise from the top left.
        private static (Single x, Single y) Edge(Int32 w, Int32 h, Double u)
        {
            const Single inset = 3;
            Single lw = w - inset * 2, lh = h - inset * 2, p = 2 * (lw + lh);
            var d = (Single)(((u % 1) + 1) % 1) * p;
            if (d < lw) return (inset + d, inset);
            d -= lw;
            if (d < lh) return (inset + lw, inset + d);
            d -= lh;
            if (d < lw) return (inset + lw - d, inset + lh);
            d -= lw;
            return (inset, inset + lh - d);
        }

        // ---------- one key ----------

        public static BitmapImage Draw(KeyFace face, PluginImageSize size, Boolean pressed, Boolean denied) =>
            Draw(face, size, denied, pressed ? new KeyAnim { Now = Motion.Now, PressAt = Motion.Now } : KeyAnim.None);

        public static BitmapImage Draw(KeyFace face, PluginImageSize size, Boolean denied, KeyAnim anim)
        {
            using var b = new BitmapBuilder(size);
            var w = b.Width;
            var h = b.Height;
            var now = anim.Now;

            var pressAge = now - anim.PressAt;
            DrawContent(b, face, w, h, denied, anim);

            if (pressAge >= 0 && pressAge < Motion.PressMs)
            {
                // Press feedback: the key brightens for a moment, without moving.
                b.FillRectangle(0, 0, w, h, new BitmapColor(255, 255, 255, 60));
            }

            if (anim.Dim > 0)
            {
                b.FillRectangle(0, 0, w, h, new BitmapColor(0, 0, 0, (Int32)(255 * Math.Min(0.6, anim.Dim))));
            }

            if (DebugDir != null && DebugName != null)
            {
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(DebugDir, $"{DebugName}-{size}-{w}x{h}.png"), b.ToArray(BitmapImageFormat.Png));
            }

            return b.ToImage();
        }

        private static void DrawContent(BitmapBuilder b, KeyFace face, Int32 w, Int32 h, Boolean denied, KeyAnim anim)
        {
            var now = anim.Now;
            var (bg, border) = ToneColors(face.Tone);
            if (face.Kind == KeyKind.Nav && face.Tone == KeyTone.Normal)
            {
                bg = "#202026";   // navigation keys sit on a lighter, flatter ground than content keys
            }

            if (denied)
            {
                bg = "#3a3a40";
            }

            b.FillRectangle(0, 0, w, h, Hex(bg));

            // In box: the green rises from the bottom when it first appears.
            if (face.Live == "inbox")
            {
                var p = Motion.OutExpo(Motion.Clamp01((now - face.LiveSince) / Motion.WipeMs));
                b.FillRectangle(0, 0, w, (Int32)(h * (1 - p)), Hex("#111114"));
            }

            if (border != null)
            {
                var alpha = 255;
                if (face.Live == "notsent")
                {
                    // Two hard flashes in the first 800 ms, then a steady outline.
                    var e = now - face.LiveSince;
                    if (e >= 0 && e < 800 && ((Int32)(e / 200)) % 2 == 1)
                    {
                        alpha = 40;
                    }
                }

                Outline(b, w, h, border, alpha);
            }

            if (face.Tone == KeyTone.Empty)
            {
                return;
            }

            switch (face.Kind)
            {
                case KeyKind.Session:
                    DrawSession(b, face, w, h, denied, anim);
                    break;
                case KeyKind.Stat:
                    DrawStat(b, face, w, h, now);
                    break;
                case KeyKind.Note:
                    b.DrawText(face.Label ?? "", 4, (Int32)(h * 0.25), w - 8, (Int32)(h * 0.5), Hex(Colors.Muted), (Int32)(h * 0.15), 0, 0);
                    break;
                default:
                    DrawAction(b, face, w, h, denied, anim);
                    break;
            }

            if (face.Badge > 0)
            {
                DrawBadge(b, face, w, h, anim);
            }
        }

        // The count rolls: the old number slides up and out while the new one comes in, and the badge pops.
        private static void DrawBadge(BitmapBuilder b, KeyFace face, Int32 w, Int32 h, KeyAnim anim)
        {
            var r = h * 0.12f;
            var cx = w - (r + h * 0.07f);
            var cy = r + h * 0.07f;
            var p = Motion.Clamp01((anim.Now - anim.BadgeAt) / Motion.BadgeMs);
            var rolling = anim.Now - anim.BadgeAt < Motion.BadgeMs && anim.Now >= anim.BadgeAt;
            var scale = rolling ? (Single)(0.65 + 0.35 * Motion.OutBack(p) + (1 - p) * 0.35) : 1f;
            b.FillCircle(cx, cy, r * scale, Hex(face.BadgeColor ?? Colors.Amber));
            var text = Hex("#111111");
            var font = (Int32)(h * 0.14);
            String Num(Int32 n) => n > 99 ? "99" : n.ToString();
            if (rolling && p < 0.6)
            {
                var k = Motion.OutExpo(p / 0.6);
                var shift = (Int32)(r * 1.4 * k);
                b.DrawText(Num(anim.BadgeFrom), (Int32)(cx - r), (Int32)(cy - r) - shift, (Int32)(r * 2), (Int32)(r * 2), Hex("#111111", (Int32)(255 * (1 - k))), font, 0, 0);
                b.DrawText(Num(face.Badge), (Int32)(cx - r), (Int32)(cy - r) + (Int32)(r * 1.4) - shift, (Int32)(r * 2), (Int32)(r * 2), text, font, 0, 0);
            }
            else
            {
                b.DrawText(Num(face.Badge), (Int32)(cx - r), (Int32)(cy - r), (Int32)(r * 2), (Int32)(r * 2), text, font, 0, 0);
            }
        }

        // Draws one key of every kind and state into the debug folder, for checking layouts without the device.
        public static void DrawSamples()
        {
            var t0 = Motion.Now;
            var samples = new (String name, KeyFace face)[]
            {
                ("sample-working", new KeyFace { Kind = KeyKind.Session, App = "claude", Label = "Keypad plugin review", Status = "Working" }),
                ("sample-permission", new KeyFace { Kind = KeyKind.Session, App = "claude", Label = "Payments retry bug", Status = "Permission" }),
                ("sample-question", new KeyFace { Kind = KeyKind.Session, App = "claude", Label = "Docs site rewrite", Status = "Question" }),
                ("sample-approval", new KeyFace { Kind = KeyKind.Session, App = "codex", Label = "Mobile release", Status = "Approval" }),
                ("sample-ready", new KeyFace { Kind = KeyKind.Session, App = "codex", Label = "API tests", Status = "Ready" }),
                ("sample-open", new KeyFace { Kind = KeyKind.Session, App = "claude", Label = "Migrate the billing service to the new queue", Status = "Open" }),
                ("sample-note", new KeyFace { Kind = KeyKind.Note, Label = "None ready" }),
                ("sample-stat", new KeyFace { Kind = KeyKind.Stat, App = "codex", Label = "This week", Percent = 93, Small = "resets Mon", ShownAt = t0 - 10000 }),
                ("sample-nav", new KeyFace { Kind = KeyKind.Nav, Icon = "back", Label = "Back" }),
                ("sample-recording", new KeyFace { Icon = "stop", IconColor = Colors.Red, Label = "Stop", Small = "0:07 · Claude", Tone = KeyTone.Recording, Live = "recording", LiveSince = t0 - 7000 }),
                ("sample-transcribing", new KeyFace { Icon = "spin", IconColor = Colors.Amber, Label = "Transcribing", Small = "0:02 · Claude", Tone = KeyTone.Sending, Live = "processing", LiveSince = t0 - 2000 }),
                ("sample-inbox", new KeyFace { Icon = "check", IconColor = Colors.Green, Label = "In box", Small = "Enter to send", Tone = KeyTone.Sent, Live = "inbox", LiveSince = t0 - 1000, LiveLength = 3000 }),
                ("sample-notsent", new KeyFace { Icon = "clip", IconColor = Colors.Slate, Label = "Not sent", Small = "text copied", Tone = KeyTone.NotSent, Live = "notsent", LiveSince = t0 - 1000, LiveLength = 5000 }),
                ("sample-badge", new KeyFace { Icon = "bell", IconColor = Colors.Amber, Label = "Waiting", Badge = 2, BadgeColor = Colors.Amber }),
            };
            foreach (var (name, face) in samples)
            {
                DebugName = name;
                Draw(face, PluginImageSize.Width116, false, new KeyAnim { Now = t0 });
            }

            // Mid-animation frames: a press and a rolling badge.
            DebugName = "sample-press";
            Draw(samples[^1].face, PluginImageSize.Width116, false, new KeyAnim { Now = t0, PressAt = t0 - 50 });
            DebugName = "sample-badge-rolling";
            Draw(samples[^1].face, PluginImageSize.Width116, false, new KeyAnim { Now = t0, BadgeAt = t0 - 120, BadgeFrom = 1 });
            DebugName = "sample-idle-home";
            Draw(new KeyFace { Kind = KeyKind.Nav, Icon = "home", Label = "Home" }, PluginImageSize.Width116, false, new KeyAnim { Now = t0, IdleRing = 0.6, IdleSmall = "in 3 s" });
            DebugName = "sample-ready-arriving";
            Draw(samples[4].face, PluginImageSize.Width116, false, new KeyAnim { Now = t0, ArriveAt = t0 - 200 });
        }

        private static void DrawAction(BitmapBuilder b, KeyFace face, Int32 w, Int32 h, Boolean denied, KeyAnim anim)
        {
            var now = anim.Now;
            var dim = face.Tone == KeyTone.Off && !denied;
            var small = denied ? (face.Why ?? "unavailable") : anim.IdleSmall ?? face.Small;
            var hasSmall = !String.IsNullOrEmpty(small);
            var nav = face.Kind == KeyKind.Nav;

            // Without a subtitle, the icon and label move down so the pair stays centred.
            var full = nav ? 0.34 : 0.40;
            var iconSize = (Int32)(w * full);
            if (face.Live == "selecting")
            {
                // The corner marks step in and out every quarter second while you drag.
                iconSize = (Int32)(iconSize * ((Int32)(now / 250) % 2 == 0 ? 0.94 : 1.1));
            }

            var iconTop = (Int32)(h * (hasSmall ? 0.09 : 0.15)) + ((Int32)(w * full) - iconSize) / 2;
            var iconColor = nav ? Colors.Ink : face.IconColor;
            var flashAge = now - anim.FlashAt;
            if (face.Live == "processing" || face.Icon == "spin")
            {
                Ticks(b, iconColor, w / 2f, iconTop + iconSize / 2f, iconSize, now);
            }
            else if (face.Icon == "check" && (face.Live == "inbox" || (flashAge >= 0 && flashAge < Motion.TickDrawMs)))
            {
                // The tick draws itself, on In box and on a confirmed action.
                var start = face.Live == "inbox" ? face.LiveSince + 90 : anim.FlashAt;
                Check(b, iconColor, iconSize, (w - iconSize) / 2, iconTop, (now - start) / Motion.TickDrawMs);
            }
            else
            {
                Icon(b, face.Icon, iconColor, iconSize, (w - iconSize) / 2, iconTop);
            }

            if (anim.IdleRing is Double ring)
            {
                // Idle return: a ring around the Home icon drains through the last five seconds.
                b.DrawArc(w / 2, iconTop + iconSize / 2, (Int32)(iconSize * 0.78), -90, (Single)(360 * ring), Hex(Colors.Ink), Math.Max(2f, w / 46f));
            }

            var label = face.Label ?? "";
            var labelTop = (Int32)(h * (hasSmall ? 0.53 : 0.60));
            var labelColor = dim ? Hex("#a0a0a8") : Hex(nav ? Colors.Ink : "#ffffff");
            // One label size on every key, sized so the longest label fits.
            b.DrawText(label, 1, labelTop, w - 2, (Int32)(h * 0.22), labelColor, FitFont(label, w, h * 0.145), 0, 0);

            if (hasSmall)
            {
                b.DrawText(small, 1, (Int32)(h * 0.75), w - 2, (Int32)(h * 0.2), Hex(denied ? "#ffffff" : "#c8c8d0"), FitFont(small, w, h * 0.135), 0, 0);
            }

            if (face.Live == "recording")
            {
                // A recording light that blinks once a second, and a short red segment sweeping along the
                // bottom edge to show recording is running (it does not measure sound).
                var elapsed = now - face.LiveSince;
                var on = (Int32)(elapsed / 500) % 2 == 0;
                b.FillCircle(w * 0.12f, h * 0.12f, w * 0.04f, Hex(on ? Colors.Red : "#5a2020"));
                var u = (elapsed % 1600) / 1600.0;
                var tri = u < 0.5 ? u * 2 : 2 - u * 2;
                var seg = w * 0.22f;
                var x = 8 + (Single)tri * (w - 16 - seg);
                b.FillRectangle((Int32)x, h - 6, (Int32)seg, 3, Hex(Colors.Red, 220));
            }

            if (face.Live is "inbox" or "notsent" && face.LiveLength > 0)
            {
                // How long this state stays before the key returns to Dictate: a line that shrinks.
                var left = 1 - Motion.Clamp01((now - face.LiveSince) / face.LiveLength);
                var color = face.Live == "inbox" ? Colors.Green : Colors.Amber;
                b.FillRectangle(10, h - 6, (Int32)((w - 20) * left), 3, Hex(color, 210));
            }
        }

        // Session key: the app's icon top left, a state icon top right, and the title below.
        // State is shown by icon and outline colour only, never by a word.
        private static void DrawSession(BitmapBuilder b, KeyFace face, Int32 w, Int32 h, Boolean denied, KeyAnim anim)
        {
            var now = anim.Now;
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
            var sx = w - pad - glyph;
            if (stateIcon == "spin")
            {
                Ticks(b, stateColor, sx + glyph / 2f, pad + glyph / 2f, glyph, now);
            }
            else if (stateIcon != null)
            {
                var arriveAge = now - anim.ArriveAt;
                var size = glyph;
                if (face.Status == "Ready" && arriveAge >= 0 && arriveAge < Motion.ArriveMs)
                {
                    // A finished session: the tick pops in and six dots burst outward, once.
                    var p = arriveAge / Motion.ArriveMs;
                    size = (Int32)(glyph * Math.Max(0.3, Motion.OutBack(Math.Min(1, p * 1.2))));
                    var cx = sx + glyph / 2f;
                    var cy = pad + glyph / 2f;
                    var dist = (Single)(glyph * (0.55 + Motion.OutExpo(p) * 0.6));
                    for (var k = 0; k < 6; k++)
                    {
                        var a = k / 6.0 * Math.PI * 2;
                        b.FillCircle(cx + (Single)Math.Cos(a) * dist, cy + (Single)Math.Sin(a) * dist, Math.Max(1.5f, w / 60f), Hex(Colors.Green, (Int32)(255 * (1 - p))));
                    }
                }

                Icon(b, stateIcon, stateColor, size, sx + (glyph - size) / 2, pad + (glyph - size) / 2);
            }

            if (face.Status == "Working")
            {
                // A short light runs around the edge once every 1.6 seconds.
                var headPos = (now % 1600) / 1600.0;
                for (var k = 0; k < 16; k++)
                {
                    var (x, y) = Edge(w, h, headPos - k * 0.012);
                    b.FillCircle(x, y, Math.Max(1.6f, w / 50f), Hex(Colors.Blue, (Int32)(240 * (1 - k / 16.0))));
                }
            }
            else if (face.Status is "Permission" or "Approval" or "Question")
            {
                // Waiting for you: the amber outline breathes, one cycle every two seconds.
                var breath = 0.35 + 0.65 * (0.5 + 0.5 * Math.Sin(now / 2000 * Math.PI * 2));
                Outline(b, w, h, stateColor, (Int32)(255 * breath));
            }
            else if (stateColor != null)
            {
                Outline(b, w, h, stateColor, 200);
            }

            var title = denied ? (face.Why ?? "") : face.Label ?? "";
            var font = (Int32)(h * (title.Length > 22 ? 0.13 : title.Length > 11 ? 0.145 : 0.165));
            b.DrawText(title, 3, (Int32)(h * 0.34), w - 6, (Int32)(h * 0.6), Hex(denied ? "#ffffff" : "#f1f1f3"), font, 0, 0);
        }

        private static void DrawStat(BitmapBuilder b, KeyFace face, Int32 w, Int32 h, Double now)
        {
            var appColor = face.App == "claude" ? Colors.Orange : Colors.Teal;
            var glyph = (Int32)(h * 0.15);
            var pad = (Int32)(w * 0.08);
            Icon(b, face.App == "claude" ? "claude" : "codex", appColor, glyph, pad, (Int32)(h * 0.06));
            b.DrawText(face.Label ?? "", pad + glyph, (Int32)(h * 0.06), w - pad * 2 - glyph, glyph, Hex(Colors.Ink), FitFont(face.Label ?? "", w - glyph, h * 0.12), 0, 0);

            // On opening Usage the bar fills from zero and the number counts up; the colour follows the value.
            var fill = Motion.OutExpo(Motion.Clamp01((now - face.ShownAt) / Motion.FillMs));
            var shownPct = face.Percent is Double pct ? pct * fill : (Double?)null;
            var value = shownPct is Double v ? $"{v:0}%" : face.Value ?? "–";
            b.DrawText(value, 2, (Int32)(h * 0.27), w - 4, (Int32)(h * 0.3), Hex(shownPct == null && face.Value == null ? Colors.Muted : "#ffffff"), (Int32)(h * 0.24), 0, 0);

            if (shownPct is Double p)
            {
                var bw = w - pad * 2;
                var by = (Int32)(h * 0.62);
                var bh = Math.Max(4, (Int32)(h * 0.05));
                b.FillRectangle(pad, by, bw, bh, Hex("#2a2a2f"));
                var width = (Int32)(bw * Math.Clamp(p, 0, 100) / 100.0);
                var barColor = p >= 90 ? Colors.Red : p >= 70 ? Colors.Amber : appColor;
                if (width > 0)
                {
                    b.FillRectangle(pad, by, width, bh, Hex(barColor));
                }
            }

            if (!String.IsNullOrEmpty(face.Small))
            {
                b.DrawText(face.Small, 1, (Int32)(h * 0.74), w - 2, (Int32)(h * 0.2), Hex("#c8c8d0"), FitFont(face.Small, w, h * 0.125), 0, 0);
            }
        }
    }
}
