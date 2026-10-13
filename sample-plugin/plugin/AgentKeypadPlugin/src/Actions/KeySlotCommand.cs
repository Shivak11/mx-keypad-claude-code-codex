namespace Loupedeck.AgentKeypadPlugin
{
    using System;

    // One command with 19 parameters: "Home key 1-9", "Moves key 1-9" and "Go to Home page".
    // Each physical key is assigned one parameter in Options+; the plugin decides what it shows and does.
    public class KeySlotCommand : PluginDynamicCommand
    {
        public KeySlotCommand()
            : base()
        {
            foreach (var (id, name) in KeypadController.Slots())
            {
                this.AddParameter(id, name, id[0] == 'h' ? "Home page" : id[0] == 'm' ? "Moves page" : "Any page");
            }
        }

        private KeypadController Controller => ((AgentKeypadPlugin)this.Plugin).Controller;

        protected override Boolean OnLoad()
        {
            ((AgentKeypadPlugin)this.Plugin).AttachRedraw(() => this.ActionImageChanged());
            return true;
        }

        protected override Boolean ProcessButtonEvent2(String actionParameter, DeviceButtonEvent2 buttonEvent)
        {
            if (buttonEvent.EventType == DeviceButtonEventType.Press)
            {
                this.Controller.PressDown(actionParameter);
            }

            // Returning false lets the service still call RunCommand for the action itself.
            return false;
        }

        protected override void RunCommand(String actionParameter) => this.Controller.Press(actionParameter);

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            this.Controller.Render(actionParameter, imageSize);

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => null;
    }
}
