# Session handoff — 2026-10-02

## What happened

- **537a went out on 10-01 at 19:02** (`e425fce`): the package name on one line in SuperAdmin's Sign-ups by origin.
  Its check-mark (`7bfaeec`) and the pilot test sheet (`e1c4714`) were committed locally and held.
- **The owner asked for a list of the last two days' work** and got it, short.
- **Google Translate, his question ("is there a fee"):** the website dropdown costs nothing, but Google's own
  page restricts it to government, non-profit and non-commercial sites and points everyone else to the paid
  Cloud Translation service (about US$20 per million characters after 500,000 free a month, from third-party
  pricing pages). Several industry sites report the widget unsupported from 2026-10-01; Google's page showed no
  such notice on 10-02 and the widget still loaded. Advice given: do not build an adviser-site feature on it.
- **The Amazon SES pilot: a test sheet, and the first test run.** He asked for "a few tests with my
  bahmanmotamed account and aws so we could decide when to port it to aws for the rest of the system". They are
  `DOCS/SES_GO_LIVE_RUNBOOK.md` section 7a: Part 1 on the invoice-type mail (no setting changes), Part 2 on the
  marketing mail, which must be tried on his account BEFORE the pilot list is cleared (the list limits both
  streams). Starting point read that morning: the pilot unchanged (`PilotAgentIds` 2 characters, `Streams` 6),
  the web log clean since 09-30.
- **Test 1, run by him about 2:30 p.m.:** delivered, bounced, complaint, both clients suppressed, both notices to
  him, the log's hard-bounce line. It passed, and it found 538.
- **538 built and deployed (2026-10-02, `9a3e7c1`):** a bounce, a spam complaint and an unsubscribe each say what they are (the
  invoice, the client record, the notice to the adviser); a bounced address can be corrected by the adviser and
  email resumes; Amazon's "delivery delay" no longer marks an email failed for good.
- **539 built and deployed (2026-10-02, `cef28f0`)** ("can u also push these images for shared use among the agents", a folder of
  34): 27 are in the shared banner gallery, cut to its strip shape, in two new groups and three old ones. Seven
  are not; two of those are held for his word on the rights (below).
- **The owner tried 538 live, about 5:10 to 6 p.m.:** a complaint reads "delivered Oct 2, 5:12 PM, then reported
  as spam by the recipient" (INV-1010); a brand-new client sent to the bounce mailbox (INV-1016) reads Bounced,
  and its Edit form carries the new note ("An email to this client's address bounced on Oct 2, 2026 ..."). The
  web log had no error after the deploy. His two earlier test clients, suppressed before 538, stayed
  "Unsubscribed" when he changed their addresses.
- **540 built, pending deploy** (offered because of that; his word: "go"): the suppressions made before 538
  get their reason from the email history, once, three minutes after the web app starts; an address the
  adviser had already corrected is switched back on. HELD for the next push, as offered.
- **541, the e-card design brief for the outside designer** (his ask, so the designer can work while we do
  "more important stuff"): written, with a sample of two current cards. His copy is
  `Documents\iPro_ECard_Design_Brief_2026-10-02\` (a PDF to forward, the text, the sample picture); the same
  brief is a Claude doc; `DOCS/ECARD_DESIGN_BRIEF.md` has it with the build notes.
- **The tool that runs long jobs changed its limits:** a background command is now stopped at ten minutes even
  while the session is working, so a 47-minute gate cannot run as one job. The gate for 538 + 539 ran as one
  build and three test runs that together cover every test once.
- **Azure restarted the web app by itself at 02:22 UTC on 10-02** (22:22 Eastern on 10-01): no deploy and no
  setting change (the activity log shows only the 19:00 deploy). It came back healthy.

## Pushed today

| Code | Item | Gate |
|---|---|---|
| `9a3e7c1` | **538** a bounce, a spam complaint and an unsubscribe each say what they are; a bounced address can be corrected; a delay is not a failure | 1361/1361 |
| `cef28f0` | **539** 27 of the owner's images in the shared banner gallery, in two new groups | 1361/1361 |

## Do this first when the owner is back

1. **538 and 539 (deployed 2026-10-02, both hosts on build 1409dca):** one push, which also carried the two docs commits held
   since 10-01. The check-mark commit is held locally (a docs-only push restarts the site) and goes out with
   the next push or the close-out, so `main` is one commit ahead of `origin/main` on purpose. His glance: the
   page editor's **Browse shared starter banners** and the newsletter's **Banner Image** show the two new
   groups; a client's record reads **Email bounced** or **Reported spam** for a suppression made from now on.
2. **The pilot, his next steps (one at a time, as he prefers):**
   - Test 2: for the two invoices to his own mailboxes (INV-1014, INV-1015, both "Viewed"): which mailbox each
     went to, whether it landed in the Inbox, whether the sender reads "Global Business Solution via iPro" and
     whether Reply addresses him. Then the same to any mailbox provider not yet tried (Gmail, Yahoo, Outlook).
   - Test 3: an estimate (done, to the bounce mailbox), a **Send reminder** on an overdue invoice, **Invite to
     Portal**, **Request Testimonial**, each once to one of his mailboxes.
   - After 538 is live, a second complaint test on a NEW client (`complaint+two@simulator.amazonses.com`; the
     first one is suppressed) should read "delivered ..., then reported as spam by the recipient" and the notice
     "<client> reported one of your emails as spam"; a new bounce client (`bounce+two@...`) should show **Email
     bounced**, and correcting its address should switch email back on.
   - Part 2 (marketing mail) needs his setting change (`Email__Ses__Streams` = `notify,news`, a restart).
   - The decision: runbook 7a, "The decision".
3. **540 is in the working tree's history as a local commit, gated, NOT pushed:** it goes out with the next push
   or the close-out. About three minutes after that start the web log should carry "Suppression reasons from
   before 538: ..."; his complaint test client should then read **Reported spam**, and his first bounce test
   client **Email bounced** (still on hold: the address he replaced it with bounced too).
4. **539, his two to decide:** `friends_group.jpg` (identifiable people) and `wall.JPG` (a painted mural) are
   out until he says he has the rights. And one for him to confirm: several of the images look like purchased
   stock graphics (the eyes and globes, the silhouettes, the globe, the marble); sharing them with every adviser
   is fine only if iPro's licence allows passing them on. To add or drop one: `make_banners_539.py` in the
   session scratchpad cuts them, then a line in `WebsiteStarterBannerCatalog` and in `SharedBanners539Tests`.
5. **541, his five points before the brief goes to the designer:** the occasions and the count (40 across 20
   proposed), the dates, the fee, the rights wording, French greetings or not. A comment on the doc asks him
   about the first.
6. **536, Monday 2026-10-05:** his follow-ups mail, "4 follow-ups overdue", around 7 a.m. Eastern unless he
   completes them first.
7. **Carried:** the builder retest when the developer's fixes arrive; 532 (Refer a Friend) after 531; decisions
   on 520, 524, 528 and 529; the open list (506, the page-view tables' retention, an adviser's icon and logo,
   the comped plans' renewal date, 519, `AsSplitQuery` on the client Details page); optional HostPapa forwarders.
8. **The calendar:** clear `Email__TrackingSigningKeyPrevious` around 17 October; ACS closes to new customers
   23 October; the new customer's first renewal 25 October; Platinum's setup-fee waiver ends 30 October; .NET 10
   in October.

Related: `DOCS/TODO.md` 538 to 541; `DOCS/ECARD_DESIGN_BRIEF.md`; `DOCS/SES_GO_LIVE_RUNBOOK.md` 7a; `DOCS/SESSION_HANDOFF_2026-10-01.md`.
