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
| ~~407~~ | ~~Marketing phase: brief + strategy/copy + design prototype~~ | **DONE 2026-08-13** (`1ce8cdd`, `f71ae5c`). Owner opened the marketing/sales phase. Produced: `MARKETING_BUSINESS_BRIEF.md` (verified ground truth incl. pricing, tier reality, the 2014 company history, and the legacy site at iproadvisers.com — owner: reference only, NOT a front door); `MARKETING_STRATEGY_AND_COPY.md` (position: "Everything your practice runs on, in one login" — which the owner's own 2014 Why Us copy already said; 15-page architecture, full copy deck, 12 accuracy findings); `marketing-site-prototype/` (home, preview-show, pricing + design-system.md — standalone HTML for review, NOT deployed). Owner decisions recorded in the strategy doc's "Owner decisions — settled 2026-08-13" block: PayPal-only, annual = 10× monthly, waiver per package via SuperAdmin. |
| ~~408~~ | ~~Home hero advertised an address no customer gets~~ | **FIXED + LIVE 2026-08-13** (`71e3e83`). Hero showed `yourname.iproadvisers.com`; signup actually issues `firstnamelastname.247advisers.com`. Owner's call: correct the marketing to match the product. View now renders from `App:TemporarySiteRootDomain` — the same key `GenerateUniqueDomainAsync` uses — so it can't drift again. Verified on prod. |
| ~~409~~ | ~~Setup-fee waiver per package + annual 10×~~ | **SHIPPED + CONFIGURED + VERIFIED 2026-08-13** (`536ef36`). `BillingRule.SetupFeeWaived` + optional `SetupFeeWaivedUntil`, edited in SuperAdmin → Packages → Edit. Public pricing, Register picker and PayPalBillingService all resolve through ONE method (`IsSetupFeeWaivedOn`), so advertised = charged by construction; promo codes discount the post-waiver amount. Setup fee is per-subscription (not in the PayPal plan), so the waiver needs NO plan re-sync; recurring price changes DO. Owner configured live: Gold + Platinum waived until Sep 30, Silver keeps $150 (deliberate: the saving is a reason to move up-tier); annual 400/600/900 saved + PayPal plans re-synced on all three (owner-clicked, success banners seen by owner). Verified on prod: struck-through $0 on Gold/Platinum cards + "WAIVED" in the Register picker + 400/600/900 annual. Anyone already subscribed keeps their old plan price — correct, and none exist yet. |
| ~~413~~ | ~~One Revenue screen: chart + invoice ledger merged, sortable~~ | **DONE 2026-08-13.** Owner: having "Revenue" (bar chart) and "Invoices" (the real ledger) as separate nav buttons meant the button NAMED revenue was the shallow one. Merged into /Reports/Revenue: by-month chart + totals on top (computed from the SAME filtered invoice set as the ledger, so they can never disagree), full ledger + tax-by-region + CSV beneath, ONE nav button. Date and Package columns sortable asc/desc (owner request). /Reports/Invoices 301-redirects. Razor gotcha fixed along the way: `word@Html.Raw(...)` hits the email-address heuristic and renders literally — use `word@(expr)`. Verified signed-in locally: chart, ledger rows, sort arrows, redirect. NOTE: the owner's "filter needs search text" report was withdrawn ("my BAD") — date-only filtering works. |
| 410 | MARKETING BUG — comparison table shows SMS on every plan | `PackageEntitlementSeeder.cs:185` seeds `SmsReminder` as included on all 4 packages; the data-driven "Compare all features" table renders a green check for a feature that DOESN'T EXIST. Copy fix on the page is not enough — the seeder row + live PackageFeatures rows must change (or the feature marked not-included) before any pricing page ships. Blocks the new pricing page. |
| 411 | MARKETING BUG — pricing cards drop the chosen plan | `Home/Index.cshtml` "Get Started" links go to `/Account/Register` with no `?package=`, though Register GET accepts it and preselects. Visitor picks Platinum, lands on an empty dropdown. One-line fix, real conversion cost. |
| 412 | Razor port of the new marketing site | The 15-page build from the strategy doc + prototype: new pages (pricing, how-it-works, 3 vertical pages, whats-included, your-data, about, contact, faq), home rebuild, /Preview/Show conversion-centre rework (frame nav + email capture), Register 2-step + checkout-continuation success page. Blocked on the owner's reaction to the prototype look. Real product screenshots must replace the HTML reconstructions before launch. §12 of the strategy doc: confirm the Azure region before /your-data makes any data-location claim. |
| 396 | BILLING -- Aug 7 signup-retry orphans: root cause never found in code; downgraded to WATCH | Everything observable now contradicts the bug: upgrade-supersede cancelled at PayPal twice under live observation (Aug 11+12), deletion-cancel twice, button-cancel once, and the #398 audit shows zero orphans. But the EXACT Aug 7 scenario -- retrying/superseding a signup whose earlier attempt was already APPROVED at PayPal -- was never replayed, and no code change ever addressed it specifically (fixes since may have covered it incidentally). Not closed, not urgent: re-check whenever a future test involves a repeated signup, and treat any new orphan as this bug resurfacing. |
| **397** | ~~BILLING — `PayPal__WebhookId` not set~~ | **FIXED 2026-08-10.** A webhook was registered at PayPal all along (`4YP499071R4023212` → `/Billing/Webhook`, all events); only the Azure app setting was missing — whoever registered it did the PayPal half and skipped the Azure half. Set `PayPal__WebhookId=4YP499071R4023212`, app restarted, then **resent the three missed `PAYMENT.SALE.COMPLETED` events for `I-UV5VSN5RM0AP` via PayPal's API: all three returned 200** (App Insights). One transient 500 when two resends raced on invoice-number generation — the unique constraint (L-12) rejected the duplicate and PayPal's retry succeeded, which is that constraint doing its job. |
| ~~398~~ | ~~BILLING -- audit ALL live sandbox subscriptions~~ | **CLEAN, 2026-08-12** (one buyer-side confirmation outstanding). Swept all PAYMENT.SALE.COMPLETED events Jul 29-Aug 12: 11 subscriptions charged; every one CANCELLED except the intentionally-active QA Platinum `I-D9S0UCEMDT03`. The month-old orphan `I-J5XBX9WEC98G` was cancelled in the owner's Aug 9 PayPal cleanup. `I-W4X6DVFPXV93` returns RESOURCE_NOT_FOUND -- it was a mistranscription, never existed. Two previously untracked IDs (`I-0KCMXAMW9NH7`, `I-E59URBUJ6482`) were the Aug 6 upgrade-test subs, cancelled same day. Blind spot CLOSED same day: the owner pulled the buyer account's FULL activity back to its Jul 4 funding -- every subscription charge in the account's life (Jul 8-9 old-era batch, Aug 6-12 test batch) maps to a verified-CANCELLED sub, and the Jul 9-Aug 6 silence rules out any hidden monthly biller. Zero orphans. Probe subs I-9NFV9F997EYU / I-3S7W7426P2MB are APPROVAL_PENDING, never charge, ignore. |
| ~~375~~ | ~~Gated-agent portal sweep~~ | **DONE 2026-08-12** (`1d37a24`). The predicted third control existed: UploadPhoto/RemovePhoto post from the exempt Profile page to their own (non-exempt) paths, so a gated agent's photo change bounced silently -- both added to the own-account exemptions. Google Calendar card already safe (feature check shows the upgrade box first). Sidebar audit: every nav item carries the gated-lock attribute except the intentionally-live three. **Bonus find, bigger than the sweep itself: 18 delete buttons across 14 views (agent portal, client portal, SuperAdmin) still used inline onsubmit=confirm(), which CSP silently drops -- every one deleted with NO confirmation.** All converted to js-confirm-submit; the client-portal layout gained the shared handler it never had. |
| ~~365~~ | ~~Azure auto-heal rules~~ | **DONE 2026-08-12.** Both App Services (`ipro-prod-web` in `ipro-production`, `ipro-prod-admin` in `ipro-prod-admin_group` -- note the different resource group): autoHealEnabled with one conservative rule -- 30 responses in 500-599 within 5 minutes recycles the container, guarded by minProcessExecutionTime 10 min so a slow cold start can never trigger a restart loop. Verified via az webapp config show on both. Closes the last remnant of the old Azure-config task. |
| ~~374~~ | ~~Delete prod test agent `zedtester`~~ | DONE 2026-08-12 by the owner -- first production run of the #406 retention path: any zedtester invoices survive in Reports -> Invoices with the 'deleted' badge. |
| ~~394~~ | ~~Tick the greeting exemption on 4 e-card designs~~ | DONE -- the owner had already ticked these during the Aug 9-10 unsubscribe work; the item sat stale until 2026-08-12, when a screenshot of simple-birthday confirmed the flag (owner vouches for the other three). Lesson: UI-side owner actions leave no trail I can see; mark items done when the owner says so, or ask for one screenshot. |
| ~~395~~ | ~~Send one e-card to a GMAIL address~~ | **PASSED 2026-08-12, and doubled as a live proof of the whole consent system.** Owner sent two cards from BoBMot1 to bahman.motamed@gmail.com (an address that had unsubscribed during earlier testing): the Halloween card was REFUSED by the dispatcher ('Recipient has unsubscribed') because it is promotional; the simple-birthday card DELIVERED because it carries the greeting exemption + the owner's opt-back-in -- exactly the day-one design. Gmail placement: **Promotions tab, not Spam** (normal for a new sender; the win is no spam folder). Tracking recorded Sent 11:49 -> Delivered 11:49 -> Opened 11:52 on a real mainstream inbox, which also closes email-tracking item F (#386): the /portal/EmailActivity surface verified live, signed in, with a real send. |

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
| **406** | **Financial records now survive agent deletion + SuperAdmin Invoices ledger** | SHIPPED 2026-08-12 (`03d1614`), owner-reported gap: deleting an agent shredded their invoices -- IPRO's own accounting record -- so revenue history shrank retroactively and an ex-customer could never get invoice copies (business practice deletes agents ~a month after cancelling). Now: (1) invoices carry a frozen bill-to snapshot (backfilled for existing rows); (2) the eraser RETAINS Invoices/InvoiceLineItems/Billings/SubscriptionChanges by default, reports what it kept, and offers an explicit full-shred checkbox on the erasure preview for QA/test agents; (3) new SuperAdmin ledger at /Reports/Invoices: period filter, search, tax-collected-by-region (the CRA remittance number), CSV export, per-invoice View/Print incl. deleted agents; (4) the agent-facing Cancel Subscription confirm now tells agents to save invoice copies first. VERIFY (owner): open /Reports/Invoices in Admin, reprint one invoice, export CSV; on the next test-agent deletion use the erasure-preview page and tick the full-shred box. |

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
| ~~379~~ | ~~Secretary / assistant sub-user logins~~ | **SHIPPED 2026-08-12** (`2623248`), owner decisions: everything-except-Billing; seats by tier via the `team_members` package feature (Silver 1 / Gold 2 / Platinum 5 / Broker 10, SuperAdmin-adjustable). Team member signs in with their own email on the normal login page and acts AS the agent (NameIdentifier = agent id + TeamMemberId marker claim); middleware keeps /Billing and /Team owner-only; ChangePassword targets the member's own row; temp passwords display once, never emailed; sidebar shows an 'Owner access required' modal instead of silently bouncing. New 'My Team' page under Billing. Verified END-TO-END in the local browser: add member -> temp password -> member login -> forced password change -> full portal access as the agent -> Billing bounced. Eraser deletes TeamMembers with the agent. Design details in the roadmap doc's team-member section. Owner adjusted live config 2026-08-12: Silver EXCLUDED via SuperAdmin (upsell lever); Gold 2 / Platinum 5 / Broker 10 kept -- the seeder never overwrites SuperAdmin entitlement edits, so this sticks. |
| 380 | SMS reminders | Not built. Vendor pricing researched 2026-07-20 (Twilio US) and **2026-08-12 (full Canada cost model + provider comparison incl. WhatsApp -- see the SMS section of the roadmap doc)**: ~$800-1,000/mo at 100 agents x 10 SMS + 10 WhatsApp each way daily; Sent (sent.dm) evaluated and rejected (no track record, support blackholes); Telnyx/Plivo are the price-competitive credible alternatives to Twilio. |
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
