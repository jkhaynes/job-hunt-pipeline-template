using AngleSharp.Html.Parser;

namespace JobHunt;

public record LinkedInJob(bool Closed, bool? EasyApply, string Description, string? Posted, string? Applicants);

/// <summary>
/// Reads LinkedIn's public, logged-out job page for one job ID: open or closed, Easy Apply or not,
/// and the description. This is an opt-in exception to the "no LinkedIn scraping" rule
/// (criteria.yaml: linkedin.check_job_page). One request per new role, no login, no account.
/// </summary>
public static class LinkedInJobPage
{
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("job-hunt-pipeline/1.0 (personal use)");
        return http;
    }

    static readonly TimeSpan Spacing = TimeSpan.FromSeconds(5);
    static bool _stopped;

    /// <summary>
    /// Null when unavailable; the role then falls back to the normal lookup. Requests are spaced
    /// 5s apart; a 429 is honored (Retry-After, capped at 60s) and retried once, and a second 429
    /// stops LinkedIn checks for the rest of the run.
    /// </summary>
    public static async Task<LinkedInJob?> GetAsync(string jobId)
    {
        if (_stopped) return null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Task.Delay(Spacing);
            try
            {
                using var res = await Http.GetAsync($"https://www.linkedin.com/jobs-guest/jobs/api/jobPosting/{jobId}");
                if (res.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
                    if (wait > TimeSpan.FromSeconds(60)) wait = TimeSpan.FromSeconds(60);
                    Console.WriteLine($"  LinkedIn page: rate limited, waiting {wait.TotalSeconds:0}s");
                    await Task.Delay(wait);
                    continue;
                }
                if (!res.IsSuccessStatusCode)
                {
                    Console.WriteLine($"  LinkedIn page: HTTP {(int)res.StatusCode}, skipped");
                    return null;
                }
                return Parse(await res.Content.ReadAsStringAsync());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  LinkedIn page: {ex.Message}, skipped");
                return null;
            }
        }
        _stopped = true;
        Console.WriteLine("  LinkedIn page: still rate limited, skipping LinkedIn checks for the rest of this run");
        return null;
    }

    public static LinkedInJob Parse(string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        string? Text(string selector) => doc.QuerySelector(selector)?.TextContent.Trim() is { Length: > 0 } t
            ? System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ") : null;

        // Onsite apply is LinkedIn's Easy Apply; offsite sends you to the company's site.
        bool? easyApply = html.Contains("apply-link-onsite") ? true : html.Contains("apply-link-offsite") ? false : null;

        var descriptionHtml = doc.QuerySelector(".show-more-less-html__markup")?.InnerHtml ?? "";
        var criteria = doc.QuerySelectorAll(".description__job-criteria-item")
            .Select(i => $"{i.QuerySelector(".description__job-criteria-subheader")?.TextContent.Trim()}: " +
                         $"{i.QuerySelector(".description__job-criteria-text")?.TextContent.Trim()}");
        var description = JobBoards.HtmlToText(descriptionHtml);
        if (description.Length > 0) description += "\n\n" + string.Join("\n", criteria);

        return new LinkedInJob(
            Closed: doc.QuerySelector(".closed-job__flavor--closed") is not null,
            EasyApply: easyApply,
            Description: description,
            Posted: Text(".posted-time-ago__text"),
            Applicants: Text(".num-applicants__caption"));
    }
}
