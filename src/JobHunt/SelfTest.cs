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
        var hard = new HardRules { MinSalary = 160000, Countries = ["US"], ExcludeTitleWords = ["junior", "data engineer"] };
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
        var companyRules = new HardRules { ExcludeCompanies = ["GitHub", "Microsoft"] };
        Check(Matcher.CompanyFilter(a with { Company = "Microsoft Corporation" }, companyRules) is not null
              && Matcher.CompanyFilter(a with { Company = "GitHub, Inc." }, companyRules) is not null, "drops excluded companies");
        Check(Matcher.CompanyFilter(a with { Company = "Microsofty Labs" }, companyRules) is null
              && Matcher.CompanyFilter(a, companyRules) is null, "doesn't drop look-alike or other companies");
        string[] levels = ["staff", "principal", "lead"];
        Check(Matcher.TitleHasWord("Staff AI Engineer", levels) && Matcher.TitleHasWord("Tech Lead, LLM Platform", levels), "spots level words in titles");
        Check(!Matcher.TitleHasWord("Senior AI Engineer", levels) && !Matcher.TitleHasWord("Senior Engineer, AI Leadership Tools", levels), "doesn't match partial words");
        Check(new FitScore { PeopleManagement = "player_coach", DirectReports = 5 }.PeopleTag() == "Player-coach (~5 direct reports)"
              && new FitScore { PeopleManagement = "people_manager" }.PeopleTag() == "People manager"
              && new FitScore { PeopleManagement = "hands_on" }.PeopleTag() is null, "tags manager-ish roles, not hands-on ones");
        var homeRules = new HardRules { HomeState = "OH" };
        JobRole Restricted(params string[] states) => new() { Job = a, Posting = new Posting { Remote = "fully_remote", SalaryMax = 200000, StateRestrictions = [.. states] } };
        Check(Matcher.HardFilter(Restricted("WI"), homeRules) is not null, "drops roles restricted to another state");
        Check(Matcher.HardFilter(Restricted("OH", "IN"), homeRules) is null && Matcher.HardFilter(Restricted(), homeRules) is null, "keeps roles open to the home state");
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

        // Pay parsing: LinkedIn's pay box and ranges in description text
        Check(LinkedInJobPage.AnnualMax("$156,750.00/yr - $215,000.00/yr") == 215000m, "reads a yearly pay box");
        Check(LinkedInJobPage.AnnualMax("$75.00/hr - $85.00/hr") == 176800m, "converts an hourly pay box to annual");
        Check(LinkedInJobPage.PayInText("The base pay range is $153K - $192K plus bonus.") == 192000m, "reads a K range in text");
        Check(LinkedInJobPage.PayInText("Salary: $130,000 to $145,000 depending on experience") == 145000m, "reads a 'to' range in text");
        Check(LinkedInJobPage.PayInText("We raised a $5M Series A and offer great benefits.") is null, "ignores a lone dollar amount");
        Check(LinkedInJobPage.PayInText("Pay: $130,000-$145,000 K plus benefits") == 145000m, "ignores a stray K after a full number");
        Check(LinkedInJobPage.AnnualMax("$250,000,000 - $900,000,000") is null, "treats absurd amounts as unknown");
        var boxPage = LinkedInJobPage.Parse($"<div class=\"compensation__salary-range\"><h3 class=\"compensation__heading\">Base pay range</h3><div class=\"salary compensation__salary\"> $120,000.00/yr - $150,000.00/yr </div></div>{desc}");
        Check(boxPage.SalaryMax == 150000m && boxPage.Description.Contains("LinkedIn pay range"), "reads the pay box on a job page");

        // LinkedIn search results (public search card markup as of Sep 2026)
        var results = LinkedInSearch.ParseResults("""
            <li><div class="base-card" data-entity-urn="urn:li:jobPosting:4471000001">
              <h3 class="base-search-card__title"> Principal Software Engineer </h3>
              <h4 class="base-search-card__subtitle"><a class="hidden-nested-link"> Example Robotics </a></h4>
              <span class="job-search-card__location"> United States </span><time datetime="2026-09-24"> 3 hours ago </time></div></li>
            <li><div class="base-card" data-entity-urn="urn:li:jobPosting:4471000002">
              <h3 class="base-search-card__title">Lead .NET Engineer</h3>
              <h4 class="base-search-card__subtitle"><a class="hidden-nested-link">Contoso Health</a></h4>
              <span class="job-search-card__location">Austin, TX</span></div></li>
            """);
        Check(results.Count == 2 && results[0].Job == new AlertJob("4471000001", "Principal Software Engineer", "Example Robotics", "United States"),
              "parses LinkedIn search result cards");
        Check(results[0].Age == "3 hours ago", "reads the posting age from a search card");
        Check(LinkedInSearch.IsWithin("3 hours ago", 24) && LinkedInSearch.IsWithin("14 minutes ago", 24) && LinkedInSearch.IsWithin("Just now", 24) && LinkedInSearch.IsWithin("2 days ago", 72),
              "keeps results from the past day");
        Check(!LinkedInSearch.IsWithin("1 day ago", 24) && !LinkedInSearch.IsWithin("2 weeks ago", 24) && !LinkedInSearch.IsWithin(null, 24),
              "drops results older than a day, or with no age");

        // Queue round-trips through JSON, keeping the alert date
        var queuePath = Path.Combine(Path.GetTempPath(), $"jobhunt-queue-{Guid.NewGuid():N}.json");
        var dated = results[1].Job with { AlertDate = new DateTimeOffset(2026, 9, 24, 13, 0, 0, TimeSpan.Zero) };
        new JobQueue(queuePath).Save([dated]);
        var reloaded = new JobQueue(queuePath).Items;
        File.Delete(queuePath);
        Check(reloaded.Count == 1 && reloaded[0] == dated, "saves and reloads the queue");

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
