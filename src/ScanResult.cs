namespace UpdateCraft;

public enum ScanOutcome
{
    /// <summary>Nothing security-relevant was found.</summary>
    Clean,
    /// <summary>At least one item was flagged as security-related or suspicious.</summary>
    Flagged,
    /// <summary>The check could not run (no API key, network or API error).</summary>
    Unavailable,
    /// <summary>The user passed --skip-check.</summary>
    Skipped,
    /// <summary>The app cannot be updated at all (e.g. its folder is not a repository).</summary>
    Blocked,
}

/// <summary>The outcome of one app's safety check, before the user has decided anything.</summary>
public sealed record ScanResult(App App, ScanOutcome Outcome, List<Verdict> Verdicts, string? Error)
{
    public static ScanResult Blocked(App app, string reason) => new(app, ScanOutcome.Blocked, [], reason);

    public int Suspicious => Verdicts.Count(v => v.Risk == Risk.Suspicious);
    public int Security => Verdicts.Count(v => v.Risk == Risk.Security);

    /// <summary>Whether updating is the sensible default; anything else must be opted into explicitly.</summary>
    public bool Recommended => Outcome switch
    {
        ScanOutcome.Clean or ScanOutcome.Skipped => true,
        ScanOutcome.Flagged => Suspicious == 0,
        _ => false,
    };

    /// <summary>One-line, markup-formatted result for summary tables and selection lists.</summary>
    public string Describe() => Outcome switch
    {
        ScanOutcome.Clean => Verdicts.Count == 0 ? "[green]✓ nothing new[/]" : $"[green]✓ {Verdicts.Count} checked, clean[/]",
        ScanOutcome.Flagged => (Suspicious > 0 ? $"[bold red]{Suspicious} suspicious[/]" : "") +
                               (Suspicious > 0 && Security > 0 ? ", " : "") +
                               (Security > 0 ? $"[yellow]{Security} security-related[/]" : "") +
                               $" [grey]of {Verdicts.Count}[/]",
        ScanOutcome.Unavailable => $"[yellow]⚠ not checked[/] [grey]({Spectre.Console.Markup.Escape(Shorten(Error ?? "", 50))})[/]",
        ScanOutcome.Skipped => "[yellow]⚠ check skipped[/]",
        _ => $"[red]✗ {Spectre.Console.Markup.Escape(Error ?? "blocked")}[/]",
    };

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
