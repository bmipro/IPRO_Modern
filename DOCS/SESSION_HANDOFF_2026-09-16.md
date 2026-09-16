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
| `6c9264d` | **491** the sending gate (30/minute, 100/hour, transactional reserve, settings) and the pause path in the four blast loops | 841/841 |

## Close-out 2026-09-16

Close-out as asked at the end of the day, after 491 and the white-label sizing. Final build on both hosts
before this close-out: `34e36af`. Tree clean and pushed.

| Code | Item | Gate |
|---|---|---|
| `6c9264d` | **491** the sending gate (30/minute, 100/hour, transactional reserve, settings) and the pause path in the four blast loops | 841/841 |
| docs | Microsoft's decline of 442 (both halves) and the owner's acceptance posted on the case; the 442 row corrected; the white-label re-sizing (roadmap, TODO 378, `DOCS/WHITE_LABEL_UPGRADE.md`, and a reading copy in the owner's Documents folder) | -- |

White-label, the owner's question of the afternoon: three options sized against the shipped code --
(A)-lite about a week, (A) 10-11 build days, (B) 2-3 months plus legal -- with what (A) does not cover
(the sender identity on a per-subscription cap, Google Calendar welded to the platform host, tracking and
unsubscribe links, the Terms). Recommendation: nothing before launch or in the first two weeks after; start
(A) the week of 5 October if a named partner is waiting; otherwise let the first partner conversation
choose (A) or (B).

Owner's decision: the three small undecided items get decided tomorrow.

Backups: `IPRO_Modern_backup_<stamp>.zip` in `C:\Users\admin\OneDrive\Codex_Code_Bkup` and
`C:\Users\admin\Documents\IPRO_Backups` (git archive of HEAD, stamped at the close-out); build servers
shut down; MySQL stays a Windows service.

## Do this first tomorrow

1. **The three small items, the owner decides first thing:** the client Edit form saying why the newsletter
   tick is ignored for an unsubscribed client; the bare `/Admin/` address landing on the login page; Hangfire
   deleting a job once its retries are exhausted. Each under an hour, red-first, one gate for the lot.
2. **PayPal live cutover** -- not to be left past tomorrow: production is still sandbox; portal-to-portal (the
   owner enters the live client id, secret and webhook id), then the webhook and a first live charge path
   verified together, with days of margin before the 21st.
3. **Owner-side:** the DNS switch for the four live names on or before 21 September (DOCS/14 -- APPEND to
   `App__AliasHosts`, 483), the 19th or 20th is fine; the lawyer's review of both legal documents; the
   designer's items and the /insurance package when ready.
4. **Google review:** watch support@iproadvisers.com and bahman.motamed@gmail.com; answer from the same
   Google account; do not touch Branding while it runs. **Microsoft:** nothing until mid-October, then a
   new quota request with 30 days of history (DOCS/TODO.md 442).
5. **Launch-week expectation at the cap:** a 500-client newsletter takes about five hours and lands
   complete; two advisers sending the same morning queue behind each other; birthday cards on a busy
   date spread over the day. Email Activity shows the rows filling in.
6. **White-label:** nothing before launch; `DOCS/WHITE_LABEL_UPGRADE.md` when a partner conversation starts.
7. After launch week: 471, 450, the From display name (480), the Entra client secret rotation (482).

Related: `DOCS/TODO.md` 442, 488-491, 378; `DOCS/WHITE_LABEL_UPGRADE.md`; `DOCS/DNS_ZONE_RUNBOOK.md`;
`DOCS/SESSION_HANDOFF_2026-09-15.md`.
