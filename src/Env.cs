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
}
