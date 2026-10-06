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

Related: `DOCS/TODO.md` 532 and 551; `DOCS/SESSION_HANDOFF_2026-10-04.md` (yesterday's close-out and list).
