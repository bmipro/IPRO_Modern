# Post-sweep audit — 2026-08-20

Six parallel auditors run against `94d36c3` immediately after five same-day deploys
(61 files, +2,719 / −331). Five examined the changed surface; one did nothing but check whether
this project's own status documents tell the truth about its code.

**This file is the authority for these findings.** When one is fixed, edit it HERE — the same rule
`AUDIT_RECONCILIATION_2026-08-17.md` carries, for the same reason.

---

## Verdict

**2 CRITICAL, 15 HIGH, 20 MEDIUM, 12 LOW, plus 8 documentation errors.**

Both criticals were found independently by two auditors that never saw each other's work — the
strongest confidence signal available here.

The audit also confirmed a great deal: **31 of ~45 documented status claims verify TRUE in code**;
no XSS regression survived the AngleSharp 0.17 → 1.7 major bump despite heavy adversarial probing;
admin authentication fails **closed**; there is no third transaction-enlistment bug; and the
`PrepaidValue` math (the month-5 worked example, the month-10 crossover, the rounding direction) is
exactly as designed. **The core designs are sound. The wiring around them is where it broke.**

### The pattern — the most important finding

The chronic failure this project has documented since the 2026-08-14 ultra-audit ("fixes applied at
the call site that once failed, never as an invariant") has **not gone away. It moved:**

- **Fixed the library, not the caller** — `A5-H12`'s guard is real, tested, and unreachable from the
  production delete path.
- **Fixed one app, not both** — `ADMIN-7`'s revalidator exists in Admin; the agent portal has the
  identical hole.
- **Obeyed the invariant in the new path, violated it in the sibling written the same week** — the
  convert path calls `GetCurrentCycleStart`; the cancel path, four days later, uses
  `Billing.StartDate`, with the prohibition written out in that very file.
- **Asserted the property, tested only the mechanism** — the overlay test checks that eight named CSS
  properties are gone, so it passes while overlays remain constructible.

---

## Exposure: what is live NOW vs latent

| Finding | Status |
|---|---|
| Sanitizer deletes `<form>`/`<button>` **contents** | **LIVE** — silent data loss on every content save |
| Double-charge after cancellation (monthly included) | **LIVE** — the moment any agent cancels |
| Shared-file guard never runs | **LIVE** — on any agent deletion |
| Erasure transaction lock hazard | **LIVE** — on deletion of any agent with real data |
| Annual clawback measures from `StartDate` | Latent — no annual subscriber has renewed yet |
| SSRF via DNS rebinding | Needs an agent with the custom-domain entitlement |
| Everything else | Latent or narrow |

> **QA DAY-4 WARNING.** The planned "cancel + delete BobyMot" step trips **three** live findings
> simultaneously (double-charge state, blob guard, erasure lock hazard). Do not run it until
> wave 1 below has shipped.

---

## CRITICAL

### C1 — The cross-agent shared-file guard has never run in production
*Found independently by the data-layer auditor and the truth auditor.*

`src/IPRO.Admin/Controllers/AgentsController.cs:442` takes `PreviewAsync`, `:452-464` permanently
deletes every URL in that **unfiltered** list, and only `:494` calls `EraseAsync` — where the A5-H12
re-check lives, gated `if (execute && ...)` (`src/IPRO.DataAccess/AgentDataEraser.cs:317`). The
filtered result (`report.Blobs` / `report.SharedBlobsKept`) is used only in an audit-log string.
The preview list is filtered by the **old three-table check only**.

So the finding A5-H12 describes is still fully live: delete agent A, and agent B's shared starter
image dies with it — broken images on their public pages and in already-delivered mail, no undo.
`BlobReferenceGuardTests.cs:89` passes because it asserts on the eraser's report, never the controller.

**Design conflict — RESOLVED 2026-08-24, owner chose option A.** `AgentsController.cs` deliberately
deleted files *before* rows (an exception between the two stranded 10 files on 2026-08-04), but the
shared-file check can only work *after* rows are gone. Both could not hold. Decision: **delete after
the shred**, accepting a small stranding window that the pre-destruction log line makes recoverable
by hand — because a wrongly deleted file another agent still displays is not recoverable at all.

**Consequence for the record:** `A5-H12` must revert to NOT FIXED, and today's handoff claim
*"High (actionable): 0"* was false when written.

### C2 — Annual cancellation measures from `Billing.StartDate`, robbing renewed subscribers
*Found independently by the billing auditor and the truth auditor.*

`src/IPRO.Billing/PayPalBillingService.cs:765` passes `subscription.StartDate` as the period start.
`StartDate` is written only at activation (`:573`, `:1488`) and **never advanced on renewal**.

For any annual subscriber past their first anniversary: `MonthsUsedRoundingUp` caps at 12
(`PrepaidValue.cs:58,90,96`) → `refundNet = paidNet − monthly×12 ≤ 0` → the crossover branch sets
`PaidThroughAt = StartDate.AddYears(1) + 2d`, **a date in the past** → gated immediately, `$0`
refund, no refund-queue row. Gold annual renewed 2026-03-10 for $678, cancels 2026-04-15: correct
outcome is 2 months used, **$480 + $62.40 = $542.40 refund**, access to 2026-05-12. Actual: nothing,
and the site goes dark the same day — while `BillingController.cs:226` prints *"You keep full access
until March 12, 2026"*, five weeks in the past.

The prohibition is stated at `PayPalBillingService.cs:3648-3656`. The convert path (written four
days earlier) obeys it via `ResolvePaidThroughEndAsync` + `GetCurrentCycleStart`; the cancel path
does not. Invisible to tests because every `PrepaidValueTests` fixture seeds a first-year `StartDate`.

**Fix:** `GetCurrentCycleStart(await ResolvePaidThroughEndAsync(subscription), BillingPeriod.Annually)`.

---

## HIGH

| # | Finding | Where |
|---|---|---|
| H1 | **Sanitizer deletes removed tags' contents.** `KeepChildNodes` is false, so `<button>CTA TEXT</button>` → empty and `<form><p>text</p></form>` → empty. Sanitisation runs on **write**, and `NewsletterController.cs:398` re-sanitises and re-saves *existing* article content. Silent, permanent, agent-visible data loss. **Reproduced as a failing test in our own suite.** | `HtmlContentSanitizer.cs:28-30` |
| H2 | **Double-charge after cancellation.** `GetActiveSubscriptionAsync` matches `Active` only, so a cancelled-but-paid-through agent sees no package card, a **past-dated "Trial active" banner**, and Subscribe buttons that enter the no-subscription branch and bill immediately — a second charge for a window already paid for. | `PayPalBillingService.cs:48`, `BillingController.cs:45-82`, `Views/Billing/Index.cshtml:71-80` |
| H3 | **The two entitlement resolvers now disagree.** `IsAccessGatedAsync` and `HasAccessBulkAsync` were updated for `PaidThroughAt`; `ResolveBillingRuleIdAsync` was **not** — it falls through to `AgentUser.PackageId` (stale for anyone who last activated before 2026-08-06). Same agent, two package answers. `OverdueInvoiceReminderJob` switched between them today. | `PackageEntitlementService.cs:186-225` vs `:114-128` |
| H4 | **SSRF: DNS rebinding defeats `PublicHostGuard`, with a readout.** Guard and fetch resolve independently; an agent controlling their own DNS alternates public/private answers and re-triggers via `RetryDomain` every 15s. Not blind: `RootLastError` (internal status code + redirect target) is rendered **verbatim** in the portal. The code comment dismissing rebinding as out of scope is wrong. | `PublicHostGuard.cs:16-19`, `DomainCheckService.cs:98/164/200/230`, `Views/Website/Index.cshtml:661` |
| H5 | **Promo cap slots leak permanently.** The claim comment says an abandoned checkout releases its slot via `CancelPendingChangesAsync`; `CancelPendingPaymentAsync` (the "Cancel checkout" button and Resume Payment) never calls the release. There is also **no sweeper for stale pending changes**. The code is then silently dropped on retry — the agent pays full price *and* the un-waived setup fee. | `PayPalBillingService.cs:147-161`, `:676-716` |
| H6 | **Setup fee wrongly waived after a convert-downgrade.** The completion waiver keys on "an Applied Downgrade with no later Applied Subscribe". A convert is an Applied Downgrade that is already complete, so a later voluntary re-signup gets $150–$400 waived. | `PayPalBillingService.cs:218-232` |
| H7 | **A recoverable SendGrid rejection kills every drip enrollment permanently.** 401/403 (rotated key, exhausted credits, unverified sender) and the not-configured result classify as permanent → `Status = Failed` with `SendAttempts` not even incremented. **No resume path exists** — `Active` is only ever assigned at enrollment creation. Recovery means re-enrolling, which re-sends every prior step. | `SendGridEmailService.cs:78-80`, `DripCampaignJob.cs:152-156` |
| H8 | **The webhook's per-event catch swallows unsubscribes and spam complaints.** All `SuppressAllAsync` paths sit inside the new try; a DB hiccup is caught, logged, answered 200 — SendGrid never retries and the opt-out is lost permanently. Traded recoverable duplicate stats for an unrecoverable lost opt-out (the one event class with legal weight). | `NewsletterController.cs:572-620` |
| H9 | **The erasure transaction is a production hazard on B1ms.** ~66 tables' locks held to commit, including four whose delete predicates have **no production index** (`PortalMessages`, `PortalDocuments`, `PortalAppointmentRequests`, `ClientInvoices` — raw-DDL tables). The blob re-check is **inside** the transaction: ~30 round-trips per file, five of them unindexed `LIKE '%url%'` scans over longtext **across all agents**. 200 files ≈ 6,000 statements holding write locks. Tests can't catch it: `EnsureCreated` gives the test DB FK indexes production lacks. | `AgentDataEraser.cs:268-330`, `StartupSchemaRepair.cs:444-591` |
| H10 | **A detected retention violation is committed anyway.** `shortfall` is computed inside the transaction, then `CommitAsync` runs unconditionally — the exact 2026-08-14 disaster is detected and made permanent, with a banner warning the admin *after* the loss is durable. Unfixable before today; two lines now. | `AgentDataEraser.cs:301-335` |
| H11 | **Rollback does not leave "locked out but intact".** Real order: PayPal cancelled → **all blobs deleted** → **Azure hostnames + certs unbound** → `IsActive=0` → transaction → shred. A shred failure rolls back rows only; files, domains and the subscription are already gone irreversibly, the audit log never runs, and the admin gets a generic 500. | `AgentsController.cs:419-494`, `AgentDataEraser.cs:253-268` |
| H12 | **The convert's pending row is auto-cancelled as "stale" within the hour.** `BeginPaidChangeAsync` writes `BillingId` = the **new Pending** billing; `ScheduleDowngradeAsync` writes the **old Active** one. The hourly sweeper only understands the latter and cancels the convert as stale while the agent is still at PayPal — which also lifts the UX-TERMSWITCH guard shipped the same day. Root enabler: `currentBillingId` is a declared-but-never-read parameter. | `PayPalBillingService.cs:1590/1633-1647/1985-1998` |
| H13 | **The drip catch's own `SaveChangesAsync` is unguarded, and `LastError` is unbounded.** `LastError` is `varchar(1000)`; `ex.Message` is not truncated anywhere. A long message → *Data too long* → the catch's save throws → the whole job aborts → Hangfire retries → same row, **same send**. `SendAttempts` never persists, so the cap never engages. `HandleSendFailure` also never advances `NextSendAt`, so a poison row sits at position 1 of every batch. | `DripCampaignJob.cs:134-143`, `IPRODbContext.cs:501` |
| H14 | **DidYouKnow retries transient failures forever.** The new `continue` leaves the item claimed for the 15-minute sweep, and `DidYouKnowEmailQueueItem` has **no attempt counter** — unlike every other claimed sender. If the send actually succeeded and only the response timed out, the client receives that article every 15 minutes indefinitely, and the agent's screen shows "Sending" forever. A comment in `SendClaims.cs:442` asserts this path is bounded; it is not. | `DidYouKnowEmailDispatchJob.cs:166-176` |
| H15 | **The cancel confirmation promises an automatic refund.** *"will be sent to your PayPal account within a few business days"* — for a wholly manual process, unconditionally, including months 6–9 where PayPal's ~180-day window has closed and the refund queue itself shows **EXPIRED**. | `BillingController.cs:229` |

---

## MEDIUM

| # | Finding | Where |
|---|---|---|
| M1 | **The overlay control does not prevent overlays.** `transform` + negative `margin` + `100vw/100vh` reconstructs a full-viewport opaque cover — verified against the real library. The test asserts only that the eight named properties are gone. **Reproduced as a failing test.** | `HtmlContentSanitizer.cs:36-39` |
| M2 | **`RebuildRequestMeeting` is ungated.** Same `RemoveRange` block destruction as `RebuildResources`, 60 lines below it, still on `AdminAccess`, with an enabled button and a confirm that doesn't mention loss. | `AgentsController.cs:197`, `Views/Agents/Details.cshtml:44` |
| M3 | **Refund tax uses today's province**, not the rate charged on the original invoice — contradicting DOCS/22 line 36. `lastPaid.TaxRate` is already loaded five lines later. Ontario→Alberta move under-refunds $24 on a $300 refund and keeps HST already remitted to CRA. | `PayPalBillingService.cs:762` |
| M4 | **Refund amount uses the current `BillingRule.MonthlyPrice`**, repricing history — the BILL-CREDITPRICE rule DOCS/22 itself cites. A price rise always shrinks the refund and can move the crossover earlier. | `PayPalBillingService.cs:764` |
| M5 | **A PayPal-initiated cancellation bypasses the entire design.** The reconcile job and the CANCELLED webhook set `Status = Cancelled` without `PaidThroughAt` and without a refund row → instant gating, nobody learns money is owed. Our shipped ToS explicitly invites cancelling "through PayPal". | `PayPalBillingService.cs:1245+`, `:3189-3196` |
| M6 | **DOCS/22 Stage C is reported shipped; two pieces did not ship.** No credit-note mechanism exists (the CRA tax-by-region figure over-reports after any refund), and `RefundStatus.ConvertedToCredit` is unreachable — no action sets it — while the queue banner instructs the operator to use it. | `ReportsController.cs`, `RefundsController.cs` |
| M7 | **The per-agent catch continues on a shared, dirty change tracker.** Agent A's unsaved mutations ride the ChangeTracker into agent B's `SaveChangesAsync`; a `DbUpdateException` poisons the context so every remaining agent fails identically. Needs `ChangeTracker.Clear()`. | `PayPalBillingService.cs:819-833` |
| M8 | **ADMIN-7 was fixed in Admin only.** `IPRO.Web` has no `ValidatePrincipal`, so deactivating an *agent* leaves their 8-hour sliding session live — including through an erasure. | `src/IPRO.Web/Program.cs` |
| M9 | **Article images count against the quota but their upload path never checks it** — the only upload path that doesn't. `ImageSizeBytes` also never resets when an image is removed. | `ArticlesController.cs:84,136` |
| M10 | **The new 1024 MB default is enforced but never displayed.** Every user-facing site still uses `LimitValue ?? 0` → "1150.3 MB of **0** MB used" for a blank limit, which is a deliberately supported configuration. | `DocumentsController.cs:81`, `Views/Documents/Index.cshtml:9`, +3 |
| M11 | **`sendResult == null` still advances the enrollment** past a step never sent, and stamps `LastSentAt`. Reachable when a step is deleted mid-run. One-line fix. | `DripCampaignJob.cs:104` |
| M12 | **Consent-driven cancels never write `CancelledAt`** (all three paths), so "when did we stop mailing this person" — the CASL question — is unanswerable. The sweep is also unbounded (no `.Take()`). | `DripCampaignJob.cs:77`, `EmailConsentService.cs:205,249` |
| M13 | **The public site never goes offline.** `IsAccessGatedAsync` has exactly three call sites — portal middleware, Billing page, layout. None is the public website. DOCS/22 claims this "becomes true"; it did not. | `PublicWebsiteController.cs:788-810` |
| M14 | **"Lockout first" is not first** — blobs and Azure unbinds happen before it, a window ADMIN-6 widened today. No try/catch around `EraseAsync`, so a failure yields a raw 500 with no audit entry. | `AgentsController.cs:440-497` |
| M15 | **Nothing guards deleting an agent who is owed an unresolved refund**, and `eraseFinancialRecords: true` destroys the row. The pre-delete guard reads `Status == Active` only, so a paid-through agent deletes with no warning. | `AgentsController.cs:415-416` |
| M16 | **Downgrade-completion dunning ignores the new Cancel row** — an agent who scheduled a downgrade then cancelled is dunned at day 3 and day 7. | `PayPalBillingService.cs:906-912` |
| M17 | **IPv6 transition prefixes bypass the guard**: `::127.0.0.1`, `::7f00:1`, NAT64 `64:ff9b::`, 6to4, Teredo. Only `::ffff:` is unwrapped. Gap confirmed; exploitability unproven (unroutable in the auditor's environment). | `PublicHostGuard.cs:28-34` |
| M18 | **`A5-H11`'s registry misses two containers** the app uploads to: `ecard-art`, `starter-content`. Orphans there are invisible to the report. | `BlobReferences.cs:61-69` |
| M19 | **`A5-M-JOBISOLATION` covered one loop.** `NotifyBillingIssuesAsync` has three un-isolated loops and `SubscriptionBillingJob.RunAsync` has no guard between the two calls — the finding's own stated consequence is only partly closed. | `PayPalBillingService.cs:840-886` |
| M20 | **`ADMIN-2`'s "ResumePayment deliberately not guarded" is false, and backwards.** It *is* guarded — after `CancelPendingPaymentAsync` voids the invoice. On divergence the agent loses their resumable checkout **and** is refused. | `PayPalBillingService.cs:664-673` |

---

## LOW

- **L1** Public IP literals are accepted as custom domains, contradicting two comments that say otherwise (`PublicHostGuard.cs:11-13`, `DomainCheckService.cs:90`). `NormalizeDomain` also doesn't strip `@`.
- **L2** Telemetry scrubbing covers only `RequestTelemetry`; trace/exception/dependency items keep whatever `Operation.Name` the SDK set. (`request.Url`, the field that definitely holds the token, is correctly scrubbed.)
- **L3** The two `SensitiveDataTelemetryInitializer` copies are identical *today* but nothing prevents drift, and the Admin copy has zero test coverage.
- **L4** `Articles.ImageSizeBytes` is added inside `EnsureDripCampaignEnrollmentSchemaAsync` — a future refactor of the drip function silently takes it along.
- **L5** `RemoveDomainAsync` ignores `_options.Enabled`, so an operator with automation disabled still gets live bindings and managed certs deleted on agent deletion.
- **L6** Build warning `CS8602` at `AgentsController.cs:481` (the null branch is also unreachable — the service is registered unconditionally; the real unconfigured path returns `Skipped` and is logged as an error per domain).
- **L7** Transient retries insert a fresh `DripCampaignStepSend` per attempt, inflating per-campaign failure statistics.
- **L8** Drip consent reads the `Client` snapshot `Include`d at batch start; a mid-batch unsubscribe still receives their step. (DidYouKnow re-reads fresh — the stronger pattern.)
- **L9** `RefundPayPalTransactionId varchar(64)` vs comma-joined transaction lists that can exceed it; error 1406 would throw *after* PayPal was cancelled. SUSPECTED, unverified against live sql_mode.
- **L10** The convert's deferred `start_time` widens the abandoned-approval window from minutes to months. SUSPECTED — exact PayPal ACTIVATED timing for future-dated starts unverified.
- **L11** `_RichEditor.cshtml:34` renders stored HTML raw in both apps, and the three public managed-page partials use `@Html.Raw`. The doc's own note that "one editor view still renders raw stored HTML" remains true.
- **L12** Sanitisation applies on **write** only; content authored before 2026-08-20 still holds live `<form>` blocks and overlay CSS until re-saved. No backfill exists.

---

## Documentation corrections required

| Doc | Correction |
|---|---|
| `AUDIT_RECONCILIATION` | `A5-H12` → **NOT FIXED** (C1) |
| `AUDIT_RECONCILIATION` | `ADMIN-2/BILLING-9` → strike "ResumePayment deliberately not guarded" (M20) |
| `AUDIT_RECONCILIATION` | Downgrade to PARTIAL: `A5-H11` (M18), `SO-M-NEW-6` (L3), `A5-M-JOBISOLATION` (M19), `A5-M-QUOTA` (M9/M10) |
| `AUDIT_RECONCILIATION` | Regenerate `## Counts` and re-title the OPEN section: it says **54 open**; **15** are (4 HIGH all accepted/deferred, 11 LOW). Every per-severity number is wrong. |
| `AUDIT_RECONCILIATION` | Heading cites `1fLB` — not a valid SHA. Correct: `1f6cedd`. |
| `AUDIT_RECONCILIATION` | Strike the Newtonsoft.Json "nothing pins it" bullet — the pin exists at `Directory.Packages.props:47`. |
| `DOCS/22` | Stage C → **PARTIAL** (M6); the ToS clause lives in `Views/Shared/_LegalTerms.cshtml`, not `Views/Home/Terms.cshtml`; Stage D added **4** tests, not 6. |
| `DOCS/TODO.md` | Medium sweep was **18** fixes, not 16 (18+2+1+1 = 22). `A5-M-RESEND` wording: the code flips anything not `Paid`, so a Void invoice resent becomes Sent. |
| `handoff.md` | **"High (actionable): 0" was false** — C1 and the items above. |

---

## Wave 1 — FIXED 2026-08-24 (branch `fix/audit-wave-1`)

All five live-exposure findings closed. Every fix was verified BOTH ways: the new test was run
against the pre-fix code and observed to FAIL, then against the fix and observed to PASS. That
two-way check is the thing whose absence let C1 ship green on 2026-08-18.

| Finding | Fix |
|---|---|
| **H1** sanitizer destroyed removed tags' contents | Two passes: pass 1 (`KeepChildNodes=false`) kills dangerous elements WITH their bodies, so `<script>` cannot leak as visible text; pass 2 (`KeepChildNodes=true`) **unwraps** every form control, so the words survive and no element remains. THREE wrong turns, each caught by a test rather than by review: (a) a single blanket unwrap leaked `alert(1)` as text; (b) keeping `<button>` allowed-but-attribute-stripped preserved prose but left an inert control, violating the existing A5-M-SANITIZER assertion — the full-suite gate caught it and the correct answer was to remove ALL form controls and let the unwrap keep their text, satisfying both rules with the existing test **unmodified**; (c) an H2 test that passed against the broken code because its assertion sat inside a condition that was never true. |
| **C1** shared-file guard never ran | Ordering reversed to ROWS BEFORE FILES. The controller now calls `EraseAsync` first and deletes only from `report.Blobs` (the filtered list); `plan.Blobs` is used solely for the pre-destruction recovery log. The old files-first ordering's rationale (stranding, 2026-08-04) is now carried by that log line. |
| **H9** blob re-check inside the transaction | Moved to AFTER commit. It needs the rows gone, not the locks — the slowest part of an erasure no longer holds write locks across ~66 tables. |
| **H10** retention violation committed anyway | Now a VETO: `RollbackAsync`, an empty blob list so the caller deletes nothing, and the controller aborts with an explicit audit entry. The agent is left deactivated but whole (Admin → Activate restores). |
| **H2** post-cancel double-charge | Two halves. Money: the no-active-subscription branch now finds a cancelled-but-paid-through row and starts the new subscription at `PaidThroughAt` (`deferredStart`), so cover is continuous and no day is paid for twice. UI: `BillingController` surfaces the row and the Billing page shows "cancelled — you keep X until <date>" instead of falling through to a stale, past-dated "Trial active" banner. The trial branch now also requires the trial to actually be in the future. |

New tests: `AgentDeleteBlobOrderingTests` (3, controller-level — the level at which C1 was visible),
plus H2 and sanitizer cases in `PrepaidValueTests` / `MediumSweepTests`.

**M1 (overlay) is explicitly NOT fixed** and its test is `[Fact(Skip=...)]` with the reason inline:
the deny-list cannot close it, and the real fix is a CSS allow-list that needs care not to break
existing newsletter formatting. Wave 2.

## Billing wave -- FIXED 2026-08-25 (branch `fix/billing-wave`)

The owner asked for the whole PayPal/billing cluster in one wave, "no assumptions, all passes
based on facts and tested before deploying." Discipline as wave 1: every fix's test was run
against the PRE-FIX code and observed to FAIL first (or, for H13/H14, the fix was reverted and
the green tests observed to go red -- same proof, other direction).

| Finding | Fix |
|---|---|
| **C2** | The cancel path now measures from the CURRENT cycle: `GetCurrentCycleStart(ResolvePaidThroughEndAsync(...))`, resolved BEFORE the PayPal cancel (next_billing_time stops being reported once the subscription dies). The renewed-annual fixture the whole suite lacked now exists; pre-fix it produced $0 refund and a PaidThroughAt five months in the past. |
| **M3** | Refund tax = the rate on the most recent PAID invoice (`Invoice.TaxRate`), falling back to the live calculation only when no invoice exists. Pre-fix an Ontario->Alberta mover was refunded at 5% for tax charged at 13%. |
| **M4** | The clawback's monthly rate = `Billing.Amount / 10` -- the subscription's own frozen annual price under the documented two-months-free structure (DOCS/22:45). NOT the invoice SubTotal (first-year invoices include the setup fee, which would corrupt the rate) and NOT today's `BillingRule.MonthlyPrice` (reprices history). Amount/10 keeps the month-10 crossover exact by construction for every historical price and promo discount. |
| **M5** | Cancellation outcome extracted to `ApplyCancellationOutcomeAsync` -- ONE place a subscription's death becomes money-and-access truth -- called by the self-cancel, the CANCELLED/EXPIRED webhook, and the reconcile job. Guard: only a row that is Active right now takes it (supersede/replay/self-cancel-race arrive already Cancelled). SUSPENDED now maps to Failed in the reconcile too, matching the webhook door -- it used to fold into Cancelled, which would have minted a refund row for a reactivatable subscription. |
| **H5** | `CancelPendingPaymentAsync` releases the claimed promo slot like its sibling always did; and a stale-checkout sweep (48h, in the hourly job) cancels never-completed checkouts -- change + billing -- releasing their slots. Scheduled downgrades are untouchable by selection: they ride an ACTIVE billing. |
| **H12** | The apply loop now recognises a Downgrade row whose billing is PENDING as an in-flight convert checkout and leaves it alone -- completion activates it, Cancel-checkout voids it, the 48h sweep cleans true abandonment. Pre-fix the hourly sweeper ate the convert as "stale" while the agent was still at PayPal. |
| **H6** | The completion waiver keys on `ProratedCredit == 0` (a convert always carries its credit; a scheduled row writes 0) and a later Applied CANCEL now consumes the waiver like a Subscribe does. Pre-fix a finished convert waived $150-$400 on any later voluntary re-signup. |
| **H15** | The cancel message stopped promising an automatic refund. In-window: "queued; our team processes refunds manually." Window closed (months 6-9): "our team will contact you to arrange it." |
| **M7** | The per-agent catch clears the shared change tracker -- continuing dirty meant one agent's poisoned save failed every following agent identically. Proven with a poisoned-tracker fixture: pre-fix 0 of 2 agents applied, post-fix the healthy one does. |
| **M16** | CONFIRMED empirically via the convert-then-cancel path (a convert's billing predates its AppliedAt, so the billing-only acted-on check missed the answer). Converts are now excluded from completion dunning by selection (`ProratedCredit == 0`, same distinguisher as H6) and an Applied Cancel row counts as acted-on. The unanswered-scheduled-downgrade reminder still fires (regression-pinned). |
| **M19** | Per-item isolation + tracker clear in all three `NotifyBillingIssuesAsync` loops, and `SubscriptionBillingJob.RunAsync` guards every stage (the PayPal reconcile already had one). A throwing email for agent A no longer starves agent B's reminder, and a stage failure no longer starves the stages after it. |
| **H13** | `LastError` truncated to its 1000-char column (compose-then-truncate for the give-up message); transient failures back off `NextSendAt` by an hour per attempt instead of hogging the batch head; the catch's own SaveChangesAsync is guarded with a tracker clear. |
| **H14** | `DidYouKnowEmailQueueItems.SendAttempts` added (StartupSchemaRepair, both apps). Transient failures and exceptions both register attempts; at 5 the item retires as Failed with the last error preserved. The infinite 15-minute redelivery loop is closed, which makes the SendClaims cross-reference comment true. |

**Dispositions, not fixes:**

- **New B (downgrade email links the canonical host)** -- works as designed. `PortalUrlHelper`'s
  header rules that OUT-OF-BAND links (emails, background jobs) go to canonical deliberately: a
  baked-in custom host can outlive its binding, while canonical is always safe to land on
  signed-out. Found by proposing the "fix" and then reading the header that already rejected it.
- **New C (Billing page shows a stale NextBillingDate)** -- mitigated, not broken.
  `ReconcileActiveSubscriptionsWithPayPalAsync` runs hourly and corrects >26h drift; the display
  can lag at most ~1 cycle + tolerance. The 30-day skew observed 2026-08-25 was the QA harness's
  daily/Monthly frequency mismatch, and the downgrade's own resolve self-healed it live.
- **M6 (Stage C credit notes, `ConvertedToCredit` unreachable)** -- unshipped FEATURE work, not a
  defect fix; stays on the owner-decision list.

New tests: `BillingWaveTests` (20: refunds x3, M5 x2, H5/H12 x4, H6 x3, M7/M16 x3, M19 x1,
H15/New A x4 -- controller-level where the defect lived), `SubscriptionBillingJobStageTests` (1),
`JobRetryBoundTests` (3) -- 24 in all. The chronic-trap note: every pre-existing PrepaidValue fixture seeded a
FIRST-year StartDate, which is exactly why C2 shipped invisible; the renewed fixture now exists.

**Remaining open in the register after this wave:** M1 (CSS allow-list), H3 (resolver split), H4
(SSRF pinning), H7 (SendGrid classification + resume), H8 (webhook suppression swallow), H11/M14
(erasure ordering only PARTLY closed by wave 1 -- Azure unbinds still precede the shred and
EraseAsync has no try/catch), M2, M8, M13 (public site never goes offline), M9-M12, M15 (agents
owed unresolved refunds are deletable with no warning -- WIDENED by M5, which mints more refund
rows), M17, M18, plus LOW and the owner-decision list. The claim "PayPal/billing: no known open
defects" was made here on 2026-08-25 and DISPROVED the same day by the four-auditor billing audit
below -- kept as a lesson, not a fact.

## Four-auditor billing audit -- 2026-08-25 (against a827bda) -- and WAVE 2, same day

The owner asked "is there any other issue left with PayPal?" and then for auditors "to check the
billing from a to z." Four parallel auditors (money math, state machine, jobs, truth) ran against
the deployed a827bda. The truth auditor's verdict on the billing wave: **all 13 fixes HOLD** at
their production call sites with genuinely-failing-pre-fix tests -- no C1-class green-but-useless
test -- with one caveat (M5's reconcile leg was untested; now it is, see below). The audit also
found what the tests could not: three of the most serious findings were defects the billing wave
itself created. "New A" definition for the record: the scheduled-downgrade disclosure (message,
term-switch message, and Billing-page banner now state the subscription end, the PayPal
re-approval, and the access pause BEFORE the agent commits).

### Wave 2 -- FIXED 2026-08-25 (branch fix/billing-wave-2), every fix verified both ways

| Finding | Fix |
|---|---|
| **C (HIGH, wave-created)** | DripCampaignJob's H13 tracker clear detached the rest of the tracked batch: later enrollments' emails SENT but their NextStepIndex++ silently stopped persisting -- guaranteed duplicate steps next tick, worse than the abort it replaced. The batch now BREAKS after a clear; the untouched remainder runs next tick. Pinned by an invariant test: emails delivered == advances persisted. |
| **A (HIGH)** | ApplyCancellationOutcomeAsync priced every cancel as if Billing.Amount was captured on that row for the running cycle -- false for deferred-start rows (converts, annual-to-annual upgrades): $270 phantom refunds on $0 collected, refund instructions exceeding the referenced capture, and PaidThroughAt slashed months into the past. The refund base is now min(Amount, invoices actually settled in the running cycle); a never-billed stretch takes the access-until-paid-through branch with $0 refund and the credit intact. |
| **D (HIGH)** | The double-mint race: three cancel doors guarded by read-then-act status checks could each mint a refund row. The fence is now a BillingCancellationClaims INSERT (PK = BillingId) committed in the SAME transaction as the outcome: the loser's duplicate-key rolls its whole mint back, and a crash mid-mint rolls the claim back with it (a status-flip claim was rejected for exactly that reason). New table via StartupSchemaRepair, both apps. |
| **B (HIGH)** | The 48h sweep consults PayPal before voiding: unreachable/unknown -> wait a pass; ACTIVE/APPROVED -> never voided (a lost activation, logged loudly); APPROVAL_PENDING -> best-effort PayPal cancel, then sweep. The money net: a payment captured against a Cancelled/Expired subscription now mints a refund-queue row (AppliedAt NULL so it can never consume the H6 waiver or suppress M16 dunning) instead of existing only as an error log. The stale-approval message stopped promising "you will not be charged" and points at the refund flagging instead. |
| **ISO (MED)** | The reconcile loop's outcome leg now fails per-row (try/catch + tracker clear, M7-style); pre-fix one poisoned row aborted the whole hourly sweep indefinitely. |
| **E (MED, wave-created)** | Expired rows get PaidThroughAt since the billing wave, but every gate honored it on Cancelled only -- instant lockout on paid-up time. All four predicates (both entitlement gates, the Billing-page banner, the H2 resubscribe deferral) now honor Cancelled OR Expired. |
| **DRIFT (MED)** | MonthsUsedRoundingUp iterated a cursor that .NET clamps at month-end (Jan 31 -> Feb 28 -> Mar 28...), counting a phantom month for cycles starting the 29th-31st -- a $67.80 under-refund on the worked example, against the agent-favouring rule. Boundaries are now anchored on periodStart. |
| **SLOT (MED)** | Promo slot releases moved AFTER the void's save commits, in all three release sites -- releasing first let a failed save release the same claim repeatedly (capped codes over-admitting). A crash now leaks a slot (fail-closed) instead of freeing claimed capacity. |
| **TxnRef (MED)** | RefundPayPalTransactionId now stores only the SETTLING transaction (last comma segment, failed-marker prefix stripped, clamped to the varchar(64)) -- the raw comma-joined retry list overflowed the column and pointed the queue at a FAILED transaction. |
| **M5R (coverage)** | The truth auditor's caveat closed: the reconcile door's outcome leg now has a scripted-PayPal test proving the full DOCS/22 outcome (not a raw status flip). |
| **QUEUE (LOW)** | The refund screen no longer instructs "convert to credit" -- an action that does not exist (M6 remains an owner-decision feature; the screen now says waive or arrange manually). |

18 new tests in BillingWave2Tests (16 defect-pinned red-to-green, 2 designed both-sides pins),
verified in aggregate by a combined reverse run: pre-fix code fails 16 of 18. New scripted-PayPal
test double (deterministic token/subscription/cancel HTTP) unlocks reconcile- and sweep-leg
coverage that the offline HTTP factory could not reach.

### Wave 3 -- FIXED 2026-08-25 late evening (branch fix/billing-wave-3): F2, F3, F5 + the owner's refund-policy decision

The owner picked F2-F5 off the open set plus the parked policy decision ("least complicated; worst
case I lose ~2 months"). F4 (month-end drift) was already closed in wave 2. Every fix verified both
ways (aggregate reverse run: 5 of 6 wave-3 tests fail on pre-fix code; the 6th is a designed
both-sides pin).

| Item | Fix |
|---|---|
| **F2** | Renewals de-tax at the rate the subscription was SOLD at (the last paid invoice's rate), never the agent's current province, and the renewal invoice carries that rate via an explicit override -- an Ontario-built $678.00 gross stays $600 + 13% after a move to Alberta, and Billing.Amount stays 600 instead of becoming 645.71 and poisoning every later proration. The legitimate promo-lapse Amount-sync (2026-08-16) is regression-pinned and survives. |
| **F3** | Profile's free-text Province box is now the same dropdown Register uses (legacy/US values preserved as a selectable option, never silently rewritten). The alias map gained the entries whose absence already zero-rated real signups: "Yukon Territory" -- the register dropdown's own label -- plus PEI, P.E.I., NWT. |
| **F5** | Every package plan sync clears the frozen promo plan ids for promos restricted to that package, BEFORE any PayPal call -- they lazily recreate against the current price at the next checkout. Closes the ADMIN-2 defect class for the promo sibling. |
| **POLICY** | Post-upgrade annual cancel (owner decision, this date): refund = full unused value at the row's rate (Amount - used x Amount/10), capped at everything actually settled in the running cycle across the agent's rows. The queue note tells the operator how much to take from which prior transaction when the refund exceeds this row's own capture. Wave-2's interim row-scoped cap is superseded (its A2 test revised with the decision cited); the impossible-refund guarantee stands -- the queue never instructs more than the cycle collected. |

6 new tests in BillingWave3Tests; wave-2's A2 revised to the decided policy. NormalizeProvince
widened private->internal for the pure alias test.

### Wave 4 -- FIXED 2026-08-25 night (branch fix/billing-wave-4): the last four billing MEDIUMs

| Item | Fix |
|---|---|
| **State F6** | Terminal states are never relabelled: a late SUSPENDED/EXPIRED delivery for an already-Cancelled/Expired row is logged and ignored (it used to flip Cancelled -> Failed, silently voiding paid-through honor with no repair path). And a CANCELLED/EXPIRED arriving for a SUSPENDED (Failed) row now takes the M5 outcome door -- suspension no longer forfeits the DOCS/22 outcome for a paid year. |
| **State F5** | "Keep My Current Plan" is convert-aware: a Downgrade row whose billing is Pending is an in-flight convert checkout, so the undo now voids the whole checkout -- change + billing + promo slot (released after the save, per SLOT) + a best-effort PayPal cancel of the approval. Pre-fix only the change row died and the live approval link could later execute the very change the agent had undone. |
| **F3c / jobs-4** | A row with ANOTHER Active billing alongside it was SUPERSEDED -- its value already moved as proration credit -- so the outcome door now raw-flips it instead of minting a clawback refund (value was being handed out twice). And a Cancel row referencing the applied downgrade's OWN billing no longer consumes the H6 waiver or suppresses M16 dunning (null-safe: only a concrete same-billing match is excluded -- the first predicate regressed the wave-2 null-BillingId test and was caught by it). |
| **Jobs-5** | Stage 3 (duplicate-subscription convergence) fails per pair with an immediate save and a tracker clear on error -- the batched save used to discard every convergence on one poison and hand stage 4 a dirty tracker. |

7 new tests in BillingWave4Tests (6 defect-pinned, 1 designed pin); aggregate reverse run: 6 of 7
fail on pre-fix code. With this wave the four-auditor billing audit has **zero open MEDIUMs; only
the 10 recorded LOWs remain** in the billing set.

### Wave 5 -- FIXED 2026-08-25 night (branch fix/billing-wave-5): the LOW sweep, all 10

Owner-approved ranking: #2/#3 first, #1/#4/#5 next, #6-#10 last. All ten fixed the same evening;
every defect test observed RED on pre-fix code (aggregate reverse run: 10 of 10 fail).

| # | Fix |
|---|---|
| **2** | PayPal webhook amounts parse through one seam with InvariantCulture -- the host-culture parse read "678.00" as 67,800 on any comma-decimal culture. |
| **3** | ClientInvoices.TaxRate repaired to decimal(7,5) (the Quebec fix's sibling table), with the EF model aligned (incl. DocumentNumber/ViewToken lengths the raw DDL indexes). |
| **1** | A convert derives credit from money ACTUALLY PAID: zeroed-Amount rows fall back to settled invoices, and zero paid is refused BEFORE any checkout row exists (the pre-fix fallback priced the credit at full list). |
| **4** | Resume on an abandoned convert re-applies downgradeMode="convert" (the shape is read before the void erases it) -- the agent no longer silently gets a scheduled downgrade they never asked for. |
| **5** | A CANCELLED/EXPIRED webhook for a Pending checkout voids the whole checkout -- change row cancelled, promo slot released after the save. |
| **6** | A Failed-to-Active recovery clears the suspension-era CancelledAt (every later door writes with ??=). |
| **7** | The suspension dunning email stops instructing a retry that does not exist; it now says the subscription is suspended and to subscribe again or contact support. |
| **8** | The day-3 dunning touch is never skipped: an overdue first run sends Day:3, the next run sends Day:7 -- the two-touch design no longer silently degrades to one. |
| **9** | Completion reminders name the billing period the agent chose ("Gold (annual billing)"), closing the term-switch complete-on-the-old-term gap. |
| **10** | Reconciling an ended row with NO billing date anywhere flips raw with an error log instead of inventing a year of paid-through from a computed fallback. |

10 new tests in BillingWave5Tests. One test-design lesson recorded: LOW-1's first assertion ("no
Pending rows") was satisfiable by the broken code because the failure exit voids its own rows --
tightened to "no convert row was EVER created". TryParsePayPalAmount and the IPRO.Web
InternalsVisibleTo grant were added for the seam test.

**With this wave the four-auditor billing audit is fully dispositioned: 0 HIGH, 0 MEDIUM, 0 LOW
open.** What remains billing-adjacent lives outside that audit: the pre-audit register items
(H3/H4/H7/H8, H11/M14, M15, M2/M8/M9-M13/M17/M18, the 12 pre-audit lows), M6 (credit-note
feature, owner decision), and the one-off agent-Province data check.

### Audit findings deliberately NOT fixed in wave 2 (now the open billing set)

From the four reports, deduped -- F2/F3/F5 + the refund policy went to wave 3; F6, state-F5,
F3c/jobs-4 and jobs-5 went to wave 4; the ten LOWs went to wave 5. **This section is now empty**
except one deliberate residue: the DYK counter hardening (jobs 7 -- server-side increment +
SentAtUtc retire predicates) rode along conceptually with H14 but was never separately fixed;
it stays the audit's single recorded LOW-hardening remainder, bounded either way by the cap.

## Erasure wave -- FIXED 2026-08-26 (branch fix/erasure-wave): H11 remainder, M14, M15

The gate the owner asked for before any real-customer deletion (BobyMot's delete never exercised
these: 0 files, no custom domain, no refund owed). Controller-level tests, red/green verified
(reverse run: 3 of 4 fail pre-fix; the 4th is ADMIN-6's regression pin, green both sides).

| Item | Fix |
|---|---|
| **M15** | An agent owed an UNRESOLVED refund (any SubscriptionChange with RefundStatus Pending) cannot be deleted -- refused before anything is attempted, with the owed total in the audit entry and on screen, pointing at the Refunds queue. The billing waves had WIDENED this exposure by minting refund rows from more doors. |
| **H11** | The Azure hostname/cert unbind now runs AFTER the shred commits (the domain list is still read before -- it dies with the rows). Pre-fix a failed shred rolled back the rows while the customer's live domain was already unbound: site dark, account "intact", recovery a manual DNS-and-cert crawl. The reversed trade -- a post-shred unbind failure leaves only a dangling binding on an already-404 site -- is the same reasoning as ROWS BEFORE FILES. Proven with the retention-veto shape: a vetoed delete now leaves the domain BOUND. |
| **M14** | EraseAsync is caught: a mid-shred database failure (proven with a SIGNAL trigger) now produces an AgentDeleteFailed audit entry and a precise on-screen message -- "locked out but intact, Activate restores, nothing external touched" -- instead of a raw 500 with no trail. |

4 new tests in AgentDeleteSafetyTests. Test-craft note: the first failure trigger (renaming a
shredded table) silently did nothing -- the eraser discovers its tables DYNAMICALLY, so a missing
table just drops out of the list; and the first RED run was voided by MySQL being down
(connection errors masquerading as failures -- the handoff's own documented trap, nearly walked
into again). Both reruns are genuine.

With this wave the erasure path's register items are closed: C1/H9/H10 (wave 1), H11/M14/M15
(here). A STRONG live rehearsal still wants a QA agent WITH uploaded files and a bound custom
domain -- noted in memory.

## Security + drip wave -- FIXED 2026-08-26 (branch fix/security-drip-wave): H4, M17, M18, M11, M12

The owner's recommended slice: the security pair + the two-line registry + the drip pair. Every
defect test red on pre-fix code (surgical reverse run: 14 of 21 fail with the four behaviours
reverted; the 7 that stay green are the pinned-handler/registry contract pins).

| Item | Fix |
|---|---|
| **H4** | SSRF via DNS REBINDING closed structurally. `PublicHostGuard.CreatePinnedHandler()` is a SocketsHttpHandler whose ConnectCallback resolves, validates (`FilterForConnect` -- any blocked address in the answer refuses the whole connection), and dials only approved addresses -- atomically, so there is no second internal resolve for an attacker's alternating nameserver to win. DomainCheckService now fetches through it; the friendly pre-checks stay for good error copy, but the security boundary is the connection. (`RootLastError` still surfaces the redirect target verbatim -- reviewed as acceptable: it is the agent's OWN public domain, not an internal probe result, now that the fetch can only reach public addresses.) |
| **M17** | Every IPv4-embedding IPv6 transition prefix is unwrapped and re-checked: IPv4-compatible `::a.b.c.d`, NAT64 `64:ff9b::/96` + local-use, 6to4 `2002::/16`, Teredo (server AND client v4, client bytes XOR 0xFF). Pre-fix only `::ffff:` was -- so `64:ff9b::a9fe:a9fe` (the metadata endpoint via NAT64) walked straight through. 10 blocked + 4 still-public cases pinned. |
| **M18** | `ecard-art` and `starter-content` added to `BlobReferences.Containers`. Both were uploaded to and their URL columns registered; only the report's container enumeration was blind, so orphans there were invisible. A source-walking test now fails if any `Container = "..."` constant in src/ is missing from the registry -- it can't drift again. |
| **M11** | A null `DispatchDripStepAsync` (campaign/step vanished mid-run) is a bounded transient failure, not success: no `LastSentAt`, no index advance. Pre-fix it stamped LastSentAt and advanced past a step that never sent. A past-the-end index now completes the enrollment before dispatch. |
| **M12** | All three drip-cancel paths (per-send, the sweep, SuppressAll) stamp `CancelledAt` -- the CASL "when did we stop mailing this person" answer, previously null on every path. The sweep is now bounded (`Take(batchLimit)`, default 500) so an hourly unbounded ToListAsync over every suppressed enrollment cannot grow without limit. |

7 new tests in SecurityDripWaveTests (14 InlineData cases). IPRO.Utility gained InternalsVisibleTo
for the connect-time seam (ResolveHook / FilterForConnect).

**After this wave the register's open set is:** H3 (resolver split), H7 (SendGrid classification +
drip resume), H8 (webhook suppression swallow), M1 (overlay CSS allow-list -- the skipped test),
M2, M8, M13 (public site never offline), M9/M10 (quota display), the 12 pre-audit LOWs, the DYK
counter hardening residue, M6 (owner decision), and the wave-3 doc corrections. Nothing left in
the open set is a live money-or-data-loss defect.

## Launch runway Phase 1 -- H8 + M8 FIXED 2026-08-27 (branch `fix/consent-session-wave`)

First slice of the Sept 21 launch plan. Both verified both ways; the reverse run turns exactly the
two WIRING tests red and leaves the contract pins green.

| Item | Fix |
|---|---|
| **H8** | The SendGrid webhook's per-event catch swallowed the three events that carry a legal instruction. Every suppression path runs inside that try, so a database hiccup while recording an unsubscribe or spam complaint was caught, logged, and answered **200** -- which tells SendGrid the event is recorded and must never be resent. The opt-out was unrecoverable. Now a failed CONSENT event (`unsubscribe`, `group_unsubscribe`, `spamreport`) withholds the acknowledgement and returns 503 so SendGrid redelivers; ordinary delivery events still sink alone exactly as JOBS-6 intended. The trade is explicit: duplicated statistics on a redelivered batch are recoverable, a lost opt-out is not. |
| **M8** | ADMIN-7's revalidation shipped in Admin only, so an agent's 8-hour SLIDING cookie kept full portal access after deactivation -- and after DELETION, walking around against rows that no longer exist. `AgentCookieRevalidator` mirrors the Admin pattern. It turned out **larger than the register described**: team members sign in as themselves and act AS the agent (NameIdentifier = the agent's id + a `TeamMemberId` marker claim), so checking only the agent would leave a revoked assistant with a live session on a healthy account. Both ends are checked, and a marker claim belonging to a different agent is refused. |

**Test-craft note worth keeping.** The first version of the H8 tests pinned only the classifier --
"is `unsubscribe` a consent event?" -- which would have passed green while the webhook ignored the
answer entirely: the exact C1 shape, caught before it shipped this time. Two tests now drive the
REAL `SendGridEvents` action (generated ECDSA P-256 keypair, genuine signature over
timestamp+payload, a renamed `Clients` table underneath) and assert the status code the action
actually returns. M8 had the same exposure -- `EvaluateAsync` could be perfect while nothing
called it -- so a source-walk test fails if the agent cookie stops pointing at the revalidator or
the revalidator stops being registered.

22 new tests. Register HIGH count: **3 open -> 1 open (H7 only; H3 next).**

## Launch runway Phase 1 -- H3 FIXED 2026-08-27 (branch `fix/resolver-split`)

`ResolveBillingRuleIdAsync` -- the SINGULAR path behind `GetAccessAsync`, which nearly every
controller calls -- matched `Status == Active` only. Finding nothing, it fell through to
`AgentUser.PackageId`, a column nothing rewrites when a plan changes. So a cancelled-but-
paid-through agent was resolved against whatever package they held BEFORE their last change, and
was refused features they had paid for and were still inside the paid period of. The bulk resolver
and `IsAccessGatedAsync` both learned `PaidThroughAt` when DOCS/22 shipped; this one never did.

Fixed by giving it the same predicate the other two use: an Active row wins, else a Cancelled OR
Expired row whose `PaidThroughAt` is still in the future (Expired included for the same reason
wave-2 E added it to the gates -- the door that grants access and the resolver that decides WHAT
they get must read the same set, or an agent is let in and handed nothing).

**Both resolvers carried comments demanding they "stay logically identical". Nothing enforced it,
which is exactly how they drifted.** Five tests now do, including the reverse direction: when the
paid period really has ended BOTH must withdraw access, so a fix that erred permissive fails too.
Reverse run: 3 of 5 red on the pre-fix code, with both regression pins green.

**Register HIGH count: 1 open -- H7 only.**

Merged and **verified live on both hosts at `f9361e5`** (2026-08-27). Suite at the gate:
336 passed / 0 failed / 1 skipped. Rule-6 check clean -- all four workflow runs completed, none
cancelled.

## Launch runway Phase 1 -- H7 FIXED 2026-08-27 (branch `fix/drip-recovery`)

Two halves, matching the finding.

**Classification** (`SendGridEmailService`): 401 and 403 join 429/5xx as TRANSIENT -- they mean
the ACCOUNT is broken (rotated/revoked key, exhausted credits, unverified sender); the recipient
was never the problem, and the account being fixed is exactly the outcome worth waiting for. The
not-configured and missing-sender results are transient for the same reason: config is fixed in
config, not by discarding queued work. What stays permanent: payload/recipient 4xxs (400, 413),
where retrying the same send IS spam. Only two callers branch on `IsTransient` (drip, DYK) and
both retry under caps, so the reclassification converts instant-permanent-death into bounded
retries and changes nothing else.

**Resume** (`CampaignsController.ResumeFailedEnrollments` + a button on the campaign page):
Failed -> Active, attempts reset, due now -- and `NextStepIndex` deliberately untouched: a failed
send never advanced it (JOBS-7), so it still points at the exact step that never went out and the
campaign continues with NO replays. Re-enrolling -- the only recovery that existed before -- would
re-send every prior step into the client's inbox. Resuming a client who unsubscribed meanwhile is
safe: the job's consent sweep and pre-send `IsSuppressed` check cancel the row before anything is
dispatched. The button's count is computed campaign-wide, not from the page's Take(50) slice, so
the label matches what the action does.

**Proof.** 10 new tests (`DripRecoveryTests`), reverse-run **5 red / 5 green** on pre-fix
classification -- the five reds are exactly the defect (401, 403, not-configured, the end-to-end
key-rotation kill through REAL service -> REAL dispatcher -> REAL job, and the cap's give-up
message); the five greens are the resume feature and the both-directions pins (400 stays
permanent; the transient cap still bounds a long outage). The service is driven through an
internal `ClientFactory` seam (the `PublicHostGuard.ResolveHook` pattern) so the classification
exercised is the production line, not a copy in the test -- the C1 lesson, applied not recited.

Two test-harness defects were caught and fixed DURING the red/green cycle, both worth recording:
the job's due-predicate translates `DateTime.UtcNow` to MySQL `UTC_TIMESTAMP()`, which truncates
to whole seconds -- a row resumed at hh:mm:ss.4 is not "due" until the next second (irrelevant at
hourly cadence, a coin-flip in a test); and the stub's null response-headers turned a healthy 202
into an NRE that the catch-all classified transient -- a green-looking stub bug.

**Register HIGH count: 0 open.** Every HIGH from every audit is now closed.

Merged and **verified live on both hosts at `21da3e6`** (2026-08-27). Suite at the gate: 346
passed / 0 failed / 1 skipped. Rule-6 check clean -- all four runs completed, none cancelled.
Smoke: app 200, login 200, admin 302, agent site 200.

## Launch runway Phase 2, Wave A -- M13 + M2 FIXED 2026-08-27 (branch `fix/medium-gating-pair`)

**M13 -- the public site now goes offline, as the cancel dialog has always promised.** The gate
lives in `FindWebsiteForHostAsync`, the single funnel behind page render, robots, sitemap, lead
submission, custom forms and testimonials -- a gated agent's site resolves to null and is
indistinguishable from one that does not exist (the render path's existing 404, so dead sites
deindex; and no leads are harvested into an account nobody pays for). `IsAccessGatedAsync`
honours `PaidThroughAt`, so a cancelled-but-paid-through site stays ONLINE until the promised end
of the paid period. The verdict is cached 2 minutes per agent because host resolution runs on
every public page view -- the price is a just-reactivated site can lag up to two minutes.
**Deliberate boundary:** client-portal routes (`/portal`) authenticate separately and are NOT
gated here -- a client's access to their own documents is not this fix's to revoke.

**M2 -- `RebuildRequestMeeting` takes the same SuperAdmin gate as its sibling.** Same
`RemoveRange` destruction as `RebuildResources`, sixty lines apart; now the same
`[Authorize(Policy = "SuperAdmin")]`, an honest confirm (the old one said "edits intact" while
the action deletes every block on the page -- only the FORM is reused), and a DISABLED button for
support admins -- disabled, not hidden, per the owner's standing rule.

**Proof.** 7 tests (`PublicSiteGatingTests`), reverse-run **6 red / 1 green** on pre-fix code --
the green is the active-agent pin (the gate must not take paying customers offline). The M2
reflection pin covers BOTH siblings so neither silently loses its gate again; the cache test
proves the entitlement query runs once, not per page view.

**Register MEDIUM count: 7 -> 5 open** (M1, M9, M10, M20, M6-decision).

## Remediation plan

**Wave 1 — stop the bleeding (live exposure).**
H1 sanitizer content destruction · H2 post-cancel double-charge · C1 blob guard actually running ·
H9 move the blob re-check out of the transaction · H10 roll back on retention shortfall.
**Blocks QA day-4.**

**Wave 2 — correctness.**
C2 `StartDate` → `GetCurrentCycleStart` · M3/M4 refund from the invoice, not today's prices · H3
resolver split · M5 PayPal-initiated cancels · H5 promo release · H6 setup-fee waiver · H7 SendGrid
classification + a resume path · H8 suppression events must not be swallowed · H12 convert's pending
row · H13/H14 retry bounds · H4 SSRF IP pinning + stop echoing `RootLastError` · M2 gate the sibling ·
M8 agent-portal revalidation · H15/M13 make the promises match the code.

**Wave 3 — truth and prevention.**
Apply every documentation correction above. Add the regression tests these findings prove are
missing — starting with a **controller-level** blob test (the one that would have caught C1 on day
one), an annual-cancel-after-renewal fixture, a cancelled-but-paid-through Billing-page test, and a
resolver-agreement test with a divergent `AgentUser.PackageId`.

**Standing rule reaffirmed:** a fix is not done when the library is right — it is done when the
production caller is right, in **both** apps, with a test that exercises the path a customer takes.
