# Azure Infrastructure — decisions, state, and the three open cost levers

Last updated **2026-08-18**. This is the durable record of the platform-level decisions made after
the 2026-08-07 independent review, so they do not get buried in session logs. The apps:
`ipro-prod-web` (RG `ipro-production`) and `ipro-prod-admin` (RG `ipro-prod-admin_group`), both on
App Service Plan `ipro-prod-plan`, **Basic B2 Linux, one instance** (2 cores / 3.5 GB,
**$25.55 USD/mo list**, Canada East).

> ### Price correction, 2026-08-18 — read this before using any older figure
> The price sheet written on 2026-08-07 used **Windows** App Service rates for a plan that is
> **Linux** (`kind: linux`, confirmed via `az appservice plan list`). Both products exist in the
> retail API under nearly identical names — `Azure App Service Basic Plan` (Windows) and
> `Azure App Service Basic Plan - Linux` — and the wrong one was read.
>
> | SKU | Windows (used by mistake) | Linux (what we actually run) |
> |---|---|---|
> | B2 | $120.45/mo | **$25.55/mo** |
> | S2 | $160.60/mo | **$128.48/mo** |
>
> The error moved **both** levers, in opposite directions: a second worker is **4.7× cheaper**
> than recorded ($25.55, not $120) and the staging slot is **2.5× more expensive**
> ($103, not $41). Every figure below is Linux, Canada East, USD list, re-pulled from
> `prices.azure.com` on 2026-08-18. The Azure invoice is likely billed in CAD (≈1.35×) and may
> carry credits — these are list prices, not the statement. The `az consumption` API returns cost
> as `null` for this login, so actual spend could not be read.

## Done and verified (2026-08-07)

### 64-bit workers — `use32BitWorkerProcess: false`, both apps
The .NET 8 apps had been running in 32-bit worker processes, capping usable address space at
roughly 2–4 GB regardless of machine RAM — a plausible contributor to the unexplained 2026-08-07
process death (16 Hangfire jobs + EF in one squeezed process). Verified three ways: config
read-back, Kudu shows the `dotnet` worker as a 64-bit process, full smoke green.
**Rollback:** `az webapp config set ... --use-32bit-worker-process true`.

### Run-from-package — `WEBSITE_RUN_FROM_PACKAGE=1`, both apps
Deploys previously extracted loose files under the running process; on Windows the old assemblies
could keep serving until an explicit restart. That "deploy succeeded but old code serving" pattern
was observed twice (2026-08-07 outage day, and again on the /health deploy). With run-from-package
the site mounts the deployed zip read-only and every deploy atomically recycles onto the new
package — the site runs either the old package or the new one, never a mix.

Pre-flight established that the app writes nothing under its own folder (all uploads are Azure
Blob Storage) and Data Protection keys live in `%HOME%` outside the package, so sessions survive.
Proven with a live pipeline test: GitHub Actions deploy → worker PID changed (1863 → 1867) → all
sites green — **zero manual restarts**. The old restart-after-deploy procedure is retired. The
GitHub workflow needed no changes.
**Rollback:** delete the `WEBSITE_RUN_FROM_PACKAGE` app setting and redeploy (the pre-cutover
loose files are still in wwwroot).

### Also standing (from the review response)
- Per-app GitHub Actions **concurrency groups** — deploys queue, never overlap (`42d3762`).
- **`/health`** liveness endpoints (no dependency checks, deliberately) wired to App Service
  health checks on both apps (`ecdc1d6`).

## What production actually costs today (measured 2026-08-18)

| Resource | Spec | USD/mo |
|---|---|---|
| App Service plan `ipro-prod-plan` | Basic B2 **Linux**, 1 instance — hosts **both** apps | 25.55 |
| MySQL `ipro-mysql-prod` compute | Flexible Server, Burstable **B1ms**, v8.0.21, no HA, no geo-backup | 13.51 |
| MySQL storage | 20 GB provisioned, autogrow on, 360 IOPS | ~2.53 |
| MySQL backup | 7-day retention, LRS — free up to 100% of provisioned storage | 0.00 |
| Blob `iprostorageprod` | Standard LRS Hot — **17 MB actually stored** | ~0.00 |
| App Insights + Log Analytics | `PerGB2018`, 30-day retention, **0.31 GB/mo ingested** @ $2.76/GB | 0.86 |
| Bandwidth out | first **100 GB/mo free**, then $0.08/GB — nowhere near the free tier | 0.00 |
| | | **≈ 42.45** |

Two facts worth internalising: **storage and bandwidth are not costs for IPRO and will not be for a
long time** (17 MB stored, a third of a gigabyte of logs a month). Compute is the entire bill. Any
future cost conversation is a compute conversation.

## The three open cost levers (deliberately NOT bought yet)

They solve different problems and are frequently confused.

### 1. Second worker = availability (+$25.55/mo)
Scale out to 2 instances of the current B2 plan. One process dying becomes "one of two died,
agents noticed nothing", and the health check pulls a sick instance from rotation immediately
instead of the ~1-hour single-instance restart. **Does NOT require a tier change** — Basic allows
up to 3 instances; it is purely a second B2 bill.

Engineering notes when bought: Hangfire is multi-server-safe by design (no double-fired recurring
jobs); ARR session affinity is on, so session-stored state (e.g. the registration verify code)
stays pinned to one instance.

**This lever is far cheaper than the 2026-08-07 sheet claimed** — $25.55, not $120. At that price
the "wait until paying agents arrive" trigger deserves rethinking: it is roughly the cost of one
Silver subscription per month to convert a single-instance outage into a non-event.

### 2. Staging **slot** = deploy safety (+$102.93/mo via S2) — now the *worst* of the three
A slot is a second copy of the app (own private URL) on the same plan: deploy there, verify, then
**swap** atomically into production; rollback is one swap back. **Requires Standard tier or
higher — Basic has no slots.** The only upgrade that does not *reduce* production compute is
B2 → **S2** (both 2 cores / 3.5 GB): $25.55 → $128.48, a **+$102.93** delta. S1 looks cheaper
(+$38.69) but halves the CPU that two live apps share, so it is a downgrade in disguise.

Two reasons this lever has got worse since it was written:

1. **Run-from-package already delivers atomic deploys** (see above), so the slot's remaining value
   is only pre-swap verification against real Azure config, warm handovers, and instant rollback.
2. **A slot shares the plan's app settings and, critically, the production database.** The
   2026-08-07 note called that "acceptable, but know it". It is now known to be *not* acceptable —
   see "Why staging cannot share the production database" below. A slot cannot be pointed at a
   different database without diverging its config so far that it stops being a slot in any useful
   sense.

### 3. Separate staging **environment** = the thing actually wanted (+$29.20/mo, or ≈$16 idle)
Not a slot — a genuinely separate small deployment with **its own database**, fed from a branch.

| Resource | Spec | USD/mo |
|---|---|---|
| App Service plan | Basic **B1** Linux, 1 instance — hosts staging web + admin, same trick as prod | 13.14 |
| MySQL | Flexible Server Burstable B1ms + 20 GB (both are the floor — no smaller SKU exists) | 16.04 |
| Blob storage | own container, negligible volume | ~0.02 |
| App Insights | **do not enable it on staging** | 0.00 |
| Bandwidth | internal testing only, under the 100 GB free tier | 0.00 |
| | | **≈ 29.20** |

**The idle lever:** Azure MySQL Flexible Server can be **stopped** (up to 7 days at a time,
auto-restarts after); while stopped only storage bills. Started only when testing:
**≈ $16–18/mo**.

So the comparison that matters:

| Option | USD/mo delta | Own database? | Gives a preview URL? |
|---|---|---|---|
| Second worker | +25.55 | n/a | no — it is an availability lever |
| Staging slot (B2 → S2) | +102.93 | **no** | yes |
| Separate staging env | **+29.20** (≈16 idle) | **yes** | yes |

The separate environment is roughly **a third the price of the slot and strictly safer**. Slots are
the right tool when staging and production are meant to share everything except the code; that is
not this system.

## Why staging cannot share the production database

Two independent reasons, either one sufficient:

1. **It writes real data.** Signing up a test agent creates a real agent; a test send mails real
   clients; billing touches real PayPal. That is the obvious one.
2. **PayPal plan IDs are per-mode data stored in a mode-agnostic column.** `BillingRule.PayPalMonthlyPlanId`
   and `PayPalAnnualPlanId` (edited in SuperAdmin → Packages) hold a *sandbox* plan ID or a *live*
   plan ID — they are different strings for the same package, and there is only one column.
   A staging instance running `IsSandbox=true` against a shared database would read the **live**
   plan ID and post it to the **sandbox** API. Every checkout fails. This is not configurable
   around; it requires a second database.

## PayPal: sandbox and live can run side by side — but not in one instance

Established 2026-08-18. Worth recording because it has always been done as one-or-the-other.

- **At PayPal's end there is no conflict.** One business account, one developer dashboard, two
  separate worlds that both exist permanently: `api-m.sandbox.paypal.com` and `api-m.paypal.com`,
  each with its own Client ID/Secret, its own webhooks and webhook IDs, its own subscription plan
  IDs, and fake vs real money. Holding live credentials does not disable sandbox.
- **At IPRO's end, one instance is one mode.** `IPRO.Billing/PayPalSettings.cs` carries a single
  `ClientId` / `ClientSecret` / `WebhookId`, and `BaseUrl` is derived from `IsSandbox`. Switching
  means editing Azure app settings and restarting. `PayPalBillingService.cs` already fails loudly
  and correctly when credentials and mode disagree.
- **With a staging environment you get both at once**, which is the clean answer:

  | | `IsSandbox` | Credentials | WebhookId |
  |---|---|---|---|
  | production app | `false` | live | live webhook |
  | staging app | `true` | sandbox | sandbox webhook |

- Existing safety rail: `PayPalBillingService.cs` refuses to create the daily test plans unless
  `IsSandbox` is true, so the QA daily-billing harness can only ever touch sandbox.

## What a staging environment buys beyond "look before it's live"

The preview URL is the least of it. In rough order of value:

1. **It retires testing on production.** The QA daily-billing runs (TODO #367–369) currently
   execute against the live system with a real PayPal sandbox agent. This is what replaces that.
2. **A safe place for destructive paths.** Agent deletion and data erasure. The FK-cascade defect
   that destroyed paid invoice `IPRO-2026-000008` would have surfaced in staging rather than in
   revenue.
3. **It re-tests the empty-database boot.** Both apps were made to survive a blank DB; nothing
   currently re-checks that. A staging database that can be dropped and recreated does.
4. **Real email sends without mailing real clients** — including reading a SpamAssassin score on a
   genuine send, which is the open remainder of the unsubscribe work.
5. **Proves the backups restore.** There are 7-day MySQL backups that have never been restored.
6. **Custom-domain and SSL rehearsal** without touching a customer's live domain.

## Ongoing costs that are not the Azure bill

- **Anthropic API key.** The AI Daily Assistant draws on a shared prepaid, monitored budget
  (`AiBillingSettings` / `AiUsageDailyLogs`). Staging needs its own key or the feature disabled, or
  test runs spend real money.
- **SendGrid.** Needs its own API key and sender, or staging sends damage production sending
  reputation.
- **PayPal.** Sandbox credentials only. Never live keys on staging.
- **A second set of app settings to keep in sync.** This is the real recurring cost and the main
  reason to say no.
- **Setup effort.** Provisioning, config, a seeded and usable test database, and a second deploy
  workflow: a day or two of work, not an afternoon.

## Trigger points (the recommendation on record)

Revised 2026-08-18 in light of the corrected prices:

- **Second worker** — reconsider now rather than "when paying agents arrive". At $25.55 the
  original reasoning ("too expensive to buy speculatively") was based on a figure 4.7× too high.
- **Staging slot** — do not buy. Superseded by the separate environment on both price and safety.
- **Separate staging environment** — buy it when the driver is *testing billing and email safely*,
  not when it is *looking at a page before it ships*. For visual review the local environment
  (`DOCS/16_LOCAL_DEV.md`, `localhost:5100`) already does the job for free. The moment it earns its
  keep is the next time a QA billing cycle would otherwise run against production.

**Scheduled revisit: week of 2026-08-24**, with Bahman. Nothing to be purchased before then.

## Still open from the same revisit
- **Auto-heal rules**: recycle the worker on a burst of 5xx — much faster than the health check's
  single-instance response. Config-only; queued.
