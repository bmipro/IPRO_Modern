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

## The template builder, looked at again (afternoon)

- The owner wants to bring the builder in as a Beta and asked for a fresh look first, judged against what site
  builders are expected to do. Walked through his local build (127.0.0.1:5087) read-only: nothing saved, exported,
  restored or applied; two test edits were discarded by reloading. The server process is not ours (left running).
- Verdict given to him: good editing, not ready for customers. The five conditions before a Beta are in
  `DOCS/TODO.md` 467. Fixed since September: the split hero on a phone, the preview's device toggle, local times
  in the history, subheadings stored in sentence case.
- Corrected in the same conversation: contract v0.4 section 12 already says iPro opens only sites whose every block
  is an allowed export type, and keeps each page's SEO fields itself. So the block limit is our rule, and today the
  bakery could not use the Beta at all.
- The note asking the developer for a review package is in the owner's Documents folder (he forwards it). When the
  package arrives: code read-only, the exports through the v0.4 checker, a round trip, then a v0.5 draft.

## The developer's review package (evening)

- The owner had the developer package the builder while he was there: `C:\Users\admin\Projects\IPRO.TemplateBuilder\review-packages\2026-10-10`
  (a zip beside it; README, REVIEW-ANSWERS, CONTRACT-NOTES, EXPORT-PROVENANCE, build, source, six exports, design
  definitions, test results). Snapshot `review-2026-10-10`: UNCOMMITTED work on `a0ac178`, branch
  `feature/builder-ux-refresh`.
- **Checked, read-only, nothing of his executed:** all 358 files match the manifest's hashes; the handover fixture he
  worked from is ours byte for byte; the six exports pass the contract's stated rules (our own check); his return of
  our fixture after a heading edit differs by that heading alone, and after "Use this design" by nothing. So the
  September finding (a design replaced the customer's content) is fixed. His run records 232 .NET and 25 frontend
  tests passing; not re-run here.
- **The server code was read** (a delegated read, its main points confirmed line by line): uploads, package import,
  rendering and export are careful. It has no sign-in and no separation between customers, one global write limit,
  large images decoded in memory, Development mode only, and GrapesJS loaded from unpkg without an integrity hash --
  all of which he says himself; it cannot be hosted for customers as it is. Two new small defects: line breaks in body
  text are very likely not rendered (`TemplatePageRenderer.cs` line 554 encodes, then replaces a newline the encoder
  has already turned into a character reference; read in the code, not seen running), and `null` passes where the
  contract wants an empty string for `imageRef` / `logoRef`. One point of the delegated read was wrong and dropped:
  "both button fields or neither" and "file names at most 100 characters" are our own clarifications 6.2 and 6.14 of
  29 September.
- **The look:** his design files show seven palettes, seven font pairings, header choices, section tones and four
  gallery designs that exist only in the builder. Contract section 5 deliberately left the live look to iPro's own
  designs. Options put to the owner: (A) hide those controls when a site comes from iPro; (B) carry palette, font
  pairing and header choice across onto our existing overrides (accent, background, font; a heading/body font pair and
  a dark palette have no place on our side yet); (C) one shared design system, which is the paused template redesign.
  Recommended: A for internal use, B for the customer Beta. Undecided.
- **NOT done:** the hands-on pass on his review copy. `Start-Review.ps1` serves it on port 5090; it was not listening
  on three checks, and his build was not started from here. The copy on 5087 is his working copy: look only.
- **For our side, when the exporter is built:** our clarification 6.2 says a Text block carries both button fields or
  neither. A live block with button text and no link (the bakery's, after the link bug of 558) must be sent with the
  link it actually opens, or the builder refuses the package. We also owe him a consolidated CONTRACT.md v0.4 with the
  29 September clarifications in it, and a yes to replacing the stale v0.3 file at the root of his repository.
- The two check scripts are kept beside our fixtures (`Documents\iPro_TemplateBuilder_v0.4\checker\`): run them with `python -I` from outside the
  package, the package folder and our fixture as arguments. The September checker was lost with a cleared scratchpad.

## Pushed

| Commit | What | Gate |
|---|---|---|
| `f9a264e` | **560** the Reviews block shows reviews the owner picked (live on build `9e1573c`) | 1588/1588 |
| `15c6824` | **506 and 434** a domain's CAA records are read and explained; a website lead can be deleted (live on build `9e1573c`) | 1588/1588 |

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

## Close-out 2026-10-10

- **Code today:** 560 (`f9a264e`) and 506 with 434 (`15c6824`), live on build `9e1573c` at 11:37 a.m., gated together
  (1588/1588), verified at /health/version on both hosts with live checks. This close-out's docs push follows; its SHA
  is what both hosts show after it. Two backups of the pushed HEAD (OneDrive `Codex_Code_Bkup`, Documents
  `IPRO_Backups`).
- **Local machine:** the build servers shut down; the local app, the static preview and the blob emulator stopped and
  their tabs closed; the local test site's rows put back; `.claude/launch.json` restored; MySQL is the IPROLocalMySQL
  service, nothing to do. The builder on 127.0.0.1:5087 is the developer's process: left running.

## Do this first next time

1. **The owner's answers on the template builder:** the two stages (internal use, then a customer Beta)? The look: A,
   then B? A go-ahead to the developer on six small fixes (no formatting buttons on plain text; "Unsaved changes" only
   after a change; Preview labelled as save-then-preview; the photo choice shown correctly on a customer's site; line
   breaks in body text; no `null` image references). From the developer: the commit id of this snapshot, and his
   estimate for hosting the builder for many customers at once.
2. **The hands-on pass** on the review copy once it runs on port 5090 (save, restore, the plain-text toolbar, a body
   with two lines, nested pages in the menu, a page from each blueprint), then the findings note for the developer
   and the shape of contract v0.5 (the look, locked placeholders).
3. **SES step 8.2** was due about 10-11 (`Email__Ses__Streams` = `notify,news`, his change), then 8.3.
4. **His first look at what only a sign-in shows:** the leads page with Delete, the reviews rows in the block editor,
   the Business hours card on My Website. Fix whatever he finds.
5. **Carried:** the bakery's three call buttons to re-save; his answer on the Open now line once per page; Refer a
   Friend still OFF; "Shown in your emails as" for www.4iPro.com and www.LAvenueBakery.com; 4ipro.com still on GoDaddy
   forwarding; the rest of the open list above.

Related: `DOCS/TODO.md` 506, 434, 560; `DOCS/SESSION_HANDOFF_2026-10-09.md` (557, 558, 559).
