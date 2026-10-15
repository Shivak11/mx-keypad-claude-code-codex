namespace Loupedeck.AgentKeypadPlugin
{
    using System;
    using System.IO;
    using System.Text.Json;

    // The choices that depend on the user's own tools, read from ~/.mx-keypad/settings.json when the plugin starts.
    // Every value has a default, so a missing or unreadable file gives the behaviour of the original sample.
    internal sealed class KeypadSettings
    {
        private static readonly String FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mx-keypad", "settings.json");

        private static KeypadSettings _current;

        public static KeypadSettings Current => _current ??= Load();

        // How the dictation app is started and stopped: "fn-hands-free" (hold fn, tap Shift, release fn to start;
        // tap fn to stop), "fn-tap" (tap fn to start and again to stop) or "shortcut" (send DictationShortcut to
        // start and again to stop).
        public String DictationTrigger { get; set; } = "fn-hands-free";

        // Used when DictationTrigger is "shortcut", for example "control+option+space".
        public String DictationShortcut { get; set; } = "";

        // The shortcut that takes an area screenshot in the user's screenshot tool, and the name shown on the key.
        public String ScreenshotShortcut { get; set; } = "control+command+option+4";

        public String ScreenshotTool { get; set; } = "Xnapper";

        // The browser that opens the page shown in an agent's browser panel, by name and bundle id.
        public String BrowserName { get; set; } = "Dia";

        public String BrowserBundleId { get; set; } = "company.thebrowser.dia";

        private static KeypadSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var loaded = JsonSerializer.Deserialize<KeypadSettings>(
                        File.ReadAllText(FilePath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (loaded != null)
                    {
                        PluginLog.Info($"settings: read {FilePath}");
                        return loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, $"settings: could not read {FilePath}; using the defaults");
            }

            return new KeypadSettings();
        }

        // Turns "control+option+space" into the SDK's key and modifier values. Returns false for text it cannot read.
        public static Boolean TryParseShortcut(String text, out VirtualKeyCode key, out ModifierKey modifiers)
        {
            key = default;
            modifiers = ModifierKey.None;
            if (String.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return false;
            }

            for (var i = 0; i < parts.Length - 1; i++)
            {
                switch (parts[i].ToLowerInvariant())
                {
                    case "control": case "ctrl": modifiers |= ModifierKey.Control; break;
                    case "command": case "cmd": modifiers |= ModifierKey.Command; break;
                    case "option": case "alt": modifiers |= ModifierKey.Option; break;
                    case "shift": modifiers |= ModifierKey.Shift; break;
                    default: return false;
                }
            }

            var last = parts[^1];
            var name = last.Equals("space", StringComparison.OrdinalIgnoreCase) ? "Space"
                : last.Length == 1 && Char.IsLetterOrDigit(last[0]) ? "Key" + last.ToUpperInvariant()
                : null;
            return name != null && Enum.TryParse(name, out key);
        }
    }
}
