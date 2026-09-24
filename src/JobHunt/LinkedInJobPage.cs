using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace JobHunt;

public record LinkedInJob(bool Closed, bool? EasyApply, string Description, string? Posted, string? Applicants,
    string? SalaryText = null, decimal? SalaryMax = null, string? Seniority = null, string? EmploymentType = null);

/// <summary>
/// Reads LinkedIn's public, logged-out job page for one job ID: open or closed, Easy Apply or not,
/// LinkedIn's pay box when it has one, and the description. This is an opt-in exception to the
/// "no LinkedIn scraping" rule (criteria.yaml: linkedin.check_job_page). No login, no account.
/// </summary>
public static partial class LinkedInJobPage
{
    public static async Task<LinkedInJob?> GetAsync(string jobId) =>
        await LinkedInHttp.GetAsync($"https://www.linkedin.com/jobs-guest/jobs/api/jobPosting/{jobId}") is { } html
            ? Parse(html) : null;

    public static LinkedInJob Parse(string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        string? Text(string selector) => doc.QuerySelector(selector)?.TextContent.Trim() is { Length: > 0 } t
            ? Regex.Replace(t, @"\s+", " ") : null;

        // Onsite apply is LinkedIn's Easy Apply; offsite sends you to the company's site.
        bool? easyApply = html.Contains("apply-link-onsite") ? true : html.Contains("apply-link-offsite") ? false : null;

        var descriptionHtml = doc.QuerySelector(".show-more-less-html__markup")?.InnerHtml ?? "";
        // "Seniority level", "Employment type", "Job function", "Industries"
        var criteria = doc.QuerySelectorAll(".description__job-criteria-item")
            .Select(i => (Key: Regex.Replace(i.QuerySelector(".description__job-criteria-subheader")?.TextContent ?? "", @"\s+", " ").Trim(),
                          Value: Regex.Replace(i.QuerySelector(".description__job-criteria-text")?.TextContent ?? "", @"\s+", " ").Trim()))
            .Where(c => c.Key.Length > 0)
            .DistinctBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);
        var description = JobBoards.HtmlToText(descriptionHtml);
        var salary = Text(".compensation__salary");
        if (description.Length > 0)
            description += "\n\n" + string.Join("\n", criteria.Select(c => $"{c.Key}: {c.Value}")) +
                           (salary is null ? "" : $"\nLinkedIn pay range: {salary}");

        return new LinkedInJob(
            Closed: doc.QuerySelector(".closed-job__flavor--closed") is not null,
            EasyApply: easyApply,
            Description: description,
            Posted: Text(".posted-time-ago__text"),
            Applicants: Text(".num-applicants__caption"),
            SalaryText: salary,
            SalaryMax: AnnualMax(salary),
            Seniority: criteria.GetValueOrDefault("Seniority level"),
            EmploymentType: criteria.GetValueOrDefault("Employment type"));
    }

    [GeneratedRegex(@"\$\s?([\d,]+(?:\.\d+)?)\s*(K)?", RegexOptions.IgnoreCase)] private static partial Regex MoneyRx();

    // A pay RANGE ("$153K - $192K", "$130,000 to $145,000", "$75/hr – $85/hr"). Ranges only, so a stray
    // "$5M funding round" in the description isn't mistaken for pay.
    [GeneratedRegex(@"\$\s?\d[\d,]*(?:\.\d+)?\s*K?(?:\s*(?:/|per\s*)(?:yr|year|hr|hour|annum))?\s*(?:-|–|—|to)\s*\$?\s?\d[\d,]*(?:\.\d+)?\s*K?(?:\s*(?:/|per\s*)(?:yr|year|hr|hour|annum))?", RegexOptions.IgnoreCase)]
    private static partial Regex RangeRx();

    /// <summary>Top of the first pay range stated in free text, as annual USD, or null. No model call.</summary>
    public static decimal? PayInText(string? text) =>
        text is null ? null : RangeRx().Match(text) is { Success: true } m ? AnnualMax(m.Value) : null;

    /// <summary>Top of a pay range as annual USD: "$156,750.00/yr - $215,000.00/yr" → 215000; "$85/hr" → 176800.</summary>
    public static decimal? AnnualMax(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // "K" only scales small numbers: "$153K" is 153,000, but "$145,000 K" (seen in the wild) stays 145,000.
        var values = MoneyRx().Matches(text)
            .Select(m => decimal.Parse(m.Groups[1].Value.Replace(",", "")) is var v && m.Groups[2].Success && v < 1000 ? v * 1000 : v)
            .ToList();
        if (values.Count == 0) return null;
        var max = values.Max();
        var annual = text.Contains("/hr", StringComparison.OrdinalIgnoreCase) || text.Contains("hour", StringComparison.OrdinalIgnoreCase) || max < 1000
            ? max * 2080 : max;
        return annual > 2_000_000 ? null : annual; // not a salary; better unknown than wrong
    }
}
