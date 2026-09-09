# Session handoff — 2026-09-08 and 09-09 (12 days to launch)

## What happened

- **Ticket 2608310040012537 (442):** Microsoft declined the quota increase on volume (28 messages in
  30 days, 09-04). A reframed reply went out today: the domain only moved to ACS on 30 August, the
  launch is 21 September, "send until throttled" means throttling real customers on launch day,
  engagement tracking asked for first, the original 500/min and 10,000/hour restated, hygiene
  evidence attached. If declined again, launch runs at 100/hour (every sender queues and retries;
  newsletters spread over hours) and we re-apply with 30 days of launch data.
- **Prospect (463):** a corporate customer, about 20 insurance agents on a private package, sending
  from their own corporate domain. Assessed in `DOCS/MASOUD_PROPOSAL.md`; waiting for the owner's
  demo. What works today: a hidden custom package, an invite code capped at 20, 20 accounts, no DNS
  pointing needed. What would be built: a per-organization sending domain verified on ACS (about
  two days), a billed-by-invoice mode if they want one invoice (about a day). Condition: the ACS
  quota, or a dedicated ACS email resource for that customer.
- **464 shipped:** the public package table stops selling four things we do not offer (Coupon
  manager, Need analysis calculator, Did you know manager, Get a quote form with email function),
  withdrawn the 28-August way (definitions gone, codes retired so existing databases lose the rows at
  startup, constants removed). Seven real features renamed on every row to three-to-five-word names the owner approved
  (Website leads inbox; Social posts: draft and track; Website menu editor (3 levels); Built-in SEO
  and sitemap; Email delivery tracking; Content in any language; Website analytics). Tests
  red-first; full gates; deployed and verified on both hosts.
- **465 shipped:** a Social links block that shows the footer's social profiles in the page body,
  icons only or icons with names, in all three templates, with one shared icon map and a package
  row; guide section in 04.

| commit | what | gate |
|---|---|---|
| `5f12fd7` | **464** four package-table rows withdrawn, two renamed | 700/700 (31m58s) |
| `b9514e6` | **464 (2)** seven rows shortened to three to five words (owner-approved wording) | whole tree |
| `8f81cfd` | **465** Social links block: the footer's profiles in the page body, icons or icons with names, all three templates, one shared icon map; a package row; guide section | whole tree |
| `4029218` | **466** help articles open with 'In this guide', a list of links to their sections (three or more) | whole tree |
| `451aed5` | **468** (09-09) a Help icon on every portal page opening the guide and section for that page; HelpLinksTests keeps the map complete | whole tree |
| `3cef520` | **469** (09-09) the Team Member Logins guide, indexed, the Help icon on My Team pointing at it | whole tree |
| `9b89ad2` | **462(a)(d)(e)** (09-09) Did You Know help text says the articles are emailed; Articles Delete asks for confirmation; the Blog-block gate is pinned to the guide (the code already gated it) | whole tree |
| _see log_ | **470** (09-09, owner's launch call) Did You Know starter block: SuperAdmin picks starter articles per business type; provisioning creates the agent's Articles first and fills the block with real ids; preview shows the teasers | whole tree |
| `06f309b` | **432** (09-09) Admin header clock shows the platform's time zone (Admin:TimeZone, Eastern when unset) with a short label, not the server's UTC | whole tree |
| `cea3ac8` | **462(b)+(c)** (09-09) dead CalendarReminderJob removed, its stale Hangfire definition dropped at startup; Marketing Calendar on the agent's local date, month bounded by local midnights | whole tree |
| `455ee57` | **418** (09-09) invoice numbers from a never-decrementing counter (platform per year, client invoices per agent and type); seeded from the existing maximum; row-locked | whole tree |
| `6aa7e6d` `bcc9e95` | 442 timeline; Masoud proposal + TODO 463 | docs |

## Findings worth keeping

- **A package row with no code check is a claim, not a feature.** The withdrawal test from 28 August
  (`WithdrawnFeatureTests`) plus today's (`RetiredFeaturesSeptemberTests`) are the pattern: the
  seeder only ever adds rows, so a definition change alone fixes fresh installs and leaves production
  selling the thing forever. Definitions, retired list, constants: all three, or none of it works.
  Still listed with no code check but real (left as is): Menu and sub-menu creator, Built-in SEO
  tool, Email report and tracking system, Supports multilingual content, Detailed visitor tracking.

## Deployed

09-09, build `8496a90`: 468 (verified on both hosts). Local MySQL was found stopped this morning (no shutdown lines in its log: killed with the session or a restart); the owner installed it as the Windows service IPROLocalMySQL.

Third push, build `5d09e6d`: 466 (verified on both hosts). Earlier, build `affb5e9`, verified at `/health/version` on both hosts: 464, 464 (2), 465, plus the held docs
(442 timeline, Masoud proposal, this handoff). Tree clean and pushed.

## Do this first tomorrow

1. **Watch the ticket.** Microsoft's reply decides whether launch sends at 500/min or 100/hour.
2. **PayPal live cutover** remains the one launch-day blocker on money.
3. **After the Masoud demo:** fill the questions section of `DOCS/MASOUD_PROPOSAL.md`, then decide
   the sending-domain work.

## Open work, ranked (inventory taken 2026-09-09, 12 days to launch)

**Launch-critical**
1. 442 -- ACS sending quota and engagement tracking: waiting on Microsoft; fallback is launch at 100/hour (senders queue), re-apply with 30 days of data.
2. PayPal live cutover (owner): the one launch-day blocker on money; every package re-synced to live plans.
3. Owner-side pre-launch: Postmaster Tools, the PayPal Verified badge, a "Contact us" channel.

**Bugs, medium**

**Bugs, low**
- 454 (rest): nine agent/ops senders still discard the send result; they log, so acceptable.
- 447: two load-only test failures; measures in place, watch only. 396: billing watch item, not reproduced since August.

**Decisions waiting on the owner**
- 412 the 15-page marketing site: only Home, Terms and Privacy exist; blocked on the prototype's look; real screenshots needed before any marketing push.
- 463 Masoud proposal, after the demo. 458 optional per-website custom 404 message.

**Wishlist / future**
- 467 the new builder as a Beta next to the current editor (post-launch).
- 378 broker / white-label / organization model (teammates exist; org grouping and white-label do not).
- 380 SMS reminders (cost model done), in-portal payments, real-estate IDX listings, social auto-publishing, vertical starter packs beyond Accountants.
- Post-launch engineering: fold StartupSchemaRepair DDL into migrations; unify SeedGuard/StartupGuard; a Standard-tier slot swap so deploys stop costing ~90 s of 503; a per-organization sending domain (from the Masoud assessment).

## Close-out 2026-09-09

Four pushes, each verified at `/health/version` on both hosts before its tick:

| Code | Item | Gate |
|---|---|---|
| `455ee57` | **418** invoice numbers from a counter that only goes up (platform per year, client invoices per agent and type); the concurrency test caught that an EF-composed `SELECT ... FOR UPDATE` does not lock (derived table), so the increment is one atomic UPDATE | 717/717 |
| `cea3ac8` | **462(b)+(c)** dead CalendarReminderJob removed, stale Hangfire definition dropped at startup; Marketing Calendar on the agent's local date | 721/721 |
| `06f309b` | **432** Admin header clock in the platform's time zone (Admin:TimeZone, Eastern when unset, labelled ET) | 724/724 |
| `9b89ad2` | **462(a)(d)(e)** Did You Know help text says the articles are emailed; Articles Delete confirms; Blog-block gate pinned to the guide | see the tick |

Final build on both hosts: `ed0cd56`. Tree clean and pushed.

Then the close-out the owner asked for: both snapshot zips (`git archive HEAD` to
`OneDrive\Codex_Code_Bkup` and `Documents\IPRO_Backups`), build servers shut down, no test host
running. **Local MySQL is now the Windows service `IPROLocalMySQL`** (installed and started by the
owner today, AUTO_START): nothing to stop before a reboot, and nothing to hand-start after one.
Never `mysqladmin shutdown` it after a gate. Check `netstat -ano | findstr :3306` before a gate; if
nothing listens, the owner runs `Start-Service IPROLocalMySQL`.

Also today, outside the code: the Starter Articles how-to (the add button is at the top; Business
Type is free text and must read exactly `Mortgage`), a rewritten Canadian mortgage glossary and a
mortgage-process table for the Mortgage starter articles (both handed over as HTML to paste), and
two gently rewritten Mortgage page texts with a title suggestion ("Bank or Broker: Who Should
Arrange Your Mortgage?").

## Do this first tomorrow

1. **Ticket 2608310040012537 (442)** -- the reframed reply went out 09-08; watch for Microsoft's answer.
   Until granted, launch runs at 100/hour and every sender queues and retries.
2. **PayPal live cutover** -- production is still sandbox; the one item that stops real money on launch day.
3. **Owner pre-launch list** -- Postmaster Tools, the PayPal Verified badge, a "Contact us" channel,
   the Masoud demo (463).
4. **Launch-week quiet** -- the agreed bug list is empty (418, 462 all six, 432 shipped). Remaining rows
   are watch items (447, 396, 454 rest) and post-launch wishlist (467, 378, 380, 458, 412).
## Known-open

- **462** six small findings from the guide research -- all six closed 09-09.
- The owner could not find a new help section at the bottom of a 24-section guide; 466 is the fix. Rule from it: a guide gets a section list, and a new section still goes where the reader expects it, not just at the end.
- **450** is live: worth one manual check of a Poll (Visitors Vote) block on the owner's site.

Related: `DOCS/TODO.md` 442, 447, 450, 461–465; `DOCS/MASOUD_PROPOSAL.md`; `DOCS/SESSION_HANDOFF_2026-09-02.md`.
