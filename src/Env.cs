namespace UpdateCraft;

public static class Env
{
    /// <summary>
    /// Reads a variable from this process, falling back to the saved user and machine values. A terminal
    /// (notably every tab of an already-running Windows Terminal) keeps the environment it started with,
    /// so a key saved with <c>SetEnvironmentVariable(..., "User")</c> would otherwise be invisible until
    /// the terminal is fully restarted.
    /// </summary>
    public static string? Get(string name)
    {
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            var value = Environment.GetEnvironmentVariable(name, target);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return null;
    }

    /// <summary>
    /// Reads <c>KEY=VALUE</c> lines from a .env file. Blank lines, <c>#</c> comments and a leading
    /// <c>export </c> are ignored, and a value may be wrapped in single or double quotes.
    /// </summary>
    public static Dictionary<string, string> ReadFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"env file not found: {path}");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
                value = value[1..^1];
            if (value.Length > 0) values[line[..eq].Trim()] = value;
        }
        return values;
    }
}
