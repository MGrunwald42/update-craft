using System.Diagnostics;
using Spectre.Console;

namespace UpdateCraft;

public sealed record Options(string Root, int? SinceDays, bool SkipCheck, bool Interactive, string? GitHubToken, string? TypeSafeApiKey);

/// <summary>The update / safety-check / build / run workflows for one app.</summary>
public sealed class Updater(Options opts)
{
    // Set once TypeSafe rejects the key; shared by every app in this run so the key is not tried again.
    private bool _typeSafeRejected;

    // ───────────────────────── status ─────────────────────────

    public void PrintStatus()
    {
        var table = new Table().RoundedBorder().BorderColor(Color.Grey)
            .AddColumn("[bold]App[/]").AddColumn("[bold]Branch[/]").AddColumn("[bold]Latest local commit[/]")
            .AddColumn("[bold]Last pull[/]").AddColumn("[bold]Built exe[/]").AddColumn("[bold]Built CLI[/]");

        // Re-read every time: another update-craft (or an earlier menu action) may have pulled meanwhile.
        var state = StateStore.Load();
        foreach (var app in App.All)
        {
            var dir = app.Dir(opts.Root);
            if (!Git.IsRepo(dir))
            {
                table.AddRow($"[bold]{app.Display}[/]", "[grey]—[/]", "[yellow]not cloned[/]", "[grey]—[/]", "[grey]—[/]", "[grey]—[/]");
                continue;
            }
            string branch, head, headAge;
            try
            {
                branch = Git.Branch(dir);
                head = Git.HeadSummary(dir);
                headAge = Ago(Git.HeadTime(dir));
            }
            catch (Exception ex) { branch = "?"; head = ex.Message; headAge = ""; }

            var pulled = state.LastPull.TryGetValue(app.Id, out var lp) ? Ago(lp) : "[grey]unknown[/]";
            table.AddRow($"[bold]{app.Display}[/]", Markup.Escape(branch),
                $"{Markup.Escape(Shorten(head, 48))} [grey]({headAge})[/]", pulled,
                BuiltCell(app.Exe(opts.Root)), BuiltCell(app.CliExe(opts.Root)));
        }
        AnsiConsole.Write(table);
    }

    private static string BuiltCell(string exe) =>
        File.Exists(exe) ? $"[green]✓[/] {Ago(File.GetLastWriteTime(exe))}" : "[grey]not built[/]";

    // ───────────────────────── update ─────────────────────────

    /// <summary>Safety check → pull → build → (optionally) run, for a single app. Returns false if aborted or failed.</summary>
    public async Task<bool> UpdateAsync(App app, bool runAfter)
    {
        AnsiConsole.Write(new Rule($"[bold deepskyblue1]{app.Display}[/]").LeftJustified());
        if (!CanUpdate(app)) return false;
        if (!Git.IsRepo(app.Dir(opts.Root)) &&
            !Confirm($"{app.Display} is not cloned yet. Clone {app.CloneUrl} into {app.Dir(opts.Root)}?", true))
            return false;

        var scan = await ScanAsync(app);
        if (!Decide(scan)) return false;
        if (!PullOrClone(app)) return false;
        if (!Build(app)) return false;

        if (runAfter || (opts.Interactive && Confirm($"Start {app.Display} now?", true)))
            Run(app, []);
        return true;
    }

    /// <summary>
    /// Update several apps with every decision made up front: safety-check all of them, let the user pick
    /// which to update once, pull those, then build them back to back without further questions.
    /// </summary>
    public async Task<bool> UpdateManyAsync(IReadOnlyList<App> apps, bool runAfter)
    {
        // ── 1. Safety check every app ──
        var scans = new List<ScanResult>();
        foreach (var app in apps)
        {
            AnsiConsole.Write(new Rule($"[bold deepskyblue1]{app.Display}[/] [grey]· safety check[/]").LeftJustified());
            scans.Add(CanUpdate(app) ? await ScanAsync(app) : ScanResult.Blocked(app, "folder exists but is not a git repository"));
        }

        // ── 2. One decision for all of them ──
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold]Safety check summary[/]").LeftJustified());
        var summary = new Table().RoundedBorder().BorderColor(Color.Grey)
            .AddColumn("[bold]App[/]").AddColumn("[bold]Result[/]").AddColumn("[bold]Recommendation[/]");
        foreach (var s in scans)
            summary.AddRow($"[bold]{s.App.Display}[/]", s.Describe(), s.Recommended ? "[green]update[/]" : "[red]review first[/]");
        AnsiConsole.Write(summary);

        var candidates = scans.Where(s => s.Outcome != ScanOutcome.Blocked).ToList();
        List<App> selected;
        if (candidates.Count == 0)
        {
            selected = [];
        }
        else if (opts.Interactive)
        {
            var prompt = new MultiSelectionPrompt<ScanResult>()
                .Title("Which apps should be [bold]pulled and built[/]? [grey](space toggles, enter confirms)[/]")
                .NotRequired()
                .PageSize(10)
                .HighlightStyle(new Style(Color.DeepSkyBlue1, decoration: Decoration.Bold))
                .UseConverter(s => $"{s.App.Display,-12} {s.Describe()}" +
                                   (Git.IsRepo(s.App.Dir(opts.Root)) ? "" : " [grey](will be cloned)[/]"))
                .AddChoices(candidates);
            // Apps with suspicious findings, or that could not be checked, start unticked: updating them is opt-in.
            foreach (var s in candidates.Where(s => s.Recommended)) prompt.Select(s);
            selected = AnsiConsole.Prompt(prompt).Select(s => s.App).ToList();
        }
        else
        {
            selected = candidates.Where(s => s.Recommended).Select(s => s.App).ToList();
            foreach (var s in candidates.Where(s => !s.Recommended))
                AnsiConsole.MarkupLine($"[yellow]Skipping {s.App.Display}:[/] {s.Describe()} [grey](non-interactive — run it on its own to decide)[/]");
        }

        if (selected.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Nothing selected — no repository was changed.[/]");
            return false;
        }

        // ── 3. Pull everything that was approved ──
        var results = scans.ToDictionary(s => s.App, s => new BatchRow(s));
        foreach (var app in selected)
        {
            AnsiConsole.Write(new Rule($"[bold deepskyblue1]{app.Display}[/] [grey]· pull[/]").LeftJustified());
            results[app].Pulled = PullOrClone(app);
        }

        // ── 4. Build back to back, no questions ──
        foreach (var app in selected.Where(a => results[a].Pulled == true))
        {
            AnsiConsole.Write(new Rule($"[bold deepskyblue1]{app.Display}[/] [grey]· build[/]").LeftJustified());
            var sw = Stopwatch.StartNew();
            results[app].Built = Build(app);
            results[app].BuildTime = sw.Elapsed;
        }

        // ── 5. Summary ──
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold]Update summary[/]").LeftJustified());
        var table = new Table().RoundedBorder().BorderColor(Color.Grey)
            .AddColumn("[bold]App[/]").AddColumn("[bold]Safety check[/]").AddColumn("[bold]Pull[/]").AddColumn("[bold]Build[/]");
        foreach (var r in results.Values)
            table.AddRow($"[bold]{r.Scan.App.Display}[/]", r.Scan.Describe(),
                r.Pulled switch { true => "[green]✓[/]", false => "[red]✗ failed[/]", null => "[grey]skipped[/]" },
                r.Built switch
                {
                    true => $"[green]✓[/] {r.BuildTime:m\\:ss}",
                    false => "[red]✗ failed[/]",
                    null => "[grey]—[/]",
                });
        AnsiConsole.Write(table);

        var built = results.Values.Where(r => r.Built == true).Select(r => r.Scan.App).ToList();
        if (built.Count > 0)
        {
            if (runAfter)
            {
                foreach (var app in built) Run(app, []);
            }
            else if (opts.Interactive)
            {
                var start = AnsiConsole.Prompt(new MultiSelectionPrompt<App>()
                    .Title("Start any of them now? [grey](space toggles, enter confirms — none is fine)[/]")
                    .NotRequired()
                    .UseConverter(a => a.Display)
                    .AddChoices(built));
                foreach (var app in start) Run(app, []);
            }
        }

        return results.Values.All(r => r.Pulled is null || (r.Pulled == true && r.Built == true));
    }

    private sealed class BatchRow(ScanResult scan)
    {
        public ScanResult Scan { get; } = scan;
        public bool? Pulled { get; set; }
        public bool? Built { get; set; }
        public TimeSpan BuildTime { get; set; }
    }

    /// <summary>A folder that exists but is not a repository is never touched.</summary>
    private bool CanUpdate(App app)
    {
        var dir = app.Dir(opts.Root);
        if (Git.IsRepo(dir) || !Directory.Exists(dir) || !Directory.EnumerateFileSystemEntries(dir).Any()) return true;
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(dir)} exists but is not a git repository.[/]");
        return false;
    }

    /// <summary>Clones a missing repository, otherwise fast-forward pulls. Records the pull time on success.</summary>
    private bool PullOrClone(App app)
    {
        var dir = app.Dir(opts.Root);
        if (!Git.IsRepo(dir))
        {
            AnsiConsole.MarkupLine($"[bold]Cloning[/] {Markup.Escape(app.CloneUrl)} …");
            if (!Git.Clone(app.CloneUrl, dir)) { AnsiConsole.MarkupLine("[red]git clone failed.[/]"); return false; }
            StateStore.MarkPulled(app.Id);
            return true;
        }

        var dirty = Git.DirtyFiles(dir);
        if (dirty.Length > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠ {dirty.Length} locally modified file(s) in {Markup.Escape(dir)}:[/]");
            foreach (var f in dirty.Take(10)) AnsiConsole.MarkupLine($"   [grey]{Markup.Escape(f)}[/]");
            AnsiConsole.MarkupLine("[grey]  The pull is fast-forward only and will stop if these conflict.[/]");
        }

        AnsiConsole.MarkupLine($"[bold]Pulling[/] {Markup.Escape(app.Slug)} …");
        var before = Git.HeadSummary(dir);
        if (!Git.Pull(dir))
        {
            AnsiConsole.MarkupLine("[red]git pull failed.[/] Resolve it in the repository and try again.");
            return false;
        }
        StateStore.MarkPulled(app.Id);
        var after = Git.HeadSummary(dir);
        AnsiConsole.MarkupLine(before == after
            ? "[green]✓ Already up to date.[/]"
            : $"[green]✓ Updated to[/] {Markup.Escape(after)}");
        return true;
    }

    private DateTimeOffset SinceForApp(App app)
    {
        if (opts.SinceDays is { } days) return DateTimeOffset.UtcNow.AddDays(-days);
        // Not cloned yet: look back a month.
        if (!Git.IsRepo(app.Dir(opts.Root))) return DateTimeOffset.UtcNow.AddDays(-30);
        if (StateStore.Load().LastPull.TryGetValue(app.Id, out var last)) return last;
        // First run of this tool on an existing checkout: the newest local commit approximates the last pull.
        return Git.HeadTime(app.Dir(opts.Root));
    }

    // ───────────────────────── safety check ─────────────────────────

    public async Task<bool> CheckOnlyAsync(App app)
    {
        AnsiConsole.Write(new Rule($"[bold deepskyblue1]{app.Display}[/]").LeftJustified());
        var scan = await ScanAsync(app);
        return scan.Outcome is not ScanOutcome.Unavailable;
    }

    /// <summary>
    /// Scans GitHub issues and merged PRs updated since the last pull, classifies them with Jev and prints
    /// what was flagged. Never asks anything — see <see cref="Decide"/>.
    /// </summary>
    private async Task<ScanResult> ScanAsync(App app)
    {
        if (opts.SkipCheck)
        {
            AnsiConsole.MarkupLine("[yellow]⚠ Safety check skipped (--skip-check).[/]");
            return new ScanResult(app, ScanOutcome.Skipped, [], null);
        }

        var since = SinceForApp(app);
        AnsiConsole.MarkupLine($"[bold]Safety check[/] — issues & pull requests on [link={app.IssuesUrl}]{app.Slug}[/] " +
                               $"updated since [aqua]{since.ToLocalTime():yyyy-MM-dd HH:mm}[/] ({Ago(since)})");

        if (_typeSafeRejected)
        {
            AnsiConsole.MarkupLine("[yellow]⚠ TypeSafe rejected the API key earlier in this run, so it is not tried again.[/]");
            return new ScanResult(app, ScanOutcome.Unavailable, [], "TypeSafe rejected the API key");
        }

        using var checker = SafetyChecker.Create(opts.TypeSafeApiKey);
        if (checker is null)
        {
            AnsiConsole.MarkupLine("[yellow]⚠ TYPESAFE_API_KEY is not set, so issues cannot be classified.[/]");
            return new ScanResult(app, ScanOutcome.Unavailable, [], "TYPESAFE_API_KEY not set");
        }

        List<Verdict> verdicts;
        try
        {
            using var gh = new GitHubClient(opts.GitHubToken);
            var fetched = await AnsiConsole.Status().Spinner(Spinner.Known.Dots)
                .StartAsync("Fetching issues and pull requests from GitHub…", _ => gh.ItemsSinceAsync(app, since));

            // Open or rejected pull requests are not part of the branch being pulled, so only issues and
            // merged PRs can describe code that actually lands on this machine.
            var items = fetched.Where(i => !i.IsPullRequest || i.Merged).ToList();
            var skipped = fetched.Count - items.Count;
            if (skipped > 0)
                AnsiConsole.MarkupLine($"[grey]Skipping {skipped} unmerged pull request(s) — their code is not on the branch you pull.[/]");

            if (items.Count == 0)
            {
                AnsiConsole.MarkupLine("[green]✓ No issues or merged pull requests were opened or updated in that window.[/]");
                return new ScanResult(app, ScanOutcome.Clean, [], null);
            }

            verdicts = await AnsiConsole.Progress()
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask($"Classifying {items.Count} item(s) with Jev", maxValue: items.Count);
                    return await checker.ClassifyAsync(app, items, n => task.Value = n);
                });
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ Safety check failed:[/] {Markup.Escape(ex.Message)}");
            return new ScanResult(app, ScanOutcome.Unavailable, [], ex.Message);
        }

        if (checker.KeyRejected)
        {
            _typeSafeRejected = true;
            AnsiConsole.MarkupLine("[red]✗ TypeSafe rejected the API key (401). Check TYPESAFE_API_KEY; no further requests will be made with it.[/]");
            return new ScanResult(app, ScanOutcome.Unavailable, [], "TypeSafe rejected the API key");
        }

        PrintVerdicts(app, verdicts);
        return new ScanResult(app, verdicts.Any(v => v.Risk != Risk.Clean) ? ScanOutcome.Flagged : ScanOutcome.Clean, verdicts, null);
    }

    /// <summary>Asks whether to pull a single app after its scan. Returns true when it is OK to pull.</summary>
    private bool Decide(ScanResult scan)
    {
        switch (scan.Outcome)
        {
            case ScanOutcome.Clean or ScanOutcome.Skipped:
                return true;
            case ScanOutcome.Blocked:
                return false;
            case ScanOutcome.Unavailable:
                return Confirm("Continue WITHOUT a safety check?", false);
        }

        AnsiConsole.MarkupLine($"[grey]Review the linked items (Ctrl+click) or {scan.App.IssuesUrl} before deciding.[/]");
        if (scan.Suspicious > 0)
            AnsiConsole.MarkupLine("[bold red]Pulling and building runs this code (including build scripts) on your machine.[/]");
        // A suspicious finding defaults to "no"; security-only findings (usually fixes) default to "yes".
        return Confirm($"Continue and pull the latest {scan.App.Display} source?", scan.Suspicious == 0);
    }

    private static void PrintVerdicts(App app, List<Verdict> verdicts)
    {
        var suspicious = verdicts.Count(v => v.Risk == Risk.Suspicious);
        var security = verdicts.Count(v => v.Risk == Risk.Security);
        var unchecked_ = verdicts.Count(v => v.Risk == Risk.Unchecked);
        var clean = verdicts.Count - suspicious - security - unchecked_;

        var flagged = verdicts.Where(v => v.Risk != Risk.Clean)
            .OrderByDescending(v => v.Risk == Risk.Suspicious).ThenByDescending(v => v.Malicious + v.Security)
            .ToList();

        if (flagged.Count > 0)
        {
            var table = new Table().RoundedBorder().BorderColor(Color.Grey).Expand()
                .AddColumn("[bold]Verdict[/]").AddColumn("[bold]#[/]").AddColumn("[bold]Title[/]")
                .AddColumn(new TableColumn("[bold]Malicious[/]").RightAligned())
                .AddColumn(new TableColumn("[bold]Security[/]").RightAligned())
                .AddColumn(new TableColumn("[bold]Severity 0-3[/]").RightAligned());

            foreach (var v in flagged)
            {
                var tag = v.Risk switch
                {
                    Risk.Suspicious => "[bold white on red] SUSPICIOUS [/]",
                    Risk.Security => "[black on yellow] SECURITY [/]",
                    _ => "[grey]unchecked[/]",
                };
                var title = $"[link={v.Item.Url}]{Markup.Escape(Shorten(v.Item.Title, 70))}[/]\n" +
                            $"[grey]{v.Item.Kind} · {v.Item.State} · @{Markup.Escape(v.Item.Author)} · {Ago(v.Item.UpdatedAt)}" +
                            (v.Item.RecentComments.Count > 0 ? $" · {v.Item.RecentComments.Count} new comment(s)" : "") + "[/]" +
                            (v.Error is null ? "" : $"\n[red]{Markup.Escape(v.Error)}[/]");
                table.AddRow(new Markup(tag), new Markup($"{v.Item.Number}"), new Markup(title),
                    new Markup(v.Error is null ? Pct(v.Malicious) : "–"), new Markup(v.Error is null ? Pct(v.Security) : "–"),
                    new Markup(v.Error is null ? $"{v.Severity:0.0}" : "–"));
            }
            AnsiConsole.Write(table);
        }

        AnsiConsole.MarkupLine(
            $"Checked [bold]{verdicts.Count}[/] item(s): " +
            $"[red]{suspicious} suspicious[/], [yellow]{security} security-related[/], " +
            $"[green]{clean} unremarkable[/]" + (unchecked_ > 0 ? $", [grey]{unchecked_} could not be classified[/]" : "") + ".");
        if (flagged.Count == 0)
            AnsiConsole.MarkupLine("[green]✓ Nothing suspicious found.[/]");
    }

    // ───────────────────────── build & run ─────────────────────────

    public bool Build(App app)
    {
        var dir = app.Dir(opts.Root);
        if (!Git.IsRepo(dir))
        {
            AnsiConsole.MarkupLine($"[red]{app.Display} is not cloned at {Markup.Escape(dir)}.[/]");
            return false;
        }
        // One cargo run for the desktop app and its CLI, so the crates they share compile once.
        AnsiConsole.MarkupLine(
            $"[bold]Building[/] {app.Display} [grey](cargo build --release -p {app.Package} -p {app.CliPackage})[/] …");
        var sw = Stopwatch.StartNew();
        int code;
        try { code = Shell.Live("cargo", dir, "build", "--release", "-p", app.Package, "-p", app.CliPackage); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]"); return false; }

        if (code != 0)
        {
            AnsiConsole.MarkupLine($"[red]✗ Build failed (exit code {code}).[/]");
            return false;
        }
        AnsiConsole.MarkupLine($"[green]✓ Built in {sw.Elapsed:m\\:ss}[/] → [link]{Markup.Escape(app.Exe(opts.Root))}[/]");
        AnsiConsole.MarkupLine($"  [grey]CLI →[/] [link]{Markup.Escape(app.CliExe(opts.Root))}[/]");
        return true;
    }

    public bool Run(App app, IEnumerable<string> args)
    {
        var exe = app.Exe(opts.Root);
        if (!File.Exists(exe))
        {
            AnsiConsole.MarkupLine($"[yellow]{app.Display} has not been built yet ({Markup.Escape(exe)}).[/]");
            if (!opts.Interactive || !Confirm("Build it now?", true) || !Build(app)) return false;
        }
        // Shell-execute so the app is fully detached and its log output doesn't spill into this console.
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = app.Dir(opts.Root), UseShellExecute = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi);
        AnsiConsole.MarkupLine($"[green]▶ Started {app.Display}.[/]");
        return true;
    }

    // ───────────────────────── helpers ─────────────────────────

    private bool Confirm(string question, bool defaultValue)
    {
        if (!opts.Interactive)
        {
            AnsiConsole.MarkupLine($"{Markup.Escape(question)} [grey](non-interactive: {(defaultValue ? "yes" : "no")})[/]");
            return defaultValue;
        }
        return AnsiConsole.Confirm(Markup.Escape(question), defaultValue);
    }

    private static string Pct(double p)
    {
        var color = p >= 0.5 ? "red" : p >= 0.2 ? "yellow" : "grey";
        return $"[{color}]{p * 100:0}%[/]";
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public static string Ago(DateTimeOffset t)
    {
        var d = DateTimeOffset.Now - t;
        if (d.TotalMinutes < 2) return "just now";
        if (d.TotalHours < 2) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 2) return $"{(int)d.TotalHours} h ago";
        return $"{(int)d.TotalDays} days ago";
    }
}
