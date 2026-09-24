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
                Url = "https://jobs.lever.co/example-robotics/demo", Ats = "lever", PostedDate = "2026-09-21",
                Remote = "fully_remote", SalaryMin = 175000, SalaryMax = 205000,
                StatusCheckUrl = "https://jobs.lever.co/example-robotics/demo/apply",
            },
            Fit = new FitScore
            {
                Score = 86, StackMatch = "strong",
                OneLine = "Senior backend role building the fleet-telemetry APIs on .NET 10, Azure, and SQL Server.",
                WhyFit = [
                    "Eight years of C#/.NET API work matches the core stack exactly",
                    "Led a telemetry ingestion rewrite that cut p95 latency 60%, the same problem space",
                    "Tech lead experience fits the role's mentoring and design-review scope",
                ],
                Gaps = ["Minor: no Kafka listed, though the resume shows Azure Service Bus messaging"],
            },
            Research = new Research
            {
                Contacts = [
                    new Contact { Name = "Sam Rivera", Title = "Technical Recruiter", Role = "recruiter", Linkedin = "https://www.linkedin.com/", Source = "https://example.com/team", Confidence = "high" },
                    new Contact { Name = "Priya Nair", Title = "Engineering Manager, Platform", Role = "hiring_manager", Linkedin = "https://www.linkedin.com/", Source = "https://example.com/blog", Confidence = "low" },
                ],
                InterviewLoop = [new LoopDetail { Detail = "Recruiter screen, then a system design round and a pairing session (per the engineering blog)", Source = "https://example.com/blog/hiring" }],
            },
            Draft = "Hi Sam, I saw the Senior Backend Engineer role on the telemetry team. I led a rewrite of our ingestion APIs in .NET that cut p95 latency by 60%, which sounds close to what this team is building. Is the role focused on the ingestion side or the query APIs?",
            Applicants = "46 applicants",
        };
        lead.DraftTo = lead.Research.Contacts[0];

        var second = new JobRole
        {
            Job = new AlertJob("1000000002", "Staff Software Engineer, Platform", "Contoso Health", "Austin, TX (Remote)"),
            Posting = new Posting { Url = "https://www.linkedin.com/jobs/view/1000000002/", Remote = "fully_remote", PostedDate = "2 days ago (LinkedIn)" },
            Fit = new FitScore
            {
                Score = 72, StackMatch = "partial",
                OneLine = "Staff platform role on a C# and Go stack, owning shared services for a healthcare SaaS product.",
                WhyFit = ["Deep C#/.NET service experience", "Owned shared auth and logging libraries used by six teams"],
                Gaps = ["Minor: Go is a secondary language here", "Major: no HIPAA or healthcare compliance experience shown"],
                RedFlags = ["Quarterly travel to Austin"],
            },
            Applicants = "Over 200 applicants",
        };
        second.Flags.AddRange(["Easy Apply", "Pay not posted", "Description from LinkedIn"]);

        var near = new JobRole
        {
            Job = new AlertJob("1000000004", "Senior Software Engineer", "Northwind Logistics", "United States (Remote)"),
            Posting = new Posting { Url = "https://boards.greenhouse.io/northwind/jobs/demo" },
            Fit = new FitScore { Score = 55, OneLine = "Senior role on a mostly Python and Airflow data platform.", Gaps = ["Major: primary language is Python"] },
        };
        var check = new JobRole
        {
            Job = new AlertJob("1000000005", "Senior .NET Engineer", "Tailspin Recruiting", "United States (Remote)"),
            Posting = new Posting { Reason = "Not on a public job board (search skipped)" },
        };
        check.Flags.Add("Agency repost");

        var filtered = new List<JobRole>
        {
            new() { Job = new AlertJob("1000000003", "Junior .NET Developer", "Fabrikam Finance", "Chicago, IL (Hybrid)"), FilterReason = "Title contains \"junior\"" },
            new() { Job = new AlertJob("1000000006", "Senior C# Developer", "Adventure Works", "United States (Remote)"), FilterReason = "Closed on LinkedIn (no longer accepting applications)" },
            new() { Job = new AlertJob("1000000007", "Lead .NET Engineer", "Wide World Importers", "United States (Remote)"), FilterReason = "Pay tops out at $140,000, below $150,000" },
        };

        const string summary = "7 new roles, 2 worth a look, 1 above 80. Top: Example Robotics Senior Backend Engineer (.NET) (86). 1 near misses, 1 to check by hand, 3 filtered out.";
        return DigestRenderer.Render(template, "demo", summary, [lead, second], [near], [check], filtered);
    }
}
