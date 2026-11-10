# Session handoff -- 2026-11-09

## Branches and state: read this first

- **Live (production):** portal and SuperAdmin on `3b036f66` since 7:29 PM (portal) and 7:36 PM (SuperAdmin)
  Eastern on 2026-11-09, on the owner's Run workflow and approval: 630 and 631. Live builder
  (builder.iproadvisers.com) on `11bc328`, unchanged.
- **Rollback for tonight's release:** iPro `809510ae` (651, live since this morning), by the owner's Run workflow
  and approval.
- **Staging:** iPro on this close-out's docs commit (no code after `3b036f66`); staging builder `11bc328`.
- **Branches:** `fix/630-631` is fully in `main` and can be deleted. Nothing is parked; no `hold/` branch.
- **Production database:** both iPro accounts on `caching_sha2_password` since about 8:10 PM (590 part 1). Still
  MySQL 8.0.46 on Standard_B1ms.

## What happened

| When (Eastern) | What | State |
|---|---|---|
| morning | 651 (the client list in pages): the owner's look and release, one step at a time from the hospital's small computer | **live** as `809510ae` |
| morning | 654: the builder's old Actions packages deleted (GitHub's 90% storage warning) | done |
| 11:20 AM | 653: the release's restart check waits about three minutes and never restarts an app that is starting (`677c23a9`; the step scripts run over five timelines each, DeployWorkflowTests 7 of 7) | live in the workflows; **first production run tonight: portal served at check 8, SuperAdmin at check 7, no restart** |
| afternoon | 630 (PayPal sign-ups stuck at Pending are caught up hourly; one shared PayPal access token) and 631 (seven SuperAdmin "new" forms keep only typed fields): built, related classes 325 of 325, full gate **2,587 of 2,587 at the first try**, committed as `c3ad7330` | **live** as `3b036f66` |
| afternoon | The Clients page at phone width: nothing to change. Headless Edge lays pages out at 500 px at least; at a true 390 px the page fits. 651's note corrected | closed |
| about 8 PM | 590 part 1: both database accounts switched to `caching_sha2_password` by two pastes the owner ran in Cloud Shell, each password read from its app's settings (nobody typed one) and proven before the change; afterwards new sign-ins by the live apps seen, sites 200, no errors | done |
| evening | 590 parts 2 and 3 written up: `DOCS/MYSQL84_UPGRADE_RUNBOOK.md` | rehearsal tomorrow, on the owner's word |

## Close-out 2026-11-09

| Code | What | Gate | State |
|---|---|---|---|
| `677c23a9` | 653 (release workflows) | step scripts + DeployWorkflowTests 7 of 7 | live in the workflows |
| `c3ad7330` | 630, 631 | 2,587 of 2,587 at the first try | live as part of `3b036f66` |
| `11bc328` | Builder | gated 10-30 | live, unchanged |

Backups of this commit (two snapshot zips, each opened and checked against git) and a recovery kit are in
`OneDrive\Codex_Code_Bkup` and `Documents\IPRO_Backups`.

## Do this first next session

1. After a reboot neither local builder runs: the owner double-clicks the two Start-Builder cmd files.
2. Check the hosts (`/health/version`): portal and SuperAdmin `3b036f66` (a later docs commit shows on staging
   only), live builder `11bc328`.
3. 590 part 2, the rehearsal, with the owner: `DOCS/MYSQL84_UPGRADE_RUNBOOK.md` from "Part 2". About an hour; his
   part is opening Cloud Shell and three pastes. Write the measured times into the runbook's part 3 and TODO 590.
4. 590 part 3 at a quiet hour after a clean rehearsal (the same evening, or another day if he prefers), well before
   2026-12-31.
5. 646 (keep your own website) waits for the owner's price; 615, 633, 642 and 645 wait as before.
6. Scratchpad tools disappear after about a week: restore from the newest notes-and-tools zip before a gate or release.
