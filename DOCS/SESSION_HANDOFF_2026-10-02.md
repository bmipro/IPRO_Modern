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
- **540 built and deployed (2026-10-02, `fab66d8`)** (offered because of that; his word: "go"): the suppressions made before 538
  get their reason from the email history, once, three minutes after the web app starts; an address the
  adviser had already corrected is switched back on. Held, then pushed at his word ("push it and let me know
  when it is fully out") at 22:56 with the docs commits held since the afternoon; both hosts on `149b3ff` at
  23:03. Its first run, 11:02 p.m. Eastern: 3 clients had no reason recorded; the email history found 1 bounce
  and 1 spam complaint; none was switched back on; 1 stays Unsubscribed.
- **541, the e-card design brief for the outside designer** (his ask, so the designer can work while we do
  "more important stuff"): written, with a sample of two current cards. His copy is
  `Documents\iPro_ECard_Design_Brief_2026-10-02\` (a PDF to forward, the text, the sample picture); the same
  brief is a Claude doc; `DOCS/ECARD_DESIGN_BRIEF.md` has it with the build notes.
- **The accountants brief** (`DOCS/VERTICAL_PAGE_BRIEF_ACCOUNTANTS.md`, the package contract for outside work)
  carried its brand-domains paragraph four times (484's docs step, 09-12); it is there once now (`149b3ff`).
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
| `fab66d8` | **540** clients blocked before 538 get their reason from the email history; an address already corrected is switched back on | 1367/1367 |

## Close-out 2026-10-02

Final build on both hosts before this close-out: `149b3ff` (23:03); this section's own docs commit follows it.
The day's code: 538 (`9a3e7c1`) and 539 (`cef28f0`) behind one full gate (1361), 540 (`fab66d8`) behind its
own (1367), each red first; a gate now runs as a build and four test parts that cover every test once (the
tool's new ten-minute limit). Outside the code: the SES pilot's test sheet and test 1 (runbook 7a), the answer
on Google Translate's fee, and 541, the e-card brief, which waits for his five points.

**State left:** the SES pilot unchanged (`Email__Ses__PilotAgentIds` and `Email__Ses__Streams` not touched
today); the web log has 540's line at 03:02:47 UTC and no error from the app since one Hangfire database
timeout on 10-01 at 10:38 a.m. Eastern. 540's backfill runs again at every web start and only ever looks at
clients still without a reason (today, the one left Unsubscribed), so it is harmless; a later release can
drop it.

Backups of the pushed HEAD: `IPRO_Modern_backup_<stamp>.zip` in `C:\Users\admin\OneDrive\Codex_Code_Bkup`
and `C:\Users\admin\Documents\IPRO_Backups`. Build servers shut down; no local app, emulator or test
process running; MySQL is the Windows service and needs nothing. Reboot-ready.

## Do this first tomorrow

1. **538, 539 and 540 are live** (`9a3e7c1`, `cef28f0`, `fab66d8`; nothing was held at the close-out; 541's
   answers, 10-03, are one docs commit held for the next push).
   His glance: the page editor's **Browse shared starter banners** and the newsletter's **Banner Image** show
   the two new groups; a client's record reads **Email bounced** or **Reported spam**, now also for the
   suppressions from before 538 that the email history could explain.
2. **The pilot, his next steps (one at a time, as he prefers):**
   - Test 2 passed 10-03: INV-1014 reached his Gmail Inbox from "Global Business Solution via iPro", with
     SPF, DKIM and DMARC all PASS and Reply-To his own address; INV-1015 reached his business mailbox (not
     Junk) with the same sender and Reply-To; both read Viewed and opened on his Android phone too. No Yahoo or
     Outlook.com mailbox to try (runbook 7a).
   - Test 3 (sent to him 10-03): an estimate (test 1's went to the bounce mailbox, never seen in an inbox), a
     **Send reminder** on an overdue invoice, **Invite to Portal**, **Request Testimonial**, each once to his
     Gmail test client.
   - Done on 10-02 after 538 went live: the complaint on a new client (INV-1010) reads "delivered ..., then
     reported as spam by the recipient"; the new bounce client (INV-1016) shows **Bounced** and its Edit form's
     note. Not yet seen: correcting that client's address and saving, which should switch its email back on.
   - Part 2 (marketing mail) needs his setting change (`Email__Ses__Streams` = `notify,news`, a restart).
   - The decision: runbook 7a, "The decision".
3. **540's first run** (11:02 p.m. Eastern, 10-02): 3 clients from before 538 had no reason; the email history
   found 1 bounce and 1 spam complaint; none was switched back on; 1 stays **Unsubscribed** (no bounce or
   complaint on record near its time). His glance: his complaint test client reads **Reported spam**, his
   first bounce test client **Email bounced** (the address he replaced it with bounced too; a real address
   switches it back on).
4. **539, his two to decide:** `friends_group.jpg` (identifiable people) and `wall.JPG` (a painted mural) are
   out until he says he has the rights. And one for him to confirm: several of the images look like purchased
   stock graphics (the eyes and globes, the silhouettes, the globe, the marble); sharing them with every adviser
   is fine only if iPro's licence allows passing them on. To add or drop one: `make_banners_539.py` in the
   session scratchpad cuts them, then a line in `WebsiteStarterBannerCatalog` and in `SharedBanners539Tests`.
5. **541 is ready to send** (his answers on 10-03: "40 ok, December first by Nov 15, assignment + waiver,
   English only"). The PDF in `Documents\iPro_ECard_Design_Brief_2026-10-02\` carries them; the fee stays out
   of the brief, his to agree with the designer. Part 1 (the 9 December designs) is due 15 November: load it
   the week it arrives, so advisers have it for December.
6. **536, Monday 2026-10-05:** his follow-ups mail, "4 follow-ups overdue", around 7 a.m. Eastern unless he
   completes them first.
7. **Carried:** the builder retest when the developer's fixes arrive; 532 (Refer a Friend) after 531; decisions
   on 520, 524, 528 and 529; the open list (506, the page-view tables' retention, an adviser's icon and logo,
   the comped plans' renewal date, 519, `AsSplitQuery` on the client Details page); optional HostPapa forwarders.
8. **The calendar:** clear `Email__TrackingSigningKeyPrevious` around 17 October; ACS closes to new customers
   23 October; the new customer's first renewal 25 October; Platinum's setup-fee waiver ends 30 October; .NET 10
   in October.

Related: `DOCS/TODO.md` 538 to 541; `DOCS/ECARD_DESIGN_BRIEF.md`; `DOCS/SES_GO_LIVE_RUNBOOK.md` 7a; `DOCS/SESSION_HANDOFF_2026-10-01.md`.
