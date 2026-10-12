# Session handoff -- 2026-10-12

## What happened

- The owner, first thing: "Lets do the GST/HST number on client invoices and push it with the Prininting fix. Also
  could layout the SES step 8.2 that is waiting for me".
- **569**, parked on the branch `hold/569-print-layout` the night before, was brought into the tree (the same two
  files) and gated with 570.
- **570:** a business's GST/HST number, typed once on the Profile page and printed on its invoices and estimates.
  See the row in `DOCS/TODO.md`.
- **SES step 8.2, laid out for him** (it is his change, `Email__Ses__Streams` = `notify,news` on `ipro-prod-web`):
  look at Amazon's Reputation metrics first (Healthy, bounce under 2%, complaint under 0.05%); change the one value;
  tell us; one test send. Read-only checks this morning: the setting is 6 characters long (`notify`), the pilot
  setting is absent, the four keys are present; the web log of 10-10 to 10-12 has no line naming an SES problem, and
  no failure at all since 561 went out (the 78 before it were the rate limiter's "invalid IP address").
- A mistake of mine, caught by the safety check and not run: a clean-up command that removed files after changing
  directory. Use a NEW folder for each download instead of clearing an old one.

## Pushed

| Commit | What | Gate |
|---|---|---|

## Still his

- **SES step 8.2** as laid out above. When he says it is saved: the setting should be 11 characters, the site Healthy,
  the log clean; then his one test send (From "... via iPro" at the news address, Delivered in Email Activity).
  Then 8.3 (the alarms) after a few days.
- **To use 570:** his GST/HST number on his Profile page; the bakery's on theirs.
- The bakery: type the phone number again in the three call buttons. Refer a Friend is still OFF. "Shown in your
  emails as" for www.4iPro.com and www.LAvenueBakery.com.

## Offered and open

A one-time "we do not charge tax" setting; a way to forget a mistyped remembered item; a managed "My items" price
list (held back); one row per day for the one-at-a-time automatic emails; the Open-now line once per page on the
bakery's home page. The template builder: his answers from the developer; ours are the importer for stage 1, the
consolidated contract v0.4 and a v0.5 draft.

## Not seen signed in

The Profile page's GST/HST number box (570) and the editor's reminder; and, from 10-11, the editor's live totals and
discount choice in use, the remembered items, Email Activity's period, pages, Needs attention tab and search. His
screenshots have shown the invoice document (tax boxes, an item discount, the print preview) and the Email
Activity list. His first use of each of the others is its first real look: fix whatever he finds.

Related: `DOCS/TODO.md` 569, 570, 531; `DOCS/SES_GO_LIVE_RUNBOOK.md` section 8; `DOCS/SESSION_HANDOFF_2026-10-11.md`.
