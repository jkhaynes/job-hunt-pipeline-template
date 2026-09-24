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
}

public class LinkedInRules
{
    public bool CheckJobPage { get; set; }
}

public class HardRules
{
    public string Remote { get; set; } = "fully_remote";
    public bool UsOnly { get; set; } = true;
    public string? HomeState { get; set; }
    public int MinSalary { get; set; }
    public string UnknownSalary { get; set; } = "keep";
    public List<string> ExcludeTitleWords { get; set; } = new();
    public List<string> Agencies { get; set; } = new();
}

public class ScoringRules
{
    public int MinScoreToResearch { get; set; } = 60;
    public int NearMissMin { get; set; } = 50;
    public int MaxRolesPerDigest { get; set; } = 12;
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
}

public class FitScore
{
    public int Score { get; set; }
    public string? StackMatch { get; set; }
    public List<string> WhyFit { get; set; } = new();
    public List<string> Gaps { get; set; } = new();
    public List<string> RedFlags { get; set; } = new();
    public string? OneLine { get; set; }
}

public class Contact
{
    public string? Name { get; set; }
    public string? Title { get; set; }
    public string? Role { get; set; }
    public string? Linkedin { get; set; }
    public string? Email { get; set; }
    public string? Source { get; set; }
    public string? Confidence { get; set; }
}

public class LoopDetail
{
    public string? Detail { get; set; }
    public string? Source { get; set; }
}

public class Research
{
    public List<Contact> Contacts { get; set; } = new();
    public List<LoopDetail> InterviewLoop { get; set; } = new();
}

/// <summary>Everything the pipeline learned about one alert job.</summary>
public class JobRole
{
    public required AlertJob Job { get; init; }
    public Posting? Posting { get; set; }
    public FitScore? Fit { get; set; }
    public Research? Research { get; set; }
    public Contact? DraftTo { get; set; }
    public string? Applicants { get; set; }
    public string? Draft { get; set; }
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
