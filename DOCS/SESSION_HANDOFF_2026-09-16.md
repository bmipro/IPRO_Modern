# Session handoff — 2026-09-16 (5 days to launch)

## What happened

- **Microsoft declined both halves of 442 (morning, ticket 2608310040012537):** the sending limit for
  insufficient history (re-apply after 30 days of ACS usage) and engagement tracking because the domain
  is on default limits (moot since 488). The owner's acceptance -- launch at the current limits, new
  request in mid-October, close the case -- was posted on the case through the CLI on his go at 12:02.

- **491 (afternoon):** the platform now lives inside the cap. Checking the code at 30/minute and 100/hour
  found that the four blast loops sent flat out and wrote every 429 down as a permanently Failed
  recipient (my 8 September note in the 442 row claiming otherwise was wrong and is corrected). Now
  `EmailSendGate` paces every Azure send (bulk behind a transactional reserve; limits are settings with
  the ACS defaults), and a throttled send pauses with its rows Queued and resumes on the next minutely
  run instead of failing them. Red first, 25 tests. Details in `DOCS/TODO.md` 491; the keys to change
  when Microsoft relents are in DOCS/DNS_ZONE_RUNBOOK.md.

- **Demo at 13:00:** the owner asked for nothing to move on production until it was over; nothing did.

## Pushed today

| Code | Item | Gate |
|---|---|---|
| _see log_ | **491** the sending gate (30/minute, 100/hour, transactional reserve, settings) and the pause path in the four blast loops | whole tree |

## Do this first tomorrow

1. **PayPal live cutover** -- production is still sandbox; portal-to-portal, then the webhook and a first
   live charge path verified together.
2. **Owner-side:** the DNS switch for the four live names on or before 21 September (DOCS/14 -- APPEND to
   `App__AliasHosts`, 483); the designer's items; the /insurance package when ready; the lawyer's review of
   both legal documents (README-review-notes, 14 September entry).
3. **Google review:** watch support@iproadvisers.com and bahman.motamed@gmail.com; answer from the same
   Google account; do not touch Branding while it runs. **Microsoft:** nothing until mid-October, then a
   new quota request with 30 days of history (DOCS/TODO.md 442).
4. **Launch-week expectation at the cap:** a 500-client newsletter takes about five hours and lands
   complete; two advisers sending the same morning queue behind each other; birthday cards on a busy
   date spread over the day. Email Activity shows the rows filling in.
5. Small items offered, not yet decided: the client Edit form note for an unsubscribed client; `/Admin/`
   landing on the login page; Hangfire deleting a job once its retries are exhausted.
6. After launch week: 471, 450, the From display name (480), the Entra client secret rotation (482).

Related: `DOCS/TODO.md` 442, 488-491; `DOCS/DNS_ZONE_RUNBOOK.md`; `DOCS/SESSION_HANDOFF_2026-09-15.md`.
