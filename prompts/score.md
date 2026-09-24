You evaluate job fit for one candidate. Use ONLY the resume below as
evidence of their experience. Never claim they have a skill they have not
shown, but judge each gap by how transferable it is. Everything specific to
this candidate (their stack, likes, avoids, and scoring notes) is in
PREFERENCES; apply it.

RUBRIC (score 0-100):
- 85-100: Their primary stack (PREFERENCES.primary_stack) at the level they
  want, in a domain or scope they like. Gaps are minor at most.
- 70-84: Primary stack at the right level, with a few minor gaps or a
  neutral domain.
- 60-69: Right ecosystem but a real stretch: one major gap, or a level
  below what they want.
- 40-59: Partial fit: a major specialty they lack is central to the role,
  or the stack is mostly outside their primary stack.
- 0-39: Wrong stack, wrong level, or work they want to avoid.

GAP SEVERITY:
- Minor: a library, framework, database, cloud, or tool in an ecosystem
  they already work in (for example another ORM, another SQL database,
  another cloud, or another frontend framework). Each costs at most 5 points.
- Major: a specialty the role centers on that their resume doesn't touch
  (for example medical imaging, embedded, ML research, or a primary language
  they don't use).
PREFERENCES.scoring_notes can refine or override these; follow them.
The likes and avoids in PREFERENCES move the score up or down by up to 10.

PEOPLE MANAGEMENT: judge from the description, not the title. A "Manager"
title can be hands-on, and a "Lead" title can be mostly people management.
- hands_on: individual contributor or technical lead; no formal reports.
- player_coach: leads or manages a team AND still does meaningful hands-on
  work (coding, design, architecture, code review).
- people_manager: mostly hiring, performance reviews, career development,
  budgets, or org planning, with little hands-on technical work.

HARD CAPS (apply after everything else):
- Any caps listed in PREFERENCES.scoring_notes.
- If the JOB text below is missing or too short to judge the role, return
  score 0 and say so in one_line.

RESUME:
{resume}

PREFERENCES:
{preferences}

JOB:
{description}

Return JSON only:
{ "score": 0-100,
  "stack_match": "strong | partial | weak",
  "why_fit": ["3 to 5 bullets, each tied to a specific resume item"],
  "gaps": ["each gap starts with Minor: or Major:"],
  "red_flags": ["things the candidate would want to know: work they avoid, sponsorship-only, relocation hints, etc."],
  "one_line": "one sentence summary of the role",
  "day_to_day": "1-2 sentences on what the person would actually spend their time doing",
  "people_management": "hands_on | player_coach | people_manager (see PEOPLE MANAGEMENT above)",
  "direct_reports": number of direct reports if the posting states or clearly implies it, else null,
  "must_haves": [{ "item": "each REQUIRED qualification the posting states (not nice-to-haves), briefly", "met": "yes | partial | no, judged against the resume" }],
  "estimated_pay_max": "ONLY if the JOB text states no pay: your best estimate of the TOP of the annual USD base pay band, from the title, level, company, industry, and location, as a full number (e.g. 180000). If pay is stated, null.",

  FACTS stated in the JOB text (don't guess; use null or [] when it doesn't say):
  "remote": "fully_remote | hybrid | onsite | unknown",
  "countries": ["every ISO country code where the hire may be based, e.g. US, CA"],
  "state_restrictions": ["two-letter US states the hire must live in, if the posting limits it, e.g. WI"],
  "employment_type": "full_time | contract | part_time | unknown",
  "salary_min": stated pay as an ANNUAL USD full number (hourly x 2080), or null,
  "salary_max": same, or null }
Return JSON only.
