namespace JobHunt;

/// <summary>Fictional roles for `--demo`: renders a sample digest with no Claude, Gmail, or setup.</summary>
public static class Demo
{
    public static string Render(string template)
    {
        var lead = new JobRole
        {
            Job = new AlertJob("1000000001", "Senior Backend Engineer (.NET)", "Example Robotics", "United States (Remote)"),
            Posting = new Posting
            {
                Url = "https://www.linkedin.com/jobs/view/1000000001/", PostedDate = "3 hours ago (LinkedIn)",
                Remote = "fully_remote", SalaryMin = 170000, SalaryMax = 200000,
                CompanyUrl = "https://jobs.lever.co/example-robotics/demo",
            },
            Fit = new FitScore
            {
                Score = 86, StackMatch = "strong",
                OneLine = "Senior backend role building the fleet-telemetry APIs on .NET, Azure, and SQL Server.",
                DayToDay = "Designing and building high-throughput telemetry APIs, reviewing code, and owning the service's reliability and on-call runbooks.",
                MustHaves =
                [
                    new MustHave { Item = "6+ years building backend services in C#/.NET", Met = "yes" },
                    new MustHave { Item = "SQL Server or PostgreSQL at scale", Met = "yes" },
                    new MustHave { Item = "Cloud experience (Azure preferred)", Met = "yes" },
                    new MustHave { Item = "Event streaming (Kafka or similar)", Met = "partial" },
                ],
                WhyFit =
                [
                    "Eight years of C#/.NET API work matches the core stack exactly",
                    "Led a telemetry ingestion rewrite that cut p95 latency 60%, the same problem space",
                    "Tech lead experience fits the role's design-review scope",
                ],
                Gaps = ["Minor: no Kafka listed, though the resume shows Azure Service Bus messaging"],
            },
            Applicants = "46 applicants",
        };

        var second = new JobRole
        {
            Job = new AlertJob("1000000002", "Manager, Software Engineering", "Contoso Health", "Austin, TX (Remote)"),
            Posting = new Posting { Url = "https://www.linkedin.com/jobs/view/1000000002/", Remote = "fully_remote", PostedDate = "7 hours ago (LinkedIn)" },
            Fit = new FitScore
            {
                Score = 78, StackMatch = "strong",
                OneLine = "Player-coach manager for a five-person .NET team on a healthcare scheduling product.",
                DayToDay = "Leading a small team through planning and code review while still building features in C# and Angular.",
                MustHaves =
                [
                    new MustHave { Item = "Experience leading an engineering team", Met = "yes" },
                    new MustHave { Item = "C#/.NET and a modern frontend framework", Met = "yes" },
                    new MustHave { Item = "Healthcare or HIPAA experience", Met = "no" },
                ],
                WhyFit = ["Current team lead of four engineers", "Full-stack .NET and Angular experience"],
                Gaps = ["Major: no healthcare or HIPAA experience shown"],
                RedFlags = ["Quarterly travel to Austin"],
            },
            Applicants = "Over 200 applicants",
        };
        second.Flags.AddRange(["Player-coach (~5 direct reports)", "Easy Apply", "Pay not posted"]);

        var near = new JobRole
        {
            Job = new AlertJob("1000000004", "Senior Software Engineer", "Northwind Logistics", "United States (Remote)"),
            Posting = new Posting { Url = "https://www.linkedin.com/jobs/view/1000000004/" },
            Fit = new FitScore { Score = 55, OneLine = "Senior role on a mostly Python and Airflow data platform.", Gaps = ["Major: primary language is Python"] },
        };

        var filtered = new List<JobRole>
        {
            new() { Job = new AlertJob("1000000003", "Junior .NET Developer", "Fabrikam Finance", "Chicago, IL"), FilterReason = "Title contains \"junior\"" },
            new() { Job = new AlertJob("1000000006", "Senior C# Developer", "Adventure Works", "United States (Remote)"), FilterReason = "Closed on LinkedIn (no longer accepting applications)" },
            new() { Job = new AlertJob("1000000007", "Lead .NET Engineer", "Wide World Importers", "United States (Remote)"), FilterReason = "Posted pay tops out at $140,000, below $150,000" },
            new() { Job = new AlertJob("1000000008", "Senior Software Engineer", "Tailspin Toys", "Denver, CO"), FilterReason = "Not fully remote (hybrid)" },
        };
        var queued = new List<AlertJob> { new("1000000009", "Staff Software Engineer", "Litware", "United States (Remote)") };

        const string summary = "8 new roles, 2 worth a look, 1 above 80. Top: Example Robotics Senior Backend Engineer (.NET) (86). " +
                               "1 near misses, 0 to check by hand, 4 filtered out, 1 queued for next run. Searches read 50 deep.";
        return DigestRenderer.Render(template, "demo", summary, [lead, second], [near], [], filtered, queued);
    }
}
