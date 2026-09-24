namespace JobHunt;

public class Matcher(ClaudeClient claude, string resume, string preferencesText, string scorePrompt)
{
    const string System = "You are a careful, honest job-fit evaluator. You answer with JSON only.";

    /// <summary>Title rule; needs only the alert, so it also applies to roles whose posting wasn't found.</summary>
    public static string? TitleFilter(AlertJob job, HardRules hard)
    {
        var title = job.Title.ToLowerInvariant();
        var word = hard.ExcludeTitleWords.FirstOrDefault(w => title.Contains(w.ToLowerInvariant()));
        return word is null ? null : $"Title contains \"{word}\"";
    }

    public static bool IsAgency(AlertJob job, HardRules hard)
    {
        var company = job.Company.ToLowerInvariant();
        return hard.Agencies.Any(a => company.Contains(a.ToLowerInvariant()));
    }

    /// <summary>Deterministic hard filters from criteria.yaml. Returns a reject reason, or null to keep.</summary>
    public static string? HardFilter(JobRole role, HardRules hard)
    {
        var p = role.Posting!;
        if (TitleFilter(role.Job, hard) is { } titleReason) return titleReason;

        // Postings (LinkedIn's especially) often don't say. The alert's "(Remote)" comes from
        // LinkedIn's workplace-type field, so it's a fair tiebreaker for "unknown".
        if (p.Remote is null or "unknown" && role.Job.Location.Contains("(Remote)", StringComparison.OrdinalIgnoreCase))
            p.Remote = "fully_remote";

        if (hard.Remote == "fully_remote" && p.Remote != "fully_remote")
            return $"Not fully remote ({p.Remote ?? "unknown"})";

        // Filter only when the posting names countries and the US isn't one of them ("Canada or US" stays).
        if (hard.UsOnly && p.Countries.Count > 0 && !p.Countries.Any(c => c.ToUpperInvariant() is "US" or "USA"))
            return $"Must be based outside the US ({string.Join(", ", p.Countries)})";

        if (hard.HomeState is { Length: > 0 } home && p.StateRestrictions.Count > 0
            && !p.StateRestrictions.Any(s => s.Equals(home, StringComparison.OrdinalIgnoreCase)))
            return $"Must live in {string.Join(", ", p.StateRestrictions)}";

        if (p.EmploymentType is "contract" or "part_time") role.Flags.Add(p.EmploymentType == "contract" ? "Contract" : "Part time");

        var top = p.SalaryMax ?? p.SalaryMin;
        if (top is null)
        {
            if (hard.UnknownSalary == "drop") return "Pay not posted";
            role.Flags.Add("Pay not posted");
        }
        else if (top < hard.MinSalary)
            return $"Pay tops out at ${top:N0}, below ${hard.MinSalary:N0}";

        return null;
    }

    public async Task<FitScore> ScoreAsync(JobRole role)
    {
        var prompt = Prompt.Fill(scorePrompt, ("resume", resume), ("preferences", preferencesText), ("description", role.Posting!.Description));
        return Json.Parse<FitScore>(await claude.AskAsync(System, prompt, webSearch: false));
    }
}
