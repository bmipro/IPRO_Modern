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
| **396** | **BILLING — cancel fails on the signup retry/supersede path (narrowed 2026-08-10)** | The delete path **works**: deleting bobtest cancelled `I-UV5VSN5RM0AP` at PayPal within seconds, verified via the API (ACTIVE → CANCELLED 20:30:53). So `CancelSubscriptionAsync` itself is capable of reaching PayPal. What failed on Aug 7 was the **subscribe-supersede path**: retrying signup marked the earlier attempts Cancelled in the DB while they stayed ACTIVE at PayPal and billed daily (`I-J7VW2AGKWJS3` was the proven specimen). The fresh QA test exercises this exact path on every upgrade — watch it. History: 4 orphans cancelled manually by Bahman in the PayPal UI on Aug 9; Aug 10 showed exactly one charge. Possible remaining orphan `I-W4X6DVFPXV93` (monthly, ~Sep 7) — the API returned nothing for that ID, so it may be mistranscribed; if a stray $45.20 appears in September, that's it. |
| **397** | ~~BILLING — `PayPal__WebhookId` not set~~ | **FIXED 2026-08-10.** A webhook was registered at PayPal all along (`4YP499071R4023212` → `/Billing/Webhook`, all events); only the Azure app setting was missing — whoever registered it did the PayPal half and skipped the Azure half. Set `PayPal__WebhookId=4YP499071R4023212`, app restarted, then **resent the three missed `PAYMENT.SALE.COMPLETED` events for `I-UV5VSN5RM0AP` via PayPal's API: all three returned 200** (App Insights). One transient 500 when two resends raced on invoice-number generation — the unique constraint (L-12) rejected the duplicate and PayPal's retry succeeded, which is that constraint doing its job. |
| **398** | **BILLING — audit ALL live sandbox subscriptions** | The sandbox buyer account pays for *every* test signup ever made (zedtester, earlier bobtest runs, all of them). If cancels never reached PayPal, orphans have accumulated since the beginning. Don't assume it's four — list them. **Confirmed specimen: Billing ID `I-J5XBX9WEC98G`** — ACTIVE, started **Jul 9 2026**, monthly, $60.00 with no tax, $200.00 initial payment (both figures from an older build). Charged Aug 9 on its correct anniversary; next Sep 9. Bahman never created it this week — it is a **month-old orphan still billing**. **Before cancelling it, search that Billing ID in the IPRO admin:** if IPRO shows it "Cancelled" while PayPal shows ACTIVE, that is #396 demonstrated directly rather than inferred; if IPRO has no record at all, the agent was deleted and the subscription orphaned, which is a *separate* leak worth its own item. |
| 375 | Gated-agent portal sweep | Check every clickable control against the access gate's exemption list. Profile and the colour swatches were both found by hand; assume there is a third. |
| 365 | Azure auto-heal rules | All that remains of the old Azure task — 64-bit and run-from-package are resolved. |
| 374 | Delete prod test agent `zedtester` | Destructive; needs the owner's go-ahead. |
| **394** | **Tick the greeting exemption on 4 e-card designs** | **Bahman, 5 minutes, do this first.** `admin.iproadvisers.com` → E-Card Designs → `simple-birthday`, `birthday-audi`, `anniversary-1`, `anniversary-2` → tick "Personal greeting — may still be sent to unsubscribed clients". Every design ships with it OFF by design, so until this is done an unsubscribe stops birthday cards too. |
| **395** | **Send one e-card to a GMAIL address** | Every deliverability test on 2026-08-08 went to `test@gbssurveillance.com` — Bahman's own cPanel/SpamAssassin box using SpamCop, an unusually harsh judge almost no real client uses. There is still **zero** data on what a mainstream provider does with an IPRO e-card. |

## Owner-driven — waiting on Bahman, not on code

| # | Item | Notes |
|---|---|---|
| ~~399~~ | ~~Aug 10 charge count~~ | **PASSED** — exactly one $45.20 on Aug 10. bobtest since deleted (see 400). |
| **400** | **QA billing restart — day 0 DONE** | bobtest **deleted 2026-08-10 20:30**; deletion cancelled `I-UV5VSN5RM0AP` at PayPal, **verified via API**. The webhook (#397) and the double-tax fix are now live, so the rerun tests the whole pipeline for real. Old bobtest data (incl. the $51.08 invoices) is gone with the agent. |
| ~~401~~ | ~~QA restart day 1~~ | **PASSED 2026-08-10, and productive.** `bob2test2` (Quebec — deliberately different tax rate, 14.975% GST+QST), sub `I-RYCAW2SJMH73`, ACTIVE, daily. Both activation sales ($172.47 setup + $45.99 first cycle) processed **organically** by the webhook — first real end-to-end run. Signup invoice 000008 correct: $190 + $28.45 QC = $218.45. **Found bug: the second activation sale got a duplicate invoice invented for it** ($172.47 setup reappeared as a spurious "$150.01 monthly recurring" invoice 000009) — fixed same hour (`f4424fd`): a sale within 6h of a settled invoice for less than its total is absorbed into it, transaction id appended. The spurious 000009 stays in the DB as test data; dies with the agent on day 4. |
| 402 | QA restart **day 2** (Aug 11): confirm overnight charge -> upgrade **QA Gold (Daily), id 8** | Test restarted 2026-08-10 evening on `bob3test3` (**Alberta, 5% GST** -- owner deliberately picked a third tax rate), sub `I-LGMP7JWH1YNM`, ACTIVE. **First run of the tax-inclusive pricing pipeline: perfect.** PayPal charged exactly $157.50 setup ($150+$7.50) + $42.00 first cycle ($40+$2.00) = $199.50 = the activation invoice; taxes stored inclusive 5%; all 4 webhooks 200 organically; $42 second sale absorbed, no duplicate invoice. Next daily charge ~10:00Z (6am ET): expect ONE invoice, $40.00 + $2.00 GST = $42.00. Then owner upgrades via console override id 8 + Upgrade Monthly; verify Gold invoice/tax AND old Silver sub goes CANCELLED at PayPal (#396 supersede path). |
| ~~405~~ | ~~Tax-rate divergence: $150 advertised, $150.01 invoiced~~ | **FIXED in code 2026-08-10** (verification = bob3test3 signup). Root cause was PayPal, not our columns: PayPal ACCEPTS a 3-decimal tax percentage ("14.975") and even echoes it back while APPROVAL_PENDING, but **bills at 14.98% after approval** -- proven with sandbox probe subscriptions. So QC could never be charged correctly as an add-on percentage. Fix: we now send **tax-inclusive gross prices we compute ourselves** (per-subscription billing_cycles override + grossed setup fee, taxes marked inclusive; probe-verified accepted verbatim). PayPal now charges exactly what the invoice says: setup $172.46 = $150 + $22.46, cycle $45.99 = $40 + $5.99. Also: webhook de-tax snaps to the stored net within 2 cents, and Invoices.TaxRate widened decimal(7,4)->(7,5) so invoices stop displaying "14.980 %" (schema repair both apps, verified locally). Probe subs I-9NFV9F997EYU, I-3S7W7426P2MB are APPROVAL_PENDING orphans in sandbox -- never approved, never charge, ignore in the #398 audit. |
| 403 | QA restart **day 3**: confirm charge → upgrade **QA Platinum (Daily), id 9** | Same checks at Platinum's price. |
| 404 | QA restart **day 4**: cancel + delete | Sub physically CANCELLED at PayPal, zero charges the following day. |

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
