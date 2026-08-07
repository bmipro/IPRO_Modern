# Security & Correctness Audit — 2026-08-05

Six specialist reviewers ran in parallel over the whole solution: auth/tenancy, injection/XSS,
billing/PayPal, jobs/concurrency, storage/erasure, and correctness of the last five days' changes.
Each was given the July audit so it would not re-report fixed findings.

**Raw total ≈ 94 findings; ~78 after removing cross-reviewer duplicates.**

## How to read this

**Only one finding below has been verified by me against production. Everything else is a
reviewer's claim and must be reproduced before it is fixed.** Reviewers in this session were
accurate on the item that was checkable, but they also stated things confidently that turned out to
be imprecise. Verification is step one of every fix, not an optional extra.

`✔ verified` — reproduced against real code or a live request.
`⚠ reported` — a reviewer's finding, plausible, not yet independently confirmed.
`×N` — how many independent reviewers found it. Two or more raises confidence considerably.

---

## FIXED SINCE

Running tally. Everything above the CRITICAL/HIGH lists below has been dealt with; anything still
listed there is still open.

| Finding | Status |
|---|---|
| Media proxy path traversal | **FIXED** `14fff55` |
| C-1 domain takeover | **FIXED** `5695a17` |
| C-2 upgrades kill recurring billing | **FIXED** `1a4cb02` |
| C-3 DidYouKnow mass-duplicate | **FIXED** — `ClaimedAtUtc` claim/complete split, 2026-08-05 |
| H-4 startup DDL race → SIGABRT | **FIXED** 2026-08-06 (below) |
| H-5 unguarded seeders → boot crash-loop | **FIXED** 2026-08-06 (below) |

**All four Criticals are closed.** C-3 was fixed on 2026-08-05 but left listed as open here until
2026-08-06 — the entry below it in the CRITICAL section is stale, not a live finding.

### ✔ H-4 + H-5 — startup races that take both apps down — **FIXED 2026-08-06**

Fixed together because they are the same failure with two triggers, and because this class has
already caused one real outage (2026-07-29, SIGABRT on both apps).

**H-4, the DDL half.** `EnsureTableColumnAsync` was check-then-ALTER with nothing catching the
collision. Both apps deploy from one push and run identical schema repair against one database, so
the process that ALTERs second gets MySQL **1060 duplicate column**, which escapes `Main` and aborts
the process. Now caught and treated as success — the desired end state is "column exists", and it
does. Only 1060 is swallowed, so a typo'd ALTER or a missing privilege still fails loudly.

Found while fixing it: `EnsureUniqueIndexAsync` caught **1062** (duplicate *data*) but not **1061**
(duplicate *index name*) — the same race, index-shaped, still able to crash startup. Now both.
`CREATE TABLE` was already safe; all 48 use `IF NOT EXISTS`.

**H-5, the DML half.** `PackageEntitlementSeeder`, `TaxRateSeeder` and `WebsiteTemplateSeeder` never
got the `SeedGuard` added in July. `PackageEntitlementSeeder` was the dangerous one: `BillingRules`
has no unique index on `PackageName`, so the race *duplicates* rows instead of throwing, and
`ToDictionaryAsync(p => p.PackageName)` then throws `ArgumentException` on the duplicate key. The bad
rows persist, so every subsequent start of both apps throws in the same place — **a one-time race
becomes a permanent boot crash-loop that no restart clears.**

Two defences, deliberately: `SeedGuard` so it cannot happen, and a duplicate-tolerant read (group by
name, take the lowest Id, log a warning) so an already-poisoned database can still boot. The first
alone would not rescue a database that had already raced.

No unguarded seeders remain.

---

### ✔ Media proxy path traversal via double URL-encoding — ×4 reviewers — **FIXED, commit 14fff55**

`MediaController` is `[AllowAnonymous]` and its container allowlist is, by its own comment, the
entire security boundary. The guard tested the **raw route value**. ASP.NET decodes the request path
exactly once, so `%252e%252e` arrived as the literal text `%2e%2e` — containing no `..` — and
`ParseBlobUrl`'s own decode turned it back into `..`, after which the Azure SDK resolved the dot
segment into a *different* container. Reads use the storage account key, so the private containers'
`PublicAccessType.None` was irrelevant.

**Confirmed exploitable in production**: a request under `agent-logos` returned a real blob out of
`agent-photos`. The `agent-documents` 404s were *not* the guard working — those blob names simply did
not exist. Only unguessable `{guid:N}_{filename}` names prevented private-document reads.

Fixed in three layers: decode to a fixed point before testing; test `..` as a path *segment* (so
`report..final.png` still works); re-parse the constructed URL and confirm it still resolves to the
authorised container; and reject traversal inside `ParseBlobUrl` itself so every caller is covered.
Verified live — the exploit returns 404/0 bytes, legitimate media still 200.

**The lesson worth keeping**: the original guard was reviewed, commented, and tested — against
single-encoded input only. A check on *input* is a guess about what the input will become; the
durable check is on the *resolved result*.

---

### ✔ C-1 (domain takeover) — **FIXED, commit 5695a17**

Both defects verified in code before changing anything: `AddDomain` tested only `AgentDomains.DomainName`
(never `AgentUsers.DomainName` or `AgentWebsites.CustomDomain`, and never the Root/Www variants that
host resolution also matches), while `FindWebsiteForHostAsync` queries `AgentDomains` **first** and only
falls back to `AgentUser.DomainName` afterwards. Since no agent has an `AgentDomains` row for their
provisioned site, claiming it was unopposed.

`DescribeDomainClaimAsync` now checks all three host variants against all three sources and refuses the
platform's own zone outright. Wired into **both** `AddDomain` and `Save` — `Save` carried the same
partial check and writes `CustomDomain` directly, so fixing only `AddDomain` would have left the
takeover reachable through the ordinary settings form. A unique index on `AgentDomains.DomainName` in
both apps' schema repair makes the database the final arbiter of the read-then-write race.

### ✔ C-2 (upgrades kill recurring billing) — **FIXED, commit 1a4cb02**

Verified rather than assumed — including one claim that was wrong. The reviewer said `NextBillingDate`
is "only ever written"; it *is* read, for downgrade scheduling and proration. The substantive claim held
anyway: `SubscriptionBillingJob` only applies due downgrades and sends notices, so **nothing in this
codebase charges anyone** — all recurring money arrives through PayPal's own engine. A `Billing` row with
an empty `PayPalSubscriptionId` therefore never bills again.

Both upgrade branches now go through `BeginPaidChangeAsync`, which starts a genuine subscription on the
new plan with the prorated difference as its setup fee. `ApplyUpgradeWithoutPaymentAsync` was deleted
rather than left unused — it is a working implementation of the defect, one call site from returning.

**Not yet exercised against PayPal.** The build is clean and `BillingController` already handles the
approval redirect at both call sites, but a sandbox upgrade should be run before any real agent upgrades.

---

## CRITICAL — verify first, then fix

### ⚠ C-1. Any agent can hijack another agent's public website and harvest their leads
`WebsiteController.cs:349` · `PublicWebsiteController.cs:655-666`

`AddDomain` rejects a domain only if it already exists in `AgentDomains`. It never checks
`AgentUsers.DomainName` (every agent's auto-provisioned `*.247advisers.com` site) or
`AgentWebsites.CustomDomain`, and there is no unique index on `AgentDomains.DomainName`. No agent has
an `AgentDomains` row for their provisioned domain. `FindWebsiteForHostAsync` resolves tenancy from
`AgentDomains` **first**.

Attacker adds `victim.247advisers.com` as their own domain; from then on the victim's site serves the
attacker's content and every lead, form and testimonial posted there is written with the attacker's
`AgentUserId`. No DNS control needed.

**If confirmed, this is the most serious finding in the audit** — it is cross-tenant takeover
reachable by any agent through normal UI, with no preconditions.

### ⚠ C-2. Every package upgrade permanently stops recurring billing
`PayPalBillingService.cs:179-191, 931, 1013-1059`

Upgrades charge a one-time prorated PayPal **order**, cancel the old subscription, and create the new
`Billing` row `Active` with an empty `PayPalSubscriptionId`. The subscription-creation branch is
gated on `changeType == Subscribe`. Nothing reads `NextBillingDate` to re-bill (reviewer reports it
is only ever written).

Result: an agent who upgrades pays once and is never charged again. Timed near cycle end,
`remainingFraction ≈ 0` → `amountDue = 0` → top tier for **$0, forever**.

**Revenue-affecting and retroactive** — if real, it has already happened to every completed upgrade.

### ✔ C-3. DidYouKnow dispatch can mass-duplicate emails to clients — **FIXED 2026-08-05** (ClaimedAtUtc claim/complete split)
`DidYouKnowEmailDispatchJob.cs:27-31, 70, 78`

Selects `SentAtUtc == null`, sends inside the loop, saves the markers only at the very end. The job is
`Cron.Minutely` and Hangfire does not skip a tick while the previous run is still executing, so a
multi-minute run has its rows re-selected and re-sent by the next run. A terminal-save failure plus
Hangfire's default 10 retries re-sends the whole batch each time.

---

## HIGH

| # | Finding | Where | Conf. |
|---|---|---|---|
| H-1 | **PayPal cancellation failure is swallowed** — `CancelPayPalSubscriptionAsync` catches everything and returns success, so agent-delete's "abort if cancellation fails" guard can never trigger. Deleted agents can keep being charged. Also makes agent-facing "Subscription cancelled" a lie. | `PayPalBillingService.cs:2028-2062` · `AgentsController.cs:248-272` | ⚠ ×2 |
| H-2 | **Support-role admin → full agent account takeover.** `Agents/Edit` is class-level `AdminAccess`, not SuperAdmin, and writes `agent.Email`. Change the email, then use public password reset. Defeats the M-2 SuperAdmin gate entirely. | `AgentsController.cs:14, 188-210, 446` | ⚠ |
| H-3 | **"Resume payment" converts a subscription to a one-time charge** — pays one month, holds the package forever, real subscription never activates. | `PayPalBillingService.cs:361-415` | ⚠ |
| ~~H-4~~ | **FIXED 2026-08-06.** ~~Startup DDL race can SIGABRT both apps.~~ `EnsureTableColumnAsync` is check-then-ALTER with no lock; both apps deploy from one push, loser gets MySQL 1060 → unhandled → crash. The July advisory-lock fix covered DML seeding only. | `Web/Program.cs:1544-1578` + Admin copy | ⚠ |
| ~~H-5~~ | **FIXED 2026-08-06.** ~~Three structural seeders never got SeedGuard~~, and `PackageEntitlementSeeder` poisons the DB into a permanent boot crash-loop (`ToDictionaryAsync` on duplicated rows) on both apps. | `PackageEntitlementSeeder.cs:31-63`, `TaxRateSeeder.cs`, `WebsiteTemplateSeeder.cs` | ⚠ |
| H-6 | **All four dispatchers claim work read-then-write**, not atomic UPDATE. Overlapping runs, the SuperAdmin "Trigger now" button, and the controller send-now path can each double-send an entire newsletter audience. | `NewsLetterDispatcher.cs:50-64` + ECard/ELetter/Poll | ⚠ |
| H-7 | **A crash or deploy mid-dispatch strands sends in `Sending` forever** — silent partial delivery, no recovery path, and manual repair re-emails everyone. | same four dispatchers | ⚠ |
| H-8 | **Email-then-mark with one terminal save + 10 retries** → up to 2,000 duplicate overdue-invoice emails to end clients from one transient DB error. | `OverdueInvoiceReminderJob.cs:62-72`, `TrialReminderJob.cs:69-81` | ⚠ |
| H-9 | **Recurring invoices can deadlock platform-wide.** Document numbers are generated against committed rows while the batch stays unsaved → duplicates → the L-12 unique index fails the whole batch → every agent's recurring invoicing stops, permanently. | `RecurringClientInvoiceJob.cs:58-83` | ⚠ ×2 |
| H-10 | **Deleting a client leaks every private portal document** (rows + blobs), and the eraser can then never find them because it scopes through the deleted parent. | `ClientService.cs:35-43` · `AgentDataEraser.cs:35,135` | ⚠ |
| H-11 | **Gallery blobs orphaned** when a Gallery block or its page is deleted; the eraser reads live blocks so it cannot see them either. Quota is freed on paper while files persist. | `WebsitePagesController.cs:681-706, 966-977` | ⚠ ×2 |
| H-12 | **Erasing one agent can destroy artwork other agents still use** — `SharedAssetUrlsAsync` doesn't check other agents' `Articles.ImageUrl`, only the starter libraries. | `AgentDataEraser.cs:141-150` | ⚠ |
| H-13 | **Form submission answers survive erasure** — `FormsController.Delete` doesn't remove `WebsiteFormSubmissionAnswers`, and the eraser scopes them through the deleted form. Visitor PII persists. | `FormsController.cs:237-258` | ⚠ |
| H-14 | **Article image replace/delete breaks already-sent newsletters** — the shared-reference check omits `NewsLetterArticles.ImageUrl`. Same "breaks mail already in inboxes" class the cert watchdog exists to prevent. | `ArticlesController.cs:177-186` | ⚠ ×2 |

---

## MODERATE (selected — full detail in the reviewer transcripts)

- **Invalid page placement silently promotes to top level.** `ResolveParentAsync` returns `null` for
  *every* rejection, and `null` means "top level" — a rejected move relocates the page and its
  children to the main menu, with a success message. (`WebsitePagesController.cs:1009-1034`)
- **`[ResponseCache]` stamps a 1-year cache on 404s** from the media endpoint — one transient miss can
  break an image for a recipient for a year. (`MediaController.cs:25`)
- **Storage quota covers 2 of 5 agent-writable containers.** No accounting on `website-media`,
  `article-media`, agent photo, logo, or `portal-documents` — the last being **client-driven**, so an
  agent's storage bill is controlled by their clients. Also fails **open** when `LimitValue` is null.
- **The Documents page shows documents-only usage** while uploads enforce documents+gallery: "12 MB of
  100 MB used", then rejected at 98 MB. (`DocumentsController.cs:43`)
- **Webhook replay is not idempotent** for `PAYMENT.SALE.COMPLETED` — a redelivery creates a second
  paid invoice and advances the billing date. (`PayPalBillingService.cs:763-812`)
- **A stale abandoned PayPal approval link can hijack the current subscription** weeks later.
- **`SubscriptionBillingJob` has no per-item isolation** — one poisoned agent starves everyone after
  it, hourly. The exact starvation pattern already fixed in the Scheduler jobs.
- **`RebuildResources` destroys agent-authored pages** under Resources and is only `AdminAccess`.
- **Re-sending a paid invoice reverts it to unpaid** and starts dunning a client who already paid.
- **SSRF via custom domain**: `DomainCheckService` fetches any host an agent types, every 5 minutes,
  with the result rendered back as a reachability oracle. No private/link-local block.
- **Starter-content libraries are Support-writable** despite headers saying SuperAdmin; a starter
  `ImageUrl` propagates to every future agent's public site.
- **Public custom-form submission has no rate limit**, unlike its two sibling endpoints.
- **`SubmitCustomForm`/`SubmitLead` etc. don't guard an empty `Host`**, unlike `RenderPageAsync`.
- **Sanitizer allows `<form>`/`<input>` and `style`** — "sanitized" content can carry a credential
  harvesting form or a full-viewport overlay. Matters most for Admin-authored starter content.
- **Erasure is non-atomic** and the agent isn't locked out first, so a mid-erase failure leaves a
  working login whose files are gone.

---

## Notable clean results

Worth recording, because these were checked hard and held:

- **Agent-portal IDOR is clean** apart from C-1 — every entity-by-id load across ~25 controllers
  carries a tenant predicate, including the block editor's cross-object reference validation.
- **Client portal isolation is clean** — the M-1 fix pattern held; no new code reintroduced it.
- **SQL injection: none.** All raw SQL is constant DDL or parameterised.
- **PayPal and SendGrid webhook signature verification both fail closed.**
- **Host-header trust (M-NEW-3) is fully generalised** with no regressions.
- **Uploads**: extension + declared content-type + magic-byte agreement on all 13 entry points; no
  SVG/HTML can currently reach a public container.
- **CSRF** on every state-changing POST except two webhooks and one anonymous probe.
- **Three-tier nav depth arithmetic is off-by-one-free**, and the hand-maintained prospect-preview
  mirror is genuinely in sync with the provisioning helper.
- **Certificate job math is correct** (local-vs-UTC handled on both the C# and PowerShell sides).

---

## Suggested order

1. ~~C-1 (domain hijack)~~ — **done, 5695a17.**
2. ~~C-2 (upgrade billing)~~ — **done, 1a4cb02.** Still needs a PayPal sandbox run.
3. **C-3 (DYK duplicate email)** — verify, then fix as part of the send-pipeline theme below.
4. **H-1 (swallowed cancellation)** — turns two "safe" guarantees into fiction.
5. **H-2 (Support→SuperAdmin gap)** — one attribute.
6. **H-4/H-5 (deploy races)** — cheap, and the failure mode is a dual-site outage.
7. **C-3/H-6/H-7/H-8 (send-pipeline duplication)** — one coherent piece of work, not five.
8. Everything else, by area.

**Two systemic themes** worth fixing as themes rather than as individual bugs:

- **Every send pipeline claims work read-then-write and records completion in one terminal save.** Any
  overlap or any save failure converts directly into duplicate or lost email. That is one design
  change across four dispatchers and three jobs.
- **The July advisory-lock fix was applied to the six newer seeders but not to the DDL helpers or the
  three older structural seeders**, which still carry the identical race that caused the 2026-07-29
  dual-SIGABRT.
