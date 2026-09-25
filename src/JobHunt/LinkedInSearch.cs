using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace JobHunt;

public record SearchResult(List<AlertJob> Jobs, bool HitCap, int DroppedAsOld);

/// <summary>
/// Runs a search through LinkedIn's public, logged-out job search (the "See all jobs" results an
/// alert email only samples 6 of) and returns every result posted in the past 24 hours.
/// Opt-in: criteria.yaml linkedin.run_searches.
/// </summary>
public static class LinkedInSearch
{
    public static async Task<SearchResult> RunAsync(string keywords, LinkedInRules rules, bool remoteOnly)
    {
        // The public search ignores its remote/experience/job-type/salary filters (tested Sep 2026) and
        // honors only keywords and the date window. "AND remote" in the keywords is the only way to
        // narrow to remote here; it can let "not remote" through, which the later remote check catches.
        var q = remoteOnly ? $"({keywords}) AND remote" : keywords;
        var baseUrl = "https://www.linkedin.com/jobs-guest/jobs/api/seeMoreJobPostings/search?" +
                      $"keywords={Uri.EscapeDataString(q)}&geoId={Uri.EscapeDataString(rules.GeoId)}&f_TPR=r{rules.PostedWithinHours * 3600}";

        var jobs = new List<AlertJob>();
        var now = DateTimeOffset.UtcNow;
        int seenResults = 0, old = 0;
        for (var start = 0; start < rules.MaxResultsPerSearch; start += 10)
        {
            if (await LinkedInHttp.GetAsync($"{baseUrl}&start={start}") is not { } html) break;
            var page = ParseResults(html);
            if (page.Count == 0) break;
            seenResults += page.Count;
            foreach (var (job, age) in page)
            {
                // f_TPR does work (verified), but enforce the window here too, from the card's own age text.
                if (!IsWithin(age, rules.PostedWithinHours)) { old++; continue; }
                // Keep LinkedIn's own location. "AND remote" only ranks, so a result isn't known to be remote;
                // the hard filter decides from the posting, then from this location.
                jobs.Add(job with { AlertDate = now });
            }
        }
        return new SearchResult(jobs, HitCap: seenResults >= rules.MaxResultsPerSearch, DroppedAsOld: old);
    }

    /// <summary>
    /// Whether a card's age text ("Just now", "14 minutes ago", "3 hours ago", "2 days ago") is under the window.
    /// Unknown or missing ages don't pass. "1 day ago" means at least 24 hours, so it fails a 24-hour window.
    /// </summary>
    public static bool IsWithin(string? age, int hours)
    {
        if (age is null) return false;
        if (age.Contains("just now", StringComparison.OrdinalIgnoreCase)) return true;
        var m = Regex.Match(age, @"(\d+)\s*(minute|hour|day|week|month|year)s?", RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        var n = int.Parse(m.Groups[1].Value);
        double ageHours = m.Groups[2].Value.ToLowerInvariant() switch
        {
            "minute" => n / 60.0, "hour" => n, "day" => n * 24, "week" => n * 168, "month" => n * 720, _ => n * 8760,
        };
        return ageHours < hours;
    }

    public static List<(AlertJob Job, string? Age)> ParseResults(string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        string Clean(string? s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();
        return doc.QuerySelectorAll("li").Select(li =>
        {
            var urn = li.QuerySelector("[data-entity-urn]")?.GetAttribute("data-entity-urn") ?? "";
            var id = Regex.Match(urn, @"jobPosting:(\d+)").Groups[1].Value;
            var job = new AlertJob(id,
                Clean(li.QuerySelector(".base-search-card__title")?.TextContent),
                Clean(li.QuerySelector(".base-search-card__subtitle")?.TextContent),
                Clean(li.QuerySelector(".job-search-card__location")?.TextContent));
            var age = li.QuerySelector("time") is { } t ? Clean(t.TextContent) : null;
            return (Job: job, Age: age);
        }).Where(r => r.Job.LinkedInId.Length > 0 && r.Job.Title.Length > 0).ToList();
    }
}
