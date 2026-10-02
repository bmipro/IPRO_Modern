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
- **538 built, pending deploy:** a bounce, a spam complaint and an unsubscribe each say what they are (the
  invoice, the client record, the notice to the adviser); a bounced address can be corrected by the adviser and
  email resumes; Amazon's "delivery delay" no longer marks an email failed for good.
- **539 built, pending deploy** ("can u also push these images for shared use among the agents", a folder of
  34): 27 are in the shared banner gallery, cut to its strip shape, in two new groups and three old ones. Seven
  are not; two of those are held for his word on the rights (below).
- **The tool that runs long jobs changed its limits:** a background command is now stopped at ten minutes even
  while the session is working, so a 47-minute gate cannot run as one job. The gate for 538 + 539 ran as one
  build and three test runs that together cover every test once.
- **Azure restarted the web app by itself at 02:22 UTC on 10-02** (22:22 Eastern on 10-01): no deploy and no
  setting change (the activity log shows only the 19:00 deploy). It came back healthy.

## Pushed today

| Code | Item | Gate |
|---|---|---|

## Do this first when the owner is back

1. **538 and 539:** built and gated; the push carries the two held docs commits as well.
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
3. **539, his two to decide:** `friends_group.jpg` (identifiable people) and `wall.JPG` (a painted mural) are
   out until he says he has the rights. And one for him to confirm: several of the images look like purchased
   stock graphics (the eyes and globes, the silhouettes, the globe, the marble); sharing them with every adviser
   is fine only if iPro's licence allows passing them on. To add or drop one: `make_banners_539.py` in the
   session scratchpad cuts them, then a line in `WebsiteStarterBannerCatalog` and in `SharedBanners539Tests`.
4. **536, Monday 2026-10-05:** his follow-ups mail, "4 follow-ups overdue", around 7 a.m. Eastern unless he
   completes them first.
5. **Carried:** the builder retest when the developer's fixes arrive; 532 (Refer a Friend) after 531; decisions
   on 520, 524, 528 and 529; the open list (506, the page-view tables' retention, an adviser's icon and logo,
   the comped plans' renewal date, 519, `AsSplitQuery` on the client Details page); optional HostPapa forwarders.
6. **The calendar:** clear `Email__TrackingSigningKeyPrevious` around 17 October; ACS closes to new customers
   23 October; the new customer's first renewal 25 October; Platinum's setup-fee waiver ends 30 October; .NET 10
   in October.

Related: `DOCS/TODO.md` 538 and 539; `DOCS/SES_GO_LIVE_RUNBOOK.md` 7a; `DOCS/SESSION_HANDOFF_2026-10-01.md`.
