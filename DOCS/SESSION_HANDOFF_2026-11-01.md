# Session handoff -- 2026-11-01

## Branches and state: read this first

- **Live (production):** portal and SuperAdmin on `2e2b95d8` since 2:02 PM Eastern on 2026-11-01; live builder
  (builder.iproadvisers.com) on `11bc328`, unchanged. The Beta is on for the owner's own adviser account only.
- **Rollback for today's release:** iPro `05e7e25b`, by the owner's Run workflow and approval.
- **Staging:** iPro on today's docs commit, its code the same as live; staging builder `11bc328`.
- `feat/622-627-batch` and `feat/632-639-644-batch` are fully in `main` and can be deleted.
- **New columns and tables made at start-up today:** `GoogleRevocations`, `ContactEmailSuppressions`,
  `GoogleCalendarConnections.FailedRuns` and `.StoppedSyncingAt`. Both live apps' start-up logs show no failure.
- **No releases from Wednesday 11-04 until the second meeting** (the owner agreed the plan).

## What happened today

| Time (Eastern) | What | State |
|---|---|---|
| Morning | 601 across the clock change, on the owner's account: a Google event at 11:30 PM on 11-05 landed on 11-05 in iPro; an appointment scheduled in iPro for 11-05 at 10:00 AM showed 10:00-11:00 in Google | **passed** |
| Morning | Pre-demo check: all public pages, both demo sites, 0 5xx in 24 h, certificates, email | clean |
| Morning | Run sheet rewritten for the second meeting; the owner corrected two things: the meeting is toward the end of this week (not 11-03, date unconfirmed), and www.411trades.com is iPro's own demo domain on MichaelTran (my old line said otherwise) | done |
| 10:27 AM-12:33 PM | Gate on the second batch: **2,444 of 2,445**. The one failure was the batch's own new test, which seeded the same adviser three times; a filter on the file's name, not the class's, meant it had not run before the gate | fixed |
| 12:40 PM | The test corrected with no change to `src`, its class re-run 50 of 50 | done |
| 12:45 PM | Every changed screen opened on the local app with rows dated 8:30 PM Mountain on 11-01; the Profile notice seen; the temporary rows and plan change put back | clean |
| 12:57 PM | Staging on `2e2b95d8`, no start-up errors | done |
| 2:01 and 2:02 PM | Live: SuperAdmin, then portal, on the owner's Run workflow and approval, a day ahead of the plan on his word | **live**, 0 5xx in the 30 minutes after, no failures in either start-up log |

## The second batch (632, 639, 644)

- **632, the audit's E-4:** the Google Calendar sync runs one at a time, no automatic retries.
- **632, E-3:** eight failed runs in a row (two hours) email the adviser once and show a notice under Calendar Source on
  Profile. The job keeps trying, and a run that works, or Reconnect, clears it. E-2, E-5 and the audit's other rows stay open.
- **639:** 16 views printed UTC, wider than the row's eight, including three client portal screens that called
  `ToLocalTime()`, which changes nothing on Azure's clock. A test now refuses it in any view.
- **644:** Amazon and Azure events carry their own short name in the opt-out source (`ses:spamreport:newsletter`). Rows
  already written stay as they are and read the same.

## New

- **646 Keep your own website**, to start after the second demo. The owner's decision is the price; the row has the order
  and estimates, about two working days in all.

## Not seen yet

- The stopped-syncing notice and email on a real broken Google connection, and a real email: tests and a local page only.
- The dates on Refer a Friend and Payments: both screens opened, neither had a dated row on the local account.
- 623, 624, 625, 627 and the exports for a limited account, as on 10-31: automated tests only.
- The first real referral, 620 on a real sign-up, 617 on the two-step page in a browser, as on 10-31.

## Open items with dates

| Item | State | Waiting for |
|---|---|---|
| Second meeting | toward the end of the week of 11-02 | the owner to confirm the date and time; a fresh check that morning |
| Sales tax outside Ontario | the one piece of the friend's advice still open | the owner's calls, or an accountant |
| 645 new PC | arrives this week | after the meeting |
| 615, 616 builder pilot | unblocked | the owner's choice of 2-3 advisers |
| 590 MySQL 8.0 to 8.4 | not started | mid-November; upgrade before 2026-12-31 |

The other standing items are as in `DOCS/SESSION_HANDOFF_2026-10-31.md`.

## The run sheet

The doc "iPro Demo Run Sheet" holds the current text. The print copy is
`Documents\iPro_Demo_Run_Sheet_2026-11_updated.pdf`; the file without `_updated` was open in the app when it was printed,
so it could not be overwritten and is out of date.

## Close-out 2026-11-01

The owner closed the week here: all of it worked, and the question they asked last was whether every line developed and
tested is live.

| Code | What | Gate | State |
|---|---|---|---|
| `a7bf43a7` | First batch, 622-627 | 2,420 of 2,420 | live as part of `2e2b95d8` |
| `2e2b95d8` | Second batch, 632 E-3 and E-4, 639, 644; test fix for 644 | 2,444 of 2,445, the class re-run 50 of 50 after the test fix, no full gate repeated | **live** on portal and SuperAdmin |
| `11bc328` | Builder | gated 10-30 | **live**, unchanged |

**Is every line that was built and tested live? Yes, read from the hosts and from git on 2026-11-01:**

- Portal and SuperAdmin serve `2e2b95d8`; the only later commits on `main` touch documents, 0 code files.
- The live builder serves `11bc328`, the builder's `main`.
- `main` equals `origin/main`, 0 uncommitted files, 0 stashes, none of the 23 worktrees holds uncommitted work, no remote
  branch outside `main`.
- **Eight local branches are not merged by commit id, and their work is in `main` anyway**, under other commits or
  rewritten since: 582, 584, 587, 588, 589, 592, 594, `hold/576-locked-boxes`. 98-100% of the code lines of six of them
  are in `main` word for word. 587's 15 test methods were folded into `ClientPortalOneAdviser586Tests.cs`, and 594's
  wording moved to `Website/Domains.cshtml`. The few remaining lines were not traced one by one. The branches can go when
  the owner wants them gone.
- **Live but not on for everyone:** the website builder Beta is on for the owner's own adviser account only, until they
  choose the pilot advisers (615, 616).
- **Not the same as live, on purpose:** the owner's two local builders (5087, 5088) run `build-e853cae` from `ipro/beta`.
  That branch's one extra commit, the .NET 10 move, is already in the live builder.

Backups of this commit and a recovery kit are in `OneDrive\Codex_Code_Bkup` and `Documents\IPRO_Backups`. Azurite, which
I started for today's local walk, is stopped; the owner's builders on 5087 and 5088 are left running.

## Do this first next session

1. After a reboot neither local builder runs: the owner double-clicks the two Start-Builder cmd files.
2. Check the hosts (`/health/version`): portal and SuperAdmin `2e2b95d8` (a later docs commit shows on staging only),
   live builder `11bc328`.
3. Before the meeting: a fresh check that morning. No releases from Wednesday.
4. After the meeting: 646, on the owner's price decision, and 645.
5. Scratchpad tools disappear after about a week: restore from the newest notes-and-tools zip before a gate or release.
