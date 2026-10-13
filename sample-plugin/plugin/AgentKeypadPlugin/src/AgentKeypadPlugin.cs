namespace Loupedeck.AgentKeypadPlugin
{
    using System;
    using System.Linq;

    // A universal plugin: it works on one fixed profile whatever app is in front.
    public class AgentKeypadPlugin : Plugin
    {
        private Action _redraw = () => { };

        public override Boolean UsesApplicationApiOnly => true;

        public override Boolean HasNoApplication => true;

        internal KeypadController Controller { get; }

        public AgentKeypadPlugin()
        {
            PluginLog.Init(this.Log);
            PluginResources.Init(this.Assembly);
            this.Controller = new KeypadController(this, () => this._redraw());
        }

        internal void AttachRedraw(Action redraw) => this._redraw = redraw;

        public override void Load()
        {
            // Key images are saved for inspection only while ~/.mx-keypad/debug-renders exists.
            var debug = System.IO.Path.Combine(this.GetPluginDataDirectory(), "key-renders");
            if (System.IO.File.Exists(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mx-keypad", "debug-renders")))
            {
                System.IO.Directory.CreateDirectory(debug);
                KeyRenderer.DebugDir = debug;
                KeyRenderer.DrawSamples();
                System.Threading.Tasks.Task.Run(() => this.Controller.DrawAllScreens());
            }
            PluginLog.Info($"AgentKeypad loaded; key renders saved to {debug}; accessibility access: {BrowserPanel.CanReadClaude()}");
            try
            {
                foreach (var name in GenericActions.GetActionNames(true).Where(n => n.Contains("Page", StringComparison.OrdinalIgnoreCase) || n.Contains("Workspace", StringComparison.OrdinalIgnoreCase)))
                {
                    var ps = GenericActions.GetActionParameters(name) ?? Array.Empty<PluginActionParameter>();
                    PluginLog.Info($"generic action {name}: {String.Join(" | ", ps.Take(12).Select(x => x.Name + "=" + x.DisplayName))}");
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "could not list generic actions");
            }
        }

        // Stops every timer and file watcher. Without this, each reload left the previous copy running,
        // still polling and still asking Anthropic for Claude usage.
        public override void Unload() => this.Controller.Dispose();
    }
}
