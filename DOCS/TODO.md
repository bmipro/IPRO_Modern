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
| ~~414~~ | ~~SIGNUP V2 — registration IS checkout (closes #411 too)~~ | **SHIPPED 2026-08-13, owner-driven** ("I dont think this is right way of signing up a new client"). Old funnel: 7 screens, the package picked 3 times, a receipt-style success page BEFORE any payment, temp password + forced change. New: pricing card carries `?package=` → Register shows a LOCKED plan summary (name, price, live waiver state, Monthly/Annual radios, one "change plan" link) → the registrant **chooses their own password on the form** (≥8 chars; no temp password, welcome email carries NO credentials) → POST creates the account, signs them in, and 302s STRAIGHT to PayPal checkout. Trial-code path signs in and lands on the dashboard. Abandon path: signed-in gated agent, /Billing shows the pending checkout with Resume. Fields dropped from signup (moved to Profile): designation, fax, mobile, time zone — TZ now auto-derived from province via hidden field (invoice dates need it). Fallback dropdown remains for visitors with no plan chosen; RegisterSuccess retired for self-signup but kept for direct hits. **VERIFIED E2E on a locally running app**: locked banner + WAIVED label render; POST → 302 `sandbox.paypal.com/checkoutnow`; abandon → Billing shows pending Gold + Resume; fresh login with chosen password → no forced change, gate routes to Billing; trial POST → dashboard signed in. Local test agents cleaned up, trial slot released. Remaining: one owner buyer-pass on prod (the local run used the same sandbox PayPal). |
| ~~415~~ | ~~Billing UX: one-pick funnel polish + collapsible compare~~ | **DONE 2026-08-13** (`54f9dcd`). Owner's post-signup feedback: renamed to "Compare and Upgrade Packages", the heading toggles the ~40 function rows (package cards + upgrade buttons stay visible), collapsed by default once subscribed so Invoice History sits one screen down. Sort arrows on the Revenue ledger got a dimmed inactive pair (`2ff9a60`). |
| **416** | **Concept 02 (navy) is the chosen home-page direction — port it (#412)** | Owner supplied "IPRO Concept 02" (outside AI concept, X:\ipro_related\web_template\main_site); massaged version committed 2026-08-13 (`49d56b3`) at `DOCS/marketing-site-prototype/concept02-navy.html`: Navy+Aqua committed (light `#173b55`/`#087d83`, dark navy fixed too), corp logo embedded, verticals = Insurance / Financial + Accountants + Mortgage + **Generic** (placeholder name, owner to decide), pricing corrected (annual 10×, Gold/Plat setup struck to $0 Waived, seats 1/2/5), 3 unverifiable claims cut (no per-vertical workflows; self-serve page builder instead of "review with a real person"; "guided human setup" trust item replaced), hero portal mock now interactive — 7 clickable tabs with keyboard support. Known limitation: <420px hides the mock's sidebar (pre-existing). **NOT deployed — it is a disk prototype.** Next: owner reviews + massage list, then the Razor port (data-driven pricing, CSP nonce scripts, real /Preview + ?package= links). Owner 2026-08-13: "too much for one day" — resumes next session. |
| ~~417~~ | ~~DATA LOSS + POST-MORTEM: Bob2Mot's paid invoice 000008 destroyed by FK cascade despite #406~~ | **FIXED + RESTORED 2026-08-14.** Owner deleted Bob2Mot (as planned) and correctly spotted that paid invoice IPRO-2026-000008 ($67.80) vanished from Revenue even though #406 was built to retain it. Root cause: #406's eraser was written on the premise the DB has NO foreign keys — false. Both apps run `MigrateAsync()` at startup, and the EF migrations created ~48 FKs, four with ON DELETE CASCADE into the ledger (`AgentUsers→Billings→Invoices→InvoiceLineItems`, `AgentUsers→SubscriptionChanges`). The eraser skipped the financial tables as designed, but deleting the AgentUsers row made MySQL cascade them away underneath it; the banner's "0 invoices (0 rows)" was a truthful post-hoc count. Never caught earlier because bob3test3 was full-shredded and zedtester had no invoices — Bob2Mot was retention's first real test. Fix (all verified by local repro through the real Admin Delete endpoint): (1) new `FinancialLedgerSchemaGuard` runs at startup in BOTH apps after MigrateAsync — drops every CASCADE FK on Billings/Invoices/SubscriptionChanges, idempotent, so even a fresh DB can't cascade into the ledger; (2) eraser now counts retained rows BEFORE deleting and recounts after — any shortfall sets `RetentionViolated` and the banner + error log scream; (3) same guard restores 000008 one-time from primary evidence (invoice email + PayPal's API records for I-XEV6M0A7PHVX; guard keys on the PayPal txn id, NOT the invoice number). Repro run after the fix: banner "Financial records retained: 1 invoices (4 rows)", all 4 rows survived, 000008 + the repro invoice both render on /Reports/Revenue. OWNER: refresh Revenue — 000008 is back, with the 'deleted' badge. Money side separately verified clean via PayPal API: sub cancelled 13:06:27Z at deletion, exactly one lifetime charge, no orphans. |
| 418 | LATENT BUG — invoice numbers are reused after a full shred | `IPRO-2026-000008` was issued TWICE: bob3test3 got 000008–000012, the shred deleted them, and the number generator (max+1 over surviving rows) handed 000008 to Bob2Mot the next day. Harmless while shreds are rare and test-only, but two customers sharing an invoice number is an accounting smell — the restore above had to key on the PayPal transaction id because the number is ambiguous. Fix direction: persist a high-water mark (or never decrement: number from a counter table, not MAX(Invoices)). |
| ~~420~~ | ~~Regression suite battery 1: deletion/retention + ledger guard~~ | **DONE 2026-08-14.** `tests/IPRO.IntegrationTests` (xUnit, net8.0) — the first automated tests this codebase has ever had. 7 tests, each against its own throwaway REAL MySQL database: schema creation carries cascade FKs into the ledger (documents the threat); the guard drops them all, idempotently; the guard restores 000008 exactly once; erase-with-retention keeps all 4 ledger rows; **the Bob2Mot scenario itself** (cascades intact) raises RetentionViolated with shortfall 4; full shred erases the ledger; preview == erase. Run: `dotnet test tests/IPRO.IntegrationTests` with local MySQL up. Found on day one: (a) a migrate-only DB is NOT the prod schema — `BillingRule.DefaultWebsiteTemplateId` and friends exist only in startup repairs (hence item 419); fixture uses EnsureCreated (entity-model schema) until 419 lands; (b) `IPRO.sln` was missing `Build.0` entries for Debug/Release|Any CPU on ALL 9 projects — `dotnet build IPRO.sln` has been a silent NO-OP forever ("Build succeeded", zero projects compiled); fixed, plus removed two dangling test-project sln entries that never existed on disk. Next batteries: billing/invoice absorb rules, entitlements vs packages, signup pipeline. |
| 419 | Extract the ~30 duplicated schema-repair functions from BOTH Program.cs into one shared class | Each app carries its own near-identical copy (~1,200 duplicated lines, Web:633-1900, Admin:285-1560) and they can drift from each other silently. Move to `IPRO.DataAccess.StartupSchemaRepair.EnsureAsync(db)`, call from both apps, and switch the test fixture from EnsureCreated to MigrateAsync+repairs so tests run the TRUE boot path. Careful mechanical surgery — do it as its own change with the test suite green before and after. |
| 418b | Regression suite battery 2: billing money paths | Invoice absorb rule (<= boundary, the bug that minted spurious 000009/000011 twice), invoice-number generation (incl. the reuse bug in 418), setup-fee waiver resolution (IsSetupFeeWaivedOn), entitlements match package definitions (would have caught the SMS row in 410 and the GoogleCalendarSync gap). |
| **421** | **ULTRA-AUDIT 2026-08-14 — 5-agent full-codebase sweep; 17 findings fixed, 8 open** | Owner asked for a full sweep after the invoice loss ("4 prior audits, none found this"). `/code-review ultra` could not run — it reviews a *diff* and caps at 500 files/12k lines; the codebase is 1,241 files / 151k lines. Ran 5 parallel Opus auditors instead (billing, web/security, admin, jobs/email, data-layer). Reports live in the session scratchpad; the ranked triage is `0-TRIAGE.md`. **Every auditor independently diagnosed the same root cause: fixes were applied at the call site that once failed, never as an invariant — so each new input combination finds a fresh gap.** FIXED + TESTED + DEPLOYED: (1) CRITICAL unauth client-portal takeover — omitted `token` became `IS NULL` and matched any uninvited client (`5013486`); (2) ledger guard ran before MigrateAsync, no-op on fresh/DR DBs (`dcd89c5`); (3) one-time 000008 restore re-fabricated a phantom invoice on every non-prod DB — deleted (`dcd89c5`); (4) time-limited 100%-off promo activated free FOREVER with no PayPal sub; (5) missing plan id degraded a subscription to a one-time order = package forever for one payment; (6) BillingPeriod unvalidated from POST (Quarterly = $0 on the monthly plan, no tax gross-up) (`e76600f`); (7) late webhook retry resurrected a Cancelled subscription, nothing ever expires an Active row; (8) setup-fee promo ignored its package restriction; (9) SyncPrimaryDomainAsync re-parented any AgentDomains row (2026-08-05 CRITICAL, second door); (10) raw X-Forwarded-For re-read, bypassing the validated pipeline (`fec8f0f`); (11) drip campaigns entirely outside the consent system — EmailChannel had no member for them, so unsubscribed clients kept receiving every step (likely source of the SpamCop complaints) (`4d6ccc9`); (12) invoices marked Paid + receipt emailed on APPROVED, before money, with setup_fee_failure_action=CONTINUE meaning a DECLINED card still produced a paid invoice (`ae16eb6`); (13-14) TrialReminder + OverdueInvoiceReminder saved their idempotency marker once after the loop → Hangfire retry re-sent to everyone (`0a2b3d8`); (15) last active SuperAdmin could demote themselves, unrecoverable in-app (`2e9ee95`); (16) proration measured from Billing.StartDate, never advanced on renewal → every upgrade after the first renewal undercharged (~48% instead of ~97% at one renewal; ~1/13 after twelve) (`14460ea`). Test suite grew 0 → 33. **STILL OPEN — see item 422.** |
| ~~422~~ | ~~Ultra-audit remainder — ALL 8 CLOSED 2026-08-15~~ | Every letter fixed and verified; nothing dropped. (a) **DONE earlier this session** — `ReconcileActiveSubscriptionsWithPayPalAsync` runs inside `SubscriptionBillingJob`, expiring Active rows PayPal says are cancelled/suspended (wrapped so a PayPal outage can't take the rest of the job down). (b) **DONE (`66a5a25`)** — BillingRules now snapshots the price each PayPal plan was CREATED at (`PayPalMonthlyPlanPrice`/`PayPalAnnualPlanPrice`, written by every sync path); Packages→Edit shows a red banner when the editable price diverges from what subscribers are actually charged, amber when the plan predates tracking. Verified by rendering BOTH banner states on the real Edit page against SQL-simulated divergence. (c) **DONE (`8dcc6d3`)** — webhook replays are a permanent no-op: the paid-webhook handler checks the txn id against every invoice on the billing before minting anything (txn id appended to the settled invoice, so the check holds forever, not for a 6h window). Chose txn-id dedupe in the handler over the proposed unique index because one invoice can legitimately absorb several txns. (d) **DONE (`8dcc6d3`)** — failed-payment webhooks no longer mint numbered invoices; one open `PAYPAL_FAILED:` marker per billing (replays append to it), and settle-matching now prefers the amount-matched unpaid invoice (±$0.02) over blind-oldest. (c)+(d) pinned by 3 tests driving the REAL internal handlers against a real DB — all 3 proven to fail on the pre-fix code. (e) **DONE earlier this session** — PortalMessages/PortalRequests gained the entitlement gate. (f) **DONE earlier this session** — all 4 upload paths meter the shared storage quota. (g) **DONE (`1a2a6ec`)** — captcha tokens carry a nonce and are single-use (burned on success, 31-min memory cache; nonce-less tokens from the prior build grace out); SubmitCustomForm got the same 5m/10 rate-limit rule as SubmitLead in both path shapes; DYK queueing dedupes per-address and caps at 10/24h. Verified live on the local QA public site through the real form (CSRF + timing gate + consent all genuine): first submit created the lead, the IDENTICAL token+answer resubmitted was rejected and logged as a Captcha spam attempt. (h) **DONE (`d7c65c9`)**: all three verified against the live schema before changing anything — both missing tables exist with an agent path, BannerSlides has never existed; they cascade today so nothing was orphaned, but it becomes real orphaning the moment a cascade is dropped, which is now something we do on purpose. `AgentDataEraserCoverageTests` proven to catch both defects by reintroducing them. Suite 33 → 45 across the sweep. |
| ~~425~~ | ~~MIGRATIONS HAVE BEEN A NO-OP SINCE 2026-07-11~~ | **FIXED 2026-08-15 (`6a6d8cb`), the safe way.** Audited all 28 invisible `Up()` bodies first: 25 deliberately empty, 3 fully idempotent (every ALTER guarded by INFORMATION_SCHEMA checks) — so no history backfill was needed; `[DbContext]` added and EF applies them for real. Rehearsed BOTH paths locally before shipping: established prod-shaped DB went 15 → 43 history rows in one boot with zero errors; an empty DB applied all 43 from scratch. A pending-migrations check after `MigrateAsync` in both apps now screams on any discoverable-but-unapplied migration, so the silent-no-op class is closed. Remaining (F2, minor): the model snapshot covers 28 of 85 tables — matters only if someone scaffolds a migration with dotnet-ef, which we don't do; schema changes still go through the repairs. |
| ~~F-SWEEP~~ | ~~Auditor 5's 14 findings: ALL FIXED or ratcheted, 2026-08-15~~ | One session, every commit tested before push: **F3** empty-DB bootstrap (both apps proven to boot on a fresh DB; also found+fixed a cross-feature crash where a swallowed e-card seeding failure took down the admin-user seeder). **F5/F7** scheduled sends whose audience was deleted now FAIL CLOSED instead of broadcasting to everyone / silently sending to nobody; 3 tests pin it. **F6** client deletion goes through declarative `ClientDataEraser` (12 orphaned tables closed, portal-document blobs now deleted, history rows unlinked not destroyed) + coverage test. **F9/F10/F11** unique indexes on WebsiteTemplates.TemplateKey, BillingRules.PackageName(191), AdminUsers.Username — all three verified rejecting duplicates live; Packages Create/Edit refuse duplicate names with a message; bootstrap admin insert SeedGuarded. **F4** BillingRules/Invoices money-column repairs shared in `BillingRuleSchema` (Admin was silently missing all 10 base pricing columns); Admin alone on a fresh DB now builds them. **F12** invoice-number race retries instead of losing a PAID invoice; EF retry assumption pinned by test. **F13** dead `NewsLetterService.DeleteAsync` removed (would have cascaded away delivery history + live unsubscribe tokens). **F14** `SchemaIntegrityReporter` ratchet at both startups: 33-pair baseline (3 deliberate ledger drops, 30 debt), any NEW model-vs-schema FK gap screams on first boot. **F1** above. Suite 33 → 42. | Auditor 5's F1, independently re-verified: 43 migration classes on disk, only the 15 with a `.Designer.cs` carry `[DbContext(typeof(IPRODbContext))]`, and `__EFMigrationsHistory` holds exactly those 15 rows — last applied `20260710155952`. EF's `MigrationsAssembly.Migrations` filters on that attribute, so the 28 hand-written migrations added since have never been discovered and `MigrateAsync()` skips them. **This is almost certainly WHY the ~1,200-line repair layer exists** (item 419): migrations went quiet, the repairs became the real schema authority, and nobody noticed because they kept the schema correct — the auditor found ZERO entity↔column drift across 1,042 properties and all 85 tables. Production is fine today. **The danger is the fix:** adding the attribute makes EF discover all 28 AND immediately apply them against databases that already contain every object they create; several `Up()` bodies hold real CREATE TABLE/ADD COLUMN DDL, so the naive fix crash-loops both App Services. Safe order: audit each `Up()` for idempotency → either make them idempotent or insert the 28 rows into `__EFMigrationsHistory` so EF treats them as applied → then add the attribute → regenerate the model snapshot (F2, 28 of 85 tables) → add a startup assertion that on-disk count and `GetPendingMigrationsAsync()` agree. Own change, on a restored copy of prod first, suite green either side. Not urgent; is the riskiest item in this file. |
| ~~426~~ | ~~Client deletion: ALL THREE fixed~~ — (a) 2026-08-15 same-day; (b)+(c) closed by the F-sweep (F5/F7 fail-closed audience + F6 `ClientDataEraser`), each pinned by tests; details in the F-SWEEP row above. Original finding text kept below for the record. | Auditor 5's F5/F6/F7. (a) **FIXED 2026-08-15:** `ClientLifeEventReminderJob` read `e.Client.AgentUserId` outside the per-row try, so ONE orphaned life event NRE'd before the loop — and since `LastCheckedAt` is only written inside the loop, the orphan kept the oldest timestamp, stayed at the front of the `Take(500)` OrderBy forever, and killed life-event AND birthday reminders **for every agent in the system, permanently**. Query now filters `e.Client != null` server-side. (b) **OPEN, CRITICAL:** `FK_NewsLetterSends_Clients_ClientId` is ON DELETE SET NULL and `NewsLetterDispatcher.cs:194-200` falls through to `_ => query` = ALL subscribers — delete a client with a scheduled one-to-one newsletter and Monday it goes to the agent's entire list, while `AudienceLabel` still shows the narrow audience. Same shape in `PollDispatcher.cs:182-187`, and deleting a ClientCategory does it too (F7) plus silently sends polls to zero recipients. Fix: fail closed, mark the send Failed. (c) **OPEN:** `ClientService.DeleteAsync` loads with no `Include` and orphans 11 more tables — worst is `PortalDocuments`, whose blobs are never deleted and whose `BlobUrl` rows are orphaned so nothing can find them again. Fix: delete through a declarative map like `AgentDataEraser`. Live orphan scan is currently clean — all latent. |
| ~~427~~ | ~~Constraint + schema-authority gaps from auditor 5 — ALL CLOSED by the F-sweep 2026-08-15~~ (F3 empty-DB boot, F4 shared `BillingRuleSchema`, F9/F10/F11 unique indexes, F12 invoice-number retry, F13 dead method deleted, F14 `SchemaIntegrityReporter` ratchet); per-item detail in the F-SWEEP row. Original finding text kept below for the record. | F3 (HIGH): neither app boots against an EMPTY database — repairs run BEFORE `MigrateAsync` and `EnsureTableColumnAsync` catches only error 1060, while a missing table raises 1146; the documented DR path is unreachable and `FinancialLedgerSchemaGuard`'s own "fresh database" comment describes a sequence that cannot happen. One-line-ish fix: also swallow `NoSuchTable`. F4: `EnsureBillingRuleSchemaAsync` exists only in Web (INVARIANTS rule 4) — 10 BillingRules columns Admin can't self-heal. F9: `WebsiteTemplates.TemplateKey` is unique in the model but has no index in the DB (its migrations are among the 28 in item 425). F10: `BillingRules.PackageName` is a de-facto natural key with no unique index and no duplicate guard in `PackagesController.Create` — 7 call sites resolve packages by name, incl. the seeder that runs at every startup. F11: `AdminUsers.Username` has no unique index despite being the login identifier, and the bootstrap insert isn't `SeedGuard`ed. F12: invoice-number generation is an unlocked read-modify-write; the unique index turns a race into an exception thrown AFTER PayPal captures the money, losing the invoice row — same end state as 417, different route. F13: `NewsLetterService.DeleteAsync` is unreferenced and would cascade away delivery history + live unsubscribe tokens. F14: ~40 repair-created tables have NO foreign keys while `OnModelCreating` declares Cascade for them — the general form of (c) above. Full report: `DOCS/audit-2026-08-14/5-data-layer.md`. |
| ~~423~~ | ~~Re-run the 5th auditor: data-layer / schema integrity~~ | **DONE 2026-08-15** — report at `DOCS/audit-2026-08-14/5-data-layer.md`, 14 findings. Two fixed same day (eraser map + coverage test; the life-event job outage). The rest became items 425/426/427, and 425 is the most consequential thing this whole audit found. Original brief below. The only agent that did not finish (hit the account usage limit mid-run). Its brief is the most valuable remaining one given the cascade incident: entity↔schema drift (properties in NEITHER migrations NOR schema repairs), repair drift BETWEEN the two apps, a full delete-behaviour catalogue beyond the ledger, eraser coverage, and constraint gaps (uniqueness the code assumes but no index enforces). Re-run with the same brief when convenient. |
| ~~430~~ | ~~THE GREAT CLEANUP 2026-08-15: all test agents deleted, one keeper remains~~ | Owner-clicked, per-agent verified. Deleted 8: JohnMot, BoBMot (#14), JohnDoes (#15), PaypalTestLAstname (#17), BoBMot1 (#27, 3,702 rows/27 tables), SilverGatingTest (#21), Paypalsecondtest (#18, 9,354 rows/31 tables/8 files — the heaviest), RaniahMotamed2 (#24). **Sole survivor: BahmanMotamed #12 (bahmanmotamed.247advisers.com), confirmed serving 200.** This doubled as the retention system's biggest real-world exercise: **13 invoices (56 ledger rows) retained across the 8 deletions**, every banner reported its counts, zero retention violations — the owner sees them all in Reports → Revenue with 'deleted' badges. PayPal side swept clean after: all 7 known real subscriptions CANCELLED (bobmot1's three pre-verified before deletion; QA Platinum I-D9S0UCEMDT03 was already cancelled Aug 12), remaining ids are the never-charging APPROVAL_PENDING probes. Every deleted agent's public site returns 404. **Consequence: the 4-day QA daily-billing protocol (items 367-369) must RESTART with one fresh signup on QA Silver Daily — its agent and subscription no longer exist. That fresh signup conveniently also exercises the new provisioning (vertical Request Meeting form + Calculators) on prod.** |
| ~~429~~ | ~~cal1 calculator library: 7 new calculators, assigned per vertical under Resources~~ | **DONE 2026-08-15.** Owner supplied `X:\ipro_related\calculators\cal1.zip` (TUFaT ~40-calculator library; 7 were already ported). Ported the Canada-appropriate remainder as new CalculatorKinds -- **Affordability** (GDS 39/TDS 44 fixed-point iteration), **Accelerated Bi-Weekly**, **Mortgage Prepayment**, **Land Transfer Tax** (province-by-province marginal brackets at CURRENT rates, not cal1's 2008 data: ON+Toronto/BC/AB fee/SK/MB/QC/NB/NS-Halifax/PE/NL, with a rebates-not-included disclaimer), **Savings Growth**, **Savings Goal** (millionaire calc generalized, inflation-honest), **After-Tax Return**. Deliberately NOT ported: Roth, US estate tax, Armey flat tax, ARM-vs-fixed, 15-vs-30, points, balloon -- US-only. All live in `_CalculatorBlock.cshtml` with the existing Canadian semi-annual compounding primitives; the agent block picker gets all 14 kinds automatically. **Resources assignment:** new `VerticalCalculatorCatalog` (one map shared by provisioning + /Preview) adds a "Calculators" category under Resources -- Mortgage 8, Insurance/Financial 6, Accountants 5, generic 3 -- each calculator its own child page so it's linkable and appears in the 3-tier nav. New signups automatic; existing agents via the EXISTING Rebuild Resources button. **Verified:** math spot-checked through the real pages in a browser -- ON LTT $600k = $8,475 exact (Toronto $16,950, BC $10,000, MB $9,650 all exact vs hand-computed brackets), after-tax 5%/35%/2.5% = 3.25%/0.73%, $400k/5.5%/25y = $2,441.57/mo with bi-weekly payoff 21.3y saving $57,659, savings growth exact vs closed form; Rebuild Resources on a local agent produced the Calculators tree and the public host-routed page rendered. Suite 45/45. |
| ~~428~~ | ~~Request Meeting page: real per-vertical meeting forms (owner's reference designs)~~ | **DONE 2026-08-15.** Owner supplied 4 reference form designs (tmp/forms) and asked for them on every vertical's starter package at `/request-meeting`; Insurance + Financial merged into ONE form since that's one vertical here. Built: (1) new `DateTime` field type end-to-end (builder dropdowns in both apps, `datetime-local` public render, whitelists via `WebsiteFormFieldTypes.All`); (2) four seeded "Request a Meeting" starter forms (All / Insurance-Financial / Mortgage / Accountants), Canadian wording (CRA not IRS), each = vertical dropdowns + shared logistics tail (format, date&time picker, notes) — name/email/phone are built into the renderer, not fields; (3) provisioning (`WebsiteStarterPagesHelper`) now copies the vertical's template into a REAL WebsiteForm the agent owns (shared `WebsiteFormTemplateCopier`, also used by AdoptTemplate) and swaps the request-meeting page's ContactForm block for a Form block pointing at it — falls back to the plain contact block if the template is deactivated/renamed; (4) `ProspectWebsitePreviewBuilder` mirrors it with fake ids, and `_WebsiteCustomForm` gained the same isPreview submit-disable gate as the testimonial form (the preview now shows forms to strangers) plus a 2-column field layout matching the reference. **Verified locally:** all 3 verticals render in /Preview; a fresh cloned agent (Insurance / Financial) provisioned the page + form on first portal visit; the public page at a host-routed local site rendered the merged form, and one REAL submission (CSRF + timing + captcha + consent) created the lead with all 5 answers incl. the datetime. Suite 45/45. **Existing agents keep their current page** — new signups only; a per-agent rebuild action is a follow-up if wanted. Owner can edit the masters in SuperAdmin → Starter Forms. |
| ~~424~~ | ~~Starter Content block images could only be typed as a URL~~ | **DONE 2026-08-15** (`25afb72`). Owner on `/StarterContent/Edit/13`: "the first Starter Block > Image URL only has image path no upload or no selection from the free pool". Each block now has an upload form plus a picker over every image already used in starter content — blocks and articles share one container, so an image uploaded for either is offered to both. Validation extracted from `ECardDesignsController` into `IPRO.Admin/Infrastructure/AdminImageUpload.cs` rather than copied (extension + declared content type + magic bytes must all agree); a storage failure now returns a message instead of a raw 500. Verified locally against Azurite: real PNG uploads → stored → written to the block → fetchable → picker then renders it; a text file renamed `.png` is rejected and leaves the existing image untouched. **Two things the owner should know:** the picker is empty until at least one starter image exists (the first upload seeds it), and "free pool" was read as *our shared starter library* — if it meant free STOCK photography (Unsplash/Pexels), that is a separate integration needing an API key and licence review. Follow-up: migrate `ECardDesignsController` onto the shared helper so its copy can't drift. |
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

## Homepage claims — two removed 2026-08-15, one needed a DB repair

The public homepage advertised two things IPRO does not do. Both are gone as of `1d10e48`.

- **"email & SMS reminders"**, listed under *On every plan*. SMS is not built — nothing in the
  codebase sends one. Worse, `PackageFeature` had `SmsReminder` seeded **included on all four
  packages**, so the pricing comparison table showed a green tick against it on every plan and the
  entitlement system believed every agent had it. Fixed in the seeder *and* via
  `RepairSmsReminderEntitlementAsync`, a one-time startup repair — the seeder alone would not have
  touched production, because `EnsureFeaturesAsync` never re-syncs `IsIncluded` on existing rows.
  Verified in production: the row now reads "Mobile SMS reminder (not yet available)" with dashes.
  **If SMS is ever built, flip the definition and delete the repair method** — otherwise it will
  switch the feature back off on every startup.
- **"One managed blog post/month, written for you"**, under *Platinum adds*. Promised a human
  writing service. Now "AI drafting for newsletters, articles and social posts".

**The lesson worth keeping:** the copy fix alone would have looked complete while the tick stayed
on the pricing table. When a claim appears in both a view and seeded data, check both.

- [ ] **Sweep the rest of the marketing surface for the same class of problem.** Only the homepage
      was audited. `/Preview`, the Register page, and the agent-facing help docs have not been
      checked against `DOCS/MARKETING_BUSINESS_BRIEF.md` section 3.

## UPGRADE PRORATION AUDIT — 2026-08-16 — ALL FIXED same day (`1909426`, see FIXED block below)

Trigger: owner upgraded his real BahmanMotamed account Silver-ANNUAL ($480 paid Jul 6 2026) →
Gold-MONTHLY on Aug 16 and asked where the unused ~$427 went. Four-agent audit, every citation
re-verified. Short answer: he was compensated **in kind, not in cash** — the new Gold PayPal
subscription was created with `start_time = Jul 6 2027` (his paid-through date), $0 up front, and
the old annual sub was properly cancelled. The banner's "Next billing Sept 16 2026" is FALSE
(defect 3); PayPal's real first charge is $60 on Jul 6 2027. The agent loses nothing; IPRO
under-collects ~$214 + all HST. Defects, all in `src/IPRO.Billing/PayPalBillingService.cs`:

1. **CRITICAL — proration unit mismatch (line 212).** `charge = newPackage MONTHLY price ×
   remaining fraction of the old ANNUAL cycle` — prices ~10.7 months of Gold as 0.89 of ONE month
   ($53.26 instead of ~$640). Correct amountDue was ~$214; it computed $0. Every annual→monthly
   upgrade is free for the tier difference.
2. **HIGH — `Math.Max(0, charge-credit)` (line 213) silently forfeits excess credit.** The
   ~$373 remainder is written to `SubscriptionChange.ProratedCredit` which NOTHING reads
   (write-only column, grep-verified). No balance, no refund path anywhere in src (ToS says
   no refunds — fine — but the ledger should still record the forfeiture).
3. **HIGH — banner lies for deferred-start upgrades (line 390).** `ActivateSubscriptionBillingAsync`
   unconditionally overwrites the correct NextBillingDate (Jul 6 2027, stored at 1199) with
   now+1 period (Sep 16 2026). Right for fresh signups, wrong for upgrades. Self-corrects only
   when the first real payment webhook lands (~11 months of wrong banner). No alarm when the
   fake date passes unpaid — GetBillingIssueAsync ignores Active rows.
4. **HIGH — HST never collected on the new sub (line 2106).** Tax gross-up is gated on
   `invoice.TaxRate > 0`; the $0 adjustment invoice short-circuits to TaxRate 0 (1729-1731), so
   the Gold sub bills $60 NET forever while the banner says "plus applicable taxes". This is
   audit-2026-08-14 issue #7, still open — this incident is its first live instance.
5. **MEDIUM — the $0 Unpaid adjustment invoice (IPRO-2026-000009) gets falsely settled later.**
   First real $60 sale (~Jul 6 2027) fails the amount-match and falls into the oldest-unpaid
   fallback (962-964): marks the $0 invoice Paid, emails a $0.00 receipt, and the $60 charge
   never gets an invoice of its own.
6. **MEDIUM/latent — credit priced from the CURRENT BillingRule row (line 211 + GetAmount).**
   Worked here only because the 10× repricing never touches existing DB rows (seeder updates
   non-price fields only), so Silver still holds $480 in prod. Any SuperAdmin price edit would
   silently recompute past customers' credit at the new price. No column records what was
   actually paid for the running period.

Cross-check for 418b: the regression battery for billing money paths should cover 1, 3 and 5.
Owner's own account is the affected one, so no customer harm; decide fix order before real
agents start upgrading.

**FIXED 2026-08-16 — the whole block (A–E) shipped in one change**, plus a 15-finding adversarial
review round on the diff itself before commit. The review's keepers, all fixed same day:
`CalculateUpgradeProration` now handles DEFERRED-start rows (a second upgrade on a repaired
account would have clamped the fraction and sold ~11 months of the next tier for one month's
difference); `ResolveNextBillingDateOnPayment` guards BOTH date-write sites (activation AND the
sale webhook — fixing only one let the other re-corrupt within minutes) and keeps only dates
beyond one period, so fresh signups/renewals behave exactly as before; the ACTIVATED webhook and
CapturePaymentAsync both refuse superseded billings AND cancel the live PayPal subscription the
buyer just approved (refusing quietly left it billing with no account attached);
`Billing.Amount` follows PayPal's settled charge (promo-lapse credit fix); zero-due invoices
settle at ACTIVATION not creation (born-paid stranded abandoned checkouts with no Resume
banner); the setup-fee completion waiver is consumed by the next Applied Subscribe and the
banner honours the same 90-day window; term switching got its UI button (it was unreachable);
undo-vs-apply race narrowed to a pre-cancel fresh-status read (row-locking residual accepted);
dunning skips agents who completed-then-cancelled. ACCEPTED tradeoff, not a bug: a scheduled
downgrade fires up to 6h BEFORE the boundary (so the cancel always beats PayPal's charge), and
within that window the "Keep My Current Plan" button can lose the race. Battery 418b: 15 tests
in `BillingProrationMatrixTests` incl. the deferred-row and date-guard cases; 60/60 green.

### Downgrade-path companion audit (same day, second 4-agent pass, all citations re-verified)

Downgrades are scheduled (`ScheduleDowngradeAsync` ~1392-1413), zero proration BY DESIGN (nothing
is cut short), applied by the hourly job OR lazily on any page load, old sub cancelled at PayPal,
nothing auto-activated (H-7), agent re-subscribes through normal approval. That skeleton is sound.
Ten defects on top of it:

- **CRITICAL — EffectiveDate frozen from the local `NextBillingDate` column, PayPal never
  consulted (1396).** On the owner's corrupted account (DB says Sep 16 2026, PayPal reality
  Jul 6 2027) a downgrade clicked today fires Sep 16: cancels a sub paid through Jul 2027,
  forfeits ~10 prepaid months (credit hardcoded 0, no refund code exists), gates the account,
  and the re-subscribe bills full Silver + setup fee immediately. ~~OWNER MUST NOT CLICK
  DOWNGRADE OR CANCEL until fixed~~ (FIXED `1909426` — safe again; owner-verified the corrected
  Jul 6 2027 banner live). No self-service undo existed once scheduled — now there is one.
- **CRITICAL — stale pending downgrade destroys a later fully-paid upgrade.** Downgrade
  scheduled while an upgrade's PayPal approval link is outstanding → `CancelPendingChangesAsync`
  cancels the upgrade LOCALLY but the approval link survives; completing it resurrects the
  Cancelled billing (CapturePaymentAsync invoice-first path has NO status filter, ~243-251) and
  the still-Pending downgrade later cancels ALL Active billings (apply loop never checks
  `change.BillingId`, 1460-1480) — killing the subscription the agent just paid for.
- **HIGH — renewal race:** apply predicate is `EffectiveDate <= now` + hourly cadence, so the
  PayPal cancel always lands AT/AFTER the renewal instant. If PayPal charges first the agent
  pays a full extra term (a whole YEAR on annual) and then gets cancelled. No refund path.
- **HIGH — no UI way to undo a scheduled downgrade** (mis-click = locked in unless they pick a
  third package or cancel everything).
- **HIGH — same-package period switch silently discarded** (line 189 compares BillingRuleId
  only): Gold monthly→Gold annual returns "already on that package", so term switching is
  IMPOSSIBLE product-wide — and as a side effect the request silently wipes any pending change.
- **MEDIUM** — equal-price lateral move takes the downgrade path (interruption + fresh setup fee
  for a price-neutral switch); re-subscribe after downgrade re-charges the setup fee and resets
  the billing anniversary; exactly ONE email after apply, no dunning → non-responder = silent
  permanent revenue loss.
- **LOW** — "downgrade now in effect" email sent even when the buyer already cancelled at
  PayPal; the requested target period (e.g. "Silver ANNUALLY") is stored but dropped at
  completion.

Fix order agreed with owner 2026-08-16 (combined with the six upgrade defects above):
(A) date integrity first — stop the line-390 clobber, extend the hourly reconcile job to sync
`NextBillingDate` from PayPal (auto-repairs the owner's row, no manual prod write), derive
downgrade EffectiveDate/apply from reconciled truth; (B) proration units + credit ledger +
historical-paid basis; (C) tax-from-agent + settle-fallback tightening; (D) downgrade UX/safety
(undo endpoint+button, apply-loop BillingId guard + status filter in capture, term-switch
support, pre-boundary apply buffer, waive setup fee on downgrade re-subscribe, dunning);
(E) battery 418b = full cross-period regression matrix so no period×direction pair is ever
first executed by a customer again.

## OPEN — three billing/UX defects found 2026-08-17 tracing "Gold annual → Silver monthly"

Owner asked how a client who prepaid Gold ANNUAL is treated when they downgrade to Silver MONTHLY.
The flow itself is correct (defers to the paid-through date, keeps Gold meanwhile, no proration
because nothing is cut short, setup fee waived, HST collected, first Silver charge on approval).
Three real defects surfaced, all verified by reading the code, NONE fixed yet:

1. **"Cancel Subscription" is immediate but promises otherwise — decide the fix.**
   `Index.cshtml:96` confirm text: *"Your site will go offline at the end of the billing period."*
   `CancelSubscriptionAsync` (PayPalBillingService.cs ~656-658) sets `Status=Cancelled` +
   `CancelledAt=now`, and `IsAccessGatedAsync` (PackageEntitlementService.cs) gates the moment no
   Active row exists. An annual subscriber 3 months in loses ~9 prepaid months instantly, and no
   refund path exists anywhere. This is ALSO the only "downgrade immediately" lever a client has.
   Two options, owner's call: (a) one-line copy fix telling the truth ("access ends immediately, no
   refund for the remainder"), or (b) make behaviour match the promise — cancel at PayPal now so
   billing stops, keep local access until the paid-through date. (b) is the better product but is
   NOT a one-liner: the hourly `ReconcileActiveSubscriptionsWithPayPalAsync` sees PayPal say
   CANCELLED and would flip the local row Cancelled, defeating the deferral. Needs a deliberate
   "cancelled but paid through" state that reconcile respects.

2. **The term-switch button (added 2026-08-16) silently destroys a scheduled downgrade.**
   `Index.cshtml:222-240`: the `isCurrent` branch renders "Switch to Annual/Monthly Billing"
   unconditionally, with no `isPending` check — unlike the other cards, which correctly render a
   disabled "Scheduled". Clicking it enters the same-package branch → `ScheduleDowngradeAsync` →
   `CancelPendingChangesAsync`, wiping the pending Silver downgrade with no warning. Mine, from
   yesterday; unambiguous, no product decision needed. Fix: hide/disable it whenever a pending
   change exists.

3. **The requested term is stored and displayed but never applied.** `ScheduleDowngradeAsync`
   persists `Period=Monthly` and the completion banner says "with monthly billing (the term you
   originally chose)", but nothing reads `change.Period` at completion — the completion form
   renders BOTH Monthly and Annually buttons, so someone who scheduled Silver-monthly can land on
   Silver-annual. Fix: pre-select/only offer the stored term, or drop the banner's claim.

Lower priority, same trace: the 90-day setup-fee waiver expires with no warning on the page or in
any email; a comped agent (no PayPal subscription id) can be gated by a downgrade with nothing to
re-approve; `ApplyDuePendingChangesAsync` makes synchronous PayPal calls on the web request thread.

## SEO pass — shipped 2026-08-16 (`6e04a5f`)

Prompted by Bahman's "was SEO ever considered?" — audit found the foundation solid (per-page
titles/meta, OG cards, ProfessionalService schema with address, per-site sitemap + robots,
noindexed previews, proper 404s) with two real holes, both now fixed:

- **Custom-domain duplicate content.** A site on both its subdomain and a bound custom domain
  self-canonicalized on each, so Google saw two competing copies. Now every SEO surface names ONE
  origin via `ResolveCanonicalOriginAsync`: the custom domain when binding+SSL are `Bound`, else
  the request host. **Deliberately canonical, not a 301** — browsers cache 301s permanently (a
  removed domain would strand past visitors), and a lapsed cert would take the site down; the
  choice recomputes per request, so deleting/breaking a domain reverts the subdomain instantly.
  Gate also accepts legacy `SslStatus='SslBound'` rows — caught by testing against real data.
- **Marketing homepage had a bare head.** Now: meta description, canonical from `App:BaseUrl`
  (placeholder-guarded so 4ipro.com defers to the real front door), OG tags, Organization schema.
- Privacy policy gained the **Website Analytics disclosure** (page views, hashed visitor, no
  cookie) — it was collecting undisclosed. Lawyer notified as a post-hand-off addition.
- New `DOCS/21_AGENT_GOOGLE_VISIBILITY.md` — the agent-side half (Google Business Profile,
  reviews, per-page meta, Search Console). Consider linking it from the portal's My Website page.

**Correction for the record:** the competitor gap analysis claimed agents lack traffic analytics.
Wrong — `/portal/WebsiteAnalytics` exists, cookieless, shipped as "privacy-safe". Remaining real
gaps, Bahman's priority order pending: IPRO's own social proof (his), abandoned-checkout email
nudge, real-time booking (post-launch), referrals/blog (later).

## Homepage: Navy/Aqua redesign — APPROVED, not started (2026-08-15)

Bahman approved porting the prototype design, not just the copy. Today's change put the settled
**words** into the existing blue Bootstrap page; the prototype's navy/aqua layout is a separate,
full-page rewrite of `Views/Home/Index.cshtml`.

Prototype: `DOCS/marketing-site-prototype/concept02-navy.html` (design + link map).
Copy source: `DOCS/marketing-site-prototype/copy-merged.html`.

**Everything below must survive the port — it is all live machinery, not decoration:**

- `@model List<BillingRule>` pricing cards, including `IsSetupFeeWaivedOn` (the same call decides
  what PayPal charges, so the displayed figure cannot drift from the charge)
- the feature comparison table, built from `package.Features` at runtime
- `?package=` carried into `/Account/Register` and `/Preview`
- `ViewBag.HeroInsight` (the AI assistant mock) and `ViewBag.TemporaryRootDomain`
- footer `/terms` and `/privacy` links

Never hardcode prices into the new page. The prototype's pricing block carries a RAZOR PORT NOTE
saying exactly this.

## Legal pages — shipped as draft, blocked on review (2026-08-15)

`/terms` and `/privacy` now exist and are linked from the site footer and the signup checkbox.
Neither existed before; the signup form required agreement to a document that was never published,
and there was no privacy policy at all.

**RELEASED 2026-08-17 (`3ef650e`).** Counsel signed off — approved as-is with minor changes, said
what exists is good enough for now, and will revisit a couple of months after launch. The draft
banner and `noindex` are gone from both pages, verified live. Unfilled values would still render as
yellow highlights, and there were none at release (all six values set).

- [x] **Legal entity confirmed 2026-08-15: iPro Advisers Inc.** "iPro Accountants" removed from the
      address line; both documents now name one entity consistently. Owner re-listed "iPro
      Accountants" on 2026-08-17; asked whether to add it back and answered **NO** the same day.
      The legal pages name **iPro Advisers Inc. only**. Settled — do not re-raise.
- [x] **Lawyer review — DONE 2026-08-17.** Approved as-is; counsel revisits a couple of months after
      launch. The clause-7 re-accept question is **CLOSED as moot** (owner, 2026-08-17): every
      account on this platform is a demo/test account, and the superseded agreement (archived at
      `DOCS/legal/archive/2026-08-15-superseded-online-subscription-agreement.txt`) belonged to the
      PREVIOUS product — approved in 2012, ran a few years, retired, zero clients since well before
      this platform. Nobody who accepted clause 7 is an active subscriber, so there is no
      re-accept-vs-notice decision to make. Do not re-raise.
- [x] **All six Legal__ values set** in `appsettings.json` (public business details, not secrets, so
      they live with the code). Effective date 15 February 2012, last updated 15 August 2026.
      `Legal__ReviewComplete` flipped to `true` on 2026-08-17.
- [ ] **Confirm two facts** the privacy policy asserts: that every Azure resource really is in
      Canada East, and Anthropic's current commercial terms on not training from API content.
- [ ] `Support:NotificationEmail` in `src/IPRO.Web/appsettings.json` is still the literal
      `CHANGE_THIS_SUPPORT_EMAIL`. Confirm it is overridden in Azure — if not, support
      notifications are going nowhere.
- [ ] `BillingCompany` has a name, email and website but **no postal address**. Invoices and the
      legal pages both want one. Consider making `Legal__RegisteredAddress` the single source and
      having invoices read it, rather than adding a second copy.

Reviewer's copy and the full change log: `DOCS/legal/` — start with `README-review-notes.md`.
The shipped text lives in `Views/Shared/_LegalTerms.cshtml` and `_LegalPrivacy.cshtml`; **edits to
the markdown must be carried into the partials**, which are what the site actually serves.

---

## Where to look for detail

| For | Read |
|---|---|
| Why a thing was built the way it was, and what was rejected | `DOCS/IPRO_Project_Status_And_Roadmap.md` |
| Rules that must stay true — routing, hosts, auth, billing | `DOCS/INVARIANTS.md` (read before touching those) |
| A bug that already happened, and how it was fixed | `DOCS/09_TROUBLESHOOTING.md` |
| Backup and release process | `DOCS/14_BACKUP_AND_RELEASE_CHECKLIST.md` |
| Running everything locally | `DOCS/16_LOCAL_DEV.md` |
