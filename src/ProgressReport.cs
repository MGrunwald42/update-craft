using System.Text.Json;

namespace UpdateCraft;

/// <summary>
/// Publishes what this run is doing to %LOCALAPPDATA%\update-craft\progress.json, so other tools can show it
/// (the MX Creative Keypad plugin in keypad-plugin\). Best effort: a failed write never interrupts an update.
/// </summary>
public static class ProgressReport
{
    /// <summary>
    /// <see cref="Phase"/> is check, pull, build, waiting (a prompt needs an answer), done or failed.
    /// <see cref="Done"/>/<see cref="Total"/> count classified items or compiled crates within the phase.
    /// </summary>
    public sealed record Snapshot(int Pid, string Phase, string? App, int AppIndex, int AppCount, int Done, int Total, DateTimeOffset UpdatedAt);

    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "update-craft", "progress.json");

    private static readonly Lock Gate = new();
    private static Snapshot _current = new(Environment.ProcessId, "start", null, 0, 0, 0, 0, DateTimeOffset.UtcNow);
    private static bool _failed;
    private static DateTime _lastWrite;

    /// <summary>Which of several apps is being worked on, shown as "2/3". Takes effect with the next phase.</summary>
    public static void Batch(int index, int count)
    {
        lock (Gate) _current = _current with { AppIndex = index, AppCount = count };
    }

    public static void Phase(string phase, App? app = null)
    {
        lock (Gate) Write(_current with { Phase = phase, App = app?.Display ?? _current.App, Done = 0, Total = 0 }, force: true);
    }

    /// <summary>Progress within the current phase; at most four writes a second.</summary>
    public static void Step(int done, int total)
    {
        lock (Gate) Write(_current with { Done = done, Total = total }, force: done >= total);
    }

    /// <summary>Something went wrong (a pull or build failed); <see cref="Finish"/> then reports "failed".</summary>
    public static void Failed()
    {
        lock (Gate) _failed = true;
    }

    /// <summary>
    /// Ends a run with done or failed. Does nothing when nothing was reported since the last call,
    /// so `status`, `run` and the menu's idle time leave the keypad alone.
    /// </summary>
    public static void Finish()
    {
        lock (Gate)
        {
            if (_current.Phase is "start" or "done" or "failed") return;
            Write(_current with { Phase = _failed ? "failed" : "done", AppIndex = 0, AppCount = 0, Done = 0, Total = 0 }, force: true);
            _failed = false;
        }
    }

    private static void Write(Snapshot s, bool force)
    {
        _current = s;
        if (!force && DateTime.UtcNow - _lastWrite < TimeSpan.FromMilliseconds(250)) return;
        _lastWrite = DateTime.UtcNow;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s with { UpdatedAt = DateTimeOffset.UtcNow }));
            // Replacing in one step means a reader never sees a half-written file.
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* best effort */ }
    }
}
