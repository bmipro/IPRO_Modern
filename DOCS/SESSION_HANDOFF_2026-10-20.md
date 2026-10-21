# Session handoff -- 2026-10-16 to 2026-10-20

## Branches and state: read this first

- `main` = production. Live build at close: **924b25b** (591), on both hosts, verified at /health/version.
  Local `main` is ahead by docs commits only (the 591 check-mark and this file): held, not pushed, because a push
  restarts both sites. Push them with the next code release.
- Three pieces of work are **built, gated and committed on their own branches, waiting for his word**. None is on
  `main`; nothing of them is pushed or deployed.

| Item | Branch (worktree under `.claude/worktrees/`) | Commits | Gate | Waiting for |
|---|---|---|---|---|
| 585 builder Beta for customers | `claude/builder-beta-for-customers-2ebe5a` (head dc99e63); builder repo `ipro/customer-beta` de9fcf0 | many; last code 508932a | 2,134 of 2,134 on 508932a; builder 400 .NET + 54 front-end | his re-check of the 5 walk-through fixes, then the Azure set-up (runbook), then release |
| 592 domain ownership proof | `claude/upbeat-moore-283b15` | 4a81bd8 code, c4ec327 TODO row | 2,114 of 2,114 | his 3 read-only SELECTs + removing www.411trades.com from michaeltran, then release, then 1 SELECT |
| 593 two-step sign-in (MFA) | `claude/musing-hodgkin-64d1ee` | 1b1987b code, cde4a0c TODO row | 2,136 of 2,136 | his try (15 min, his phone) and 4 decisions (below) |

- **Merge order:** 592's and 593's TODO rows both sit right after row 591; whichever lands second goes after
  the other. 585 merged `main` at 0b9d864 (429143c); it needs `main` merged again before its release.
- The builder repo `C:\Users\admin\Projects\ipro-builder` stays at `ba49da9` on `ipro/beta` under his two builders.

## What went live (all on his word, each gated in full first)

| Item | What | Build | Gate |
|---|---|---|---|
| 581 + 582 | A page given a new address in the builder comes back as the page it was; the form and sign-in limits hold however the address is written (security) | ff5bc61 (10-16) | 1,987 |
| 583 | Five wrong passwords lock a sign-in for 15 minutes, every kind of account (security; he kept both email limits) | 09fad22 (10-17) | 2,016 |
| 584 | SuperAdmin can start a website again from its starter pack and put the previous pages back (he used it to make michaeltran a mortgage demo) | 49d2439 (10-17) | 2,037 |
| 586 + 587 + 588 | Client portal sign-in tied to one adviser; numbers on the analytics bars; new contact/newsletter/article take only what the adviser types | e803dbf (10-18 web 11:45, admin 11:42) | 2,061 |
| 589 | No adviser can add or remove one of iPro's own addresses as a custom domain (live hole; his read-only check of production was empty) | c6d55b2 (10-18 web 19:11, admin 19:14) | 2,077 |
| 591 | Two advisers' domains can no longer share one certificate in Azure (his check empty; the Azure check found all 18 secured addresses on their own certificates) | 924b25b (10-19 web 08:06, admin 08:08) | 2,095 |

Also on `main`: TODO row 590, **upgrade the production MySQL from 8.0 to 8.4 before 2026-12-31** (Azure ends
standard support then; extended-support charges from 2027-01-01). Local MySQL is already 8.4.

## Outside the code

- Azure (10-17, he watched in the portal): uptime checks every 15 minutes from 5 US locations, alert at 3 of 5
  failing for 15 minutes to the on-call group; a C$200 monthly budget with alerts at 80%, 100% and forecast 100%.
- Production database read-only checks: he runs them in the portal's Cloud Shell, typing the connect command and
  the password himself (`SET SESSION transaction_read_only = 1;` first). Done for 589 and 591, both empty.
- Backups 10-19: a full recovery kit (both repositories, all branches, his builders' data, notes; restored and
  built in an empty folder first) in OneDrive\Codex_Code_Bkup and Documents\IPRO_Backups. 174 old daily snapshots
  (before 10-05, 2.2 GB) moved to the **Recycle Bin**; he empties it when ready.
- His builders (5087, 5088) had stopped on 10-19 afternoon with no reboot and no crash in the log; restarted on
  10-20 with his own Start-Builder scripts. Check them first each session.

## The builder Beta walk-through (10-19)

He tried the whole path on the bench (formtest, in Edge): edit, See it on my site, Send to iPro, SuperAdmin
review and Look, Bring it in (live locally), Put my previous site back, Continue my draft. All worked. Five
findings, all fixed and gated since: (1) menu links inside a look led to "Page not found"; (2) SuperAdmin listed
his "See it on my site" looks as sends; (3) the review called all 18 pages Replaced (now "No change" vs what
changed); (4) the look's footer lacked the Accessibility link; (5) three alike "Kept draft" entries. How to
re-check each: `585-walkthrough-recheck.md` in the shared notes folder.

**The desktop browser pane cancels "Are you sure?" boxes and blocks new tabs.** Walk him through confirm or
new-tab steps in his normal browser (Edge) from the start.

## The demo (10-20)

A 30-minute demo to a friend who built and sold a company: "he loved it"; they meet again in about two weeks.
Run sheet: the doc "iPro Demo Run Sheet" and `C:\Users\admin\Documents\iPro_Demo_Run_Sheet.pdf`. His advice:

1. **Limitation of liability:** already in iPro's Terms.
2. **Sales tax.** iPro charges each adviser's subscription its province's combined rate (QC 14.975% GST+QST, BC
   12% GST+PST, MB 12% GST+RST, SK 11% GST+PST, HST provinces 13-15%, AB and territories 5%), but its invoices
   carry only a GST/HST number. If iPro is not registered with Revenu Quebec, BC, Manitoba and Saskatchewan, it
   collects provincial tax it has no account to remit. **He will call those offices (or ask an accountant).**
   Found rules, to be confirmed: QC register past $30,000 in 12 months to customers not registered for QST; BC
   past $10,000; MB taxes cloud software from 2026-01-01; SK possibly from the first sale. Then: SuperAdmin ->
   Tax Rates can change a province's rate, BUT advisers' own client invoices read the same table, so the proper
   fix separates iPro's own registrations from the advisers' rates and prints QST/PST numbers. Scope it after his
   calls.
3. **Two-step sign-in:** built as 593 the same day (below).

## 593 two-step sign-in: what waits for him

Optional at first (dashboard prompt), app codes (QR) or email codes, ten recovery codes, "don't ask on this
device for 30 days", 5 wrong codes lock for 15 minutes (20 in a row: locked until SuperAdmin unlocks),
SuperAdmin can require, reset (audited, emailed) and unlock; advisers reset team members. It also fixed three
older live holes: a team member who saved Profile or a portal colour became the adviser; the login email could be
changed without the password (rated serious); saving a whole record could restore an old password. At release
everyone signs in once more, and a new password signs out other browsers.

His decisions: (a) "Remember me" has never worked (one line to fix, 7-day sessions; recommended); (b) back up the
data-protection key ring (protects authenticator secrets) to Azure Key Vault, his Azure steps; (c) confirm a new
login email by link (small follow-up); (d) QRCoder 1.8.0 (MIT) for the QR code. Two simplifications proposed
(his answer pending): the 30-day box ticked by default, and no password on the set-up screen (the emailed code
already proves it is them). Client portal two-step: plan only, about 1.5-2 days, after a week or two live.

## Still his

- 592: three read-only SELECTs (in `592-for-the-owner.md`), remove www.411trades.com from michaeltran (it was a
  former client's domain; show michaeltran on michaeltran.247advisers.com), decide the leftover Azure binding.
- 585: the Azure set-up steps in `DOCS/BUILDER_AND_STAGING_RUNBOOK.md` (on the Beta branch) before its release.
- 590: the MySQL upgrade (rehearse on a restored copy first; his steps at a quiet hour).
- Empty the Recycle Bin (2.2 GB of old snapshots).

## Do this first next session

1. Check his builders: 5087 and 5088 answer (restart with his Start-Builder scripts if not).
2. **585 re-check** (15 min): `ops\Start-BuilderBench.ps1 -SkipPublish` from the Beta worktree, then walk him
   through `585-walkthrough-recheck.md` in Edge, not the pane.
3. **593 try** (15 min, his phone) and his four decisions plus the two simplifications.
4. **592**: his three SELECTs in Cloud Shell, the 411trades.com removal, then release on his word and the
   after-release SELECT.
5. Ask what the tax offices said.
