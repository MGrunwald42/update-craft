namespace Loupedeck.UpdateCraftPlugin
{
    using System;

    // What the button shows while update-craft is busy: the app (small, top), the status in large type
    // ("57%", "pull", "?", "Failed"), the phase (small, bottom), over a bar filled to Fill (0–1).
    // UpdateCraftCommand uses null instead of a KeyView for the action's normal icon.

    public sealed record KeyView(String Title, String Main, String Sub, Double Fill, BitmapColor Color);
}
