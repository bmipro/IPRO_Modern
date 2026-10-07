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

Related: `DOCS/TODO.md` 553 and 554; `DOCS/SESSION_HANDOFF_2026-10-06.md` (yesterday's close-out and list).
