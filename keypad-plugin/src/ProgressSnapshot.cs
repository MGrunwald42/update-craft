namespace Loupedeck.UpdateCraftPlugin
{
    using System;

    // Mirrors UpdateCraft.ProgressReport.Snapshot, which update-craft writes to %LOCALAPPDATA%\update-craft\progress.json.
    // Phase is check, pull, build, waiting, done or failed; Done/Total count classified items or compiled crates.

    public sealed record ProgressSnapshot(Int32 Pid, String Phase, String App, Int32 AppIndex, Int32 AppCount, Int32 Done, Int32 Total);
}
