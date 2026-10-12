# Session handoff -- 2026-10-12

## Branches: read this first

- **`main` is what production runs, plus notes.** A fix for the live site is made on `main`, gated and pushed as
  always. Nothing of the Template Builder is on it.
- **`feat/template-builder-beta` (local only, not pushed) holds the Template Builder work:** 571 (the importer) and
  everything after it. The owner's word, 2026-10-12: nothing of it goes to production before it has been tested
  extensively on this machine and audited for code quality, security and functionality. That branch's copy of this
  file has the full story of the day.
- After anything lands on `main`, merge `main` into the branch (`git switch feat/template-builder-beta`, then
  `git merge main`), so the two never drift far. The branch goes to `main` only on the owner's word.
- A backup tag, `backup/main-before-builder-branch-20261012`, marks where local `main` stood before the split.
  Each backup run also writes a bundle of the branch beside the zips (the zips hold one branch's files only).

## What happened

- The owner, first thing: "Lets do the GST/HST number on client invoices and push it with the Prininting fix. Also
  could layout the SES step 8.2 that is waiting for me".
- **569**, parked on the branch `hold/569-print-layout` the night before, was brought into the tree (the same two
  files) and gated with 570.
- **570:** a business's GST/HST number, typed once on the Profile page and printed on its invoices and estimates.
  See the row in `DOCS/TODO.md`.
- **SES step 8.2, laid out for him** (it is his change, `Email__Ses__Streams` = `notify,news` on `ipro-prod-web`):
  look at Amazon's Reputation metrics first (Healthy, bounce under 2%, complaint under 0.05%); change the one value;
  tell us; one test send. Read-only checks this morning: the setting is 6 characters long (`notify`), the pilot
  setting is absent, the four keys are present; the web log of 10-10 to 10-12 has no line naming an SES problem, and
  no failure at all since 561 went out (the 78 before it were the rate limiter's "invalid IP address").
- A mistake of mine, caught by the safety check and not run: a clean-up command that removed files after changing
  directory. Use a NEW folder for each download instead of clearing an old one.

## The template builder: the developer's response (467)

- The owner passed on the developer's reply to the 11 October review (`C:\Users\admin\Projects\IPRO.TemplateBuilder\docs\reviews\2026-10-11\iPro_TemplateBuilder_Response_to_Review_2026-10-11.txt`).
  He accepts everything as asked: the two stages, all nine fixes in order, hiding the appearance controls on handover
  sites; 2-3 focused working days; the reviewed snapshot committed and pushed first; a new review package after.
- **He has started nothing and is waiting for the owner's explicit authorization.** A go-ahead note is in the owner's
  Documents folder for him to forward if he agrees (`iPro_TemplateBuilder_Stage1_GoAhead_2026-10-12.md`). It is a
  draft in his name: it is his to send or change.
- **Ours, in parallel, none started:** the consolidated `CONTRACT.md` v0.4 (about half a day), the stage 1 importer
  (2-3 days; without it stage 1 cannot be used), and later the v0.5 draft. The importer waits on the owner's word.
- **Later the same day** the owner relayed the developer's message: the reviewed snapshot is pushed on
  `feature/builder-ux-refresh` as `6e119e56240ec025b42e53200982c11140ab5245`. So the go-ahead was given and stage 1's
  fixes are under way on his side. Checked read-only: the commit holds all 167 files of the package's `source/`
  folder (136 byte for byte, 31 apart from line endings, none different or missing), and its handover fixture is
  ours. The check is kept as `compare_commit_with_package.py` beside the other two in the checker folder. One small
  thing for the next note to him: the package's README places the handover fixture under `source/tests/fixtures/`,
  where the package does not have it (the commit does).
- The owner also said SES step 8.2 will be tomorrow "if not urgent". It is not: marketing mail stays on Azure until
  he changes the setting, and nothing is waiting on it.

## Pushed

| Commit | What | Gate |
|---|---|---|
| `f15d566` | **570** a business's GST/HST number on its invoices and estimates to clients (live on build `909650b`) | 1644/1644 |
| `d09ddb6` | **569** a printed invoice takes the table layout whatever margins the printer adds (live on build `909650b`) | 1644/1644 |

## Still his

- **SES step 8.2** as laid out above. When he says it is saved: the setting should be 11 characters, the site Healthy,
  the log clean; then his one test send (From "... via iPro" at the news address, Delivered in Email Activity).
  Then 8.3 (the alarms) after a few days.
- **To use 570:** his GST/HST number on his Profile page; the bakery's on theirs.
- The bakery: type the phone number again in the three call buttons. Refer a Friend is still OFF. "Shown in your
  emails as" for www.4iPro.com and www.LAvenueBakery.com.

## Offered and open

A one-time "we do not charge tax" setting; a way to forget a mistyped remembered item; a managed "My items" price
list (held back); one row per day for the one-at-a-time automatic emails; the Open-now line once per page on the
bakery's home page. The template builder: his answers from the developer; ours are the importer for stage 1, the
consolidated contract v0.4 and a v0.5 draft.

## Not seen signed in

The Profile page's GST/HST number box (570) and the editor's reminder; and, from 10-11, the editor's live totals and
discount choice in use, the remembered items, Email Activity's period, pages, Needs attention tab and search. His
screenshots have shown the invoice document (tax boxes, an item discount, the print preview) and the Email
Activity list. His first use of each of the others is its first real look: fix whatever he finds.

Related: `DOCS/TODO.md` 569, 570, 531; `DOCS/SES_GO_LIVE_RUNBOOK.md` section 8; `DOCS/SESSION_HANDOFF_2026-10-11.md`.
