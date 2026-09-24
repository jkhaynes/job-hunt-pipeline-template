namespace JobHunt;

public class Researcher(ClaudeClient claude, string resume, string researchPrompt, string draftPrompt)
{
    const string ResearchSystem = "You research hiring contacts and interview processes from public sources. Every fact carries its source URL. You answer with JSON only.";
    const string DraftSystem = "You write short, plain outreach messages in the candidate's voice. You never invent facts.";

    // Two calls on purpose: search gathers facts, then a search-free call drafts,
    // so the draft can't invent things it half-found.
    public async Task ResearchAsync(JobRole role)
    {
        var p = role.Posting!;
        var prompt = Prompt.Fill(researchPrompt, ("company", role.Job.Company), ("title", role.Job.Title), ("url", p.Url));
        role.Research = Json.Parse<Research>(await claude.AskAsync(ResearchSystem, prompt, webSearch: true));

        var to = role.Research.Contacts
            .OrderBy(c => c.Confidence == "high" ? 0 : 1)
            .ThenBy(c => c.Role == "recruiter" ? 0 : 1)
            .FirstOrDefault();
        if (to is null) return;

        role.DraftTo = to;
        var draft = Prompt.Fill(draftPrompt,
            ("contact_name", to.Name), ("contact_title", to.Title), ("title", role.Job.Title),
            ("company", role.Job.Company), ("resume", resume), ("description", p.Description));
        role.Draft = (await claude.AskAsync(DraftSystem, draft, webSearch: false)).Trim();
    }
}
