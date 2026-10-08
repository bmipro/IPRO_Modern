# Session handoff -- 2026-10-07

## What happened

- **The bakery's short address went onto the platform** (553's first real run, the owner at GoDaddy): the TXT row,
  then an A row saved beside GoDaddy's two locked forwarding rows (three addresses, one visitor in three failing,
  8:52 to 9:04 a.m., while the code to delete the forwarding was awaited), forwarding deleted at 9:04, **Check now**,
  bound at 9:07, its certificate order accepted at once, secured at 9:15. Every old address of the previous site
  lands on its page; `about-us` was listed on the Home page (the starter About page was unpublished), the .com was
  made the primary domain again (the .ca had become primary), and the adviser starter pages left the sitemap.
- **A full check of the bakery's site afterwards** found it sound, three things for the bakery (two different
  phone numbers on the site: 416-333-4455 on the Call buttons, (416) 886-0458 in the contact block and footer; a
  typo, "bakey", in the home page's Search Description; Menu and Bread & Pastries sharing one search title) and
  three things true of every customer site -> **554**, built and gated 2026-10-07.
- **crm.to is back** (his question, then his steps at the .to registry, GoDaddy and the Azure portal): DNS hosted at
  GoDaddy, both names bound with managed certificates, forwarding as an alias name -> **555** so it lands on
  www.iproadvisers.com rather than the platform host. The first deploy of 554 needed a re-run: both runs ended
  "failure" with the build green and the deploy job never started.

## Pushed today

| Code | Item | Gate |
|---|---|---|
| `69685a5` | **554** what a customer site tells search engines and link checkers (live on build `50dd7ba`) | 1539/1539 |
| `57f70f5` | **555** a name that only forwards lands on the home page's own address (live on build `9ffe61c`) | 1542/1542 |

## Close-out 2026-10-07

- **Code today:** 554 (`69685a5`), live on build `50dd7ba` at 12:20 p.m.; 555 (`57f70f5`), live on build `9ffe61c` at
  1:48 p.m. Each gated in full first (1539, 1542) and verified at /health/version on both hosts with live checks. This
  close-out's docs push follows; its SHA is what both hosts show after it.
- **Outside the code:** 553's first real run (the bakery's short address, bound at 9:07 a.m., secured at 9:15); the
  bakery's site checked end to end and its owner's fixes read back (one phone number everywhere, "bakery", the .com
  primary, the starter pages out of the sitemap, `about-us` on Home); crm.to brought back (the owner's steps at the .to
  registry, GoDaddy and the Azure portal). The assistant's own change to `App__AliasHosts` and the certificate
  bindings were refused by the desktop app's safety check, so the owner made them; adding the two hostnames and
  ordering the certificates had gone through. The first deploy of 554 ended "failure" on both runs with the build
  green and the deploy job never started; a re-run of each succeeded (the chain's watch does not re-run that shape by
  itself: it was done by hand after an hour).
- **His, still to do:** his own site's **Site Title** reads "Copyright" (My Website), which 554 now shows as
  www.4ipro.com's search title; the bakery's Menu and Bread & Pastries pages share one search title; Refer a Friend
  is still OFF; "Shown in your emails as" for www.4iPro.com and www.LAvenueBakery.com.
- **Local machine:** the build servers shut down; the local app stopped after each look and its test rows removed;
  the blob emulator stopped; MySQL is the IPROLocalMySQL service, nothing to do.

## After the close-out: 556 (evening of 10-07)

- The owner asked how the bakery should show its hours; the site showed none and there was nowhere to enter them.
  **556** adds My Website > Footer > Hours (see `DOCS/TODO.md` 556 and `DOCS/04_WEBSITE_BUILDER.md`, "Show Your Hours").
- **His, once it is live:** enter the bakery's hours and pick "Bakery" as the kind of business (Footer > Hours), and
  keep the Google Business Profile hours the same. The editor card has not been seen signed in.
- The forms' consent line names the business now (it said "this adviser" on the bakery's contact form).

## Do this first tomorrow

1. **Teach the deploy watch the shape seen today:** a run that ends `failure` with its build job green and its deploy
   job `skipped` with no steps should be re-run like a cancelled one (the chain scripts' `watch_both`); today it sat
   for an hour. Look at whether the workflow itself can retry.
2. **His first look at what only a sign-in shows:** 554's two search boxes with their grey text on a page editor;
   553's setup step 2, Short address row and Old addresses; and from 10-05, the "Shown in your emails as" box, an
   e-card preview, SuperAdmin Referrals and Refunds. Fix whatever he finds.
3. **A prospect, 6 Tigers Academy (6tigers.ca):** a two-location karate school on Squarespace with Gymdesk for
   members and billing and Microsoft 365 mail. iPro can replace the site and add newsletters and e-cards; it does not
   do memberships or class billing, so the offer is beside Gymdesk, not instead of it. Only its home page and DNS
   were read.
4. **4ipro.com** is still on GoDaddy forwarding (`4ipro.com/about` is a 404 there); the two records fix it, at his
   convenience. Not built: a copy button beside the two values; a warning when `App:WebsiteAddress` drifts.
5. **Carried:** Refer a Friend once on; the first real sign-up after 551; SES 8.2 about 10-11
   (`Email__Ses__Streams` = `notify,news`, his change), then 8.3; a card and a letter on his phone after 548; the
   supplied e-card pictures still off; 539's images and licences; the stale sections of
   `DOCS/08_PUBLIC_REGISTRATION.md`; the calendar as in `DOCS/SESSION_HANDOFF_2026-10-02.md`, items 7 and 8.

Related: `DOCS/TODO.md` 553 and 554; `DOCS/SESSION_HANDOFF_2026-10-06.md` (yesterday's close-out and list).
