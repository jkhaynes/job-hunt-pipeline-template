Find the official job posting for "{title}" at "{company}" ({location}).
LinkedIn included it in a job alert on {alert_date}, so it was most likely
posted in the few days before that. If the company has several similar
openings, prefer the one posted closest to that date.

The official title may differ slightly from the LinkedIn one: extra words, a
team or product name, or a level such as II or III. Accept close variants of
the same role.

Prefer the company careers site or its ATS (Greenhouse, Lever, Ashby,
Workday, iCIMS) over job boards. Do not fetch linkedin.com pages.

Try at least two different searches before giving up, for example
"{company} careers {title}" and "{company}" "{title}" greenhouse OR lever
OR ashby OR workday.

Return JSON only:
{ "url": "...", "ats": "...",
  "confidence": "high | medium | low (how sure you are this is the same job)",
  "reason": "if url is null or confidence is low: one short line on why, e.g. no results, posting closed, only found on job boards, several similar roles",
  "posted_date": "YYYY-MM-DD or null",
  "remote": "fully_remote | hybrid | onsite | unknown",
  "countries": ["every ISO country code where the hire may be based, e.g. US, CA; empty if not stated"],
  "state_restrictions": ["two-letter US states the hire must live in, if the posting limits it, e.g. WI; else empty"],
  "employment_type": "full_time | contract | part_time | unknown",
  "salary_min": number or null, "salary_max": number or null,
  "description": "full description text",
  "status_check_url": "candidate portal URL if the ATS has one, else null" }
Salaries are ANNUAL USD as full numbers (e.g. 175000). If pay is hourly,
multiply by 2080. If you find nothing plausible, return
{ "url": null, "reason": "..." }.
Return JSON only.
