using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace UpdateCraft;

public sealed record GhComment(string Author, string Association, string Body, DateTimeOffset UpdatedAt);

public sealed record GhItem(
    int Number,
    string Title,
    string Body,
    string Author,
    string AuthorAssociation,
    string State,
    bool IsPullRequest,
    bool Merged,
    string[] Labels,
    string Url,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public List<GhComment> RecentComments { get; } = [];
    public string Kind => IsPullRequest ? (Merged ? "merged PR" : "PR") : "issue";
}

/// <summary>Reads issues, pull requests and comments from the GitHub REST API.</summary>
public sealed class GitHubClient : IDisposable
{
    private const int MaxPages = 10; // 1000 items per list is far more than a normal update window
    private readonly HttpClient _http;
    private readonly bool _hasToken;

    /// <param name="token">Optional (from <c>--env-file</c>): raises the anonymous limit of 60 requests/hour.</param>
    public GitHubClient(string? token)
    {
        _http = new HttpClient { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("update-craft/0.1");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        _hasToken = !string.IsNullOrWhiteSpace(token);
        if (_hasToken)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>Issues and pull requests (open or closed) updated since <paramref name="since"/>, with their recent comments.</summary>
    public async Task<List<GhItem>> ItemsSinceAsync(App app, DateTimeOffset since, CancellationToken ct = default)
    {
        var sinceIso = since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var items = new Dictionary<int, GhItem>();

        foreach (var el in await GetPagedAsync($"repos/{app.Slug}/issues?state=all&sort=updated&direction=desc&since={sinceIso}", ct))
        {
            var pr = el.TryGetProperty("pull_request", out var prEl) ? prEl : (JsonElement?)null;
            var item = new GhItem(
                Number: el.GetProperty("number").GetInt32(),
                Title: Str(el, "title"),
                Body: Str(el, "body"),
                Author: el.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? Str(u, "login") : "",
                AuthorAssociation: Str(el, "author_association"),
                State: Str(el, "state"),
                IsPullRequest: pr is not null,
                Merged: pr is { } p && p.TryGetProperty("merged_at", out var m) && m.ValueKind == JsonValueKind.String,
                Labels: el.TryGetProperty("labels", out var ls)
                    ? ls.EnumerateArray().Select(l => Str(l, "name")).Where(n => n.Length > 0).ToArray()
                    : [],
                Url: Str(el, "html_url"),
                CreatedAt: el.GetProperty("created_at").GetDateTimeOffset(),
                UpdatedAt: el.GetProperty("updated_at").GetDateTimeOffset());
            items[item.Number] = item;
        }

        if (items.Count == 0) return [];

        // One repository-wide call returns every comment written since the last pull, so a warning
        // posted in a comment ("this PR adds a backdoor") is seen without one request per issue.
        foreach (var el in await GetPagedAsync($"repos/{app.Slug}/issues/comments?sort=updated&direction=asc&since={sinceIso}", ct))
        {
            var issueUrl = Str(el, "issue_url");
            if (!int.TryParse(issueUrl[(issueUrl.LastIndexOf('/') + 1)..], out var number)) continue;
            if (!items.TryGetValue(number, out var item)) continue;
            item.RecentComments.Add(new GhComment(
                el.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? Str(u, "login") : "",
                Str(el, "author_association"),
                Str(el, "body"),
                el.GetProperty("updated_at").GetDateTimeOffset()));
        }

        return items.Values.OrderByDescending(i => i.UpdatedAt).ToList();
    }

    private async Task<List<JsonElement>> GetPagedAsync(string path, CancellationToken ct)
    {
        var all = new List<JsonElement>();
        for (var page = 1; page <= MaxPages; page++)
        {
            using var resp = await _http.GetAsync($"{path}&per_page=100&page={page}", ct);
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests &&
                resp.Headers.TryGetValues("x-ratelimit-remaining", out var rem) && rem.FirstOrDefault() == "0")
                throw new InvalidOperationException(_hasToken
                    ? "GitHub API rate limit reached even with a token. Wait until the limit resets (at most an hour)."
                    : "GitHub API rate limit reached (60 requests/hour without a token). Pass --env-file with a file containing GITHUB_TOKEN=<personal access token>.");
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                throw new InvalidOperationException(
                    "GitHub rejected the token from --env-file (401) — it may have expired or been revoked. Create a new one.");
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"GitHub API returned {(int)resp.StatusCode} for {path}");

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var batch = doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
            all.AddRange(batch);
            if (batch.Count < 100) break;
        }
        return all;
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public void Dispose() => _http.Dispose();
}
