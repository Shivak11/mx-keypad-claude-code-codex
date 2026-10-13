namespace Loupedeck.AgentKeypadPlugin
{
    using System;
    using System.Diagnostics;

    // One clock and the timings for every key animation. Times are milliseconds on this clock.
    // Animations are built from steps where possible, so they still read at the 8 frames a second
    // the animation timer draws (see KeypadController.AnimTick). Motion that changes a key's shape or
    // position (a press dip, a shake, a flip between screens) looked slow at that rate on the device and was removed.
    internal static class Motion
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        public static Double Now => Clock.Elapsed.TotalMilliseconds;

        public const Int32 FrameMs = 125;      // the animation timer: 8 frames a second
        public const Double PressMs = 160;     // the key brightens after a press
        public const Double TickDrawMs = 260;  // a green tick drawing itself
        public const Double BadgeMs = 420;     // a badge number rolling to its new value
        public const Double ArriveMs = 600;    // the burst when a session finishes
        public const Double WipeMs = 320;      // green rising from the bottom on In box
        public const Double FillMs = 900;      // usage bars filling

        public static Double Clamp01(Double v) => Double.IsNaN(v) ? 0 : v < 0 ? 0 : v > 1 ? 1 : v;

        public static Double OutExpo(Double p) => p >= 1 ? 1 : 1 - Math.Pow(2, -10 * p);

        public static Double InQuad(Double p) => p * p;

        public static Double OutBack(Double p)
        {
            const Double c = 1.9;
            return 1 + (c + 1) * Math.Pow(p - 1, 3) + c * Math.Pow(p - 1, 2);
        }
    }

    // What a key is doing over time, handed to the renderer with its face.
    internal sealed class KeyAnim
    {
        public Double Now;
        public Double PressAt = -1e9;
        public Double FlashAt = -1e9;
        public Double BadgeAt = -1e9;
        public Int32 BadgeFrom;
        public Double ArriveAt = -1e9;
        public Double? IdleRing;     // 1 down to 0 over the last seconds before the idle return
        public String IdleSmall;
        public Double Dim;           // 0 to about 0.45 while the idle return approaches

        public static readonly KeyAnim None = new() { Now = 0 };

        // True while a one-off effect is still playing, so the animation timer keeps drawing this key.
        public Boolean Playing =>
            this.Now - this.PressAt < Motion.PressMs
            || this.Now - this.FlashAt < Motion.TickDrawMs
            || this.Now - this.BadgeAt < Motion.BadgeMs
            || this.Now - this.ArriveAt < Motion.ArriveMs
            || this.IdleRing != null
            || this.Dim > 0;
    }
}
