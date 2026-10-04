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

## Pushed today

| Code | Item | Gate |
|---|---|---|
| `38048b5` | **547** the e-card picker shows the whole card in its preview, four designs a row | 1414/1414 |
| `38be1d0` | **548** e-cards and e-letters fill a phone's width instead of being shrunk to fit | 1414/1414 |
| `e10bd93` | **549** a Price List / Menu block for agents' websites | 1435/1435 |

## Do this first when the owner is back

1. **547 and 548 are live** (build `ec9ad9a`). The test with him: he sends himself a card and a letter and reads them on his
   phone (Gmail app and his business mailbox) and in Thunderbird: the words at full size, the photo under the
   details on the phone, side by side on the computer. Outlook on Windows was not seen here.
2. **549, the Price List / Menu block, is live** (build `eb787ea`). Next the bakery's menus can be built on its site: Bread & Pastries (List) and Drinks and Meals (Compact, with a
   "Call to order" button on tel:). The block was seen on the real app locally in all three layouts; the editor
   itself is behind sign-in, so his first look at it is the first one with his data.
3. **The pilot: test 7 PASSED (10-04, 3:39 p.m.; runbook 7a has 5 to 7), the last test on the sheet.** Test 7's steps were sent to him on 10-04 (an account type "SES test 7", two clients at
   `bounce+news@` and `complaint+news@simulator.amazonses.com`, a newsletter to it). It is the last test he runs;
   test 4 is the watch: the web log of 10-03 and 10-04 has no SES failure, bounce or complaint, and Amazon's
   Reputation metrics are his to read in the SES console. Then the decision (runbook 7a): everyone's invoice-type
   mail (Streams back to `notify`, THEN clear the pilot list), and everyone's marketing mail a week later.
4. **536, Monday 2026-10-05:** his follow-ups mail around 7 a.m. Eastern.
5. **Carried, and the calendar:** as in `DOCS/SESSION_HANDOFF_2026-10-03.md`, items 7 to 9.

Related: `DOCS/TODO.md` 546 to 549; `DOCS/SESSION_HANDOFF_2026-10-03.md`.
