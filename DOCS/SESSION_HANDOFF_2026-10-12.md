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

## The template builder: his Stage 1 package, our importer and the contract (467, 571)

- The owner: "yes build the importer and do the contract". While the importer was being built he passed on the
  developer's Stage 1 review package.
- **His package: accepted.** 363 files match their hashes, the source is commit `0064936`, the six exports pass our
  checker and the importer's own door, the two returns of our fixture differ by the one heading and by nothing. Seven
  of the nine fixes were run on our own copy (extracted to the scratchpad, port 5091, stopped after; his copies on
  5087 and 5090 were not touched); two were read in code. Two small points for him, neither blocking. The note is in
  the owner's Documents folder (`iPro_TemplateBuilder_Stage1_Review_2026-10-12.md`) for him to forward. The three
  checker scripts no longer depend on the first package's file names.
- **571, the importer:** SuperAdmin, an agent's details page, Template Builder: upload (checked against the contract,
  nothing changes), review (page by page, with a look at each page in the customer's real design), bring in, put the
  previous site back. See the row in `DOCS/TODO.md` and `DOCS/07_SUPER_ADMIN.md`. Only a site built from scratch in
  the builder is taken for now; iPro cannot hand a site over yet (the exporter is next).
- **The contract:** the consolidated v0.4 is written (`DOCS/TEMPLATE_BUILDER_CONTRACT.md`; for the developer,
  `Documents\iPro_TemplateBuilder_v0.4\CONTRACT.md`, with the 29 September text kept beside it). Doc-only; the
  fixtures are unchanged. It is the owner's to forward, with the review note.
- Local look-see, for next time: website images stored in the local blob emulator do not show in a browser, because
  the site's image policy allows only the real storage host. To see them, save the page and open the copy from a
  file (no policy header), with the site's own addresses made absolute.
- Left in the local database (`ipro_crm`): the new table, the new column and one dropped import row for site 1. The
  images it stored locally were removed with it.

## The owner's direction on the builder, evening (467)

- His words: "If you could i like you to take over the code ONLY if you can if not we could go back to the other
  template builder developer since he did extensive research with squarespace site. I also like to extensively test
  it locally with some auditors from code quality to security and functionality before thinking of deployment in
  production. I think at one point we did talk about having a switch to beta allowing clients to switch back and
  forth if they don't like it or find it buggy for now".
- **Answered yes, on evidence.** A second copy of the builder's pushed branch (`f01fd21`) builds here and passes its
  tests (247 .NET, 25 Node). Its own code is small. His Squarespace review is a written file in the repository.
  The limit said plainly: an editor's feel is judged here by scripts and pictures, not by hand at full size.
- **571 is NOT to be pushed.** It stays committed locally (`3aed496`) until the whole thing has been tested on this
  machine, audited (code quality, security, functionality: independent reviewers given the code, not our
  conclusions) and he says so.
- **Not started, waiting on him:** his confirmation after the answer, and the developer's last push. A hand-over
  request is drafted in his Documents folder (`iPro_TemplateBuilder_Handover_Request_2026-10-12.md`). Until the
  developer has stopped, his repository and working folder stay read-only for us; his copies on 5087 and 5090 were
  left running.
- For whoever picks this up: the builder's SQLite tests fail from a long folder path. The scratchpad copy passed
  only through a temporary drive letter (`subst T:`), removed afterwards. Work in a copy at a short path.

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

The three Template Builder screens in SuperAdmin (571): rendered from their real views by a test and looked at as
pictures of that markup, never signed in. The owner's first upload is the first real use.

The Profile page's GST/HST number box (570) and the editor's reminder; and, from 10-11, the editor's live totals and
discount choice in use, the remembered items, Email Activity's period, pages, Needs attention tab and search. His
screenshots have shown the invoice document (tax boxes, an item discount, the print preview) and the Email
Activity list. His first use of each of the others is its first real look: fix whatever he finds.

Related: `DOCS/TODO.md` 569, 570, 531; `DOCS/SES_GO_LIVE_RUNBOOK.md` section 8; `DOCS/SESSION_HANDOFF_2026-10-11.md`.
