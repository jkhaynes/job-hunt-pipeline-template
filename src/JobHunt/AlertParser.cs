using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace JobHunt;

public static partial class AlertParser
{
    [GeneratedRegex(@"/jobs/view/(?:[^/?""]*-)?(\d{6,})")]
    private static partial Regex JobIdRx();

    // LinkedIn's alert card (checked against a Sep 2026 email): a logo link, an outer link wrapping
    // the whole card, an inner link holding just the title, and <p>Company · Location</p>.
    // When LinkedIn changes the email, save a fresh .eml fixture, run --parse-only, and adjust here.
    public static List<AlertJob> Parse(string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        var byId = doc.QuerySelectorAll("a[href*='/jobs/view/']")
            .Select(a => (a, m: JobIdRx().Match(a.GetAttribute("href") ?? "")))
            .Where(x => x.m.Success)
            .GroupBy(x => x.m.Groups[1].Value);

        var jobs = new List<AlertJob>();
        foreach (var g in byId)
        {
            var id = g.Key;
            // The innermost link (shortest text) is the title; outer links wrap the whole card.
            var title = g.Select(x => Clean(x.a.TextContent)).Where(t => t.Length > 0).MinBy(t => t.Length);
            if (title is null) continue;

            // Widen to the largest ancestor that still only holds this one job.
            IElement box = g.First().a;
            while (box.ParentElement is { } p && OnlyJob(p, id)) box = p;

            string company, location;
            var meta = box.QuerySelectorAll("p").Select(p => Clean(p.TextContent)).FirstOrDefault(t => t.Contains(" · "));
            if (meta is not null)
            {
                var parts = meta.Split(" · ", 2);
                (company, location) = (parts[0], parts[1]);
            }
            else
            {
                var lines = TextLines(box).SkipWhile(l => l != title).Skip(1)
                    .Where(l => !l.Contains("apply", StringComparison.OrdinalIgnoreCase)).ToList();
                (company, location) = (lines.ElementAtOrDefault(0) ?? "", lines.ElementAtOrDefault(1) ?? "");
            }
            if (company.Length == 0)
                company = box.QuerySelector("img[alt]")?.GetAttribute("alt") ?? "";
            jobs.Add(new AlertJob(id, title, company, location));
        }
        return jobs;
    }

    static bool OnlyJob(IElement el, string id) =>
        el.QuerySelectorAll("a[href*='/jobs/view/']")
          .Select(a => JobIdRx().Match(a.GetAttribute("href") ?? ""))
          .Where(m => m.Success).All(m => m.Groups[1].Value == id);

    static IEnumerable<string> TextLines(INode node) =>
        node.NodeType == NodeType.Text
            ? [Clean(node.TextContent)]
            : node.ChildNodes.SelectMany(TextLines).Where(l => l.Length > 0);

    static string Clean(string s) => Regex.Replace(s, @"\s+", " ").Trim();
}
