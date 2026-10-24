using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace UpdateCraft;

public enum Risk { Clean, Security, Suspicious, Unchecked }

public sealed record Verdict(GhItem Item, double Malicious, double Security, double Severity, string? Error)
{
    // Thresholds: lean towards flagging — a false alarm costs one keypress, a miss could cost a compromised machine.
    public const double MaliciousThreshold = 0.5;
    public const double SecurityThreshold = 0.6;

    public Risk Risk =>
        Error is not null ? Risk.Unchecked
        : Malicious >= MaliciousThreshold || Severity >= 2.5 ? Risk.Suspicious
        : (Security >= SecurityThreshold && Severity >= 1.0) || Severity >= 1.5 ? Risk.Security
        : Risk.Clean;
}

/// <summary>Classifies GitHub issues/PRs with TypeSafe's Jev model (System One).</summary>
public sealed class SafetyChecker : IDisposable
{
    private const string Endpoint = "https://api.typesafe.ai/v1/systemone";
    private const int Parallelism = 6;
    private const int MaxBodyChars = 4000;
    private const int MaxCommentChars = 1000;
    private const int MaxComments = 10;

    private readonly HttpClient _http;
    private volatile bool _keyRejected;

    /// <summary>True once TypeSafe has answered 401 for this key; no further requests are made with it.</summary>
    public bool KeyRejected => _keyRejected;

    private SafetyChecker(string apiKey)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public static SafetyChecker? Create(string? apiKey) =>
        apiKey is null ? null : new SafetyChecker(apiKey);

    // Asked together over the same state; they run in parallel on the model side.
    private static readonly object Questions = new Dictionary<string, object>
    {
        ["malicious"] = new
        {
            type = "noul",
            instructions =
                "Does `item` (or a comment on it) report, suspect or discuss malicious or tampered code in `repository` " +
                "or its dependencies — for example a backdoor, hidden data or credential exfiltration, an obfuscated payload, " +
                "a malicious build script, a compromised maintainer account or release, a hijacked or typosquatted dependency, " +
                "or a contribution someone flagged as suspicious? Also answer yes if the item itself is a pull request whose " +
                "described change looks like it introduces such behaviour.",
            criteria = new Dictionary<string, string>
            {
                ["true"] = "Malicious, tampered or supply-chain-compromised code is reported, suspected or being introduced",
                ["false"] = "No sign of malicious or tampered code; ordinary bug, feature, question or maintenance",
            },
        },
        ["security"] = new
        {
            type = "noul",
            instructions =
                "Does `item` concern a security vulnerability or a security-sensitive change in `repository` — for example " +
                "memory safety in file parsers, remote code execution, path traversal, unsafe handling of untrusted files or " +
                "network input, injection, privilege or sandbox escape, exposed secrets, or weakened security checks?",
            criteria = new Dictionary<string, string>
            {
                ["true"] = "The item is about a security vulnerability, exploit or security-relevant change",
                ["false"] = "The item has no security relevance",
            },
        },
        ["severity"] = new
        {
            type = "score",
            instructions =
                "How dangerous would it be for someone to pull, build and run the latest source of `repository`, " +
                "judging only from what `item` and its comments describe?",
            criteria = new[]
            {
                "No security relevance: an ordinary bug report, feature, question, documentation or refactoring",
                "Minor: security hardening or a theoretical concern with no known exploit",
                "Serious: a real vulnerability that a crafted file or input could exploit",
                "Critical: malicious, backdoored or compromised code that would run on the user's machine when building or running the app",
            },
        },
    };

    public async Task<List<Verdict>> ClassifyAsync(App app, IReadOnlyList<GhItem> items, Action<int>? progress = null,
        CancellationToken ct = default)
    {
        var results = new Verdict[items.Count];
        var done = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, items.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
            async (i, token) =>
            {
                results[i] = await ClassifyOneAsync(app, items[i], token);
                progress?.Invoke(Interlocked.Increment(ref done));
            });
        return [.. results];
    }

    private async Task<Verdict> ClassifyOneAsync(App app, GhItem item, CancellationToken ct)
    {
        var request = new
        {
            model = "jev-latest",
            state = new
            {
                repository = new { name = app.Slug, description = $"{app.Display}, an open-source desktop app written in Rust" },
                item = new
                {
                    kind = item.IsPullRequest ? "pull request" : "issue",
                    number = item.Number,
                    title = item.Title,
                    author = item.Author,
                    author_association = item.AuthorAssociation,
                    state = item.State,
                    merged = item.IsPullRequest ? item.Merged : (bool?)null,
                    labels = item.Labels,
                    body = Truncate(item.Body, MaxBodyChars),
                    recent_comments = item.RecentComments
                        .TakeLast(MaxComments)
                        .Select(c => new { author = c.Author, author_association = c.Association, body = Truncate(c.Body, MaxCommentChars) }),
                },
            },
            questions = Questions,
        };

        for (var attempt = 0; ; attempt++)
        {
            if (_keyRejected) return new Verdict(item, 0, 0, 0, "TypeSafe rejected the API key; not retried");
            try
            {
                using var resp = await _http.PostAsJsonAsync(Endpoint, request, ct);
                if (IsTransient(resp.StatusCode) && attempt < 5)
                {
                    var delay = resp.Headers.RetryAfter?.Delta
                        ?? TimeSpan.FromSeconds(Math.Pow(2, attempt) + Random.Shared.NextDouble());
                    await Task.Delay(delay, ct);
                    continue;
                }
                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                {
                    _keyRejected = true;
                    return new Verdict(item, 0, 0, 0, "TypeSafe rejected the API key (401)");
                }
                if (!resp.IsSuccessStatusCode)
                    return new Verdict(item, 0, 0, 0, $"TypeSafe returned {(int)resp.StatusCode}: {Truncate(await resp.Content.ReadAsStringAsync(ct), 200)}");

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                var answers = doc.RootElement.GetProperty("answers");
                return new Verdict(item,
                    answers.GetProperty("malicious").GetProperty("noul").GetDouble(),
                    answers.GetProperty("security").GetProperty("noul").GetDouble(),
                    answers.GetProperty("severity").GetProperty("score").GetDouble(),
                    null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (attempt < 2) { await Task.Delay(TimeSpan.FromSeconds(1 + attempt), ct); continue; }
                return new Verdict(item, 0, 0, 0, ex.Message);
            }
        }
    }

    // 429/529 are the documented retryable codes; under sustained load TypeSafe also returns
    // undocumented 499 "internal_error" responses (seen on large scans), and 408/5xx are transient as well.
    private static bool IsTransient(HttpStatusCode status) =>
        (int)status is 408 or 429 or 499 or 529 or >= 500;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + " …[truncated]";

    public void Dispose() => _http.Dispose();
}
