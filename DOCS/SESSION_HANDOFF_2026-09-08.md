# Session handoff — 2026-09-08 (13 days to launch)

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
| _see log_ | **466** help articles open with 'In this guide', a list of links to their sections (three or more) | whole tree |
| `6aa7e6d` `bcc9e95` | 442 timeline; Masoud proposal + TODO 463 | docs |

## Findings worth keeping

- **A package row with no code check is a claim, not a feature.** The withdrawal test from 28 August
  (`WithdrawnFeatureTests`) plus today's (`RetiredFeaturesSeptemberTests`) are the pattern: the
  seeder only ever adds rows, so a definition change alone fixes fresh installs and leaves production
  selling the thing forever. Definitions, retired list, constants: all three, or none of it works.
  Still listed with no code check but real (left as is): Menu and sub-menu creator, Built-in SEO
  tool, Email report and tracking system, Supports multilingual content, Detailed visitor tracking.

## Deployed

Build `affb5e9`, verified at `/health/version` on both hosts: 464, 464 (2), 465, plus the held docs
(442 timeline, Masoud proposal, this handoff). Tree clean and pushed.

## Do this first tomorrow

1. **Watch the ticket.** Microsoft's reply decides whether launch sends at 500/min or 100/hour.
2. **PayPal live cutover** remains the one launch-day blocker on money.
3. **After the Masoud demo:** fill the questions section of `DOCS/MASOUD_PROPOSAL.md`, then decide
   the sending-domain work.

## Known-open

- **462** six small findings from the guide research.
- The owner could not find a new help section at the bottom of a 24-section guide; 466 is the fix. Rule from it: a guide gets a section list, and a new section still goes where the reader expects it, not just at the end.
- **450** is live: worth one manual check of a Poll (Visitors Vote) block on the owner's site.

Related: `DOCS/TODO.md` 442, 447, 450, 461–465; `DOCS/MASOUD_PROPOSAL.md`; `DOCS/SESSION_HANDOFF_2026-09-02.md`.
