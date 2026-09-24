using System.Diagnostics;

namespace JobHunt;

/// <summary>Offline checks for the logic that doesn't touch Gmail or Claude. Run with --self-test.</summary>
public static class SelfTest
{
    public static void Run()
    {
        // JSON extraction survives fences and preamble
        var fit = Json.Parse<FitScore>("Here you go:\n```json\n{ \"score\": \"82\", \"why_fit\": [\"a\"] }\n```");
        Check(fit.Score == 82 && fit.WhyFit[0] == "a", "Json.Parse strips fences and reads string numbers");

        // Dedupe key normalizes company suffixes and case
        var a = new AlertJob("1", "Senior .NET Engineer", "Acme, Inc.", "Remote");
        var b = new AlertJob("2", "senior .net engineer", "ACME", "United States");
        Check(SeenStore.Key(a) == SeenStore.Key(b), "SeenStore.Key collapses the same role");

        // Hard filters
        var hard = new HardRules { MinSalary = 175000, ExcludeTitleWords = ["junior", "data engineer"] };
        string? Filter(string title, string remote, decimal? max) =>
            Matcher.HardFilter(new JobRole { Job = a with { Title = title }, Posting = new Posting { Remote = remote, SalaryMax = max } }, hard);
        Check(Filter("Senior Engineer", "fully_remote", 200000) is null, "keeps a good role");
        Check(Filter("Junior Engineer", "fully_remote", 200000) is not null, "drops excluded title word");
        Check(Filter("Senior Engineer", "hybrid", 200000) is not null, "drops hybrid");
        Check(Filter("Senior Engineer", "fully_remote", 150000) is not null, "drops low pay");
        var unknown = new JobRole { Job = a, Posting = new Posting { Remote = "fully_remote" } };
        Check(Matcher.HardFilter(unknown, hard) is null && unknown.Flags.Contains("Pay not posted"), "keeps and flags unknown pay");
        var abroad = new JobRole { Job = a, Posting = new Posting { Remote = "fully_remote", Countries = ["PL"], SalaryMax = 200000 } };
        Check(Matcher.HardFilter(abroad, hard) is not null, "drops roles based outside the US");
        var either = new JobRole { Job = a, Posting = new Posting { Remote = "fully_remote", Countries = ["CA", "US"], SalaryMax = 200000 } };
        Check(Matcher.HardFilter(either, hard) is null, "keeps roles open to the US or another country");
        var remoteAlert = new JobRole { Job = a with { Location = "Madison, WI (Remote)" }, Posting = new Posting { Remote = "unknown", SalaryMax = 200000 } };
        Check(Matcher.HardFilter(remoteAlert, hard) is null, "uses the alert's (Remote) when the posting doesn't say");
        var hybridPosting = new JobRole { Job = a with { Location = "Chicago, IL (Remote)" }, Posting = new Posting { Remote = "hybrid", SalaryMax = 200000 } };
        Check(Matcher.HardFilter(hybridPosting, hard) is not null, "posting's own hybrid beats the alert");
        var homeRules = new HardRules { HomeState = "MI" };
        JobRole Restricted(params string[] states) => new() { Job = a, Posting = new Posting { Remote = "fully_remote", SalaryMax = 200000, StateRestrictions = [.. states] } };
        Check(Matcher.HardFilter(Restricted("WI"), homeRules) is not null, "drops roles restricted to another state");
        Check(Matcher.HardFilter(Restricted("MI", "OH"), homeRules) is null && Matcher.HardFilter(Restricted(), homeRules) is null, "keeps roles open to the home state");
        var contract = new JobRole { Job = a, Posting = new Posting { Remote = "fully_remote", SalaryMax = 200000, EmploymentType = "contract" } };
        Check(Matcher.HardFilter(contract, homeRules) is null && contract.Flags.Contains("Contract"), "keeps and flags contract roles");
        var hourly = new Posting { SalaryMin = 70, SalaryMax = 85 };
        hourly.NormalizePay();
        Check(hourly.SalaryMax == 176800 && hourly.SalaryMin == 145600, "reads hourly pay as annual (x2080)");
        var annual = new Posting { SalaryMax = 190000 };
        annual.NormalizePay();
        Check(annual.SalaryMax == 190000, "leaves annual pay alone");
        var agencyRules = new HardRules { Agencies = ["jobgether", "success recruitments"] };
        Check(Matcher.IsAgency(a with { Company = "Jobgether" }, agencyRules) && !Matcher.IsAgency(a, agencyRules), "flags agency reposts");
        Check(!PostingResolver.IsConfident(new Posting { Url = "https://x.com", Confidence = "low" })
              && PostingResolver.IsConfident(new Posting { Url = "https://x.com", Confidence = "medium" }), "low-confidence match is unverified");

        // Job board detection and title matching
        Check(JobBoards.FromUrl("https://job-boards.greenhouse.io/buyersedgeplatformrecruiting/jobs/4728280005") == new Board("greenhouse", "buyersedgeplatformrecruiting"), "reads Greenhouse board from URL");
        Check(JobBoards.FromUrl("https://jobs.lever.co/acme/1b2c-3d4e") == new Board("lever", "acme"), "reads Lever board from URL");
        Check(JobBoards.FromUrl("https://jobs.ashbyhq.com/filevine/abc-123") == new Board("ashby", "filevine"), "reads Ashby board from URL");
        Check(JobBoards.FromUrl("https://careers.cvshealth.com/job/123") is null, "ignores non-ATS URLs");
        string[] titles = ["Senior Data Engineer", "Full Stack .NET Engineer – Semantic Kernel", "Staff Engineer"];
        Check(JobBoards.MatchTitle("Full Stack .NET Engineer", titles) == 1, "matches a title with extra words");
        Check(JobBoards.MatchTitle("staff engineer", titles) == 2, "matches exact title ignoring case");
        Check(JobBoards.MatchTitle("Engineer", ["Senior Engineer", "Staff Engineer"]) is null, "refuses ambiguous matches");

        // LinkedIn public job page (markup as of Sep 2026)
        const string desc = "<div class=\"show-more-less-html__markup\"><p>We build .NET services.</p><ul><li>C#</li></ul></div>";
        var closedPage = LinkedInJobPage.Parse($"<figure class=\"closed-job\"><figcaption class=\"closed-job__flavor--closed\">No longer accepting applications</figcaption></figure>{desc}");
        Check(closedPage.Closed && closedPage.Description.Contains(".NET services"), "detects closed LinkedIn jobs");
        var easy = LinkedInJobPage.Parse($"<a data-tracking-control-name=\"public_jobs_apply-link-onsite\">Apply</a><span class=\"num-applicants__caption\"> 152 applicants </span>{desc}");
        Check(!easy.Closed && easy.EasyApply == true && easy.Applicants == "152 applicants", "detects Easy Apply and applicant count");
        Check(LinkedInJobPage.Parse($"<a data-tracking-control-name=\"public_jobs_apply-link-offsite_sign-up-modal\">Apply</a>{desc}").EasyApply == false, "detects apply-on-company-site");

        // Alert parsing, using the shape of a real Sep 2026 LinkedIn card (logo link, outer card link, inner title link)
        var jobs = AlertParser.Parse("""
            <table><tr><td>
              <a href="https://www.linkedin.com/comm/jobs/view/4468920340/?trk=x"><img alt="Buyers Edge Platform"></a>
              <a href="https://www.linkedin.com/comm/jobs/view/4468920340/?trk=y"><table><tr><td>
                <a href="https://www.linkedin.com/comm/jobs/view/4468920340/?trk=z"> Full Stack .NET Engineer </a></td></tr>
                <tr><td><p> Buyers Edge Platform · Chicago, IL (Remote) </p></td></tr>
                <tr><td>Actively recruiting</td></tr></table></a>
            </td></tr>
            <tr><td><a href="https://www.linkedin.com/comm/jobs/view/4470645365/"><table><tr><td>
                <a href="https://www.linkedin.com/comm/jobs/view/4470645365/">Senior Software Engineer</a></td></tr>
                <tr><td><p>Filevine · United States (Remote)</p></td></tr></table></a>
            </td></tr></table>
            """);
        Check(jobs.Count == 2, "parses two jobs");
        Check(jobs[0] == new AlertJob("4468920340", "Full Stack .NET Engineer", "Buyers Edge Platform", "Chicago, IL (Remote)"), "takes title from the inner link, not the card");
        Check(jobs[1] == new AlertJob("4470645365", "Senior Software Engineer", "Filevine", "United States (Remote)"), "splits company · location");

        // Rendering never emits a non-http link or raw HTML from untrusted text
        Check(!DigestRenderer.Link("javascript:alert(1)", "x").Contains("href"), "rejects javascript: links");
        Check(DigestRenderer.Link("https://x.com/a", "<b>").Contains("&lt;b&gt;"), "encodes link text");

        Console.WriteLine("Self-test passed.");
    }

    static void Check(bool ok, string what)
    {
        if (!ok) throw new UnreachableException("Self-test failed: " + what);
        Console.WriteLine("  ok  " + what);
    }
}
