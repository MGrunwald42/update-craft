using System.Diagnostics;

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
