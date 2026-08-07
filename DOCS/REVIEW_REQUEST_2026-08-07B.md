# Independent Review Request #2 — 2026-08-07 (post-remediation)

Written for a reviewer with **no context from the sessions that produced these changes**. This is
the follow-up to `REVIEW_REQUEST_2026-08-07.md`: that review's findings were all accepted, and this
request asks you to audit the **fixes and everything else that changed since**. Please disagree
freely — the previous review was valuable precisely because it did.

## Repository

```
Local : C:\Users\admin\Documents\Codex\2026-06-30\ca\work\iPro_Project\iPro_Project\IPRO_Modern
Remote: https://github.com/bmipro/IPRO_Modern.git   (public)
Branch: main
```

**Scope: everything after `5625d95`** (the commit the first review examined). The prior findings
are tracked as R-numbers with statuses at the bottom of `DOCS/SECURITY_AUDIT_2026-08-05.md`;
the day's narrative is `DOCS/SESSION_LOG_2026-08-07B.md`.

## The commits under review

| Commit | Change |
|---|---|
| `42d3762` | GitHub Actions concurrency groups (deploys serialize per app) |
| `72fe5f1` | Local dev environment docs + start script (`DOCS/16_LOCAL_DEV.md`) |
| `ecdc1d6` | `/health` liveness endpoints, both apps + `IsNeverShadowedPrefix` addition |
| `82a972a` | R-H7 portal route alias for `FollowUpQueue`; R-L4 unread-badge predicate flip |
| `4a3e365` | Billing block: R-H1 (3 call sites), R-H2 (status-verified 422), R-H10 (both caps), R-L1 doc; **plus the access-gate /portal prefix normalization** (R-L3) |
| `f13218d` | R-H3 schedule detach, R-H5 restore-before-validate, R-H6 cross-table domain check, R-H8 loud 1062, first-boot seeder ordering (both apps) |
| `48e6a3e`, later docs | Session log, R-tracking table, infrastructure doc |

**Azure config changes (not in the repo):** `healthCheckPath=/health`,
`use32BitWorkerProcess=false`, `WEBSITE_RUN_FROM_PACKAGE=1` — on both apps. Rationale and
verification in `DOCS/18_AZURE_INFRASTRUCTURE.md`.

## How these were verified (so you can attack the method too)

Unlike the changes the first review examined, every fix here was exercised at runtime in a local
environment (`DOCS/16_LOCAL_DEV.md`) before deploy: the 404 was reproduced then re-verified fixed
in an authenticated session; the billing paths ran against the real PayPal sandbox (including a
real cancel failure leaving the row Active); R-H3 was proven with a forced save failure via SQL
trigger; R-H5 with a real Support-role admin; run-from-package with a live pipeline deploy that
recycled the worker (PID change) without manual restart.

## Questions for the reviewer

1. **The gate normalization (`4a3e365`, `src/IPRO.Web/Program.cs` ~line 234).** The access-gate
   middleware now strips a leading `/portal` before its exemption checks. Is there ANY input where
   this loosens the gate — path tricks, casing, encoded segments, `/portalX`, double prefixes —
   that lets a locked-out agent reach a non-Billing page? This is the security-sensitive change of
   the batch.
2. **Route-order URL generation.** Since `7052444`, generated URLs prefer the `/portal` route
   (that is what silently broke the gate's Subscribe POST). Please sweep for OTHER consumers that
   assume unprefixed paths — middleware, path comparisons, redirects, email links, webhooks
   (`PayPalReturn`), rate-limit endpoint rules in `appsettings.json` (they match literal paths like
   `POST:/Account/Login` — do portal-prefixed POSTs bypass the limiter?).
3. **R-H1 completeness.** Are there any remaining paths that mark a Billing row `Cancelled` (or
   otherwise change local billing state) without a confirmed PayPal stop?
4. **R-H2 state list.** 422 → status lookup accepts CANCELLED / EXPIRED / APPROVAL_PENDING /
   APPROVED as non-billing. Is that list right? Is SUSPENDED correctly treated as a failure?
5. **R-H3.** Is detaching the schedule (plus Added entries) sufficient, or can other Modified
   state from a failed iteration still leak into the next save? Same pattern elsewhere in the
   Scheduler project?
6. **R-H5/R-H6 restructure** (`src/IPRO.Admin/Controllers/AgentsController.cs`). Restore now runs
   before validation and empty-posted values are treated as "no attempt". Can a Support admin
   still mutate any protected field (including via direct POST)? Any bypass of the new
   cross-table domain check (case, whitespace, trailing dot, punycode)?
7. **R-H10 pattern.** On a cap race the loser is recorded past the cap with an Error log (the
   discount was already granted). Reasonable, or should the earlier grant itself be restructured?
8. **Run-from-package / 64-bit.** wwwroot is now read-only and the process is 64-bit. We found no
   local file writes; please double-check (including anything writing under ContentRootPath at
   runtime, temp-file assumptions, and the e-card/letter composers).
9. **The local environment itself.** `DOCS/16_LOCAL_DEV.md` documents that EF migrations after
   2026-07-10 are dead code (no `[DbContext]` attribute) and schema ships via the raw-SQL Ensure
   functions. Any way this dual system bites production?
10. **Priorities.** From your reading, what is now the highest-risk open area? (Our belief: the
    mass-duplicate-email block from the 2026-08-05 audit — its H-6/H-7/H-8 — followed by the
    erasure findings.)

## How to reproduce locally

`DOCS/16_LOCAL_DEV.md` — portable MySQL + Azurite + both apps; the environment stands up from
scratch in under an hour and every verification above is repeatable in it.
