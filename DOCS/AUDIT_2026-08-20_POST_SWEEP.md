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
