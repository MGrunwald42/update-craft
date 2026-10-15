using System.Text.Json;

namespace UpdateCraft;

/// <summary>Remembers when each app was last pulled, so the issue scan covers exactly what changed since.</summary>
public sealed class StateStore
{
    private static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string FilePath = Path.Combine(AppData, "update-craft", "state.json");
    // Written by versions named "artcraft-updater" and "craft-updater"; read when no new state exists yet.
    private static readonly string[] LegacyFilePaths =
    [
        Path.Combine(AppData, "artcraft-updater", "state.json"),
        Path.Combine(AppData, "craft-updater", "state.json"),
    ];

    public Dictionary<string, DateTimeOffset> LastPull { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static StateStore Load()
    {
        try
        {
            var path = File.Exists(FilePath) ? FilePath : LegacyFilePaths.FirstOrDefault(File.Exists);
            if (path is not null)
                return JsonSerializer.Deserialize<StateStore>(File.ReadAllText(path)) ?? new();
        }
        catch { /* a corrupt state file just means we fall back to the HEAD commit time */ }
        return new();
    }

    /// <summary>Read-modify-write, so concurrent runs for different apps don't overwrite each other's entries.</summary>
    public static void MarkPulled(string appId)
    {
        var state = Load();
        state.LastPull[appId] = DateTimeOffset.UtcNow;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }
}
