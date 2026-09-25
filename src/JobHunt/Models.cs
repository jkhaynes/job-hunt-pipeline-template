using System.Text.Json;
using System.Text.Json.Serialization;

namespace JobHunt;

public record AlertJob(string LinkedInId, string Title, string Company, string Location)
{
    public string LinkedInUrl => $"https://www.linkedin.com/jobs/view/{LinkedInId}/";

    /// <summary>When the alert email arrived; a hint for picking the right posting.</summary>
    public DateTimeOffset? AlertDate { get; init; }
}

public class Criteria
{
    public HardRules Hard { get; set; } = new();
    public Dictionary<string, object> Preferences { get; set; } = new();
    public ScoringRules Scoring { get; set; } = new();
    public LinkedInRules Linkedin { get; set; } = new();
    public ModelChoices Models { get; set; } = new();
    public GmailSettings Gmail { get; set; } = new();
    /// <summary>Digest heading and email subject, so each profile's digest is easy to tell apart.</summary>
    public string DigestTitle { get; set; } = "Job digest";
}

public class GmailSettings
{
    /// <summary>The Gmail label your job alert emails get (set up with a Gmail filter). Empty: don't read alert emails.</summary>
    public string AlertsLabel { get; set; } = "JobAlerts";
}

public class ModelChoices
{
    public string Scoring { get; set; } = "claude-sonnet-5";
    public string TitleCheck { get; set; } = "claude-haiku-4-5";
}

public class LinkedInRules
{
    public bool CheckJobPage { get; set; }
    public bool RunSearches { get; set; }
    public List<string> SkipSeniority { get; set; } = new();
    public int PostedWithinHours { get; set; } = 24;
    public string GeoId { get; set; } = "103644278";
    public int MaxResultsPerSearch { get; set; } = 250;
    public List<string> Searches { get; set; } = new();
}

public class HardRules
{
    public string Remote { get; set; } = "fully_remote";
    /// <summary>Countries you can work from. A posting limited to other countries is filtered; empty = no country rule.</summary>
    public List<string> Countries { get; set; } = new();
    public string? HomeState { get; set; }
    public int MinSalary { get; set; }
    public string UnknownSalary { get; set; } = "keep";
    public List<string> ExcludeTitleWords { get; set; } = new();
    public List<string> ExcludeCompanies { get; set; } = new();
    public List<string> Agencies { get; set; } = new();
}

public class ScoringRules
{
    public int MinScore { get; set; } = 60;
    public int NearMissMin { get; set; } = 50;
    public decimal RunBudgetUsd { get; set; } = 10;
    public int MaxRunMinutes { get; set; } = 45;
    public int ParallelCalls { get; set; } = 4;
}

public class Posting
{
    public string? Url { get; set; }
    public string? Ats { get; set; }
    public string? Confidence { get; set; }
    public string? Reason { get; set; }
    public string? PostedDate { get; set; }
    public string? Remote { get; set; }
    public List<string> Countries { get; set; } = new();
    public List<string> StateRestrictions { get; set; } = new();
    public string? EmploymentType { get; set; }
    public decimal? SalaryMin { get; set; }
    public decimal? SalaryMax { get; set; }

    // ponytail: anything under 1,000 is read as an hourly rate. A band quoted in thousands ("175")
    // would be misread; the resolve prompt asks for full annual numbers to avoid that.
    public void NormalizePay()
    {
        static decimal? Annual(decimal? v) => v is > 0 and < 1000 ? v * 2080 : v;
        (SalaryMin, SalaryMax) = (Annual(SalaryMin), Annual(SalaryMax));
    }
    public string? Description { get; set; }
    public string? StatusCheckUrl { get; set; }
    /// <summary>The company's own posting (Greenhouse, Lever, or Ashby) when found; cards link to it.</summary>
    public string? CompanyUrl { get; set; }
}

public class FitScore
{
    public int Score { get; set; }
    public string? StackMatch { get; set; }
    public List<string> WhyFit { get; set; } = new();
    public List<string> Gaps { get; set; } = new();
    public List<string> RedFlags { get; set; } = new();
    public string? OneLine { get; set; }
    /// <summary>Model's estimate of the top of the pay band when the posting doesn't state pay.</summary>
    public decimal? EstimatedPayMax { get; set; }
    public List<MustHave> MustHaves { get; set; } = new();
    public string? DayToDay { get; set; }
    /// <summary>"hands_on", "player_coach", or "people_manager", judged from the description.</summary>
    public string? PeopleManagement { get; set; }
    public int? DirectReports { get; set; }

    /// <summary>Card tag for manager-ish roles; null for hands-on roles (no tag, to keep cards clean).</summary>
    public string? PeopleTag() => PeopleManagement switch
    {
        "player_coach" => "Player-coach" + Reports(),
        "people_manager" => "People manager" + Reports(),
        _ => null,
    };

    string Reports() => DirectReports is > 0 and var n ? $" (~{n} direct reports)" : "";

    /// <summary>"flexible" when the posting says its specific stack can be learned; "specific" or "unknown" otherwise.</summary>
    public string? StackFlexibility { get; set; }
    /// <summary>"core" when AI is the product itself; "adjacent" or "none" otherwise.</summary>
    public string? AiRole { get; set; }

    /// <summary>Card tags read from the posting, beyond the people tag.</summary>
    public IEnumerable<string> PostingTags()
    {
        if (StackFlexibility == "flexible") yield return "Stack-flexible";
        if (AiRole == "core") yield return "AI is the product";
    }

    // Facts read from the description in the same call (so there's no separate extraction call).
    public string? Remote { get; set; }
    public List<string> Countries { get; set; } = new();
    public List<string> StateRestrictions { get; set; } = new();
    public string? EmploymentType { get; set; }
    public decimal? SalaryMin { get; set; }
    public decimal? SalaryMax { get; set; }

    public void CopyFactsTo(Posting p)
    {
        (p.Remote, p.Countries, p.StateRestrictions, p.EmploymentType, p.SalaryMin, p.SalaryMax) =
            (Remote, Countries, StateRestrictions, EmploymentType, SalaryMin, SalaryMax);
        p.NormalizePay();
    }
}

public class MustHave
{
    public string? Item { get; set; }
    /// <summary>"yes", "partial", or "no", judged against the resume.</summary>
    public string? Met { get; set; }
}

/// <summary>Everything the pipeline learned about one alert job.</summary>
public class JobRole
{
    public required AlertJob Job { get; init; }
    public Posting? Posting { get; set; }
    public FitScore? Fit { get; set; }
    public string? Applicants { get; set; }
    public string? FilterReason { get; set; }
    public List<string> Flags { get; } = new();
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        WriteIndented = true,
    };

    /// <summary>Pulls the JSON object out of a model reply: strips fences and any prose around it.</summary>
    public static T Parse<T>(string reply)
    {
        int start = reply.IndexOf('{'), end = reply.LastIndexOf('}');
        if (start < 0 || end < start) throw new JsonException("No JSON object in reply: " + Trim(reply));
        return JsonSerializer.Deserialize<T>(reply[start..(end + 1)], Options)
               ?? throw new JsonException("Null JSON in reply");
    }

    static string Trim(string s) => s.Length > 200 ? s[..200] + "..." : s;
}
