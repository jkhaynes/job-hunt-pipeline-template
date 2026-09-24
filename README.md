# job-hunt-pipeline

A daily GitHub Actions job that reads your LinkedIn job alert emails, finds each role's real posting, filters and scores it against your resume and criteria, researches who to contact, drafts outreach, and emails you one digest.

It **drafts but never sends**. The only email it ever sends is the digest, to you.

![Sample digest with fictional roles](docs/digest-demo.png)

*Sample digest built from fictional roles. Run `dotnet run --project src/JobHunt -- --demo` to render it yourself, no setup needed.*

## How it works

1. **Read alerts.** Unread emails labeled `JobAlerts` in your Gmail are parsed into jobs (title, company, location, LinkedIn job ID). Jobs already seen are skipped before any cost.
2. **Find the real posting.** The pipeline checks the company's public job list on Greenhouse, Lever, or Ashby first. This costs nothing and needs no search. It learns which companies use which list (`data/boards.json`). Otherwise Claude searches the web for the official posting, and every link is checked to make sure it actually loads.
3. **Filter.** Hard rules from `config/criteria.yaml` run in code: remote only, US only, a home-state residency rule, a pay floor, title words, and agency tagging. Hourly pay is converted to annual.
4. **Score.** Claude scores fit from 0 to 100 against your resume, using a rubric that separates minor, learnable gaps from major ones.
5. **Research.** For roles above the bar, Claude finds the likely recruiter and hiring manager (with sources) and what's public about the interview loop, then writes a short outreach draft. A separate call writes the draft, with no web access, so it can't invent facts.
6. **Digest.** You get one self-contained HTML file with full cards for the best roles, plus near misses, "check manually", and filtered-out sections, each with its reason.

Claude runs through the Claude Code CLI on **your Claude subscription** (Pro or Max), not API billing. Every run prints its usage.

## Ground rules

- **Drafts only.** Nothing is ever sent to a recruiter. The digest recipient comes only from the `DIGEST_TO` secret, never from model output.
- **Public contact info only.** Emails are shown only if they're published somewhere with a source link. No guessing email formats.
- **Every claim has a source.** Contacts and interview details link to where they were found.
- **No LinkedIn scraping by default.** The pipeline reads the alert emails you already receive. There is an **opt-in** setting (`linkedin.check_job_page`) that checks LinkedIn's public job page for each new role, which filters closed jobs and handles Easy Apply roles. LinkedIn's terms prohibit automated access, so it's off unless you turn it on.
- **Keep your copy private.** It holds your resume and job-search history.

## Setup

About 30 minutes. You need a GitHub account, a Gmail account that gets LinkedIn job alerts, and a Claude Pro or Max subscription. Local testing also needs the [.NET 10 SDK](https://dotnet.microsoft.com/download) and [Claude Code](https://docs.claude.com/en/docs/claude-code).

### 1. Make your own copy

Click **Use this template → Create a new repository**, and make it **private**. Clone it locally.

### 2. Add your resume and criteria

- Replace `config/resume.md` with your resume and any extra accomplishments in markdown. The scorer and drafter may only use what's in this file.
- Edit `config/criteria.yaml`: pay floor, remote rule, home state, title words to skip, and your stack and preferences in plain English.

### 3. Set up LinkedIn alerts and a Gmail label

1. Create LinkedIn job alerts (Remote filter, **Daily**, **Email**). Each alert email only shows about **6 jobs**, so several narrow searches beat one broad one. For example:
   - `senior AND ("C#" OR ".NET") AND (backend OR API)`
   - `("tech lead" OR "lead engineer") AND ("C#" OR ".NET")`
   - `(staff OR principal) AND ("C#" OR ".NET")`
2. In Gmail, search `from:(jobalerts-noreply@linkedin.com)`, then **Create filter** and **Apply the label** `JobAlerts`. Don't choose "Mark as read": the pipeline only reads unread alerts.

### 4. Create Gmail API credentials

1. In [Google Cloud Console](https://console.cloud.google.com), create a project and enable the **Gmail API**.
2. Open **OAuth consent screen** (Google Auth Platform) and choose **External**. Fill in **Branding**: app name, support email, a homepage URL, and a privacy policy URL. A simple page on your own site works. Google only checks that the fields are filled in for a personal app.
3. Under **Data Access**, add the scopes `https://www.googleapis.com/auth/gmail.modify` and `https://www.googleapis.com/auth/gmail.send`.
4. Under **Audience**, add yourself as a test user, then **Publish app** so the status reads **In production**. Ignore the "needs verification" banner: an unverified app works fine for your own account. **Publish before step 6.** Tokens issued while the app is in Testing expire after 7 days.
5. Under **Clients**, create a **Desktop app** client and download its JSON. Keep it **outside** the repo.
6. Get a refresh token. A browser opens: click "Advanced" and then "Go to … (unsafe)" (it's your own app), and **tick both checkboxes**.
   ```
   dotnet run --project src/JobHunt -- --auth path/to/client_secret.json
   ```

### 5. Get a Claude subscription token

```
claude setup-token
```

### 6. Add repository secrets

Go to **Settings → Secrets and variables → Actions**, or use the `gh secret set NAME` command, which prompts for each value:

| Secret | Value |
|---|---|
| `CLAUDE_CODE_OAUTH_TOKEN` | From `claude setup-token` |
| `GMAIL_CLIENT_ID` | From `--auth` |
| `GMAIL_CLIENT_SECRET` | From `--auth` |
| `GMAIL_REFRESH_TOKEN` | From `--auth` |
| `DIGEST_TO` | Your own email address |

### 7. Run it

Go to **Actions → daily-job-digest → Run workflow** for a first run. After that it runs every day at 10:00 UTC (6 AM Eastern). To change the time, edit the `cron` line in `.github/workflows/daily.yml`.

## Running locally

Run these from the repo root:

```
dotnet run --project src/JobHunt -- --demo                       # sample digest, no setup
dotnet run --project src/JobHunt -- --self-test                  # offline logic checks
dotnet run --project src/JobHunt -- --parse-only fixtures        # check the alert parser on saved .eml files
dotnet run --project src/JobHunt -- --from-fixtures fixtures --limit 3   # full pipeline on saved emails, no Gmail
dotnet run --project src/JobHunt -- --dry-run --limit 3          # real Gmail, but no send, mark-read, or seen.json
```

To test with your own alerts, save a few as `.eml` files in `fixtures/` (in Gmail: ⋮ → **Download message**).

## Cost and usage

Each role costs roughly **$0.15–0.40 at API list price**. Postings found on a public job list are cheapest. Researched roles cost the most. On a subscription that isn't billed separately; it counts toward your plan's usage limits. Every run logs its calls, tokens, and list-price equivalent, and prints them in the digest email. Twenty roles a day used well under 1% of a weekly Max limit in testing.

## Customizing

- **Scoring:** `prompts/score.md` holds the rubric, gap severity, and hard caps. It reads your preferences from `criteria.yaml`.
- **Outreach voice:** `prompts/draft.md`. Paste in two messages you've actually sent as style examples.
- **Digest look:** `templates/digest.html` is plain HTML and CSS. The colors are CSS variables at the top.
- **Model:** `ModelId` in `src/JobHunt/ClaudeClient.cs`.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `invalid_grant` from Gmail after a week | The OAuth app was in Testing when you got the token. Publish it and rerun `--auth`. |
| "0 new jobs" from non-empty alerts | LinkedIn changed its email markup. Save a fresh alert to `fixtures/`, run `--parse-only`, and adjust `AlertParser.cs`. |
| Many roles in "Check manually" | Common for Easy Apply roles and recruiter reposts with no public posting. The opt-in LinkedIn check handles most of them. |
| Workflow can't push `seen.json` | Check the workflow has `contents: write` permission and that branch protection isn't blocking the bot. |

## License

MIT. See [LICENSE](LICENSE).

Built by [Jessica Haynes](https://www.jessbuilds.dev) with [Claude Code](https://claude.com/claude-code).
