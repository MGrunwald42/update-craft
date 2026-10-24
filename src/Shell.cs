using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace UpdateCraft;

/// <summary>Thin wrappers for running external tools (git, cargo).</summary>
public static class Shell
{
    /// <summary>Runs a command and captures stdout; throws on a non-zero exit.</summary>
    public static string Capture(string file, string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Start(psi, file);
        p.StandardInput.Close();
        var stderrTask = p.StandardError.ReadToEndAsync();
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{file} {string.Join(' ', args)} failed: {stderrTask.Result.Trim()}");
        return stdout.Trim();
    }

    /// <summary>Runs a command with its output streamed straight to this console; returns the exit code.</summary>
    public static int Live(string file, string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { WorkingDirectory = workDir, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Start(psi, file);
        p.WaitForExit();
        return p.ExitCode;
    }

    /// <summary>
    /// Like <see cref="Live"/> for cargo, but also reads its progress bar ("Building [==>  ] 123/456: crate")
    /// off stderr and reports done/total. The output still reaches this console unchanged.
    /// </summary>
    public static int Cargo(string workDir, Action<int, int> onProgress, params string[] args)
    {
        var psi = new ProcessStartInfo("cargo")
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // A redirected stderr is not a terminal, so cargo would drop its progress bar and colours.
        // "always" needs a width too: without one cargo stops with an error.
        psi.Environment["CARGO_TERM_PROGRESS_WHEN"] = "always";
        psi.Environment["CARGO_TERM_PROGRESS_WIDTH"] = AnsiConsole.Profile.Width.ToString();
        if (AnsiConsole.Profile.Capabilities.Ansi) psi.Environment["CARGO_TERM_COLOR"] = "always";

        using var p = Start(psi, "cargo");
        var buf = new char[4096];
        var line = new StringBuilder();
        int n;
        while ((n = p.StandardError.Read(buf, 0, buf.Length)) > 0)
        {
            Console.Error.Write(buf, 0, n);
            foreach (var c in buf.AsSpan(0, n))
            {
                if (c is not ('\r' or '\n')) { line.Append(c); continue; }
                var m = CargoProgress.Match(line.ToString());
                if (m.Success) onProgress(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
                line.Clear();
            }
        }
        p.WaitForExit();
        return p.ExitCode;
    }

    // Colour codes surround "Building", so match on the numbers after the bar's closing bracket.
    private static readonly Regex CargoProgress = new(@"Building\b.*?\]\s*(\d+)/(\d+)", RegexOptions.Compiled);

    public static bool Exists(string file)
    {
        try { Capture(file, Environment.CurrentDirectory, "--version"); return true; }
        catch { return false; }
    }

    private static Process Start(ProcessStartInfo psi, string file)
    {
        try
        {
            return Process.Start(psi) ?? throw new InvalidOperationException($"could not start {file}");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException($"'{file}' was not found — is it installed and on PATH?");
        }
    }
}

public static class Git
{
    public static bool IsRepo(string dir) => Directory.Exists(Path.Combine(dir, ".git"));

    public static string Branch(string dir) => Shell.Capture("git", dir, "rev-parse", "--abbrev-ref", "HEAD");

    public static string HeadSummary(string dir) => Shell.Capture("git", dir, "log", "-1", "--format=%h %s");

    public static DateTimeOffset HeadTime(string dir) =>
        DateTimeOffset.FromUnixTimeSeconds(long.Parse(Shell.Capture("git", dir, "log", "-1", "--format=%ct")));

    /// <summary>Tracked files with local modifications (untracked files are ignored).</summary>
    public static string[] DirtyFiles(string dir) =>
        Shell.Capture("git", dir, "status", "--porcelain", "--untracked-files=no")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool Pull(string dir) => Shell.Live("git", dir, "pull", "--ff-only") == 0;

    public static bool Clone(string url, string dest) =>
        Shell.Live("git", Path.GetDirectoryName(dest)!, "clone", url, Path.GetFileName(dest)) == 0;
}
