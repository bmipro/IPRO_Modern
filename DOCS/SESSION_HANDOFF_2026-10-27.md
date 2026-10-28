# Session handoff -- 2026-10-27

## Branches and state: read this first

- `main` = production. No code was released today. The live code is still `ed773aa` (593 two-step sign-in, with
  598-609 underneath); the live build string is the docs commit on top of it (`f07a7f6` before tonight's docs push,
  the close-out push's SHA after it).
- Tonight's docs push carries: 609 ticked (8.2 and 8.3), row 610 (found and done), the to-do file's tidy, and this file.
- Side worktree `.claude/worktrees/598-portal-batch` (branch `feat/598-portal-batch`, head `b2fb701`) is fully in main
  through `release/598-603`; it can be removed when convenient, with the merged branches listed on 10-26.

## What happened today (all outside the code)

| Time (Eastern) | What | State |
|---|---|---|
| Afternoon | Quick health check before his demo: both hosts Healthy on f07a7f6, 0 5xx in 24 h on both apps, pages 0.1-0.26 s, every certificate valid (nearest: the *.247advisers.com Sectigo wildcard, 2027-01-25) | his demo "went well" |
| 4:14 PM | **609 step 8.2**: he set `Email__Ses__Streams` from `notify` to `notify,news` on `ipro-prod-web` in the portal; read back by name; web restarted 20:14 UTC, start-up probe passed in 40 s, no warning or failure in the log | done |
| 4:16 PM | His test newsletter (`POST Newsletter/Send` 20:16:32 UTC) reached his Gmail Inbox from "iPro Advisers Inc. via iPro" <mail@news.iproadvisers.com>; Amazon's report back at 20:16:36 (`POST SesEmailEvents` 200); ACS showed no API request around it | done |
| ~4:45 PM | **609 step 8.3**, done in his AWS console with him watching: CloudWatch (Canada Central) alarms "SES bounce rate over 2 percent" (`Reputation.BounceRate` > 0.02) and "SES complaint rate over 0.05 percent" (`Reputation.ComplaintRate` > 0.0005), Average over 1 hour, missing data as good, both to the new SNS topic `ipro-ses-alarms` (two of his addresses, both Confirmed). Not `ipro-ses-events`, which carries delivery reports to iPro | done; the bounce alarm OK, the complaint alarm Insufficient data at creation |
| Evening | **610**: the staging database `ipro-mysql-staging` had autoscale IOPS on (billed per I/O, C$0.67 a day while idle); switched off on his word ("go for it") with `az mysql flexible-server update --auto-scale-iops Disabled`; read back Disabled, 360 IOPS, Ready. `ipro-mysql-prod` untouched (already Disabled) | done; check the meter stops |
| Evening | Backups confirmed for him: `ipro-mysql-prod` daily, 35 days, Canada East, not geo-redundant (latest 2026-10-27 17:15 UTC); iPro's own nightly dump in `iprostorageprod/db-backups`, 31 copies, latest 06:15 UTC, 1.5 MB; `ipro-mysql-staging` daily, 7 days | as expected |

## The System Book: complete

All in `Documents\iPro_System_Book` (never the public repo):

- **Part 3 edition 1.1** (20 pages): chapters 12-16 rewritten for 597-609 and 593 from the live code; the "found" pages are
  now a status list (12 of 16 done). The edition 1 PDF is kept.
- **Appendices A and B** (8 pages): A lists all 139 documents (93 reference documents in nine groups, 46 handoffs);
  B is the monthly costs read from the bills: Azure (Cost Management, actual, CAD) C$105.54 in September, C$86.78 for
  October 1-26, about C$140 a month expected from November; Amazon about US$30 a month (Business Support+ US$29 plus
  SES), paid from starter credits (US$118.79 left on 10-27, expire 2027-09-29, run out around January). Azure's
  first-year free offer still covers the database processing (meter "B1MS Compute - Free") until about mid-2027;
  from November two databases exceed its 750 hours (about C$18 a month, estimate).
- **Open-work list** (3 pages, `open_work_2026-10-27.html`, PDF `iPro_Open_Work_2026-10-27_updated.pdf`): replaces
  Part 6, chapter 28.

## Done by him today

- 411trades.com and ouritems.ca are on auto-renew at GoDaddy.
- support@ forwarding tested: it reaches the mailbox he reads.
- Two-step sign-in turned on for his own adviser account ("I love it").
- The SUSPENDTEST trial invite code switched off.
- The 601 Google Calendar check had already passed on 10-26 (he asked; the 10-26 handoff records it).

## The to-do file tidy

17 rows and headings that still read open were checked and closed with "Tidy 2026-10-27": 412, 418b, 442, 467
(now 585), 477, 531 (done with 609), the staging and secretary-mode decisions, the unsubscribe check, the August
deploy item, and five stale headings. Still open from the old sections: 471 (his decision on the Azure CLI's rights
on this PC), 396 (watch), the flaky-test watch, the 8 low audit candidates never attacked, the one-time cleanup of
orphan answer rows, and the setup-fee line never seen on an existing adviser's invoice.

## Open items with dates

| Item | Branch | State | Waiting for |
|---|---|---|---|
| 610 check | -- | setting changed 10-27 | Cost analysis on 10-29/30: the "Paid IO LRS IO Rate Operations" meter for `ipro-builder-staging` stops |
| 596 bridge | -- | live on .NET 10 | remove `DOTNET_ROLL_FORWARD` on both live and both staging apps about 10-31, on his word |
| Second demo | -- | -- | a fresh health check and an updated run sheet before about 11-03 |
| His two Template Builders | builder repo `ipro/beta` on .NET 10 (e853cae) | 5087 and 5088 run the .NET 8 build | the .NET 10 build in place before 11-10, on his OK |
| 585 builder Beta for customers | `claude/builder-beta-for-customers-2ebe5a` | Azure parts A-C done 10-22 | Part D: the customer-builder patch, main merged in, `staging.yml` to 10.x, gate, first staging deploy on his word |
| 590 MySQL 8.0 to 8.4 | -- | open | before 2026-12-31, staging first |
| *.247advisers.com certificate | -- | Sectigo, manual | find the purchase, renew or replace; start early January (expires 2027-01-25) |

## Still his

- 600: credit notes for refunds marked before 10-25 (a read-only SELECT first).
- Sales tax outside Ontario (QST, PST, RST registration).
- The 69 stock photos' licences, and the sources of four pictures added since.
- Dropping the two old sample packages (Review/1, Review/2) on his site.
- Emptying the Recycle Bin of old backup snapshots.
- 471: whether to narrow the Azure CLI's rights on this PC.
- Optional: the blank cost lines in Appendix B.

## Do this first next session

1. His builders (5087, 5088): if down, he double-clicks Start-Builder.cmd and Start-Builder-For-Handovers.cmd.
2. From 10-29: the 610 meter in Cost analysis (read-only query, `azure_costs.py` style: `az rest` POST to
   Cost Management, daily granularity, group by ResourceGroupName and Meter).
3. Before 10-31: remove the .NET 10 bridge (his word). Before about 11-03: demo prep. Before 11-10: the builders.
4. Then 585 Part D.
