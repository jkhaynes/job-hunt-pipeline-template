Extract facts about this job posting. Use only the text below.

Role: {title} at {company}

POSTING:
{description}

Return JSON only:
{ "remote": "fully_remote | hybrid | onsite | unknown",
  "countries": ["every ISO country code where the hire may be based, e.g. US, CA; empty if not stated"],
  "state_restrictions": ["two-letter US states the hire must live in, if the posting limits it, e.g. WI; else empty"],
  "employment_type": "full_time | contract | part_time | unknown",
  "salary_min": number or null, "salary_max": number or null }
Salaries are ANNUAL USD as full numbers (e.g. 150000). If pay is hourly,
multiply by 2080. If the posting doesn't say, use null.
Return JSON only.
