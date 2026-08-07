# Azure Infrastructure — decisions, state, and the two open cost levers

Last updated 2026-08-07. This is the durable record of the platform-level decisions made after the
2026-08-07 independent review, so they do not get buried in session logs. The apps: `ipro-prod-web`
(RG `ipro-production`) and `ipro-prod-admin` (RG `ipro-prod-admin_group`), both on App Service Plan
`ipro-prod-plan`, **Basic B2, one instance** (2 cores / 3.5 GB, ~$120 USD/mo list, Canada East).

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

## The two open cost levers (deliberately NOT bought yet)

They solve different problems and are frequently confused:

### 1. Second worker = availability (~+$120/mo)
Scale out to 2 instances of the current B2 plan. One process dying becomes "one of two died,
agents noticed nothing", and the health check pulls a sick instance from rotation immediately
instead of the ~1-hour single-instance restart. **Does NOT require a tier change** — Basic allows
up to 3 instances; it is purely a second B2 bill.
Engineering notes when bought: Hangfire is multi-server-safe by design (no double-fired recurring
jobs); ARR session affinity is on, so session-stored state (e.g. the registration verify code)
stays pinned to one instance.

### 2. Staging slot = deploy safety (~+$41/mo via S2)
A slot is a second copy of the app (own private URL) on the same plan: deploy there, verify, then
**swap** atomically into production; rollback is one swap back. **Requires Standard tier or
higher — Basic has no slots.** The natural move is B2 → **S2** ($161/mo list): identical hardware
(2 cores / 3.5 GB), the +$41 buys the Standard feature set. Note run-from-package already
delivered atomic deploys, so the slot's remaining value is pre-swap verification against real
Azure config, warm handovers, and instant rollback. Both slots would share the one MySQL database,
so the staging boot runs the same schema repair — acceptable, but know it.

### Price sheet (retail list, Canada East, 2026-08-07, via prices.azure.com)
| Option | Runs | ~USD/mo | Delta |
|---|---|---|---|
| Today | B2 × 1 | $120 | — |
| Second worker | B2 × 2 | $241 | +$120 |
| Slot | S2 × 1 + slot | $161 | +$41 |
| Both | S2 × 2 | $322 | +$202 |

### Trigger points (the recommendation on record)
Buy **neither while no real agents are live** — the local environment, atomic deploys and health
monitoring cover today's risk. When paying agents arrive: **second worker first** (availability is
what customers feel; it converts a 2026-08-07-style outage into a non-event), the **slot** when
deploy frequency makes even brief deploy blips matter.

### Still open from the same revisit
- **Auto-heal rules**: recycle the worker on a burst of 5xx — much faster than the health check's
  single-instance response. Config-only; queued.
