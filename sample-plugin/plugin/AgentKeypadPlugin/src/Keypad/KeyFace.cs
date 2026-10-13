namespace Loupedeck.AgentKeypadPlugin
{
    using System;

    internal enum KeyTone { Normal, Off, Done, Selecting, Recording, Sending, Sent, NotSent, Opening, Empty }

    // Action: icon and label. Nav: Back, Home, More, drawn quieter so content keys stand out.
    // Session: one Codex or Claude session. Stat: a usage number. Note: an empty-state message.
    internal enum KeyKind { Action, Nav, Session, Stat, Note }

    // One key's appearance and behaviour at a given moment.
    internal sealed class KeyFace
    {
        public KeyKind Kind = KeyKind.Action;
        public String Icon;
        public String IconColor = "#e5e5ea";
        public String Label;
        public String Small;
        public KeyTone Tone = KeyTone.Normal;
        public Int32 Badge;
        public String BadgeColor;

        // Session keys
        public String App;          // "claude" or "codex"
        public String Status;       // Permission, Question, Approval, Ready, Working, Idle
        public String StatusColor;
        public String Age;

        // Stat keys
        public Double? Percent;
        public String Value;

        // Called when the key is pressed. Null means the key is unavailable.
        public Action Act;

        // Shown in the subtitle when an unavailable key is pressed.
        public String Why;

        // Animated states: "recording", "processing", "selecting", "inbox", "notsent".
        // LiveSince is when the state began (Motion clock); LiveLength is how long it lasts, for a countdown line.
        public String Live;
        public Double LiveSince;
        public Double LiveLength;

        public String SessionId;     // session keys: which session, so a finish can play once on whichever key shows it
        public Double ShownAt;       // usage keys: when the Usage screen opened, for the bar fill

        // True when the face keeps changing on its own and needs the animation timer.
        public Boolean Animated(Double now) =>
            this.Live != null
            || (this.Kind == KeyKind.Session && this.Status is "Working" or "Permission" or "Approval" or "Question")
            || (this.Kind == KeyKind.Stat && now - this.ShownAt < Motion.FillMs + 600);

        public static KeyFace Empty() => new() { Tone = KeyTone.Empty };
    }

    internal static class Colors
    {
        public const String Amber = "#f2b33d";
        public const String Teal = "#3fd0bd";
        public const String Orange = "#f59a5b";
        public const String Blue = "#7ab4f5";
        public const String Red = "#f27878";
        public const String Slate = "#9aa5b5";
        public const String Violet = "#b39cf5";
        public const String Cyan = "#4fd2e6";
        public const String Green = "#5fd68e";
        public const String Yellow = "#f0d264";
        public const String Ink = "#dcdce2";
        public const String Muted = "#8e8e96";
    }
}
