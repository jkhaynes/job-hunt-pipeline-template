Research this role for a candidate preparing outreach.
Company: {company}   Role: {title}   Posting: {url}

Find, using web search:
1. The recruiter or talent acquisition partner most likely to own this
   role, and the likely hiring manager (engineering manager/director
   for this team). Include title and LinkedIn profile URL.
2. Any email address that is PUBLISHED on a public page. Do not guess
   email formats. If none is published, use null.
3. What is publicly known about the interview loop (stages, take-home,
   live coding) from the posting, company blog, or review sites.

Every item must include the source URL where you found it.
If you are not confident a person owns this role, set confidence "low".

Return JSON only:
{ "contacts": [{ "name": "", "title": "", "role": "recruiter | hiring_manager",
                 "linkedin": "", "email": null, "source": "", "confidence": "high | low" }],
  "interview_loop": [{ "detail": "", "source": "" }] }
Return JSON only.
