# IPRO — Open Work

**Last regenerated: 2026-08-08.**

## Why this file exists

Until today the backlog lived in two places, and neither was safe on its own:

- `DOCS/IPRO_Project_Status_And_Roadmap.md` — durable and versioned, but 2,300 lines. Things stay in
  it and still become invisible. Both the white-label and secretary-mode items were in there the
  whole time and still had to be asked after.
- A task tracker in the tooling — scannable, but **not a file, not in git, and in no backup**. If that
  state is lost, every item in it is lost with it. This working directory and the assistant's memory
  files have already been wiped once by a reboot.

So this file is the durable, scannable middle: one line per item, committed, in every backup.
The roadmap remains the place for the *why* — design notes, history, rejected alternatives.

**Keep it honest:** when something is finished, edit the entry or delete it. A stale "not yet fixed"
becomes a false bug report — that happened on 2026-08-08 with the setup-fee item, which had been
fixed weeks earlier and got re-raised as work because the doc still said it was open.

---

## Active

| # | Item | Notes |
|---|---|---|
| 375 | Gated-agent portal sweep | Check every clickable control against the access gate's exemption list. Profile and the colour swatches were both found by hand; assume there is a third. |
| 365 | Azure auto-heal rules | All that remains of the old Azure task — 64-bit and run-from-package are resolved. |
| 374 | Delete prod test agent `zedtester` | Destructive; needs the owner's go-ahead. |
| 387 | E-card / e-letter one-click unsubscribe | The concrete reason e-cards land in spam: newsletters pass `listUnsubscribeUrl`, cards and letters don't. Can't be fixed with a header alone — `SendGridEmailService.cs:67` also sends `List-Unsubscribe-Post: One-Click`, which RFC 8058 says needs a real HTTPS endpoint that honours the opt-out. **Product question first:** should a client be able to opt out of birthday cards from their own adviser separately from newsletters? |

## Owner-driven — waiting on Bahman, not on code

| # | Item | Notes |
|---|---|---|
| 367 | QA billing **day 2** | Confirm the overnight PayPal charge on `bobtest`, then upgrade to QA Gold (Daily), package id **8**. |
| 368 | QA billing **day 3** | Confirm charge, upgrade to QA Platinum (Daily), id **9**. |
| 369 | QA billing **day 4** | Cancel + delete `bobtest`, then verify against PayPal's API that the subscription is physically CANCELLED. |

## Decisions needed before work can start

- **White-label: cosmetic (A) or full reseller (B)?** Blocks #378 entirely. (A) is ~2–3 weeks and
  mostly additive; (B) is a different product.
- **Secretary mode: what can an assistant see?** Everything the agent sees, or role-scoped? Blocks
  #379's design.
- **Standard App Service tier?** Buys real deployment slots (zero-downtime swap instead of
  restart-and-poll). Costs money. Current setup works without it.
- **In-portal payments** — blocked on there being a Stripe or equivalent merchant account at all.

## Backlog — designed or conceptual, none started

| # | Item | State |
|---|---|---|
| 378 | Broker / team / white-label model | Designed 2026-07-22, reconfirmed wanted 2026-08-01 |
| 379 | Secretary / assistant sub-user logins | Conceptual only; reinstated 2026-08-01 after being silently dropped from a backlog rewrite for a week |
| 380 | SMS reminders | Not built; vendor pricing researched 2026-07-20 |
| 380 | In-portal payments | Not started; recommendation recorded 2026-07-23 |
| 380 | Real estate vertical: IDX listings | Not scoped |
| 380 | Social media **auto-publishing** | The shipped Social Posts feature is a composer/tracker only |
| 380 | Vertical starter packs | Accountants content shipped; other verticals still have the small default set |

## Standing constraints — do not pick these up opportunistically

- **Template System V2 / Wix-style templating is PAUSED** pending the consultant. Nav v2 (2026-07-30)
  was one explicit, scoped exception, not a lifting of the pause.
- **SSL certificates expire 2026-10-19.** Renewal is scripted (`ops/New-AgentCert.ps1`) and monitored,
  but still manual. An expiry breaks newsletter images in already-delivered mail.
- **PayPal runs in sandbox in production** (`PayPal__IsSandbox=true`). QA-only packages must set
  `IsHiddenTestPackage`.

## Known-unverified — shipped, but never confirmed by a human using it

- **E-Cards and E-Letters sending.** Builds and post-deploy log checks are all that back these; nobody
  has clicked Send as a real agent and looked at a delivered card. Same gap that produced the
  2026-08-08 sign-in regression, so it is listed here rather than claimed as working.
  Since 2026-08-08 there is at least a place to look: **`/portal/Email Activity`** shows every send
  with per-recipient Delivered/Opened/Clicked. Sending one real card and watching that page go from
  Sent to Delivered is the test that closes this item.
- **Delivery tracking for cards / letters / polls / Did You Know.** The wiring was verified end-to-end
  locally against seeded events, and the webhook branch is a straight copy of the newsletter path that
  has worked in production for weeks — but no *real* SendGrid event for a card has been observed
  landing yet, because webhooks can't reach localhost. First real send confirms it.
- **Setup fee on an existing agent's invoice.** The other four disclosure surfaces were verified live;
  the invoice line item is traced through the code but not yet seen on screen. Check `bobtest`'s
  invoice `IPRO-2026-000010` when convenient.

---

## Where to look for detail

| For | Read |
|---|---|
| Why a thing was built the way it was, and what was rejected | `DOCS/IPRO_Project_Status_And_Roadmap.md` |
| Rules that must stay true — routing, hosts, auth, billing | `DOCS/INVARIANTS.md` (read before touching those) |
| A bug that already happened, and how it was fixed | `DOCS/09_TROUBLESHOOTING.md` |
| Backup and release process | `DOCS/14_BACKUP_AND_RELEASE_CHECKLIST.md` |
| Running everything locally | `DOCS/16_LOCAL_DEV.md` |
