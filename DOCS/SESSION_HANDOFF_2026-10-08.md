# Session handoff -- 2026-10-08

## What happened

- **556, opening hours, is live** (`d25f6b1`, on build `bd31a53` at 8:12 p.m. on 10-08; gate 1550/1550). The owner asked
  on the evening of 10-07 how the bakery should show its hours, then "build it", then "push it". My Website > Footer >
  **Hours**: a row a day, a note, the kind of business. Shown in the footer, on the agent-info card and beside the
  contact form, as an Open now / Closed now line under a banner's button, and in the structured data. Nothing renders
  until hours are set; the live checks found none shown on the bakery, www.4ipro.com or the owner's temporary address,
  and every site still says `ProfessionalService` until a kind is chosen. Details: `DOCS/TODO.md` 556 and
  `DOCS/04_WEBSITE_BUILDER.md`, "Show Your Hours".
- **The forms' consent line names the business** (same commit): the bakery's contact form now reads "I agree that
  L'Avenue Boulangerie Inc. may use my information to respond to this request." It said "this adviser". This is every
  customer's contact, download and custom form: the company name, else the person's name.
- **The owner's first look at two signed-in screens** (10-07, by screenshot): the page editor's two search boxes with
  their hint and Old addresses (554, 553), and the short-address setup step on My Website (553). Both read as built.
- **The deploy watch** in the push script (`chain_556.sh`, scratchpad) now re-runs a run that ends `failure` with no
  job failed and its deploy job skipped before any step, at most twice. It was not needed today: both runs succeeded
  first time. The workflow files themselves are unchanged.

## Pushed

| Commit | What | Gate |
|---|---|---|
| `d25f6b1` | **556** opening hours, entered once and shown across a site (live on build `bd31a53`) | 1550/1550 |
| `05388c4` | **557** the hours are picked from lists, and the footer line is a choice (live on build `aae6490`) | 1552/1552 |

## Close-out 2026-10-08

- Both hosts verified at /health/version after the code push, and again after this close-out's docs push (its SHA is
  what they show now). Two backups of the pushed HEAD: OneDrive `Codex_Code_Bkup` and Documents `IPRO_Backups`.
- **Local machine:** the build servers shut down; the local app and the static preview stopped and their tabs closed;
  the local test site's rows put back (website 2: no custom domain, empty footer settings, template 1, the added block
  removed); `.claude/launch.json` restored; the blob emulator stopped; MySQL is the IPROLocalMySQL service, nothing to do.

## After the close-out: 557 (later on 10-08)

- The owner entered the bakery's hours (556's first real use). Two things came back: the save was refused because the
  browser's time boxes had no AM/PM, and he did not want the hours in the footer. **557**: the times are dropdowns,
  and the footer line is a tick-box that is off by default (see `DOCS/TODO.md` 557).
- **His:** the bakery's Tuesday opening time was saved as 8:39 a.m.; it shows in the Tuesday dropdown to be corrected.
  The kind of business was set to "Cafe or coffee shop" at the time of his screenshot.
- Item 1 of the list below is done apart from that correction.

## 10-09: 558 and 559 (one commit)

- **558:** the bakery's "Call to order" and "Give us a call" buttons had fallen back to /contact: a tel: link whose
  number starts with a bracket was dropped without a word. Fixed, and a link that is not kept is now said so.
  **His:** type the number again in the three buttons (Home, Bread & Pastries, Drinks and Meals) and save each.
- **559:** the owner found it confusing that the hours were entered under Footer. The card is on My Website now
  ("Business hours"), with a tick-box for each place the hours show. Item 2's "a switch to hide the hours on one
  card" in the list below is done by this.
- The bakery's hours had shown as empty right after 557 went live and were back in his next screenshot; he did not
  say whether he had cleared them. Nothing in a deploy writes saved settings.

## Do this first tomorrow

1. **His, for the bakery:** My Website > Footer > Hours: enter the hours, pick "Bakery" as the kind of business, Save
   Hours; then read the home page, the contact page and the footer on a phone. Keep the Google Business Profile hours
   the same. The Hours card has only been seen as served markup (the page needs a sign-in), so this save is its first
   real use: fix whatever he finds.
2. **Not built in 556:** two openings in one day (the note covers a lunch break); dated holiday hours; a switch to hide
   the hours on one card.
3. **Still unseen signed in:** 553's Short address row in the domain manager; from 10-05, the "Shown in your emails
   as" box, an e-card preview, SuperAdmin Referrals and Refunds.
4. **The deploy workflow:** look at whether the workflow itself can retry the "failure, nothing failed, deploy
   skipped" shape, rather than the push script doing it.
5. **A prospect, 6 Tigers Academy (6tigers.ca):** a two-location karate school on Squarespace with Gymdesk for members
   and billing and Microsoft 365 mail. iPro can replace the site and add newsletters and e-cards; it does not do
   memberships or class billing, so the offer is beside Gymdesk, not instead of it. Its kind of business is in 556's
   list ("Gym, studio or sports school"). Only its home page and DNS were read.
6. **4ipro.com** is still on GoDaddy forwarding (`4ipro.com/about` is a 404 there); the two records fix it, at his
   convenience. Not built: a copy button beside the two values; a warning when `App:WebsiteAddress` drifts.
7. **Carried:** Refer a Friend is still OFF; "Shown in your emails as" for www.4iPro.com and www.LAvenueBakery.com;
   the first real sign-up after 551; SES 8.2 about 10-11 (`Email__Ses__Streams` = `notify,news`, his change), then
   8.3; a card and a letter on his phone after 548; the supplied e-card pictures still off; 539's images and
   licences; the stale sections of `DOCS/08_PUBLIC_REGISTRATION.md`; the calendar as in
   `DOCS/SESSION_HANDOFF_2026-10-02.md`, items 7 and 8.

Related: `DOCS/TODO.md` 556; `DOCS/SESSION_HANDOFF_2026-10-07.md` (554, 555, the bakery's domain, crm.to).
