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
  startup, constants removed). Two real features renamed on every row: "Website leads inbox
  (prospect manager)" and "Social posts: draft, check platform limits, and track". Three tests
  red-first; full gate; deployed and verified on both hosts (see the TODO row for the commit).

| commit | what | gate |
|---|---|---|
| `5f12fd7` | **464** four package-table rows withdrawn, two renamed | 700/700 (31m58s) |
| _see log_ | **464 (2)** seven rows shortened to three to five words (owner-approved wording) | whole tree |
| _see log_ | **465** Social links block: the footer's profiles in the page body, icons or icons with names, all three templates, one shared icon map; a package row; guide section | whole tree |
| `6aa7e6d` `bcc9e95` | 442 timeline; Masoud proposal + TODO 463 | docs |

## Findings worth keeping

- **A package row with no code check is a claim, not a feature.** The withdrawal test from 28 August
  (`WithdrawnFeatureTests`) plus today's (`RetiredFeaturesSeptemberTests`) are the pattern: the
  seeder only ever adds rows, so a definition change alone fixes fresh installs and leaves production
  selling the thing forever. Definitions, retired list, constants: all three, or none of it works.
  Still listed with no code check but real (left as is): Menu and sub-menu creator, Built-in SEO
  tool, Email report and tracking system, Supports multilingual content, Detailed visitor tracking.

## Do this first tomorrow

1. **Watch the ticket.** Microsoft's reply decides whether launch sends at 500/min or 100/hour.
2. **PayPal live cutover** remains the one launch-day blocker on money.
3. **After the Masoud demo:** fill the questions section of `DOCS/MASOUD_PROPOSAL.md`, then decide
   the sending-domain work.

## Known-open

- **462** six small findings from the guide research.
- **450** is live: worth one manual check of a Poll (Visitors Vote) block on the owner's site.

Related: `DOCS/TODO.md` 442, 447, 450, 461–465; `DOCS/MASOUD_PROPOSAL.md`; `DOCS/SESSION_HANDOFF_2026-09-02.md`.
