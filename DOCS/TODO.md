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
| **396** | **BILLING — cancellations never reach PayPal** | **Most serious open item.** Sandbox shows **3 concurrent Silver subscriptions billing daily** (Aug 8 and Aug 9, 3 × $45.20 each). They match the 3 Subscribe attempts within 11 minutes on Aug 7; IPRO marked two "Cancelled" — **in its own DB only**. On a real card a customer who retried signup twice would be billed 3× forever while the portal showed one subscription. This is audit finding **H-7, recorded as fixed** — so the fix likely covers only the downgrade path, not retry/supersede, or the cancel call fails silently. **PROVEN, not inferred (2026-08-09):** Billing ID **`I-J7VW2AGKWJS3`** is listed in IPRO's Subscription History as *Cancelled, Aug 7*, while PayPal shows it **ACTIVE**, cycle *Daily*, next payment Aug 10, $45.20 — charging every day since IPRO thought it stopped. Same subscription, two contradictory truths. The third duplicate is `I-W4X6DVFPXV93`. The genuinely live one, which must be KEPT, is **`I-UV5VSN5RM0AP`** — start Aug 7, cycle Daily, next Aug 10, initial payment $169.50. Note that PayPal has this one **exactly right**; only IPRO's copy says "Sep 7 / Monthly", which is #397 in a single screenshot. |
| **397** | **BILLING — `PayPal__WebhookId` is not set in production** | **Confirmed root cause** of "PayPal charged 6 times, IPRO recorded nothing." `PayPalBillingService.cs:2419` returns false when WebhookId is blank → `HandleWebhookAsync` false → `Unauthorized()`. **Every webhook PayPal has ever sent has been rejected with 401.** The `PAYMENT.SALE.COMPLETED` handler is correct and has never been allowed to run. Fix: register the webhook in the PayPal dashboard → `https://app.iproadvisers.com/billing/webhook`, then set `PayPal__WebhookId`. SuperAdmin → PayPal Setup shows whether it's configured but cannot set it. |
| **398** | **BILLING — audit ALL live sandbox subscriptions** | The sandbox buyer account pays for *every* test signup ever made (zedtester, earlier bobtest runs, all of them). If cancels never reached PayPal, orphans have accumulated since the beginning. Don't assume it's four — list them. **Confirmed specimen: Billing ID `I-J5XBX9WEC98G`** — ACTIVE, started **Jul 9 2026**, monthly, $60.00 with no tax, $200.00 initial payment (both figures from an older build). Charged Aug 9 on its correct anniversary; next Sep 9. Bahman never created it this week — it is a **month-old orphan still billing**. **Before cancelling it, search that Billing ID in the IPRO admin:** if IPRO shows it "Cancelled" while PayPal shows ACTIVE, that is #396 demonstrated directly rather than inferred; if IPRO has no record at all, the agent was deleted and the subscription orphaned, which is a *separate* leak worth its own item. |
| 375 | Gated-agent portal sweep | Check every clickable control against the access gate's exemption list. Profile and the colour swatches were both found by hand; assume there is a third. |
| 365 | Azure auto-heal rules | All that remains of the old Azure task — 64-bit and run-from-package are resolved. |
| 374 | Delete prod test agent `zedtester` | Destructive; needs the owner's go-ahead. |
| **394** | **Tick the greeting exemption on 4 e-card designs** | **Bahman, 5 minutes, do this first.** `admin.iproadvisers.com` → E-Card Designs → `simple-birthday`, `birthday-audi`, `anniversary-1`, `anniversary-2` → tick "Personal greeting — may still be sent to unsubscribed clients". Every design ships with it OFF by design, so until this is done an unsubscribe stops birthday cards too. |
| **395** | **Send one e-card to a GMAIL address** | Every deliverability test on 2026-08-08 went to `test@gbssurveillance.com` — Bahman's own cPanel/SpamAssassin box using SpamCop, an unusually harsh judge almost no real client uses. There is still **zero** data on what a mainstream provider does with an IPRO e-card. |

## Owner-driven — waiting on Bahman, not on code

| # | Item | Notes |
|---|---|---|
| **399** | **Aug 10: count the charges — this is now a real test** | Late on 2026-08-09 Bahman cancelled every sandbox subscription **except `I-UV5VSN5RM0AP`**. So Aug 10 should show **exactly one $45.20 charge**. **More than one = orphans remain** (the Automatic Payments list had a "See more" button, so it was never fully enumerated) → finish #398. **IPRO will still show nothing, and that is expected**, not a new fault: `PayPal__WebhookId` is still unset so the charge is still rejected with 401. IPRO only starts recording once #397 is done. |
| 367 | QA billing **day 2** | **BLOCKED — see #396/#397.** The overnight charges *did* happen (PayPal billed daily, correctly). IPRO recorded none of them because the webhook is rejected with 401. Do not upgrade to Gold until #397 is fixed, or day 3 will be just as blind. **The test worked** — it caught two real bugs, which is the only reason we know about either. |
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

- **The unsubscribe link and the suppression it triggers.** Deployed and verified locally against the
  real dispatcher (4-case truth table) and live at the HTTP level on both hosts — but **nobody has
  yet clicked the link in a delivered card** and then confirmed the next card does not arrive. That
  is items #394/#395 above and the whole point of the feature.
- **Setup fee on an existing agent's invoice.** The other four disclosure surfaces were verified live;
  the invoice line item is traced through the code but not yet seen on screen. Check `bobtest`'s
  invoice `IPRO-2026-000010` when convenient.

## Confirmed working on 2026-08-08 — recorded so nobody re-opens them

- **E-Cards and E-Letters really send.** Long listed as unverified. Bahman sent both to a real mailbox
  and they arrived. The composed card renders correctly, contact block and all.
- **Delivery tracking end-to-end, including via the webhook.** An e-letter bounced with a real SMTP
  `550` and the reason appeared on `/portal/EmailActivity` with SentAt set and Delivered empty. That
  reason could only have arrived through the SendGrid event webhook branch added the same day, which
  cannot be tested locally because webhooks can't reach localhost.
- **Clicks are recorded**, not only opens — Bahman confirmed by looking.

## Deliverability — what is actually known, and what was noise

Two *different* failures got conflated on 2026-08-08. Keep them apart:

| Symptom | Cause | Fixed? |
|---|---|---|
| `550 ... is in an RBL ... spamcop.net` — never delivered | SendGrid's **shared sending IP** is SpamCop-listed. Seen on two different pool IPs (`149.72.123.24`, `149.72.120.130`) hitting both an e-letter *and* an e-card. Content-independent — the receiving server refuses the connection before reading anything. | **No.** Untouched. Resend; it varies per send. Real fix is a dedicated SendGrid IP, only worth it at volume. |
| `***SPAM***` prepended — delivered, but filed as junk | Content scoring. E-cards are one large image carrying ~10 words, and were sent **HTML-only** with **no unsubscribe**. E-letters, being text, were never tagged. | **Yes.** Adding a plain-text alternative part + `List-Unsubscribe` took the same card on the same server from `***SPAM***` (19:03) to clean (19:29). |

The image-to-text ratio was *not* addressed and may still matter with other filters — but it proved
not to be decisive on the strictest recipient available.
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
