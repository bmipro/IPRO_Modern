# Handoff — 2026-08-20 (supersedes the 2026-08-18 version)

Companion to `DOCS/TODO.md` (durable backlog), `DOCS/AUDIT_RECONCILIATION_2026-08-17.md`
(per-finding truth), and `DOCS/22_PREPAID_VALUE.md` (the billing redesign). Where they disagree
with this snapshot, they win.

---

## Where things stand

> **CORRECTION (2026-08-20, post-audit): the table below is wrong.** A six-auditor pass found
> 2 CRITICAL and 15 HIGH defects, several introduced by that same day's work. "High (actionable): 0"
> was false. See `DOCS/AUDIT_2026-08-20_POST_SWEEP.md` — and do NOT run QA day-4 (cancel + delete)
> until its wave 1 has shipped.


**Production is at `783d98e`**, both apps verified serving it, all surfaces healthy.
Suite: **209/209** (63 tests written today). Five deploys today, every one gated on a green
full suite first.

**The defect register is effectively cleared above low severity:**

| Severity | Morning | Now |
|---|---|---|
| Critical | 1 | **0** |
| High (actionable) | 3 | **0** |
| Medium | 27 | **0** |
| Low (open) | 17 | **14** |
| Owner decisions | 4 | 7 |

Live register (rev 6, everything visible, no filters):
https://claude.ai/code/artifact/a7327af0-04c2-47c0-897c-a42c62e7c2b7

## What shipped today, in order

1. **Seven-medium batch** (`fix/audit-medium-seven`): M-9 bulk entitlements, HtmlSanitizer
   9.2.995/AngleSharp 1.7.1, sanitizer hardening, SSRF guard on the domain checker, telemetry
   path-token scrub, admin cookie revalidation (demotion verified live), Rebuild Resources
   SuperAdmin-only — plus the AccessDenied view that gate exposed as missing.
2. **Prepaid-value stages A–C** (`feat/prepaid-value-honesty`, DOCS/22): cancellation stops
   robbing agents. Cancelled-but-paid-through, the annual discount clawback with the month-10
   crossover, the SuperAdmin → Refunds manual queue (net/HST/gross precomputed, 180-day window
   countdown), honest confirm dialog, ToS clause 3 rewritten. The owner's month-5 example
   ($300 + $39 = $339) is a permanent test.
3. **Stage D** (`feat/downgrade-offer-both`): annual downgraders choose defer or CONVERT
   ("switch now + ~N months free", supersede + deferred start_time, credit at the rate actually
   paid). Term-switch pending guard + stored-term completion fixed alongside. During the build,
   re-verification showed `1909426` (Aug 16) had ALREADY fixed the proration critical, the false
   banner, the HST skip and the $0-invoice settle — the register had overstated the open money
   cluster.
4. **The 22-medium sweep** (`fix/medium-sweep`): 16 fixed (signup verify-code honesty, 404
   caching, silent parent fallback, empty-Host site mixup, paid-invoice resend, JOBS-5/6/7/8/10
   email resilience, billing-job isolation, promo cap claim-at-checkout with release, starter
   libraries SuperAdmin-only, Azure domain unbind on delete, revenue chart clock, storage
   quota + honest figure, transactional lockout-first erasure), 2 verified already fixed
   (JOBS-9, #410 — stale entries), #394 dispositioned, SETUPFEE-TXN voided by the owner's own
   invoice check.
5. **The WEB-H-1 buyer pass** completed by the owner (BobyMot #35, QA Silver Daily): money
   reconciled to the cent across two PayPal charges; QA daily clock restarted (day 1 =
   2026-08-20).

## Recommendation on the table (owner reading now)

**Audit before fixing the 14 lows.** Today's ~2,700 changed lines are the risk, not the
inventoried lows. Suggested shape: 5 parallel auditors targeted at today's changed surface
(prepaid-value/billing, consent/jobs resilience, erasure transaction) + one dedicated
doc-vs-code truth pass — stale "fixed" AND stale "open" claims both caused near-duplicate work
this week. Hand auditors the register as the exclusion list. Optional pre-clean: the trivial
third of the lows (two misleading admin texts, webhook timestamp, two URL-builder copies,
newsletter test-send host) so they don't pollute the report.

## Next steps

1. Owner decides: trivial-lows blitz first, or straight to audit briefs.
2. **QA daily protocol**: day 3 (verify overnight charges arrived, upgrade BobyMot to Platinum
   Daily FROM bobymot.247advisers.com — that also closes WEB-H-1's upgrade leg), day 4 (cancel +
   delete; the new cancel flow and refund queue get their first production exercise).
3. **Staging decision** returns Mon 2026-08-25 09:00 (scheduled reminder). Question is data
   (PIPEDA), not cost.
4. First real annual cancellation/downgrade will exercise the DOCS/22 machinery in production;
   the SuperAdmin → Refunds queue is where refunds appear.

## Today's lessons (recorded in 09_TROUBLESHOOTING)

- Raw `DbCommand`s do not join an ambient EF transaction; three sites needed
  `command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction()`. The full-suite
  gate caught the third — focused tests missed it because their seeds didn't cross the boundary.
- Stale status lists caused three near-duplicate-work incidents this week (five stale audit
  headings, the "fixed-on-Aug-16" money cluster, JOBS-9/#410). Re-verify against code before
  building; update the entry in the same commit as the fix.

## Environment

- Local dev via `ops\Start-LocalEnv.ps1`; both preview servers stopped.
- Local test data: agent 11 (drip tester) + supporttest admin — local DB only.
- Production agents: BahmanMotamed #12 (owner) + BobyMot #35 (QA daily, real sandbox sub
  `I-VG1A3CKSK6DX`, charges daily).
