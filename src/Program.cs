using System.Text;
using UpdateCraft;
using Spectre.Console;

Console.OutputEncoding = Encoding.UTF8;

try
{
    return await Cli.MainAsync(args);
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
    return 1;
}

static class Cli
{
    private const string Usage = """
        update-craft — safety-checked updater, builder and launcher for the ArtCraft apps

        USAGE
          update-craft                          interactive menu
          update-craft update <app|all> [--run] safety check → git pull → cargo build (→ start)
                                                    several apps: all checks first, one decision,
                                                    then all pulls, then all builds
          update-craft check  <app|all>         safety check only (GitHub issues & PRs via TypeSafe Jev)
          update-craft build  <app|all>         cargo build --release only (app + CLI)
          update-craft run    <app> [-- args]   start a built app
          update-craft status                   show the state of every app

        APPS
          filmcraft, pdfcraft, photocraft, vectorcraft

        OPTIONS
          --root <dir>        folder containing the app repositories (default: next to this exe)
          --since-days <n>    scan issues from the last n days instead of since the last pull
          --skip-check        pull without the safety check (not recommended)
          -h, --help          show this help

        ENVIRONMENT
          TYPESAFE_API_KEY    required for the safety check
          GITHUB_TOKEN        optional, raises GitHub's API rate limit
        """;

    public static async Task<int> MainAsync(string[] args)
    {
        var positional = new List<string>();
        var passthrough = new List<string>();
        string? root = null;
        int? sinceDays = null;
        bool skipCheck = false, runAfter = false;

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "-h" or "--help" or "/?":
                    Console.WriteLine(Usage);
                    return 0;
                case "--root":
                    root = NextValue(args, ref i, a);
                    break;
                case "--since-days":
                    sinceDays = int.TryParse(NextValue(args, ref i, a), out var d) && d > 0
                        ? d : throw new ArgumentException("--since-days needs a positive number");
                    break;
                case "--skip-check":
                    skipCheck = true;
                    break;
                case "--run":
                    runAfter = true;
                    break;
                case "--":
                    passthrough.AddRange(args[(i + 1)..]);
                    i = args.Length;
                    break;
                default:
                    if (a.StartsWith('-')) throw new ArgumentException($"unknown option {a}  (see --help)");
                    positional.Add(a);
                    break;
            }
        }

        var interactive = !Console.IsInputRedirected && AnsiConsole.Profile.Capabilities.Interactive;
        var opts = new Options(ResolveRoot(root), sinceDays, skipCheck, interactive);
        var updater = new Updater(opts);

        if (positional.Count == 0)
        {
            if (!interactive) { Console.WriteLine(Usage); return 2; }
            return await Menu.RunAsync(updater, opts);
        }

        var command = positional[0].ToLowerInvariant();
        var targets = positional.Skip(1).ToList();

        switch (command)
        {
            case "status" or "list":
                updater.PrintStatus();
                return 0;
            case "update" or "pull":
            {
                var apps = ResolveApps(targets);
                return (apps.Count == 1 ? await updater.UpdateAsync(apps[0], runAfter) : await updater.UpdateManyAsync(apps, runAfter)) ? 0 : 1;
            }
            case "check":
                return await ForEachApp(targets, updater.CheckOnlyAsync);
            case "build":
                return await ForEachApp(targets, app => Task.FromResult(updater.Build(app)));
            case "run" or "start":
                if (targets.Count != 1) throw new ArgumentException("run takes exactly one app");
                return updater.Run(ParseApp(targets[0]), passthrough) ? 0 : 1;
            default:
                throw new ArgumentException($"unknown command '{command}'  (see --help)");
        }
    }

    private static async Task<int> ForEachApp(List<string> targets, Func<App, Task<bool>> action)
    {
        var apps = ResolveApps(targets);
        var ok = true;
        foreach (var app in apps) ok &= await action(app);
        return ok ? 0 : 1;
    }

    private static List<App> ResolveApps(List<string> targets)
    {
        if (targets.Count == 0) throw new ArgumentException("name an app, or 'all'");
        return targets.Any(t => t.Equals("all", StringComparison.OrdinalIgnoreCase))
            ? App.All.ToList()
            : targets.Select(ParseApp).Distinct().ToList();
    }

    private static App ParseApp(string id) =>
        App.Find(id) ?? throw new ArgumentException(
            $"unknown app '{id}'. Supported: {string.Join(", ", App.All.Select(a => a.Id))}");

    private static string NextValue(string[] args, ref int i, string option) =>
        ++i < args.Length ? args[i] : throw new ArgumentException($"{option} needs a value");

    /// <summary>
    /// --root, then CRAFT_ROOT, then the first of this exe's folder and its parents that holds an app repository,
    /// so the tool works both when published next to the repos and when run from its own project folder.
    /// </summary>
    private static string ResolveRoot(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot)) return Path.GetFullPath(explicitRoot);
        var env = Env.Get("CRAFT_ROOT");
        if (env is not null) return Path.GetFullPath(env);

        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (App.All.Any(a => Git.IsRepo(a.Dir(dir.FullName))))
                    return dir.FullName;
        }
        return Path.GetFullPath(AppContext.BaseDirectory);
    }
}

static class Menu
{
    private enum Action { Update, UpdateAll, Run, Build, Check, Quit }

    private static readonly MenuItem<Action>[] Actions =
    [
        new(Action.Update, "Update an app      [grey](safety check → pull → build)[/]", 'u'),
        new(Action.UpdateAll, "Update all apps    [grey](check all → choose → pull all → build all)[/]", 'a'),
        new(Action.Run, "Run an app", 'r'),
        new(Action.Build, "Build only", 'b'),
        new(Action.Check, "Safety check only", 'c'),
        new(Action.Quit, "Quit", 'q'),
    ];

    public static async Task<int> RunAsync(Updater updater, Options opts)
    {
        while (true)
        {
            AnsiConsole.Clear();
            // The one-word banner is 80 columns and Figlet can't wrap it: narrower windows get it stacked
            // on two lines (45 columns), very narrow ones a plain title.
            var width = AnsiConsole.Profile.Width;
            if (width >= 81)
                AnsiConsole.Write(new FigletText("UpdateCraft").Color(Color.DeepSkyBlue1));
            else if (width >= 48)
            {
                AnsiConsole.Write(new FigletText("Update").Color(Color.DeepSkyBlue1));
                AnsiConsole.Write(new FigletText("Craft").Color(Color.DeepSkyBlue1));
            }
            else
                AnsiConsole.Write(new Rule("[bold deepskyblue1]update-craft[/]").LeftJustified());
            AnsiConsole.MarkupLine($"[grey]Repositories in[/] {Markup.Escape(opts.Root)}" +
                                   (Env.Get("TYPESAFE_API_KEY") is not null
                                       ? "   [green]● TypeSafe safety check ready[/]"
                                       : "   [yellow]● TYPESAFE_API_KEY not set — safety check unavailable[/]") +
                                   ((Env.Get("GITHUB_TOKEN") ?? Env.Get("GH_TOKEN")) is not null
                                       ? "   [green]● GitHub token[/]"
                                       : "   [yellow]● no GITHUB_TOKEN (60 GitHub requests/hour)[/]"));
            AnsiConsole.WriteLine();
            updater.PrintStatus();

            var action = KeyMenu.Show("What would you like to do?", Actions);
            if (action == Action.Quit) return 0;

            if (action == Action.UpdateAll)
            {
                await updater.UpdateManyAsync(App.All, runAfter: false);
            }
            else
            {
                var verb = action switch
                {
                    Action.Update => "Update",
                    Action.Run => "Run",
                    Action.Check => "Safety check",
                    _ => "Build",
                };
                var app = PickApp(verb);
                if (app is null) continue;
                switch (action)
                {
                    case Action.Update: await updater.UpdateAsync(app, runAfter: false); break;
                    case Action.Run: updater.Run(app, []); break;
                    case Action.Check: await updater.CheckOnlyAsync(app); break;
                    case Action.Build: updater.Build(app); break;
                }
            }

            AnsiConsole.WriteLine();
            AnsiConsole.Markup("[grey]Press any key to return to the menu…[/]");
            Console.ReadKey(intercept: true);
        }
    }

    /// <summary>Apps are picked with 1–4 (or arrows + Enter); Esc goes back.</summary>
    private static App? PickApp(string verb)
    {
        var items = App.All
            .Select((a, i) => new MenuItem<App?>(a, a.Display, (char)('1' + i)))
            .ToList();
        return KeyMenu.Show($"{verb} — which app?", items, allowEscape: true, escapeValue: null);
    }
}
