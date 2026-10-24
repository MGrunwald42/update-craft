# Keypad button for update-craft

A [Logi Actions SDK](https://logitech.github.io/actions-sdk-docs/) plugin that puts update-craft on a key of the
**MX Creative Keypad** (also Loupedeck CT / Live). Pressing the key starts update-craft in its own window. While it
runs, the key shows what it is doing:

| Key shows | When |
|---|---|
| the button's normal icon | update-craft is not running, or finished successfully |
| *App* · `check 40%` (amber bar) | the safety check is classifying issues and pull requests |
| *App* · `pulling` | git pull / clone |
| *App* · `57%` (blue bar) | cargo is building; the bar fills as crates compile |
| *App* · `needs you` | update-craft is waiting for an answer in its window |
| *App* · `Failed` (red) | a pull or build failed; stays until the key is pressed again |

When several apps are updated, a third line shows which one, e.g. `2/3`. Pressing the key while update-craft is
running does nothing.

update-craft doesn't need the plugin. Without it, update-craft writes the same small status file and nothing reads it.

## Setup

Needs Logi Options+ (it installs Logi Plugin Service) and the .NET 10 SDK.

```powershell
.\keypad-plugin\setup.ps1 -Arguments 'update all --env-file "C:\path\to\github.env"'
```

The script:

1. publishes `update-craft.exe` (via `publish.ps1`; `-Destination <dir>` installs it somewhere other than the folder
   that holds this repository),
2. writes `%LOCALAPPDATA%\update-craft\keypad.json` with the exe and the arguments the key starts it with,
3. builds the plugin. The build registers it with Logi Plugin Service: it writes a `.link` file pointing at
   `keypad-plugin\bin\Release` and asks the service to reload it.
4. checks that the service loaded it.

`-Arguments` is what update-craft gets when the key is pressed. Without it, the script keeps what `keypad.json`
already says, or uses `update all`. Pass only `--env-file "…"` to get the interactive menu (`update-craft --help`
lists the commands). You can also edit `keypad.json`; it is read on every press.

Then, in Logi Options+:

1. Select the MX Creative Keypad.
2. Under **All Actions → Installed Plugins → update-craft**, drag **Update Craft apps** onto a key (in place of a
   manually added update-craft button).
3. Leave the key's icon alone in the icon editor: a custom icon may take the place of the progress display.

Run `setup.ps1` again after pulling changes to this repository. `setup.ps1 -Uninstall` unregisters the plugin and
deletes its build output (restart Options+ for the action to disappear); `update-craft.exe` and `keypad.json` stay.

The key has no settings in Options+ on purpose. An action with settings (an SDK `ActionEditorCommand`) is stored by
Options+ as a profile action of its own Generic plugin and drawn from a saved icon. The plugin is then never asked
for an image, so the key would never show progress.

## How it works

```
update-craft.exe ──writes──▶ %LOCALAPPDATA%\update-craft\progress.json ◀──reads every 0.5 s── plugin ──▶ key image
```

- update-craft (`src\ProgressReport.cs`) writes one JSON object per change: process id, phase (`check`, `pull`,
  `build`, `waiting`, `done`, `failed`), app, which of several apps, and done/total. Each write goes to a temporary
  file that then replaces the old one, so a reader never sees half a file. Writes are best effort: if one fails,
  update-craft carries on.
- The build percentage comes from cargo's own progress bar (`Building [===>  ] 123/456`), which update-craft reads
  off cargo's stderr (`Shell.Cargo`); the output still appears in the window as before.
- The plugin (`src\UpdateCraftCommand.cs`) redraws the key only when what it shows changes, and returns no image
  otherwise, so the service draws the action's normal icon (`src\package\actionicons\`). If the process id in the
  file is no longer an `update-craft` process (window closed, crash), the key shows its normal icon again.

Polling a 150-byte file is plenty for progress measured in minutes, and it keeps the two sides independent:
either can start, stop or restart without the other noticing.

## Notes

- The percentage counts crates, not time. Crates that are already built count as done, so an incremental build
  starts high, and the last crate (the app itself, plus linking) can sit at 98–99 % for a while.
- Logs: `%LOCALAPPDATA%\Logi\LogiPluginService\Logs\plugin_logs\UpdateCraft.log` (and `_errors.log` there).
  `View changed` lines mean the plugin saw progress; `GetCommandImage` lines mean the service asked for the key's
  image. If the first appear without the second, the key isn't showing this plugin's action.
- The project was generated with `logiplugintool generate UpdateCraft` (LogiPluginTool 6.4) and trimmed. Keep
  `UpdateCraftApplication`: without a `ClientApplication` class the service refuses to load the plugin.
