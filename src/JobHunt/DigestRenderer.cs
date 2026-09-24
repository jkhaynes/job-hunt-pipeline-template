using System.Text;
using static System.Net.WebUtility;

namespace JobHunt;

public static class DigestRenderer
{
    // Everything from a posting or a web search is untrusted: always go through E() or Link().
    public static string Render(string template, string date, string summary,
        List<JobRole> kept, List<JobRole> nearMisses, List<JobRole> unverified, List<JobRole> filtered)
    {
        var cards = new StringBuilder();
        foreach (var r in kept) cards.Append(Card(r));

        var near = new StringBuilder();
        foreach (var r in nearMisses)
        {
            near.Append($"<li><strong>{r.Fit!.Score}</strong> {Link(r.Posting!.Url, $"{r.Job.Title} at {r.Job.Company}")}{FlagTags(r)}");
            near.Append($"<br>{E(r.Fit.OneLine)}");
            if (r.Fit.Gaps.Count > 0) near.Append(List(r.Fit.Gaps));
            near.Append("</li>");
        }

        var manual = new StringBuilder();
        foreach (var r in unverified)
        {
            manual.Append($"<li>{E(r.Job.Title)} at {E(r.Job.Company)} · {E(r.Job.Location)}{FlagTags(r)}<br>");
            manual.Append(Link(r.Job.LinkedInUrl, "LinkedIn listing"));
            if (r.Posting?.Url is { } guess)
                manual.Append($" · {Link(guess, PostingResolver.IsConfident(r.Posting) ? "posting" : "possible match (low confidence)")}");
            if (r.Posting?.Reason is { Length: > 0 } why) manual.Append($"<br><span class=\"hint\">Lookup: {E(why)}</span>");
            manual.Append("</li>");
        }

        var rejected = new StringBuilder();
        foreach (var r in filtered)
            rejected.Append($"<li>{E(r.Job.Title)} at {E(r.Job.Company)}: {E(r.FilterReason)}</li>");

        return template
            .Replace("{{date}}", E(date))
            .Replace("{{summary}}", E(summary))
            .Replace("{{cards}}", cards.ToString())
            .Replace("{{near_count}}", nearMisses.Count.ToString())
            .Replace("{{near_misses}}", near.ToString())
            .Replace("{{manual_count}}", unverified.Count.ToString())
            .Replace("{{manual}}", manual.ToString())
            .Replace("{{filtered_count}}", filtered.Count.ToString())
            .Replace("{{filtered}}", rejected.ToString());
    }

    static string FlagTags(JobRole r) => string.Concat(r.Flags.Select(f => $" <span class=\"flag\">{E(f)}</span>"));

    static string Card(JobRole r)
    {
        var p = r.Posting!;
        var f = r.Fit!;
        var sb = new StringBuilder("<article class=\"card\">");
        sb.Append($"<div class=\"head\"><h2>{Link(p.Url, $"{r.Job.Title} at {r.Job.Company}")}</h2><span class=\"score\">{f.Score}</span></div>");
        var applicants = r.Applicants is { } a ? $" · {a}" : "";
        sb.Append($"<div class=\"meta\">Posted {E(p.PostedDate ?? "unknown")} · {E(Pay(p))} · {E(p.Remote)} · stack match {E(f.StackMatch)}{E(applicants)}</div>");
        foreach (var flag in r.Flags) sb.Append($"<span class=\"flag\">{E(flag)}</span>");
        if (r.Flags.Contains("Easy Apply")) sb.Append($"<p>{Link(r.Job.LinkedInUrl, "Easy Apply on LinkedIn")}</p>");
        if (!string.IsNullOrEmpty(f.OneLine)) sb.Append($"<p>{E(f.OneLine)}</p>");

        sb.Append("<h3>Why you fit</h3>").Append(List(f.WhyFit));
        if (f.RedFlags.Count > 0) sb.Append("<h3>Red flags</h3>").Append(List(f.RedFlags));
        if (f.Gaps.Count > 0) sb.Append("<details><summary>Gaps</summary>").Append(List(f.Gaps)).Append("</details>");

        if (r.Research is { } res)
        {
            if (res.Contacts.Count > 0)
            {
                sb.Append("<h3>Contacts</h3><ul>");
                foreach (var c in res.Contacts)
                {
                    sb.Append($"<li>{Link(c.Linkedin, c.Name ?? "Unknown")}, {E(c.Title)} ({E(c.Role)})");
                    if (!string.IsNullOrEmpty(c.Email)) sb.Append($" · {E(c.Email)}");
                    sb.Append($" · {Link(c.Source, "source")}");
                    if (c.Confidence != "high") sb.Append(" <span class=\"low\">low confidence</span>");
                    sb.Append("</li>");
                }
                sb.Append("</ul>");
            }
            if (res.InterviewLoop.Count > 0)
            {
                sb.Append("<details><summary>Interview loop</summary><ul>");
                foreach (var d in res.InterviewLoop) sb.Append($"<li>{E(d.Detail)} ({Link(d.Source, "source")})</li>");
                sb.Append("</ul></details>");
            }
        }

        if (!string.IsNullOrEmpty(r.Draft))
            sb.Append($"<h3>Draft to {E(r.DraftTo?.Name)}</h3><div class=\"draft\"><div class=\"text\">{E(r.Draft)}</div></div>");
        if (!string.IsNullOrEmpty(p.StatusCheckUrl))
            sb.Append($"<p>{Link(p.StatusCheckUrl, "Status check portal")}</p>");

        return sb.Append("</article>").ToString();
    }

    public static string Pay(Posting p) => (p.SalaryMin, p.SalaryMax) switch
    {
        (null, null) => "pay not posted",
        ({ } lo, { } hi) => $"${lo:N0} to ${hi:N0}",
        _ => $"${p.SalaryMin ?? p.SalaryMax:N0}",
    };

    static string List(IEnumerable<string> items) => "<ul>" + string.Concat(items.Select(i => $"<li>{E(i)}</li>")) + "</ul>";

    static string E(string? s) => HtmlEncode(s ?? "");

    /// <summary>Only http(s) URLs become links; anything else renders as plain text.</summary>
    public static string Link(string? url, string text) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http")
            ? $"<a href=\"{E(u.AbsoluteUri)}\" target=\"_blank\" rel=\"noopener noreferrer\">{E(text)}</a>"
            : E(text);
}
