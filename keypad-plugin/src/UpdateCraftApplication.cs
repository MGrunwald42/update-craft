namespace Loupedeck.UpdateCraftPlugin
{
    using System;

    // Logi Plugin Service expects every plugin to have a ClientApplication. This one is linked to no application:
    // the plugin is Universal (see UpdateCraftPlugin.HasNoApplication) and watches update-craft through its progress file.

    public class UpdateCraftApplication : ClientApplication
    {
        protected override String GetProcessName() => "";

        protected override String GetBundleName() => "";

        public override ClientApplicationStatus GetApplicationStatus() => ClientApplicationStatus.Unknown;
    }
}
