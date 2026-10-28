# Session handoff -- 2026-10-26

## Branches and state: read this first

- `main` = production. Live build at close: **ed773aa** (593 two-step sign-in, with 604-608 underneath) on both
  hosts since 20:56 (web) / 20:59 (admin), 2026-10-26. Local `main` carried two docs commits (the 593 check-mark
  and this file); both go out with this close-out's push.
- The owner tested the 10-25 release (598-603) in the morning: all seven checks passed (601 parts A-E, 600 by
  looking, 599 suspend and activate on a trial test adviser, SuspendTest #44, since deleted). His test found
  606 and 607; his 601 test found 606's second half, which became 608.
- What went live today:

| Time | Build | Items | Gate |
|---|---|---|---|
| 14:47 web / 14:44 admin | 72fbbe0 | 604 the privacy page names Amazon SES and drops SendGrid; 605 brand footers link Facebook to www.facebook.com/AllAdvisers; 606 the portal Schedule box starts at 9:00 AM and questions hours outside 7 AM-9 PM, and My Requests shows a moved time; 607 SuperAdmin's suspended note on its own line, Delete says "no PayPal subscription to cancel" when there was none; 608 the client is emailed when the adviser moves the appointment in Google | 2,240 on 72fbbe0 |
| 20:56 web / 20:59 admin | **ed773aa** | 593 two-step sign-in for the adviser portal (optional; app or email codes, 10 recovery codes, 30-day remembered device; a Dashboard box asks those without it; SuperAdmin can require or reset it per adviser). Main (598-609, .NET 10) merged into the 593 branch first | **2,285 on ed773aa** (593 alone had 2,140 on fb6d9cb) |

- Release checks: both deploy runs success both times; 0 5xx on either app after each release. After 593 the
  sign-in page answers 200 and the admin site still sits behind Azure's Microsoft sign-in (401 to a bare request,
  as before).
- 593 before release: his 15-minute try passed on the merged code on this PC (set-up by email codes, a sign-in
  code, the remembered device, a recovery code with 9 left, turning it off). He chose to launch it as is:
  optional, with the Dashboard box as the reminder. **Everyone signs in once more** after this release.

| Item | Branch (worktree under `.claude/worktrees/`) | State | Waiting for |
|---|---|---|---|
| 609 newsletters, series and e-cards to Amazon SES | -- | open; the SES runbook's step 8.2 slipped past 10-11. Amazon's figures read today: Healthy, bounce 0.01%, complaint 0.00%. The marketing-mail trial already passed 10-03/04 | **his change on the morning of 10-27**: `Email__Ses__Streams` from `notify` to `notify,news` on ipro-prod-web (a restart; moves every adviser at once; undo = set it back to `notify`); then one newsletter to his own mailboxes (From mail@news.iproadvisers.com, Delivered), then 8.3's alarms |
| 585 builder Beta for customers | `claude/builder-beta-for-customers-2ebe5a` | Azure Parts A-C done 10-22 | Part D: the customer-builder patch, main merged in, `staging.yml` to dotnet 10.x, gate, first staging deploy on his word |
| 590 MySQL 8.0 to 8.4 | -- | open | before 2026-12-31 (extended-support charges from 2027-01-01); staging first |
| 596 step 2 and the bridge | -- | live on .NET 10 | remove the `DOTNET_ROLL_FORWARD` bridge on both live and both staging apps about 10-31; EF Core 10 waits for Pomelo |
| His two Template Builders | builder repo `ipro/beta` is on .NET 10 (e853cae) | 5087 and 5088 still run the .NET 8 build | put the .NET 10 build in place before 2026-11-10, on his OK |
| Merged branches | `release/604-607`, `fix/605-footer-facebook`, `claude/musing-hodgkin-64d1ee` (593), and the 10-25 ones | all in main | can be removed when convenient |

## Done outside the code

- The old `Email__SendGridApiKey` was removed from both live apps on his word (14:53 web, 14:57 admin). Both run
  on `Email__Provider` = Azure, so nothing read it; both stayed up.
- Email today: advisers' invoice-type mail to clients (`notify`) goes through Amazon SES; newsletters, series,
  e-cards (`news`) still go through Azure Communication Services until 609; iPro's own mail to advisers stays on
  Azure by design (it must move before ACS email retires on 2028-09-30).

## Still his

- 609's switch tomorrow morning (above).
- Two-step on his own live adviser account: the Dashboard box, as in his try.
- The SUSPENDTEST invite code from the 599 test: switch it off in SuperAdmin.
- 600: whether refunds marked before the 10-25 release should get credit notes (needs a read-only SELECT).
- 411trades.com and ouritems.ca renew on 2026-11-24 at GoDaddy: check auto-renew.
- Sales tax decisions (from 10-22), and emptying the Recycle Bin of old backup snapshots.

## Do this first next session

1. His builders (5087, 5088): if down, he double-clicks Start-Builder.cmd and Start-Builder-For-Handovers.cmd.
2. 609: walk him through the setting change on ipro-prod-web, then the newsletter check and 8.3's alarms; tick 609.
3. Watch the first day of two-step: sign-in problems would show as lock notices or SuperAdmin resets.
4. Before 10-31: remove the .NET 10 bridge. Before about 11-03: prepare the second demo (a fresh check and an
   updated run sheet). Before 11-10: the builders on the .NET 10 build.
5. System Book: refresh Part 3's chapters 12-16 for 597-608 and 593, and print an edition 1.1. Then 585 Part D.

## Close-out 2026-10-26

- This file, the 593 check-mark and a correction to the 10-25 file (Part 6 has 10 pages and includes chapter 28,
  the open items) go out as tonight's docs push (both apps restart for about 90 seconds); the push is verified at
  `/health/version` on both hosts before the backups.
- Then two `git archive` zips of the pushed `main` (`C:\Users\admin\OneDrive\Codex_Code_Bkup` and
  `C:\Users\admin\Documents\IPRO_Backups`), and a recovery kit (`recovery_kit.sh`: full bundles of iPro and the
  builder, his two builders' data, notes and tools, restore proof) into the same two folders.
- `dotnet build-server shutdown`; nothing of this session left running (the local test server and the storage
  emulator used for his 593 try were already stopped). MySQL is the `IPROLocalMySQL` service: nothing to stop.
  His builders on 5087 and 5088 keep running and stop at a reboot.
