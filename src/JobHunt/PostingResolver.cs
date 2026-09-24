namespace JobHunt;

public class PostingResolver(ClaudeClient claude, JobBoards boards, string resolvePrompt, string extractPrompt)
{
    const string SearchSystem = "You find official job postings on company career sites and ATS pages. You answer with JSON only.";
    const string ExtractSystem = "You extract structured facts from job postings. You answer with JSON only.";
    const int Attempts = 2;

    /// <summary>
    /// Tries the company's public job board first (no search), then Claude with web search.
    /// Returns a high or medium confidence posting, else the best low-confidence guess (the caller
    /// treats that as unverified), else a url-less posting carrying the lookup's reason, else null.
    /// </summary>
    public async Task<Posting?> ResolveAsync(AlertJob job, bool allowSearch = true)
    {
        if (await boards.FindAsync(job) is var (board, hit))
        {
            Console.WriteLine($"  found on {board.Ats} board ({board.Slug})");
            return await FromDescriptionAsync(job, hit.Url, board.Ats, hit.Description, hit.PostedDate);
        }

        if (!allowSearch)
            return new Posting { Reason = "Not on a public job board (search skipped)" };

        var search = Prompt.Fill(resolvePrompt, ("title", job.Title), ("company", job.Company), ("location", job.Location),
            ("alert_date", job.AlertDate?.ToString("yyyy-MM-dd") ?? "recently"));
        Posting? best = null;
        for (var i = 0; i < Attempts; i++)
        {
            Posting posting;
            try { posting = Json.Parse<Posting>(await claude.AskAsync(SearchSystem, search, webSearch: true)); }
            catch (Exception ex) when (i < Attempts - 1) { Console.WriteLine($"  lookup retry: {ex.Message}"); continue; }

            if (string.IsNullOrWhiteSpace(posting.Url))
            {
                Console.WriteLine($"  not found: {posting.Reason}");
                best ??= posting; // keeps the reason for the digest
                continue;
            }
            posting.NormalizePay();
            if (IsConfident(posting) && await LinkIsDeadAsync(posting.Url!))
            {
                posting.Confidence = "low";
                posting.Reason = "Link didn't load (posting closed or URL wrong)";
            }
            if (IsConfident(posting))
            {
                boards.Learn(job.Company, posting.Url);
                return posting;
            }
            Console.WriteLine($"  low confidence: {posting.Reason}");
            if (string.IsNullOrWhiteSpace(best?.Url)) best = posting;
        }
        return best;
    }

    /// <summary>
    /// Builds a posting from a description we already have (job board or LinkedIn). The description
    /// doesn't come with clean remote/country/pay fields, so one cheap, tool-free call extracts them.
    /// </summary>
    public async Task<Posting> FromDescriptionAsync(AlertJob job, string url, string source, string description, string? postedDate)
    {
        var prompt = Prompt.Fill(extractPrompt, ("title", job.Title), ("company", job.Company), ("description", description));
        var facts = Json.Parse<Posting>(await claude.AskAsync(ExtractSystem, prompt, webSearch: false));
        facts.Url = url;
        facts.Ats = source;
        facts.Confidence = "high";
        facts.PostedDate = postedDate;
        facts.Description = description;
        facts.NormalizePay();
        return facts;
    }

    public static bool IsConfident(Posting p) => !string.IsNullOrWhiteSpace(p.Url) && p.Confidence != "low";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    /// True only on clear evidence the link is bad: 404/410, or an ATS redirect to its error page.
    /// Anything else (including 403 from bot-blocking career sites, or a network error) counts as alive.
    /// </summary>
    static async Task<bool> LinkIsDeadAsync(string url)
    {
        try
        {
            using var res = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            var final = res.RequestMessage?.RequestUri?.ToString() ?? url;
            return res.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone
                   || final.Contains("error=true", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

public static class Prompt
{
    public static string Fill(string template, params (string Key, string? Value)[] values) =>
        values.Aggregate(template, (t, kv) => t.Replace("{" + kv.Key + "}", kv.Value ?? ""));
}
