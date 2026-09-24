using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JobHunt;

public record Board(string Ats, string Slug);

public record BoardJob(string Title, string Url, string Description, string? PostedDate);

/// <summary>
/// Finds postings through the public job-board APIs that Greenhouse, Lever, and Ashby expose for
/// every company, with no search and no model call. Company-to-board mappings are learned from
/// URLs Claude finds (and from name guesses) and kept in data/boards.json.
/// </summary>
// ponytail: Greenhouse, Lever, Ashby only. Workable and SmartRecruiters have similar APIs; Workday has none.
public partial class JobBoards(string path)
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    readonly Dictionary<string, Board> _known = File.Exists(path)
        ? JsonSerializer.Deserialize<Dictionary<string, Board>>(File.ReadAllText(path), Json.Options) ?? new()
        : new();

    public void Save() { lock (_known) File.WriteAllText(path, JsonSerializer.Serialize(
        new SortedDictionary<string, Board>(_known), Json.Options)); }

    [GeneratedRegex(@"(?:job-)?boards(?:\.eu)?\.greenhouse\.io/(?!embed)([\w-]+)/jobs/", RegexOptions.IgnoreCase)] private static partial Regex GreenhouseRx();
    [GeneratedRegex(@"jobs(?:\.eu)?\.lever\.co/([\w-]+)/", RegexOptions.IgnoreCase)] private static partial Regex LeverRx();
    [GeneratedRegex(@"jobs\.ashbyhq\.com/([\w.-]+)/", RegexOptions.IgnoreCase)] private static partial Regex AshbyRx();

    public static Board? FromUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (GreenhouseRx().Match(url) is { Success: true } g) return new("greenhouse", g.Groups[1].Value);
        if (LeverRx().Match(url) is { Success: true } l) return new("lever", l.Groups[1].Value);
        if (AshbyRx().Match(url) is { Success: true } a) return new("ashby", a.Groups[1].Value);
        return null;
    }

    public void Learn(string company, string? postingUrl)
    {
        if (FromUrl(postingUrl) is { } board) lock (_known) _known[Key(company)] = board;
    }

    /// <summary>Saved for companies with no Greenhouse, Lever, or Ashby list, so their names aren't guessed again.</summary>
    static readonly Board None = new("none", "");

    // Each company's list is downloaded once per run and shared by all its roles (large boards are several MB).
    readonly Dictionary<Board, Task<List<BoardJob>?>> _lists = new();

    Task<List<BoardJob>?> CachedListAsync(Board board)
    {
        lock (_lists)
        {
            if (!_lists.TryGetValue(board, out var task)) _lists[board] = task = ListAsync(board);
            return task;
        }
    }

    public async Task<(Board Board, BoardJob Job)?> FindAsync(AlertJob job)
    {
        var key = Key(job.Company);
        Board? b; bool known; lock (_known) known = _known.TryGetValue(key, out b); // roles run in parallel
        if (known && b == None) return null;
        var anyBoardExists = false;
        foreach (var board in known ? [b!] : Guesses(job.Company))
        {
            List<BoardJob>? jobs;
            try { jobs = await CachedListAsync(board); }
            catch { anyBoardExists = true; continue; } // failed, not missing: don't record "none" for this company
            if (jobs is null) continue;
            anyBoardExists = true;
            if (MatchTitle(job.Title, jobs.Select(j => j.Title).ToList()) is not { } i)
            {
                if (known) return null; // right board, but no single matching title
                continue;               // a guessed board may belong to another company with the same name
            }
            lock (_known) _known[key] = board; // a guess only sticks once it has produced a title match
            return (board, jobs[i]);
        }
        // ponytail: a company that later moves to one of these systems stays "none"; delete its line in
        // data/boards.json to have it checked again.
        if (!known && !anyBoardExists) lock (_known) _known.TryAdd(key, None);
        return null;
    }

    /// <summary>
    /// Index of the posting whose title matches the alert title: exact after normalizing, else the
    /// only one that contains it (or is contained by it). Null when none or ambiguous.
    /// </summary>
    public static int? MatchTitle(string alertTitle, IReadOnlyList<string> titles)
    {
        var a = Norm(alertTitle);
        if (a.Length < 6) return null;
        var normed = titles.Select(Norm).ToList();
        var exact = normed.IndexOf(a);
        if (exact >= 0) return exact;
        var close = normed.Select((t, i) => (t, i)).Where(x => x.t.Contains(a) || a.Contains(x.t) && x.t.Length >= 6).ToList();
        return close.Count == 1 ? close[0].i : null;
    }

    static IEnumerable<Board> Guesses(string company)
    {
        var words = Regex.Replace(company.ToLowerInvariant(), @"\b(inc|llc|ltd|corp|corporation|co)\b\.?", "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => Regex.Replace(w, "[^a-z0-9]", "")).Where(w => w.Length > 0).ToList();
        if (words.Count == 0) return [];
        var slugs = new[] { string.Concat(words), string.Join('-', words) }.Distinct();
        return slugs.SelectMany(s => new[] { new Board("greenhouse", s), new Board("lever", s), new Board("ashby", s) });
    }

    /// <summary>
    /// The board's postings; null when the board doesn't exist (404). Throws on anything else (network
    /// error, 5xx, bad JSON), so a temporary failure is never mistaken for "this company has no board".
    /// </summary>
    static async Task<List<BoardJob>?> ListAsync(Board board)
    {
        {
            var url = board.Ats switch
            {
                "greenhouse" => $"https://boards-api.greenhouse.io/v1/boards/{board.Slug}/jobs?content=true",
                "lever" => $"https://api.lever.co/v0/postings/{board.Slug}?mode=json",
                "ashby" => $"https://api.ashbyhq.com/posting-api/job-board/{board.Slug}?includeCompensation=true",
                _ => null,
            };
            if (url is null) return null;
            using var res = await Http.GetAsync(url);
            if (res.StatusCode == HttpStatusCode.NotFound) return null;
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            return board.Ats switch
            {
                "greenhouse" => root.GetProperty("jobs").EnumerateArray().Select(j => new BoardJob(
                    S(j, "title"), S(j, "absolute_url"), HtmlToText(WebUtility.HtmlDecode(S(j, "content"))),
                    DatePart(S(j, "first_published") is { Length: > 0 } fp ? fp : S(j, "updated_at")))).ToList(),
                "lever" => root.EnumerateArray().Select(j => new BoardJob(
                    S(j, "text"), S(j, "hostedUrl"), LeverText(j),
                    j.TryGetProperty("createdAt", out var c) && c.TryGetInt64(out var ms)
                        ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("yyyy-MM-dd") : null)).ToList(),
                "ashby" => root.GetProperty("jobs").EnumerateArray().Select(j => new BoardJob(
                    S(j, "title"), S(j, "jobUrl"), AshbyText(j), DatePart(S(j, "publishedAt")))).ToList(),
                _ => null,
            };
        }
    }

    static string LeverText(JsonElement j)
    {
        var parts = new List<string> { S(j, "descriptionPlain") };
        if (j.TryGetProperty("lists", out var lists))
            foreach (var l in lists.EnumerateArray()) parts.Add(S(l, "text") + "\n" + HtmlToText(S(l, "content")));
        parts.Add(S(j, "additionalPlain"));
        parts.Add(S(j, "salaryDescriptionPlain"));
        if (j.TryGetProperty("workplaceType", out var w)) parts.Add("Workplace type: " + w);
        if (j.TryGetProperty("categories", out var cat)) parts.Add("Location: " + S(cat, "location"));
        return string.Join("\n\n", parts.Where(p => p.Trim().Length > 0));
    }

    static string AshbyText(JsonElement j)
    {
        var text = S(j, "descriptionPlain") + "\n\nLocation: " + S(j, "location");
        if (j.TryGetProperty("isRemote", out var r)) text += $"\nRemote: {r}";
        if (j.TryGetProperty("compensation", out var comp)) text += "\nCompensation: " + S(comp, "compensationTierSummary");
        return text;
    }

    static string S(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static string? DatePart(string s) => s.Length >= 10 ? s[..10] : null;

    public static string HtmlToText(string html)
    {
        var text = Regex.Replace(html, @"<(br|/p|/li|/h\d|/div)[^>]*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        return Regex.Replace(WebUtility.HtmlDecode(text), @"\n\s*\n+", "\n\n").Trim();
    }

    static string Key(string company) => SeenStore.Key(new AlertJob("", "", company, "")).TrimEnd('|');

    static string Norm(string s) => Regex.Replace(Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", " "), @"\s+", " ").Trim();
}
