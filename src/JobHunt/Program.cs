using System.Diagnostics;
using JobHunt;
using MimeKit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

// Usage (run from the repo root):
//   dotnet run --project src/JobHunt -- [--out out/] [--dry-run] [--from-fixtures dir] [--limit N]
//   dotnet run --project src/JobHunt -- --auth path/to/client_secret.json
//   dotnet run --project src/JobHunt -- --self-test
//   dotnet run --project src/JobHunt -- --searches-only --limit 20   (full flow on LinkedIn searches, no Gmail, dry run)
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
            : $"{id} | closed={page.Closed} | easyApply={page.EasyApply?.ToString() ?? "unknown"} | {page.Applicants} | posted {page.Posted} | " +
              $"pay box: {page.SalaryText ?? "none"} (max {page.SalaryMax?.ToString("N0") ?? "-"}) | pay in text max: {LinkedInJobPage.PayInText(page.Description)?.ToString("N0") ?? "-"} | description {page.Description.Length} chars");
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
var searchesOnly = Flag("--searches-only"); // local testing without Gmail: LinkedIn searches only, always a dry run
var dryRun = Flag("--dry-run") || searchesOnly;
var limit = int.TryParse(Arg("--limit"), out var n) ? n : int.MaxValue;
var today = DateTime.Now.ToString("yyyy-MM-dd");

// Config
var yaml = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).IgnoreUnmatchedProperties().Build();
var criteria = yaml.Deserialize<Criteria>(File.ReadAllText("config/criteria.yaml"));
var preferences = new SerializerBuilder().Build().Serialize(criteria.Preferences);
var resume = File.ReadAllText("config/resume.md");
string ReadPrompt(string name) => File.ReadAllText($"prompts/{name}.md");

var claude = new ClaudeClient(criteria.Models.Scoring);
var limitsBefore = await ClaudeClient.LimitsAsync();
var boards = new JobBoards("data/boards.json");
var resolver = new PostingResolver(claude, boards, ReadPrompt("resolve"), ReadPrompt("extract"));
var matcher = new Matcher(claude, resume, preferences, ReadPrompt("score"));
var seen = new SeenStore("data/seen.json");
GmailClient? gmail = fixtures is null && !searchesOnly ? new GmailClient() : null;

var queue = new JobQueue("data/queue.json");
var run = Stopwatch.StartNew();
var floor = criteria.Hard.MinSalary;

// ---- Sources: queued roles first, then alert emails, then full LinkedIn searches (opt-in) ----
var alerts = searchesOnly ? [] : fixtures is not null
    ? Directory.GetFiles(fixtures, "*.eml").Select(f => MimeMessage.Load(f)).Select((m, i) => (Id: fixtures + i, Html: m.HtmlBody ?? "", m.Date)).ToList()
    : await gmail!.UnreadAlertsAsync(criteria.Gmail.AlertsLabel);
var incoming = alerts.SelectMany(a => AlertParser.Parse(a.Html).Select(j => j with { AlertDate = a.Date })).ToList();
Console.WriteLine($"{alerts.Count} alert emails, {incoming.Count} jobs in them, {queue.Items.Count} queued from last run");
var searchResults = new List<List<AlertJob>>();
if (criteria.Linkedin.RunSearches && fixtures is null)
    foreach (var search in criteria.Linkedin.Searches)
    {
        var found = await LinkedInSearch.RunAsync(search, criteria.Linkedin, remoteOnly: criteria.Hard.Remote == "fully_remote");
        Console.WriteLine($"search: {found.Jobs.Count} from the past 24h in the top {criteria.Linkedin.MaxResultsPerSearch} ({found.DroppedAsOld} older dropped): {search}");
        searchResults.Add(found.Jobs);
    }
// Interleave by rank (#1 of each search, then #2 of each, and so on), so if a run hits its budget or
// time limit, the leftovers are every search's lower-ranked results, not all of the last search.
for (var rank = 0; rank < searchResults.Select(r => r.Count).DefaultIfEmpty(0).Max(); rank++)
    foreach (var results in searchResults.Where(r => rank < r.Count))
        incoming.Add(results[rank]);

// Dedupe across runs (seen.json) and within this run (same job ID, or same company + title).
var jobs = new List<AlertJob>();
var runIds = new HashSet<string>();
var runKeys = new HashSet<string>();
foreach (var job in queue.Items.Concat(incoming))
    if (!seen.IsSeen(job) && runIds.Add(job.LinkedInId) && runKeys.Add(SeenStore.Key(job)))
        jobs.Add(job);
var overLimit = jobs.Skip(limit).ToList(); // --limit is for local testing; the rest go back in the queue
jobs = jobs.Take(limit).ToList();
Console.WriteLine($"{jobs.Count} new roles to consider");

var kept = new List<JobRole>();       // scored at or above the bar: full cards
var nearMisses = new List<JobRole>(); // scored just under it
var unverified = new List<JobRole>(); // no usable posting: check by hand
var filtered = new List<JobRole>();
var queued = new List<AlertJob>(overLimit);
var sync = new object();
void Add(List<JobRole> list, JobRole role) { lock (sync) list.Add(role); }
void Queue(AlertJob job) { lock (sync) queued.Add(job); }

// ---- Free filters: title words and excluded companies ----
var survivors = new List<AlertJob>();
foreach (var job in jobs)
{
    var reason = Matcher.TitleFilter(job, criteria.Hard) ?? Matcher.CompanyFilter(job, criteria.Hard);
    if (reason is null) survivors.Add(job);
    else filtered.Add(new JobRole { Job = job, FilterReason = reason });
}

// ---- Cheap title check (only worth it at full-search volume) ----
if (criteria.Linkedin.RunSearches && survivors.Count > 0)
{
    var triage = new Triage(claude, ReadPrompt("triage"), preferences, criteria.Models.TitleCheck);
    var drops = await triage.ScreenAsync(survivors, criteria.Scoring.ParallelCalls);
    foreach (var job in survivors.Where(j => drops.ContainsKey(j.LinkedInId)))
        filtered.Add(new JobRole { Job = job, FilterReason = $"Title check: {drops[job.LinkedInId]}" });
    survivors = survivors.Where(j => !drops.ContainsKey(j.LinkedInId)).ToList();
    Console.WriteLine($"title check dropped {drops.Count}, {survivors.Count} left");
}

// ---- Per role: LinkedIn page (closed, pay), posting, hard filters, cheap score. Parallel, within budget ----
bool OutOfRoom() => run.Elapsed.TotalMinutes >= criteria.Scoring.MaxRunMinutes || claude.CostUsd >= criteria.Scoring.RunBudgetUsd;

async Task ScoreRoleAsync(AlertJob job)
{
    // ponytail: the budget is checked before a role starts, so roles already in flight can overshoot it slightly.
    if (OutOfRoom() || (criteria.Linkedin.CheckJobPage && LinkedInHttp.Stopped)) { Queue(job); return; }
    var role = new JobRole { Job = job };
    try
    {
        LinkedInJob? linkedIn = null;
        if (criteria.Linkedin.CheckJobPage)
        {
            linkedIn = await LinkedInJobPage.GetAsync(job.LinkedInId);
            if (linkedIn is null && LinkedInHttp.Stopped) { Queue(job); return; }
            if (linkedIn?.Closed == true)
            {
                role.FilterReason = "Closed on LinkedIn (no longer accepting applications)";
                Add(filtered, role);
                return;
            }
            // Pay check before any Claude usage: LinkedIn's pay box first, else a range stated in the description.
            var postedPay = linkedIn?.SalaryMax ?? LinkedInJobPage.PayInText(linkedIn?.Description);
            if (postedPay < floor)
            {
                role.FilterReason = $"Posted pay tops out at ${postedPay:N0}, below ${floor:N0}";
                Add(filtered, role);
                return;
            }
            // LinkedIn's own Seniority and Employment type fields: free level and job-type checks.
            if (linkedIn?.Seniority is { } level && criteria.Linkedin.SkipSeniority.Contains(level, StringComparer.OrdinalIgnoreCase))
            {
                role.FilterReason = $"LinkedIn lists it as {linkedIn.Seniority}";
                Add(filtered, role);
                return;
            }
            if (linkedIn?.EmploymentType is "Contract" or "Part-time" or "Temporary") role.Flags.Add(linkedIn.EmploymentType);
            if (linkedIn?.EasyApply == true) role.Flags.Add("Easy Apply");
            role.Applicants = linkedIn?.Applicants;
        }

        var isAgency = Matcher.IsAgency(job, criteria.Hard);
        if (isAgency) role.Flags.Add("Agency repost");

        if (linkedIn?.Description.Length >= 300)
        {
            // LinkedIn path: the card links to the LinkedIn job (its Apply button leads to the company's
            // posting when you're signed in). One Claude call scores the role AND pulls out remote,
            // countries, state rules, job type, and pay, which the hard rules then check.
            role.Posting = new Posting
            {
                Url = job.LinkedInUrl, Ats = "linkedin", Confidence = "high", Description = linkedIn.Description,
                PostedDate = linkedIn.Posted is { } ago ? $"{ago} (LinkedIn)" : null,
            };
            // Free: if the company posts on Greenhouse, Lever, or Ashby, score its own full posting instead.
            // LinkedIn's copy can leave out details the company's own posting states, like location restrictions.
            if (await boards.FindAsync(job) is var (board, hit) && hit.Description.Length >= 300)
            {
                var companyPay = LinkedInJobPage.PayInText(hit.Description);
                if (companyPay < floor)
                {
                    role.FilterReason = $"Posted pay tops out at ${companyPay:N0}, below ${floor:N0}";
                    Add(filtered, role);
                    return;
                }
                role.Posting.Description = hit.Description +
                    (linkedIn.SalaryText is { } box ? $"\n\nLinkedIn pay range: {box}" : "");
                role.Posting.CompanyUrl = hit.Url;
                Console.WriteLine($"- {job.Title} at {job.Company}: using the company's posting ({board.Ats})");
            }
            role.Fit = await matcher.ScoreAsync(role);
            role.Fit.CopyFactsTo(role.Posting);
        }
        else
        {
            // Email-only path (LinkedIn check off or page unavailable): find the posting first.
            role.Posting = await resolver.ResolveAsync(job, allowSearch: !isAgency && linkedIn?.EasyApply != true);
            if (role.Posting is null || !PostingResolver.IsConfident(role.Posting)) { Add(unverified, role); return; }
        }
        if ((role.Posting.Description?.Trim().Length ?? 0) < 300)
        {
            role.Posting.Reason = "Found the posting but not its description (it may be closed)";
            Add(unverified, role);
            return;
        }

        role.FilterReason = Matcher.HardFilter(role, criteria.Hard);
        if (role.FilterReason is null)
        {
            role.Fit ??= await matcher.ScoreAsync(role);
            // Manager-ish roles are kept and tagged ("Player-coach", "People manager"), never filtered by title.
            if (role.Fit.PeopleTag() is { } people) role.Flags.Add(people);
            // Option 2 for unposted pay: keep the role, but flag a low estimate and sort it lower.
            if (role.Posting.SalaryMax is null && role.Fit.EstimatedPayMax is { } est && est < floor)
                role.Flags.Add($"Pay likely below floor (est. up to ${est:N0})");
            Console.WriteLine($"- {job.Title} at {job.Company}: score {role.Fit.Score}");
            if (role.Fit.Score < criteria.Scoring.MinScore)
                role.FilterReason = $"Score {role.Fit.Score}: {role.Fit.OneLine}";
        }
    }
    catch (Exception ex)
    {
        // Skip this role and log it rather than crashing the whole run.
        Console.WriteLine($"- {job.Title} at {job.Company}: error: {ex.Message}");
        role.FilterReason = $"Error: {ex.Message}";
    }

    if (role.FilterReason is null) Add(kept, role);
    // Near misses passed every hard rule and scored just under the bar. A hard-rule failure is filtered
    // out whatever its score (the merged call scores a role before the hard rules run).
    else if (role.FilterReason.StartsWith("Score") && role.Fit?.Score >= criteria.Scoring.NearMissMin) Add(nearMisses, role);
    else Add(filtered, role);
}

using (var gate = new SemaphoreSlim(criteria.Scoring.ParallelCalls))
    await Task.WhenAll(survivors.Select(async job =>
    {
        await gate.WaitAsync();
        try { await ScoreRoleAsync(job); } finally { gate.Release(); }
    }));
Console.WriteLine($"scored in {run.Elapsed.TotalMinutes:0} min, ~${claude.CostUsd:0.00} so far; {queued.Count} queued");

// Roles with a low pay estimate sort after everything else, then by score.
static bool LowPay(JobRole r) => r.Flags.Any(f => f.StartsWith("Pay likely below floor"));
kept = kept.OrderBy(LowPay).ThenByDescending(r => r.Fit!.Score).ToList();

// ---- Digest ----
nearMisses = nearMisses.OrderByDescending(r => r.Fit!.Score).ToList();
var summary = (kept.Count == 0
        ? $"{jobs.Count} new roles, none cleared the bar today."
        : $"{jobs.Count} new roles, {kept.Count} worth a look, {kept.Count(r => r.Fit!.Score >= 80)} above 80. " +
          $"Top: {kept[0].Job.Company} {kept[0].Job.Title} ({kept[0].Fit!.Score}).") +
    $" {nearMisses.Count} near misses, {unverified.Count} to check by hand, {filtered.Count} filtered out, {queued.Count} queued for next run." +
    (criteria.Linkedin.RunSearches ? $" Searches read {criteria.Linkedin.MaxResultsPerSearch} deep." : "");
Directory.CreateDirectory(outDir);
var digestPath = Path.Combine(outDir, $"digest-{today}.html");
File.WriteAllText(digestPath, DigestRenderer.Render(File.ReadAllText("templates/digest.html"), today, summary, kept, nearMisses, unverified, filtered, queued));
var usage = claude.UsageSummary() + "\n" + ClaudeClient.LimitsSummary(limitsBefore, await ClaudeClient.LimitsAsync());
Console.WriteLine($"Wrote {digestPath}\n{summary}\n{usage}");

if (dryRun || gmail is null)
{
    Console.WriteLine("Dry run or fixtures: not sending, not marking read, not saving seen.json or the queue");
    return 0;
}

// On empty days this sends a one-liner with no attachment, so you know the job ran.
var worthOpening = kept.Count + nearMisses.Count + unverified.Count + queued.Count > 0;
await gmail.SendDigestAsync($"Job digest {today}: {kept.Count} roles", summary + "\n\n" + usage, worthOpening ? digestPath : "");
await gmail.MarkReadAsync(alerts.Select(a => a.Id));
// Everything that got an outcome is seen; queued roles aren't, so the next run picks them up first.
var queuedIds = queued.Select(j => j.LinkedInId).ToHashSet();
foreach (var job in jobs.Concat(overLimit).Where(j => !queuedIds.Contains(j.LinkedInId))) seen.Add(job);
seen.Save();
boards.Save();
queue.Save(queued);
return 0;
