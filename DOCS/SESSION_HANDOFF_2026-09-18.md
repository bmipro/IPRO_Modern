# Session handoff — 2026-09-18 (3 days to launch)

## What happened

- **494 (morning):** the owner noticed that the home page sells a fourth starting point, **Generic**, that
  no form offered. The neutral content already existed (the "All" starter pages, forms and articles every
  vertical falls back to), so Generic is the business type that takes exactly that set. One shared list
  (`StarterBusinessTypes.Offered`) now feeds sign-up, the profile, the preview and SuperAdmin's agent
  editor; the preview's whitelist is the same list and an unknown type falls back to Generic instead of
  Accountants; the daily-assistant preview speaks each type's own language (it had insurance copy for
  everyone); Generic gets three neutral calculators. Details in `DOCS/TODO.md` 494.

- **495 (afternoon):** the owner's follow-up -- content written FOR the Generic edition, and a button on
  the panel that sells the four editions. Six Generic starter pages (the shared set's home page read
  "Professional professional services..."), eight articles in two categories drafted by two
  content-writer agents to a strict brief and edited, two forms; seeded so they reach the already-seeded
  production tables; all editable under SuperAdmin -> Starter Content. The panel's two buttons carry the
  chosen edition into sign-up and the preview. Details in `DOCS/TODO.md` 495.

- **496, the truth sweep of the public pages (afternoon):** the owner asked what else was left after
  the Generic gap. Two read-only reviewers checked every promise a prospect reads before paying
  against the code; `DOCS/TRUTH_SWEEP_2026-09-18.md` is the record. Built the same day: a new account's
  site is created AND published at sign-up (only the Publish button did it; the welcome email's main
  button opened a 404); the landing pages' setup-fee test (it would have hidden a charged fee from
  1 October); the plan table's three unbacked rows; the live preview's stale plan descriptions; the
  phone number published (owner: 1-416-363-2220) so "one number to call" is true; the hero badge,
  the Terms naming Microsoft, and about twenty wording corrections. Details in `DOCS/TODO.md` 496.

- **497, 498, 499 -- three of the sweep's open items, built the same evening (owner: "it is very
  important in my view and why wait?"):** the doubled Calculators menu on accountants' sites (497);
  the daily follow-ups email that puts the withdrawn "Email reminder" row back with something real
  behind it (498: `FollowUpReminderJob`, hourly, the adviser's own morning, once a day, its own small
  table rather than columns on AgentUsers, a profile switch, and two App Service settings that hold it
  or switch it off without a deploy); and eight-article libraries for the Insurance / Financial and
  Mortgage editions, written by content writers, read line by line, with the rules for regulated
  subjects pinned by tests (499). Building 498 found that a follow-up's due time is the adviser's OWN
  date, not UTC -- now `DOCS/INVARIANTS.md` rule 10 -- and that Google Calendar sync treats it as UTC
  (not fixed; sweep doc item 11). Details in `DOCS/TODO.md` 497-499.

- **500, a wart 499 made and the live preview showed (evening):** production's starter library holds
  eight articles the owner added himself in SuperAdmin (five Mortgage, three Insurance), unknown to the
  code. His "Buying a home?" made 499's category "Buying a Home" a near-twin in the menu and pushed the
  category page of every new mortgage site to `/buying-a-home-2`. Renamed the same evening ("Home Buying
  Basics", "Managing Your Mortgage"), with a one-time rename of the rows already written. Rule for next
  time: read the live preview's menu for the edition before naming starter content. And a second
  mistake of mine the same hour: I sent the owner to "Starter Content" to read the articles; that page
  lists PAGES. Articles are under **Starter Articles** (`/WebsiteStarterArticles`), its neighbour.

- **The local environment, fixed rather than avoided (afternoon):** a local look at 494 failed because
  IPRO.Web never listened. Cause: `ops\Start-LocalEnv.ps1` ran `Start-Process "azurite"`, which on this
  machine opens npm's extensionless shim in Notepad instead of running it, so the blob emulator never
  started and two start-up steps sat retrying 127.0.0.1:10000. The script now calls `azurite.cmd` and
  reports whether port 10000 came up; DOCS/16 says so, and says how to stop a Browser-pane run (close
  the tab first, or the pane restarts the app and its process locks the next build). The owner's
  words: "why can't you fix it instead of avoiding it?"

## Pushed today

| Code | Item | Gate |
|---|---|---|
| `4ae2eaa` | **494** the Generic edition is offered as a business type | 894/894 |
| `e493950` | **495** the Generic edition's own starter pack; buttons on the starting-point panel | 901/901 |
| `0a8914b` | **496** truth sweep of the public pages: the site goes live at sign-up; every promise checked | 930/930 |
| `259efee` | **497** one Calculators entry on an accountant's Resources menu | 961/961 |
| `24a9335` | **498** the daily follow-ups email; the plan row back under a true name | 961/961 |
| `a8cae45` | **499** article libraries for Insurance / Financial and Mortgage | 961/961 |
| `2acc4d0` | **500** the Mortgage library's categories renamed: they collided with the owner's own article | 963/963 |

## Close-out 2026-09-18

Close-out as asked when the owner left the office (about 18:45), after seven items in one day. Final
build on both hosts before this close-out: `59acc62`. Tree clean and pushed. The day's code and gates are
in the table above (494 to 500); nothing is half-built and nothing is waiting for a deploy.

**Left for the owner, first thing in the morning (his words):** switch off his three legacy starter
articles. SuperAdmin -> **Starter Articles** (`admin.iproadvisers.com/WebsiteStarterArticles`, the item
below Starter Content, which lists pages only). In the group *Insurance / Financial (uncategorized)*:
**Critical Illness** -> Deactivate, **Life Insurance** -> Deactivate. In *Mortgage (uncategorized)*:
**Using a Mortgage Professional** -> Deactivate. The grey Deactivate button, NOT the red trash; Restore
undoes it. The reviewed articles on the same subjects stay active. Then tell the assistant, who checks
the live preview's menus (`/Preview/Site?...&businessType=Mortgage` and `Insurance / Financial`).

**Saturday morning is also the first run of the follow-ups email (498):** about 7:05 a.m. in each
adviser's own time zone, only where something is due that day or fell due within the last week. Look at
`follow-up-reminders` on the Job Scheduler dashboard and at Email Activity. To hold it until launch day
the owner adds `FollowUpReminders__NotBefore` = `2026-09-21` on ipro-prod-web (Environment variables
blade, no deploy); `FollowUpReminders__Enabled` = `false` switches it off.

**Outside the code today:** content writers (background agents with strict briefs) wrote the Generic
pack and the two edition libraries; every article was read line by line before it was wired in, and a
generator refuses anything that breaks the rules. The owner's decisions of the day: publish the phone
number (1-416-363-2220); "Free preview, no card needed"; the Terms name Microsoft, no counsel needed;
build the follow-ups email and the libraries now ("why wait?"); retire one of each duplicated article.
Two mistakes of mine, both caught the same evening and written down where they will be found: a
category name chosen without looking at what production already held (500), and sending the owner to
Starter Content for articles that live under Starter Articles.

**Found and NOT fixed (needs the owner's Google account):** Google Calendar sync reads a follow-up's
date as UTC (`DOCS/TRUTH_SWEEP_2026-09-18.md` item 11; `DOCS/INVARIANTS.md` rule 10).

Backups of the pushed HEAD: `IPRO_Modern_backup_<stamp>.zip` in `C:\Users\admin\OneDrive\Codex_Code_Bkup`
and `C:\Users\admin\Documents\IPRO_Backups`. Build servers shut down; the local blob emulator (Azurite,
started this afternoon for the local look) stopped; MySQL is the Windows service and needs nothing.
Reboot-ready.

## Do this first tomorrow

0. **Owner, first thing (two minutes):** the three Deactivate clicks in the close-out section above, then
   a look at whether the first follow-up emails went out at 7:05 (Job Scheduler, Email Activity).
1. **PayPal live cutover (owner's call on the day, Friday or Saturday):** production is still sandbox;
   portal-to-portal (the owner enters the live client id, secret and webhook id), then the webhook and a
   first live charge path verified together, with margin before the 21st.
2. **Owner-side:** the DNS switch for the four live names on or before 21 September (DOCS/14 -- APPEND to
   `App__AliasHosts`, verify all four names); counsel's short read of the privacy policy's 14/17 September
   changes when convenient; the designer's items and the /insurance package when ready.
3. **The morning follow-ups email (498) goes for the first time on the morning after this deploy:** about
   7:05 a.m. in each adviser's own time zone, only to advisers with something due that day or overdue
   within the last week. Watch the `follow-up-reminders` row on the Job Scheduler dashboard and Email
   Activity. To hold it until launch day the OWNER adds the App Service setting
   `FollowUpReminders__NotBefore` = `2026-09-21` on ipro-prod-web (Environment variables blade; no
   deploy); `FollowUpReminders__Enabled` = `false` switches it off altogether.
3-bis. **Owner, in SuperAdmin -> Starter Articles (`admin.iproadvisers.com/WebsiteStarterArticles`; NOT Starter
   Content, which lists pages):** (1) "Retire one" (owner, 18 September) -- untick **Active** on your
   "Life Insurance", "Critical Illness" and "Using a Mortgage Professional"; the reviewed articles on the
   same subjects stay. Nothing is deleted. (2) When convenient, give your other five a Category so they
   are not loose links: Mortgage Process Table, Buying a home? -> `Home Buying Basics`; Mortgage Life
   Insurance, Mortgage Glossary -> `Managing Your Mortgage`; Travel Insurance -> `Protection`. Reasons in
   `DOCS/TODO.md` 500.
3a. **Owner's calls left by the truth sweep (`DOCS/TRUTH_SWEEP_2026-09-18.md`, last section):** insurance
   wording inside the portal for every business type; preview template vs real template; three stale
   lines in the Accountants library; exports for a lapsed account; **Google Calendar sync reads a
   follow-up's time as UTC (item 11) -- fix it together, on the owner's calendar**; the follow-up list's
   "today" is the server's date (item 12). Done the same day: the follow-ups email (498), the
   Calculators menu (497), the two article libraries (499) -- **the owner's read of the sixteen
   articles is still wanted** (SuperAdmin -> **Starter Articles**, `/WebsiteStarterArticles` -- NOT Starter
   Content, which lists pages only; regulated subjects).
3b. **Owner, when convenient:** read the Generic pack -- pages under SuperAdmin -> Starter Content, articles
   under Starter Articles, forms under Starter Forms, three neighbours in the sidebar (six pages, eight
   articles, two forms) and edit anything that does not sound like IPRO; every adviser who picks Generic
   from now on starts from it.
4. **Launch morning (Monday 21 September):** watch both health endpoints, the Job Scheduler dashboard and
   Email Activity with the owner while the first sends go out; a blast should read **In progress** with a
   rising total and one Information line a minute in the log, never Failed. Bulk mail goes at 80 an hour.
5. **Calendar:** clear `Email__TrackingSigningKeyPrevious` on both apps around 17 October; Microsoft quota
   re-application mid-October; .NET 8 leaves support on 2026-11-10 -- move to .NET 10 in October.
6. **After launch:** the list at the end of `DOCS/NARROW_AUDIT_2026-09-17.md`; then 471, 450, the From
   display name (480), the Entra client secret rotation (482); white-label per `DOCS/WHITE_LABEL_UPGRADE.md`.

Related: `DOCS/TODO.md` 494-500; `DOCS/SESSION_HANDOFF_2026-09-17.md`; `DOCS/NARROW_AUDIT_2026-09-17.md`.
