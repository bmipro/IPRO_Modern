# Session handoff -- 2026-10-04

## What happened

- **546 is live** (pushed on his word, "push it and let me know when it is fully out"; build `da038dc`). The admin site
  restarted first and its start-up added the 49 designs at 10:10:32 a.m. (its container log); the web app found them
  present. Details in `DOCS/SESSION_HANDOFF_2026-10-03.md`, item 5.
- **His answers:** 541, the outside designer brief, is not needed ("NO": his own collection arrived as 546). On
  phones: asked for a recommendation (yes: cards and letters were a fixed 620 px a phone shrinks to half-size words),
  then "go ahead with the reflow for both" -> **548**. On his computer the picker showed five designs a row and a
  preview that cut the card off: "can u make it 4 so we could see the live preview in whole" -> **547**.
- **Told him:** the old "Luxury car" birthday design has "Happy Birthday" lettered into its picture, so the title
  shows twice; the fix is his click in SuperAdmin -> E-Card Designs -> Luxury car -> Words in the picture: "The title
  is lettered in". **Done by him** (his screenshot, afternoon).
- **The nine supplied e-card pictures:** he switched most of them on himself (his licences): "There only a few that
  are not turned on. I approved the others."
- **His verdict on 547 to 549:** "It was an amazing experience. Everything fit like a glove and honestly I could not
  find anything I dont like."
- **A menu for a bakery he hopes to sign** (La Venue Bakery, lavenuebakery.com, two menus: Drinks and Breads): he
  liked a generic **Price list / Menu** block over a bakery-only one ("looks as good for a bakery and accountant")
  and the three layouts -> **549**, now live.
- **547 and 548 deployed (2026-10-04, build `ec9ad9a`), then 549 (build `eb787ea`)** on his word ("push it"; 547 and 548
  went first while 549's test run finished).
- **Pilot test 7 passed** (3:39 p.m.); Amazon's Reputation metrics (his screenshot): Healthy, bounce 0.00%,
  complaint 0.00%, 4 emails in 24 hours. The newsletter's page read the complaint "Unsubscribed -- complaint:
  abuse" beside the client's "Reported spam" -> **550** ("Yes please"), deployed 2026-10-04 (build `b2fb6dd`).

## Pushed today

| Code | Item | Gate |
|---|---|---|
| `6142ff0` | **546** the 2026 e-card collection: 49 approved designs across 21 occasions (built 10-03, pushed 10:08 a.m.) | 1407/1407 |
| `38048b5` | **547** the e-card picker shows the whole card in its preview, four designs a row | 1414/1414 |
| `38be1d0` | **548** e-cards and e-letters fill a phone's width instead of being shrunk to fit | 1414/1414 |
| `e10bd93` | **549** a Price List / Menu block for agents' websites | 1435/1435 |
| `0ef88cb` | **550** a spam report reads Reported spam on the newsletter's page, and is not a failure | 1443/1443 |

## Close-out 2026-10-04

- **Code today:** 546 (build `da038dc`), 547 and 548 (`ec9ad9a`), 549 (`eb787ea`), 550 (`b2fb6dd`), each gated in
  full first, each verified at /health/version on both hosts with live checks. This close-out's docs push follows
  (it carries the check-marks of 547 to 550 and the SES notes); its SHA is what both hosts show after it.
- **Outside the code, his:** SES step 8.1 (every adviser's invoice-type mail through Amazon since 5:02 p.m.; the
  stream setting `notify`, the pilot list deleted; checked by name and length, the log clean after the restart);
  pilot test 7 passed and Amazon's figures read Healthy (0.00% / 0.00%); most of the nine supplied e-card pictures
  switched on; the Luxury car design set to "title lettered in"; 541 dropped.
- **Local machine:** the build servers shut down; no dotnet or test host of this session left; Azurite (the blob
  emulator started for 549's local look) ends with the reboot; MySQL is the IPROLocalMySQL service, nothing to do.

## Do this first tomorrow

1. **536, Monday 2026-10-05, about 7 a.m. Eastern:** his follow-ups mail goes out (iPro's own mail stays on ACS:
   the SendMail metric shows it).
2. **SES 8.1's first week:** other advisers' invoices, reminders, portal invitations and testimonial requests now go
   through Amazon -- the web log (no SES failure, bounces and complaints handled), Email Activity, and his
   Reputation metrics mid-week. **8.2 about 10-11** (`Email__Ses__Streams` = `notify,news`, his change), then 8.3's
   CloudWatch alarms. The way back at any time: clear `Email__Ses__Streams`.
3. **The bakery (L'Avenue Boulangerie), once he signs it:** two pages with the Price List / Menu block -- Bread &
   Pastries as List, Drinks and Meals as Compact with "Call to order" on `tel:`; "What it lists" = Food and drink.
4. **Still to see with him:** a card and a letter on his phone after 548 (full-size words, the photo under the
   details); the Price List editor's first use with real data (549).
5. **His, when ready:** the few supplied e-card pictures still off (as licences are confirmed).
6. **Carried, and the calendar:** 539's two images and the stock-image licences; 532 Refer a Friend (later, his
   word); the calendar as in `DOCS/SESSION_HANDOFF_2026-10-02.md`, items 7 and 8 (TrackingSigningKeyPrevious
   about 17 Oct, ACS closed to new customers 23 Oct, the renewal 25 Oct, the Platinum waiver 30 Oct, .NET 10).

Related: `DOCS/TODO.md` 546 to 550; `DOCS/SES_GO_LIVE_RUNBOOK.md` (8.1 done); `DOCS/SESSION_HANDOFF_2026-10-03.md`.
