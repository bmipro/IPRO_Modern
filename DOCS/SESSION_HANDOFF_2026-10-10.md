# Session handoff -- 2026-10-10

## What happened

- The owner asked for the open list by importance. Three lines in `DOCS/TODO.md` were stale and he caught two of
  them: PayPal has been live since 2026-09-21 (two real customers have signed up since), and the legal pages were
  approved by counsel and released on 2026-08-17. The third was a certificate date (the `*.247advisers.com` wildcard
  runs to 2027-01-25, read live). All three are corrected. **Lesson: read the later handoffs before repeating a
  "standing" line from the to-do file.**
- He then asked for 506 and 434 together, then Google reviews, and "once the gate passes push it".
- **506:** the domain check reads a customer domain's CAA records while its certificate is pending; when DigiCert is
  not on the list, My Website shows the one record to add, and the operator's alert names the cause.
- **434:** a website lead can be deleted (one, or the ticked ones), with its form answers; "All" is "All open" and
  leaves dismissed leads out.
- **560:** the Reviews block takes up to six reviews the owner pastes in (name, stars, words) and shows them as cards
  under the rating, with an optional "Review us on Google" link. Nothing is fetched from Google.

## Pushed

| Commit | What | Gate |
|---|---|---|

## The open list, as agreed with the owner on 2026-10-10 (shorter by three)

**Protects the business**
1. Email move to Amazon SES (531): under way; his step 8.2 (`Email__Ses__Streams` = `notify,news`) was due about
   10-11, then 8.3.

**Brings revenue**
2. Masoud corporate prospect, about 20 agents (463): waiting on the owner's demo.
3. Bring-your-own-website package (520): his to decide.
4. Dealer-compliance mode (519): future, larger build.
5. Broker / team / white-label (378): needs his A-or-B decision.
6. 6 Tigers Academy prospect: noted only.

**Website builder**
7. The template builder (467 and what waits behind it: the announcement bar 528, the pop-up 529). He wants to talk
   about it next.
8. Hours extras: two openings in a day, dated holiday hours, the Open now line only once per page.
9. Google reviews, later step: the rating and the count kept current from Google (a paid API key, a place ID per
   customer, Google's attribution rules). Only if wanted.
10. The marketing site rebuild (412); the phone hero and sign-up questions (437, 438): parked for the marketing phase.

**Large, later**
11. SMS reminders; in-portal payments (needs a merchant account); real-estate listings; social auto-publishing; more
    vertical starter packs; voice dictation (524).

**Housekeeping**
12. Billing regression tests, battery 2 (418b); one flaky test; a retry inside the deploy workflow; the staging
    decision; this machine's access (471).

## His, small

- The bakery: type the phone number again in the three call buttons (Home, Bread & Pastries, Drinks and Meals).
- Refer a Friend is still OFF; "Shown in your emails as" for www.4iPro.com and www.LAvenueBakery.com.
- To use the new reviews cards: open a site's Reviews block, "Reviews to show".

## Not seen signed in

The leads page with its Delete buttons; the reviews rows in the block editor; the CAA note on My Website (it shows
only for a domain whose certificate is pending and whose CAA list leaves DigiCert out); the Business hours card in
its place on My Website (559). The owner's first visit to each is its first real look: fix whatever he finds.

Related: `DOCS/TODO.md` 506, 434, 560; `DOCS/SESSION_HANDOFF_2026-10-09.md` (557, 558, 559).
