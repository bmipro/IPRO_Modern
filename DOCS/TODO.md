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
| 396 | BILLING -- Aug 7 signup-retry orphans: root cause never found in code; downgraded to WATCH | Everything observable now contradicts the bug: upgrade-supersede cancelled at PayPal twice under live observation (Aug 11+12), deletion-cancel twice, button-cancel once, and the #398 audit shows zero orphans. But the EXACT Aug 7 scenario -- retrying/superseding a signup whose earlier attempt was already APPROVED at PayPal -- was never replayed, and no code change ever addressed it specifically (fixes since may have covered it incidentally). Not closed, not urgent: re-check whenever a future test involves a repeated signup, and treat any new orphan as this bug resurfacing. |
| **397** | ~~BILLING — `PayPal__WebhookId` not set~~ | **FIXED 2026-08-10.** A webhook was registered at PayPal all along (`4YP499071R4023212` → `/Billing/Webhook`, all events); only the Azure app setting was missing — whoever registered it did the PayPal half and skipped the Azure half. Set `PayPal__WebhookId=4YP499071R4023212`, app restarted, then **resent the three missed `PAYMENT.SALE.COMPLETED` events for `I-UV5VSN5RM0AP` via PayPal's API: all three returned 200** (App Insights). One transient 500 when two resends raced on invoice-number generation — the unique constraint (L-12) rejected the duplicate and PayPal's retry succeeded, which is that constraint doing its job. |
| ~~398~~ | ~~BILLING -- audit ALL live sandbox subscriptions~~ | **CLEAN, 2026-08-12** (one buyer-side confirmation outstanding). Swept all PAYMENT.SALE.COMPLETED events Jul 29-Aug 12: 11 subscriptions charged; every one CANCELLED except the intentionally-active QA Platinum `I-D9S0UCEMDT03`. The month-old orphan `I-J5XBX9WEC98G` was cancelled in the owner's Aug 9 PayPal cleanup. `I-W4X6DVFPXV93` returns RESOURCE_NOT_FOUND -- it was a mistranscription, never existed. Two previously untracked IDs (`I-0KCMXAMW9NH7`, `I-E59URBUJ6482`) were the Aug 6 upgrade-test subs, cancelled same day. Blind spot CLOSED same day: the owner pulled the buyer account's FULL activity back to its Jul 4 funding -- every subscription charge in the account's life (Jul 8-9 old-era batch, Aug 6-12 test batch) maps to a verified-CANCELLED sub, and the Jul 9-Aug 6 silence rules out any hidden monthly biller. Zero orphans. Probe subs I-9NFV9F997EYU / I-3S7W7426P2MB are APPROVAL_PENDING, never charge, ignore. |
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
| ~~402~~ | ~~QA restart day 2: overnight charge + Gold upgrade~~ | **PASSED 2026-08-11 evening -- including #396's moment of truth.** Overnight daily charge $42.00 -> exactly one invoice 000009 ($40+$2). Upgrade Silver->Gold Daily via override id 8: prorated $19.33+$0.97=$20.30 charged once (verified: ONE PayPal transaction), new sub `I-T31GV0NX2GMF` ACTIVE at $63.00/day inclusive, **old Silver `I-LGMP7JWH1YNM` went CANCELLED at PayPal within seconds** -- the upgrade-supersede path provably cancels now. Two bugs found+fixed same hour (`1dfacdb`): (1) absorb rule used strict <, so the upgrade's sale (== invoice total) minted duplicate invoice 000011 -- now <=; (2) invoice dates printed in UTC (evening upgrade dated Aug 12) -- invoice page/history/email now show the agent's local date via AgentLocalTime (moved to IPRO.DataAccess). Also fixed Gmail cramming the email totals (flex -> table). NOTE: Gold's daily cycles start Sep 11 (paid-through deferral, correct product behavior) -- so **no overnight charge before day 3**; day 3 is just the Platinum upgrade. Spurious 000011 dies with the agent on day 4. |
| ~~405~~ | ~~Tax-rate divergence: $150 advertised, $150.01 invoiced~~ | **FIXED in code 2026-08-10** (verification = bob3test3 signup). Root cause was PayPal, not our columns: PayPal ACCEPTS a 3-decimal tax percentage ("14.975") and even echoes it back while APPROVAL_PENDING, but **bills at 14.98% after approval** -- proven with sandbox probe subscriptions. So QC could never be charged correctly as an add-on percentage. Fix: we now send **tax-inclusive gross prices we compute ourselves** (per-subscription billing_cycles override + grossed setup fee, taxes marked inclusive; probe-verified accepted verbatim). PayPal now charges exactly what the invoice says: setup $172.46 = $150 + $22.46, cycle $45.99 = $40 + $5.99. Also: webhook de-tax snaps to the stored net within 2 cents, and Invoices.TaxRate widened decimal(7,4)->(7,5) so invoices stop displaying "14.980 %" (schema repair both apps, verified locally). Probe subs I-9NFV9F997EYU, I-3S7W7426P2MB are APPROVAL_PENDING orphans in sandbox -- never approved, never charge, ignore in the #398 audit. |
| ~~403~~ | ~~QA restart day 3: Platinum upgrade~~ | **PASSED 2026-08-12 morning -- every fix verified at once.** Gold->Platinum via override id 9: ONE charge $30.98 ($29.50 prorated + $1.48 GST), ONE invoice 000012 (the <= absorb fix passed on the exact path that minted yesterday's duplicate), ONE email with properly spaced totals (Gmail table fix visible), Gold `I-T31GV0NX2GMF` CANCELLED at PayPal 22s after Platinum `I-D9S0UCEMDT03` activated (supersede 2-for-2), Platinum ACTIVE $94.50/day inclusive deferred to Sep 11. Buyer-side PayPal activity reconciles charge-for-charge with our invoices across all three days. |
| ~~404~~ | ~~QA restart day 4: cancel + delete~~ | **PASSED 2026-08-12 -- PROTOCOL COMPLETE.** Agent-facing Cancel Subscription button: `I-D9S0UCEMDT03` CANCELLED at PayPal at 13:39:04Z (the third cancel flavor -- deletion 2x, supersede 2x, button 1x -- all proven). Portal gated access immediately with honest messaging. Then bob3test3 deleted via SuperAdmin (erasure preview matched the report: 28 rows/6 tables, 0 files, PayPal cancel no-op-clean), taking the spurious invoice 000011 test data with it. Sandbox is fully reconciled: zero active subscriptions, zero orphans (#398). The 4-day daily-billing protocol closed with every money path verified end-to-end against PayPal's own records across 3 provinces. |

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
