using Spectre.Console;

namespace UpdateCraft;

/// <summary>One menu entry. <paramref name="Label"/> is Spectre markup; <paramref name="Key"/> selects it instantly.</summary>
public sealed record MenuItem<T>(T Value, string Label, char Key);

/// <summary>
/// A selection list driven by arrow keys + Enter, where each item also has a single-key shortcut.
/// (Spectre's SelectionPrompt has no hotkey support.)
/// </summary>
public static class KeyMenu
{
    /// <summary>Returns the chosen value, or <paramref name="escapeValue"/> when Esc is pressed and allowed.</summary>
    public static T Show<T>(string title, IReadOnlyList<MenuItem<T>> items, bool allowEscape = false, T escapeValue = default!)
    {
        var index = 0;
        var chosen = -1;

        AnsiConsole.Live(Render(title, items, index, allowEscape, chosen))
            .AutoClear(false)
            .Start(ctx =>
            {
                // Draw right away: the loop below blocks on ReadKey before Spectre's first refresh tick.
                ctx.Refresh();
                while (chosen < 0)
                {
                    var key = Console.ReadKey(intercept: true);
                    switch (key.Key)
                    {
                        case ConsoleKey.UpArrow:
                            index = (index - 1 + items.Count) % items.Count;
                            break;
                        case ConsoleKey.DownArrow:
                            index = (index + 1) % items.Count;
                            break;
                        case ConsoleKey.Home:
                            index = 0;
                            break;
                        case ConsoleKey.End:
                            index = items.Count - 1;
                            break;
                        case ConsoleKey.Enter:
                            chosen = index;
                            break;
                        case ConsoleKey.Escape when allowEscape:
                            chosen = int.MaxValue; // sentinel: escaped
                            break;
                        default:
                            var c = char.ToLowerInvariant(key.KeyChar);
                            var hit = items.ToList().FindIndex(i => i.Key == c);
                            if (hit >= 0) index = chosen = hit;
                            break;
                    }
                    ctx.UpdateTarget(Render(title, items, index, allowEscape, chosen));
                    ctx.Refresh();
                }
            });

        return chosen == int.MaxValue ? escapeValue : items[chosen].Value;
    }

    private static Rows Render<T>(string title, IReadOnlyList<MenuItem<T>> items, int index, bool allowEscape, int chosen)
    {
        var lines = new List<Markup> { new(title) };
        for (var i = 0; i < items.Count; i++)
        {
            var key = Markup.Escape($"[{items[i].Key}]");
            lines.Add(i == index
                ? new Markup($"[bold deepskyblue1]> {key} {items[i].Label}[/]")
                : new Markup($"  [grey]{key}[/] {items[i].Label}"));
        }
        if (chosen < 0)
            lines.Add(new Markup("[grey]↑/↓ move · enter select · press a key to jump straight in" +
                                 (allowEscape ? " · esc back" : "") + "[/]"));
        return new Rows(lines);
    }
}
