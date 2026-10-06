# Session handoff -- 2026-10-05

## What happened

- **536's morning email arrived:** his follow-ups mail came in about 7 a.m., and he confirmed it reached his mailbox.
- **L'Avenue Boulangerie signed up** (the bakery 549 was shaped for), and nothing told him: "as an admin to this
  system I did not get any email saying that someone has registered. I dont think we have that mechanism. If we
  dont. Lets do it so I/admin get an email." There was none -> **551**, deployed 2026-10-05 (build `20f147f`).
- **Refer a Friend (532) is on:** "Also if you are doing that we might as well do the refere a friend system too"
  -> **built** on the design he decided on 2026-09-29 (`DOCS/TODO.md` 532), deployed 2026-10-05 (build `20f147f`). It ships OFF:
  he switches it on in SuperAdmin -> Referrals. Not built: the automatic-refund switch ("manual ... for now").
- **His question, the e-card's web address** ("when I sent a ecard the domain that it shows is the temporary site ... I want it to show
  www.4iPro.com"): there was no setting -- every card, letter and newsletter printed the free 247advisers.com
  address -> **552**, deployed 2026-10-05 (build `e82b606`). His step now: My Website -> Shown in your emails as:
  www.4iPro.com -> Save.
- **The deploy of 551 and 532 waited about an hour on GitHub** (an Actions outage: the admin build was cancelled before
  its first step and re-run); both hosts on the pushed build at 5:27 p.m., then the check-marks and two backups.

## Pushed today

| Code | Item | Gate |
|---|---|---|
| `8c87500` | **551** iPro is emailed about every sign-up, and again when the subscription starts | 1457/1457 |
| `49ec2aa` | **532** Refer a Friend -- Give $50, Get $50 (ships OFF) | 1489/1489 |
| `66e0e74` | **552** cards, letters and newsletters show the adviser's live custom domain | 1498/1498 |

## Close-out 2026-10-05

- **Code today:** 551 (`8c87500`) and 532 (`49ec2aa`), live together on build `20f147f` at 5:27 p.m.; 552
  (`66e0e74`), live on build `e82b606` at 9:00 p.m. Each gated in full first (1457, 1489, 1498), each verified at
  /health/version on both hosts with live checks. This close-out's docs push follows (552's check-mark and this
  section); its SHA is what both hosts show after it.
- **Outside the code:** GitHub's Actions outage held the first deploy about an hour (the admin run cancelled before
  its first step, re-run; the chain's watch now outlasts that). PayPal's sandbox took both gift plan shapes and read
  them back at 13% (scratchpad `paypal_probe_532.py`). The friend's sign-up page was seen locally at phone and desktop
  width; no signed-in page was seen (no password is ever typed).
- **His, still to do:** Refer a Friend is OFF until he switches it on (SuperAdmin -> Referrals). My Website -> Shown
  in your emails as: www.4iPro.com -> Save (552). The sign-up notices go to support@iproadvisers.com.
- **Local machine:** the build servers shut down; the local app was stopped after the look and its test rows removed
  (the four Refer a Friend tables stay in the local database, empty); Azurite ends with the reboot; MySQL is the
  IPROLocalMySQL service, nothing to do.

## Do this first tomorrow

1. **His first look at what only a sign-in shows** (nobody has seen these live): the "Shown in your emails as" box
   and an e-card preview reading www.4iPro.com (552); SuperAdmin -> Referrals, and Refunds -> Referral rewards; an
   adviser's Refer a Friend page with the Profile and Dashboard cards (they show once the program is ON); the credit
   note. Fix whatever he finds.
2. **Refer a Friend, once he switches it on:** follow the first referral through -- the sign-up notice naming the
   referrer, the friend's first invoice lines, the `refer-a-friend` job's line in the web log (twenty past each
   hour, at Warning), the "joined" email. The first reward is about a month after the first friend joins. The
   automatic-refund switch is still to build (his "later").
3. **The first real sign-up after 551:** two emails at support@iproadvisers.com ("New sign-up", then "Subscription
   started"). If he does not see them there, `Signups__NotificationEmail` on the web app is the setting (his change).
4. **SES:** 8.1's first week (the web log, his Reputation metrics); **8.2 about 10-11** (`Email__Ses__Streams` =
   `notify,news`, his change); then 8.3's CloudWatch alarms.
5. **The bakery (L'Avenue Boulangerie) has signed up:** its two menu pages with the Price List / Menu block (Bread &
   Pastries as List; Drinks and Meals as Compact with "Call to order" on `tel:`), with him.
6. **Carried:** a card and a letter on his phone after 548; the few supplied e-card pictures still off; 539's two
   images and the stock-image licences; the stale sections of `DOCS/08_PUBLIC_REGISTRATION.md` (offered to him as a
   separate task); the calendar as in `DOCS/SESSION_HANDOFF_2026-10-02.md`, items 7 and 8 (TrackingSigningKeyPrevious
   about 17 Oct, ACS closed to new customers 23 Oct, the renewal 25 Oct, the Platinum waiver 30 Oct, .NET 10).

Related: `DOCS/TODO.md` 532, 551 and 552; `DOCS/SESSION_HANDOFF_2026-10-04.md` (yesterday's close-out and list).
