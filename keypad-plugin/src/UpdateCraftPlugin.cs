namespace Loupedeck.UpdateCraftPlugin
{
    using System;

    // Shows update-craft's progress on an MX Creative Keypad button. The work is in UpdateCraftCommand.

    public class UpdateCraftPlugin : Plugin
    {
        // The plugin starts and watches update-craft itself; it sends no shortcuts to any application.
        public override Boolean UsesApplicationApiOnly => true;

        // A Universal plugin: it works whatever application is in the foreground.
        public override Boolean HasNoApplication => true;

        public UpdateCraftPlugin() => PluginLog.Init(this.Log);

        public override void Load()
        {
        }

        public override void Unload()
        {
        }
    }
}
