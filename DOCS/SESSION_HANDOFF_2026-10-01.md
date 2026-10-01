# Session handoff — 2026-10-01

## What happened

- **Gold's setup-fee waiver lapsed overnight, as the owner intended.** The public pricing table reads Gold
  "+ $200 one-time setup" and Platinum "waived until October 30"; the owner: "looks good as expected".
- **The morning follow-up email "stopped" on 23 September: it was the rule, not a fault** (the owner: "am i
  suppose to get follow-ups for today email everyday? it seems like Sept 23 was the last email i got"). The email
  goes on a morning when a follow-up is due that day or fell due within the last 7 days. His four open
  follow-ups were due Sep 4, 9, 16 and 16; the newest reached the one-week mark on Sep 23 and nothing has been
  due since. No errors from the job in the web log for Sep 28 to 30.
- **536 built and deployed (2026-10-01, `7b92979`):** of three options (leave it; a weekly reminder; a daily mail while anything is
  overdue) he chose the second ("obvious #2 thx for suggestion"). On the adviser's own Monday a mail goes while
  anything is still overdue, however old; the daily rule is unchanged. The email's foot, the My Profile help
  line and guide 02 say when it comes.
- **537 built and deployed (2026-10-01, `066c730`)** (the owner, with "linkedin / organic_social / ipro_relaunch -- 1" under Sign-ups
  by origin: "we know someone did but we dont know who it was. Can we have the number of signups to be clickable
  that will show who had signed up"): in SuperAdmin -> Reports -> Visitors each count opens the names, newest
  first: the adviser (linked to their record), company, email, package, when they signed up and the page they
  landed on.
- **One push for both, verified on both hosts (build 9e924a6)**, as he asked ("fix and push /depoly at the same time").
- **The owner looked at 537 with real data: "clicked and it looks good".** One thing on his screen, the package
  name broken in two ("IPro" / "Platinum"), became **537a, built, held ("fix it with the next push") and deployed that evening (2026-10-01, `e425fce`) on his
  "let me know when it is fully out"**.

## Pushed today

| Code | Item | Gate |
|---|---|---|
| `7b92979` | **536** the morning follow-up email also goes every Monday while anything is still overdue | 1338/1338 |
| `066c730` | **537** SuperAdmin Visitors: the sign-up count opens the names of who signed up | 1338/1338 |
| `e425fce` | **537a** the package name stays on one line in the names under Sign-ups by origin | 1338/1338 |

## Do this first when the owner is back

1. **537, seen by the owner with real data (2026-10-01: "clicked and it looks good"):** SuperAdmin -> Reports ->
   Visitors -> **Sign-ups by origin** -> the number beside "linkedin / organic_social / ipro_relaunch" opened the
   one sign-up: the Platinum customer of 25 September, named and linked, with company, email, package, "Signed up
   Sep 25, 2026 6:24 PM ET" and "landed on www.iproadvisers.com/". Nothing left to check on 537 itself.
2. **537a (deployed 2026-10-01 in `e425fce`, both hosts on build e425fce):** on his screen the package name broke in the
   middle ("IPro" / "Platinum"); it now stays on one line (`text-nowrap` on that span). Held on his "fix it
   with the next push", sent on his "let me know when it is fully out". His glance: SuperAdmin -> Reports ->
   Visitors -> the number beside the LinkedIn origin; "IPro Platinum" reads whole. The check-mark commit for
   537a is held locally (a docs-only push restarts the site) and goes out with the next push or the close-out,
   so `main` is one commit ahead of `origin/main` on purpose.
3. **536, Monday 2026-10-05:** around 7 a.m. Eastern his mail should arrive, "4 follow-ups overdue", listing
   the four from September, unless he completes them first (then no mail, correctly). If it does not come:
   the web container log for `FollowUpReminderJob`, and `AgentFollowUpReminders.LastDecidedOn` for adviser 12.
4. **The Amazon SES pilot (carried from 09-30, his to run):** his own test sends should each show "delivered"
   in the document's email list; then the two simulator checks (`bounce@simulator.amazonses.com`,
   `complaint@simulator.amazonses.com`); then everyone (runbook step 8) on his go. The way back, any time:
   clear `Email__Ses__Streams` (restart). `DOCS/SES_GO_LIVE_RUNBOOK.md`.
5. **Carried:** the builder retest when the developer's fixes arrive; 532 (Refer a Friend) after 531; decisions
   on 520, 524, 528 and 529; the open list (506, the page-view tables' retention, an adviser's icon and logo,
   the comped plans' renewal date, 519, `AsSplitQuery` on the client Details page); optional HostPapa forwarders
   from `mail@notify.iproadvisers.com` and `mail@news.iproadvisers.com` to support@.
6. **The calendar:** clear `Email__TrackingSigningKeyPrevious` around 17 October; ACS closes to new customers
   23 October (ours keeps working); the new customer's first renewal 25 October; **Platinum's setup-fee waiver
   ends 30 October** (the owner extended it; Gold's lapsed 30 September); .NET 10 in October.

Related: `DOCS/TODO.md` 536, 537 and 537a; `DOCS/SESSION_HANDOFF_2026-09-30.md`.
