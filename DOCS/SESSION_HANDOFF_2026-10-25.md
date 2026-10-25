# Session handoff -- 2026-10-25 (covers 10-23 to 10-25)

No handoff file was written for 10-23 or 10-24; this one carries them. Written by the 603 session (the owner
asked it for tonight's close-out).

## Branches and state: read this first

- `main` = production. Live build at close: **244d4c4** (598-603) on both hosts since 15:01 (web) / 15:04 (admin),
  2026-10-25. Local `main` carried two docs commits (the 598-603 check-mark and this file); both go out with this
  close-out's push.
- What went live since the 10-22 handoff:

| Date | Build | Items | Gate |
|---|---|---|---|
| 10-24 (Sat, early morning) | 5227a54 | 596 .NET 10 step 1 (stack switch with the `DOTNET_ROLL_FORWARD` bridge; random `AdminPreview__SharedSecret` on both apps; Always On for admin) | 2,126 on d67fc8b |
| 10-24 | 900c888 | 595 Connect your domain: a Domain page with a numbered guide per domain | 2,180 on 8cad7a9 |
| 10-24 | f2de2de | 597 the client portal shows clients only their own appointments and sent documents | 2,190 on 10dee01 |
| 10-25 | **244d4c4** | 598 portal follows the plan and tells the adviser; 599 real Suspend with a yearly review; 600 refund credit notes; 601 Google Calendar in local time; 602 YouTube footer; 603 the domain guide shows where you are, "SSL" for "Padlock" | **2,229 on 244d4c4** (598-602 gated 2,226 on 4909532; 603 2,193 on a004000; then the merged tree) |

- 10-25 release checks: both deploy runs success; 0 5xx on either app after; admin's start-up ran the new schema
  steps (credit-note and suspension columns) with no failure; the only warning is the usual
  HttpsRedirectionMiddleware at restart.
- 599 after release: the owner ran the read-only SELECT in Cloud Shell (`WHERE IsActive = 0`): **Empty set**. No
  adviser was deactivated, so no live site went offline and nobody needs a back-dated `SuspendedAt`.

| Item | Branch (worktree under `.claude/worktrees/`) | State | Waiting for |
|---|---|---|---|
| 593 two-step sign-in | `claude/musing-hodgkin-64d1ee` | built and gated (fb6d9cb, 2,140), not live; needs main merged in and a new gate (main moved a lot) | **his 15-minute try** (`593-try-it.md` in the shared notes), then release |
| 585 builder Beta for customers | `claude/builder-beta-for-customers-2ebe5a` | Azure Parts A-C done 10-22 | Part D: the customer-builder patch (`596-builder-customer-beta.patch`), main merged in, `staging.yml` to dotnet 10.x, gate, first staging deploy on his word |
| 590 MySQL 8.0 to 8.4 | -- | open | before 2026-12-31 (extended-support charges from 2027-01-01); staging first |
| 596 step 2 and the bridge | -- | live on .NET 10 | remove the `DOTNET_ROLL_FORWARD` bridge on both live and both staging apps about 10-31; EF Core 10 waits for Pomelo |
| His two Template Builders | builder repo `ipro/beta` is on .NET 10 (e853cae) | **5087 and 5088 still run the .NET 8 build** (`ipro-builder-site\build`, runtimeconfig net8.0) | put the .NET 10 build in place before 2026-11-10, on his OK |
| Merged branches | `feat/598-portal-batch`, `feat/603-domain-guide-progress`, `release/598-603` | all in main | can be removed when convenient |

## Done outside the code

- www.iproadvisors.ca (his test domain) connected on the MichaelTran demo account on 10-24 through the new guide:
  live with its certificate (GeoTrust, to 2027-04-24), the bare name forwards to www. His feedback led to 603.
- The System Book is complete: Parts 1-6 in `C:\Users\admin\Documents\iPro_System_Book` (Part 6, "Dates and
  history", ch 26-28, 8 pages, written tonight: the calendar of dates ahead, what runs by itself, the history,
  every release since launch).
- A reminder fires at 8:00 a.m. on 2026-10-26 (scheduled task `test-release-244d4c4`, in the app's Scheduled list)
  with the list of what to test.

## Still his

- Test what went live on 10-25 (the reminder has the list); 601's 8-step check (`DOCS/601_TRY_IT.md`).
- 600: whether refunds marked before the release should get credit notes (needs a read-only SELECT).
- 411trades.com and ouritems.ca renew on 2026-11-24 at GoDaddy: check auto-renew.
- Sales tax decisions (from 10-22), and emptying the Recycle Bin of old backup snapshots.

## Do this first next session

1. His builders (5087, 5088): if down, he double-clicks Start-Builder.cmd and Start-Builder-For-Handovers.cmd.
2. His test of 598-603 (8 a.m. reminder); fix anything he finds.
3. System Book: refresh Part 3's chapters 12-16 for 597-603 (they describe the portal and domains before these
   releases) and print an edition 1.1.
4. 593: merge main, gate, his try, release. Then 585 Part D.
5. Before 10-31: remove the .NET 10 bridge. Before 11-10: the builders on the .NET 10 build.

## Close-out 2026-10-25

- This file and the 598-603 check-mark go out as tonight's docs push (both apps restart for about 90 seconds);
  the push is verified at `/health/version` on both hosts before the backups.
- Then two `git archive` zips of the pushed `main` (`C:\Users\admin\OneDrive\Codex_Code_Bkup` and
  `C:\Users\admin\Documents\IPRO_Backups`), and a recovery kit (`recovery_kit.sh`: full bundles of iPro and the
  builder, his two builders' data, notes and tools, restore proof) into the same two folders.
- `dotnet build-server shutdown`; nothing of this session left running. MySQL is the `IPROLocalMySQL` service:
  nothing to stop. His builders on 5087 and 5088 keep running and stop at a reboot.
