namespace UpdateCraft;

/// <summary>
/// One supported ArtCraft tool: a GitHub repository whose Cargo workspace builds a desktop binary
/// (<see cref="Package"/>) and its headless command-line companion (<see cref="CliPackage"/>, which
/// also serves the app's MCP server).
/// </summary>
public sealed record App(string Id, string Display, string Owner, string Repo, string Package)
{
    public static readonly IReadOnlyList<App> All =
    [
        new("filmcraft", "FilmCraft", "storytold", "filmcraft", "filmcraft"),
        new("pdfcraft", "PdfCraft", "storytold", "pdfcraft", "pdfcraft"),
        new("photocraft", "PhotoCraft", "storytold", "photocraft", "photocraft"),
        new("vectorcraft", "VectorCraft", "storytold", "vectorcraft", "vectorcraft"),
    ];

    public static App? Find(string id) =>
        All.FirstOrDefault(a => string.Equals(a.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    public string Slug => $"{Owner}/{Repo}";
    public string CloneUrl => $"https://github.com/{Owner}/{Repo}.git";
    public string IssuesUrl => $"https://github.com/{Owner}/{Repo}/issues";

    public string CliPackage => Package + "-cli";

    public string Dir(string root) => Path.Combine(root, Id);
    public string Exe(string root) => Path.Combine(Dir(root), "target", "release", Package + ".exe");
    public string CliExe(string root) => Path.Combine(Dir(root), "target", "release", CliPackage + ".exe");
}
