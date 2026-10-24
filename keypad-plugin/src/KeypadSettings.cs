namespace Loupedeck.UpdateCraftPlugin
{
    using System;

    // %LOCALAPPDATA%\update-craft\keypad.json, written by setup.ps1: what the key starts.
    // A missing file or empty value means the exe is searched for and the arguments are "update all".

    public sealed record KeypadSettings(String Exe, String Arguments);
}
