# Session Log — 2026-08-07 (second session: the review response)

The independent read-only review (`DOCS/REVIEW_REQUEST_2026-08-07.md` was the brief; the report
came back the same day) found 10 High and 6 Low issues. Every one was verified against the code
before being accepted — **all 16 confirmed, none knocked down**. Three were new breakage from this
week's own fixes, three were incomplete fixes, the rest pre-existing or deliberate trade-offs with
confirmed blind spots.

The response was six steps, agreed in advance, each shipped and verified before the next started.
All six landed today. The reviewer's findings are tracked as **R-numbers** (R-H1..R-H10,
R-L1..R-L6) to avoid colliding with the 2026-08-05 audit's H-numbers — see the tracking table
appended to `SECURITY_AUDIT_2026-08-05.md`.

## The six steps

**1. Deploy serialization (`42d3762`).** Both workflows gained per-app concurrency groups
(`cancel-in-progress: false`). Verified with a real overlap: a dispatched run visibly queued behind
the in-flight push run — the exact push+dispatch collision from the outage day — then was cancelled
without starting a job.

**2. Local environment (`72fe5f1`, `DOCS/16_LOCAL_DEV.md`).** Portable MySQL 8.0.44 + Azurite +
both apps on localhost:5100/5200, config isolated from every real service. Found on day one: the
July-11+ EF migrations are dead code (no [DbContext] attribute — in production too; the raw-SQL
Ensure functions are the real schema channel), and the e-card seeders ran before their schema
function on a truly empty database.

**3. `/health` liveness endpoints (`ecdc1d6`) + Azure health checks.** Deliberately no DB
dependency: the health check's remedy is a restart, which fixes a wedged worker and nothing else.
Both App Services now have healthCheckPath set. During this step the **stale-code deploy pattern
recurred** (deploy "succeeded", old build kept serving until an explicit restart) — twice observed
now, so restart-after-deploy is standing procedure until run-from-package is revisited. Also found:
`WEBSITE_RUN_FROM_PACKAGE` is unset (explains the pattern) and the production worker runs
**32-bit** (`use32BitWorkerProcess: true`) — a plausible contributor to the unexplained process
death. Both queued for the config revisit, deliberately not changed mid-stream.

**4. Live-broken UI (`82a972a`).** R-H7: `/portal/Clients/FollowUps` 404'd for every agent
(attribute-routed action invisible to the conventional /portal route) — reproduced locally,
fixed with an explicit portal route alias, re-verified 404→200 in an authenticated session.
R-L4: the Messages unread badge counted the agent's own (always-read) messages, so it was
permanently zero — verified locally: a client message now shows "1 new" and clears on read.

**5. Billing block (`4a3e365`), exercised against the PayPal sandbox locally.**
- R-H1: all three replacement paths now check the cancel result. Post-payment paths leave the row
  Active and log at Error; the downgrade path leaves the change Pending for the hourly retry.
- R-H2: 422 → fetch the subscription's real status; only genuinely non-billing states pass.
- R-H10: both redemption caps check affected rows; a race loser is recorded past the cap loudly.
- R-L1: void-then-create ordering kept and documented (create-then-void risks two live approval
  links → double charge).
- **Found live during testing: R-L3 was a revenue blocker, not a papercut.** Generated form
  actions prefer the /portal route since `7052444`, and the access gate didn't recognize
  /portal-prefixed paths — so a billing-locked agent's Subscribe POST was silently bounced.
  A locked agent could not pay. The gate now strips the /portal prefix before its exemptions.
  Verified both states: the same POST that vanished now creates the PayPal order
  ($214.70 = $40 + $150 setup + 13% HST, correct).
- Exercised: trial-cap normal path, real-PayPal cancel failure leaves truth, first-subscribe to a
  real sandbox order, Resume Payment void-and-recreate. Not exercised (stated plainly): the 422
  status branch needs a sandbox BUYER approval — the user's planned sandbox run covers it.

**6. Jobs, startup, admin (`f13218d`).**
- R-H3: the recurring-invoice job detaches the failed schedule too. Verified with a forced save
  failure (SQL trigger): the poisoned schedule's advance was NOT committed by the next schedule's
  save, and it retried whole after the fault cleared.
- R-H5: protected-field restore moved BEFORE validation. Verified with a real Support-role admin
  created through the AdminUsers UI: profile edits now save; six identity fields stay locked.
- R-H6: DomainName is SuperAdmin-only and uniqueness now scans AgentWebsites.CustomDomain and the
  AgentDomains variants. Verified: cross-agent domain assignment rejected.
- R-H8: a 1062 during unique-index creation screams to stderr on every boot instead of silence;
  the app still boots (a data-quality crash-loop is the worse outage).
- Seeder ordering fixed in both apps; verified with a genuine first boot (14 designs, 4 letters,
  zero failures).
- R-H4 (invoice-number race) deliberately deferred: with per-schedule commits + the R-H3 fix, a
  collision costs one self-healing skipped run; the atomic counter belongs with a schema change.

## Still open

- R-H4 (atomic invoice numbering), R-H9's staging-slot half (needs Standard-tier plan — cost
  decision), R-L2/R-L5/R-L6 (cookie sniffing retires with the /portal cutover; fire-and-forget
  reset mail; Docker healthcheck works once anyone containerizes).
- Azure config revisit (user-approved, queued): 64-bit worker, run-from-package, auto-heal,
  optional second instance.
- 2026-08-05 audit backlog: the mass-duplicate-email High block (H-6/H-7/H-8) and erasure Highs
  (H-10..H-14), ~17 Moderates.
- Full /portal cutover (retire unprefixed routes + ~120 lines of compensating machinery).
- The user's sandbox buyer pass: Silver signup → pay → upgrade, now also the acceptance test for
  the unexercised billing branches.

## The pattern worth keeping

Every step's defects were caught **before deploy** by running the change, and two production bugs
(R-H7's 404, R-L3's payment block) were reproduced, fixed and re-verified in the local environment
the same hour. The one-step-one-deploy cadence plus restart-after-deploy made every production
change boring. Compare 2026-08-06's log, where the day's lesson was the opposite.
