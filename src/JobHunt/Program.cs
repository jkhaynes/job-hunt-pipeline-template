using JobHunt;
using MimeKit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

// Usage (run from the repo root):
//   dotnet run --project src/JobHunt -- [--out out/] [--dry-run] [--from-fixtures dir] [--limit N]
//   dotnet run --project src/JobHunt -- --auth path/to/client_secret.json
//   dotnet run --project src/JobHunt -- --self-test
//   dotnet run --project src/JobHunt -- --demo             (sample digest, no setup needed)
string? Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
bool Flag(string name) => args.Contains(name);

if (Flag("--self-test")) { SelfTest.Run(); return 0; }
if (Flag("--demo"))
{
    Directory.CreateDirectory("out");
    File.WriteAllText("out/digest-demo.html", Demo.Render(File.ReadAllText("templates/digest.html")));
    Console.WriteLine("Wrote out/digest-demo.html (fictional roles; open it in a browser)");
    return 0;
}
if (Arg("--auth") is { } clientJson) { await GmailClient.PrintRefreshTokenAsync(clientJson); return 0; }
if (Arg("--linkedin-check") is { } ids)
{
    // Checks LinkedIn's public job page parsing for comma-separated job IDs. No Claude, no Gmail.
    foreach (var id in ids.Split(','))
    {
        var page = await LinkedInJobPage.GetAsync(id.Trim());
        Console.WriteLine(page is null ? $"{id} | unavailable"
            : $"{id} | closed={page.Closed} | easyApply={page.EasyApply?.ToString() ?? "unknown"} | {page.Applicants} | posted {page.Posted} | description {page.Description.Length} chars");
    }
    return 0;
}
if (Arg("--parse-only") is { } parseDir)
{
    // Checks the alert parser against saved .eml files. No Claude, no Gmail.
    foreach (var f in Directory.GetFiles(parseDir, "*.eml"))
        foreach (var j in AlertParser.Parse(MimeMessage.Load(f).HtmlBody ?? ""))
            Console.WriteLine($"{Path.GetFileName(f)} | {j.LinkedInId} | {j.Title} | {j.Company} | {j.Location}");
    return 0;
}

var outDir = Arg("--out") ?? "out";
var fixtures = Arg("--from-fixtures");
var dryRun = Flag("--dry-run");
var limit = int.TryParse(Arg("--limit"), out var n) ? n : int.MaxValue;
var today = DateTime.Now.ToString("yyyy-MM-dd");

// Config
var yaml = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).IgnoreUnmatchedProperties().Build();
var criteria = yaml.Deserialize<Criteria>(File.ReadAllText("config/criteria.yaml"));
var preferences = new SerializerBuilder().Build().Serialize(criteria.Preferences);
var resume = File.ReadAllText("config/resume.md");
string ReadPrompt(string name) => File.ReadAllText($"prompts/{name}.md");

var claude = new ClaudeClient();
var limitsBefore = await ClaudeClient.LimitsAsync();
var boards = new JobBoards("data/boards.json");
var resolver = new PostingResolver(claude, boards, ReadPrompt("resolve"), ReadPrompt("extract"));
var matcher = new Matcher(claude, resume, preferences, ReadPrompt("score"));
var researcher = new Researcher(claude, resume, ReadPrompt("research"), ReadPrompt("draft"));
var seen = new SeenStore("data/seen.json");
GmailClient? gmail = fixtures is null ? new GmailClient() : null;

// Stage 1: pull alerts
var alerts = fixtures is not null
    ? Directory.GetFiles(fixtures, "*.eml").Select(f => MimeMessage.Load(f)).Select((m, i) => (Id: fixtures + i, Html: m.HtmlBody ?? "", m.Date)).ToList()
    : await gmail!.UnreadAlertsAsync();
Console.WriteLine($"{alerts.Count} alert emails");

// Dedupe before spending any API calls (across runs and within this run)
var jobs = new List<AlertJob>();
foreach (var job in alerts.SelectMany(a => AlertParser.Parse(a.Html).Select(j => j with { AlertDate = a.Date })))
{
    if (seen.IsSeen(job)) continue;
    seen.Add(job);
    jobs.Add(job);
}
Console.WriteLine($"{jobs.Count} new jobs");
jobs = jobs.Take(limit).ToList();

// ponytail: roles run one at a time. Parallelize with Task.WhenAll + a SemaphoreSlim if runs get slow.
var kept = new List<JobRole>();       // scored at or above the research bar: full cards
var nearMisses = new List<JobRole>(); // scored just under it
var unverified = new List<JobRole>(); // no confident official posting: check by hand
var filtered = new List<JobRole>();
foreach (var job in jobs)
{
    var role = new JobRole { Job = job };
    Console.WriteLine($"- {job.Title} at {job.Company}");
    try
    {
        // Title rule first: it needs no lookup, so rejected titles cost nothing.
        role.FilterReason = Matcher.TitleFilter(job, criteria.Hard);
        if (role.FilterReason is not null) { filtered.Add(role); continue; }

        // LinkedIn's public job page (opt-in): closed jobs stop here, before any lookup or usage.
        LinkedInJob? linkedIn = null;
        if (criteria.Linkedin.CheckJobPage)
        {
            linkedIn = await LinkedInJobPage.GetAsync(job.LinkedInId);
            if (linkedIn?.Closed == true)
            {
                role.FilterReason = "Closed on LinkedIn (no longer accepting applications)";
                filtered.Add(role);
                continue;
            }
            if (linkedIn?.EasyApply == true) role.Flags.Add("Easy Apply");
            role.Applicants = linkedIn?.Applicants;
        }

        // Agency reposts and Easy Apply roles rarely have a findable original posting: they get the
        // free job-board check (some agencies, like Jobgether, post on Lever) but not the Claude search.
        var isAgency = Matcher.IsAgency(job, criteria.Hard);
        if (isAgency) role.Flags.Add("Agency repost");

        // Stage 1: find the real posting, hard filters, score
        role.Posting = await resolver.ResolveAsync(job, allowSearch: !isAgency && linkedIn?.EasyApply != true);

        // No usable official posting: fall back to LinkedIn's own description when we have it.
        var usable = role.Posting is not null && PostingResolver.IsConfident(role.Posting)
                     && (role.Posting.Description?.Trim().Length ?? 0) >= 300;
        if (!usable && linkedIn?.Description.Length >= 300)
        {
            Console.WriteLine("  using LinkedIn's description");
            role.Posting = await resolver.FromDescriptionAsync(job, job.LinkedInUrl, "linkedin", linkedIn.Description,
                linkedIn.Posted is { } ago ? $"{ago} (LinkedIn)" : null);
            role.Flags.Add("Description from LinkedIn");
        }

        if (role.Posting is null || !PostingResolver.IsConfident(role.Posting))
        {
            unverified.Add(role);
            continue;
        }
        // A posting with no real description can't be scored fairly (usually closed or login-walled).
        if ((role.Posting.Description?.Trim().Length ?? 0) < 300)
        {
            role.Posting.Reason = "Found the posting but not its description (it may be closed)";
            unverified.Add(role);
            continue;
        }

        role.FilterReason = Matcher.HardFilter(role, criteria.Hard);
        if (role.FilterReason is null)
        {
            role.Fit = await matcher.ScoreAsync(role);
            Console.WriteLine($"  score {role.Fit.Score}");
            if (role.Fit.Score >= criteria.Scoring.MinScoreToResearch)
                await researcher.ResearchAsync(role); // Stage 2
            else
                role.FilterReason = $"Score {role.Fit.Score}: {role.Fit.OneLine}";
        }
    }
    catch (Exception ex)
    {
        // Skip this role and log it rather than crashing the whole run.
        Console.WriteLine($"  error: {ex.Message}");
        role.FilterReason = $"Error: {ex.Message}";
    }

    if (role.FilterReason is null) kept.Add(role);
    else if (role.Fit?.Score >= criteria.Scoring.NearMissMin) nearMisses.Add(role);
    else filtered.Add(role);
}

kept = kept.OrderByDescending(r => r.Fit!.Score).ToList();
foreach (var r in kept.Skip(criteria.Scoring.MaxRolesPerDigest))
{
    r.FilterReason = $"Over the daily cap (score {r.Fit!.Score})";
    filtered.Add(r);
}
kept = kept.Take(criteria.Scoring.MaxRolesPerDigest).ToList();

// Stage 3: digest
nearMisses = nearMisses.OrderByDescending(r => r.Fit!.Score).ToList();
var summary = (kept.Count == 0
        ? $"{jobs.Count} new roles, none cleared the bar today."
        : $"{jobs.Count} new roles, {kept.Count} worth a look, {kept.Count(r => r.Fit!.Score >= 80)} above 80. " +
          $"Top: {kept[0].Job.Company} {kept[0].Job.Title} ({kept[0].Fit!.Score}).") +
    $" {nearMisses.Count} near misses, {unverified.Count} to check by hand, {filtered.Count} filtered out.";
Directory.CreateDirectory(outDir);
var digestPath = Path.Combine(outDir, $"digest-{today}.html");
File.WriteAllText(digestPath, DigestRenderer.Render(File.ReadAllText("templates/digest.html"), today, summary, kept, nearMisses, unverified, filtered));
var usage = claude.UsageSummary() + "\n" + ClaudeClient.LimitsSummary(limitsBefore, await ClaudeClient.LimitsAsync());
Console.WriteLine($"Wrote {digestPath}\n{summary}\n{usage}");

if (dryRun || gmail is null)
{
    Console.WriteLine("Dry run or fixtures: not sending, not marking read, not saving seen.json");
    return 0;
}

// On empty days this sends a one-liner with no attachment, so you know the job ran.
var worthOpening = kept.Count + nearMisses.Count + unverified.Count > 0;
await gmail.SendDigestAsync($"Job digest {today}: {kept.Count} roles", summary + "\n\n" + usage, worthOpening ? digestPath : "");
await gmail.MarkReadAsync(alerts.Select(a => a.Id));
seen.Save();
boards.Save();
return 0;
