namespace Loupedeck.UpdateCraftPlugin
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Text.Json;
    using System.Threading;

    // A button that starts update-craft and shows its progress: phase, app and build percentage while it runs,
    // the normal icon again once it is done, and "Failed" until the next press if a pull or build failed.
    //
    // A plain dynamic command on purpose: Options+ stores an ActionEditorCommand on a key as a profile action of
    // its own Generic plugin and draws it from a saved icon, so GetCommandImage is never asked and the key never
    // changes. The exe and arguments therefore come from keypad.json (written by setup.ps1), not from the key.

    public class UpdateCraftCommand : PluginDynamicCommand
    {
        private const String DefaultArguments = "update all";

        private static readonly String DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "update-craft");

        private static readonly String ProgressFile = Path.Combine(DataDir, "progress.json");
        private static readonly String SettingsFile = Path.Combine(DataDir, "keypad.json");

        private Timer _timer;
        private volatile KeyView _view;

        public UpdateCraftCommand()
            : base(displayName: "Update Craft apps", description: "Runs update-craft and shows its progress on the button", groupName: "update-craft")
        {
        }

        protected override Boolean OnLoad()
        {
            PluginLog.Info("UpdateCraftCommand loaded; watching " + ProgressFile);
            this._timer = new Timer(_ => this.Poll(), null, 0, 500);
            return true;
        }

        protected override Boolean OnUnload()
        {
            this._timer?.Dispose();
            return true;
        }

        protected override void RunCommand(String actionParameter)
        {
            // One run at a time; pressing again while it works does nothing.
            if (IsRunning(ReadSnapshot()))
            {
                return;
            }

            var settings = ReadSettings();
            var exe = String.IsNullOrWhiteSpace(settings?.Exe) ? FindExe() : settings.Exe;
            if (exe is null || !File.Exists(exe))
            {
                this.Plugin.OnPluginStatusChanged(
                    PluginStatus.Warning,
                    $"update-craft.exe not found. Run keypad-plugin\\setup.ps1, or set \"Exe\" in {SettingsFile}.");
                return;
            }

            var arguments = String.IsNullOrWhiteSpace(settings?.Arguments) ? DefaultArguments : settings.Arguments;
            PluginLog.Info($"Starting {exe} {arguments}");
            var psi = new ProcessStartInfo(exe, arguments)
            {
                // Shell-execute so update-craft gets its own console window, where its prompts can be answered.
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe),
            };
            Process.Start(psi);
            this.Plugin.OnPluginStatusChanged(PluginStatus.Normal, null);
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            var view = this._view;
            PluginLog.Verbose($"GetCommandImage {imageSize}: {view?.ToString() ?? "<normal icon>"}");
            if (view is null)
            {
                return null;
            }

            using var b = new BitmapBuilder(imageSize);
            Int32 w = b.Width, h = b.Height;
            b.Clear(BitmapColor.Black);
            var fill = (Int32)(h * view.Fill);
            b.FillRectangle(0, h - fill, w, fill, view.Color);   // fills bottom-up like a level gauge

            // The service's default font (17 px on a 116 px key) is too small to read from a distance, so the
            // status is drawn large in the middle, between a small app name and a small phase line.
            DrawLine(b, view.Title, 0, h / 4, 0.17);
            DrawLine(b, view.Main, h / 4, h / 2, 0.40);
            DrawLine(b, view.Sub, h * 3 / 4, h / 4, 0.15);
            return b.ToImage();
        }

        // One line of white text, centred in the band [y, y + height), as large as maxFontShare of the key height
        // allows while still fitting the key's width.
        private static void DrawLine(BitmapBuilder b, String text, Int32 y, Int32 height, Double maxFontShare)
        {
            if (String.IsNullOrEmpty(text))
            {
                return;
            }

            // An average glyph is a bit over half the font size wide; keep a small margin at both sides.
            var fitsWidth = b.Width * 0.9 / (0.58 * text.Length);
            var fontSize = (Int32)Math.Min(b.Height * maxFontShare, fitsWidth);
            b.DrawText(text, 0, y, b.Width, height, BitmapColor.White, fontSize, (Int32)(fontSize * 1.1));
        }

        private void Poll()
        {
            // An exception escaping a timer callback would take down Logi Plugin Service.
            try
            {
                var view = Describe(ReadSnapshot());
                if (view == this._view)
                {
                    return;   // redraw only when something visible changed
                }

                this._view = view;
                PluginLog.Verbose($"View changed: {view?.ToString() ?? "<normal icon>"}");
                this.ActionImageChanged();
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Reading update-craft progress failed");
            }
        }

        private static KeyView Describe(ProgressSnapshot s)
        {
            if (s is null || s.Phase == "done")
            {
                return null;
            }

            if (s.Phase == "failed")
            {
                return new KeyView(s.App, "Failed", "pull or build", 1, new BitmapColor(160, 0, 0));
            }

            // Closed window or crash mid-run: nothing left to show.
            if (!IsRunning(s))
            {
                return null;
            }

            var pct = s.Total > 0 ? Math.Round((Double)s.Done / s.Total, 2) : 0;   // whole percent: at most 100 redraws a phase
            var percent = s.Total > 0 ? $"{pct * 100:0}%" : "...";
            var batch = s.AppCount > 1 ? $" · {s.AppIndex}/{s.AppCount}" : "";
            return s.Phase switch
            {
                "check" => new KeyView(s.App, percent, "check" + batch, pct, new BitmapColor(120, 90, 0)),
                "pull" => new KeyView(s.App, "pull", "git" + batch, 1, new BitmapColor(40, 60, 90)),
                "build" => new KeyView(s.App, percent, "build" + batch, pct, new BitmapColor(0, 90, 160)),
                "waiting" => new KeyView(s.App, "?", "needs you", 1, new BitmapColor(160, 110, 0)),
                _ => null,
            };
        }

        private static ProgressSnapshot ReadSnapshot()
        {
            try
            {
                // Share Delete so update-craft can swap in a new file while this read is open.
                using var fs = new FileStream(ProgressFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize<ProgressSnapshot>(fs);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }

        private static KeypadSettings ReadSettings()
        {
            try
            {
                return JsonSerializer.Deserialize<KeypadSettings>(File.ReadAllText(SettingsFile));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }

        private static Boolean IsRunning(ProgressSnapshot s)
        {
            if (s is null)
            {
                return false;
            }

            try
            {
                using var p = Process.GetProcessById(s.Pid);
                return !p.HasExited && p.ProcessName == "update-craft";   // guards against a reused process id
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        // The plugin builds into <repo>\keypad-plugin\bin, so the exe is found by walking up from there. publish.ps1
        // installs it next to the app repositories (<repo>\..), which is preferred, and leaves a copy in <repo>\publish.
        private static String FindExe()
        {
            // AppContext.BaseDirectory is Logi Plugin Service's folder; the plugin's own location is what counts.
            var start = Path.GetDirectoryName(typeof(UpdateCraftCommand).Assembly.Location);
            foreach (var relative in new[] { "update-craft.exe", Path.Combine("publish", "update-craft.exe") })
            {
                for (var dir = String.IsNullOrEmpty(start) ? null : new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                {
                    var candidate = Path.Combine(dir.FullName, relative);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }
    }
}
