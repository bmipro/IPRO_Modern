# Handoff — 2026-08-28 EOD (truth-pack round COMPLETE; the Blog is real; 24 days to Sept 21)

**2026-08-28 in one paragraph.** Homepage: Facebook/YouTube footer links and the pricing
comparison collapsed behind a toggle (48 rows was boring visitors away). Then the truth-pack
round: all 48 feature codes cross-referenced against the source, 11 questioned, **all 11
dispositioned the same day** — 7 withdrawn (rotating banner, newsboard, mail merge, printable
labels, framed link, managed SEO, designated support: startup repair DELETES the rows from
production, both halves pinned), 2 renamed to the truth (multilingual paste-from-any-editor;
CallToAction), 1 became REAL: **the Blog block** (lists the agent's published articles,
inline ?post= view, Platinum+Broker) plus **Draft-with-AI on articles** (AiDailyAssistant-gated,
fills the form, never saves — the adviser stays the author of record). The pricing table went
42 → 35 hidden rows and now sells only what exists. Suite 378 → 388, zero skipped. Five deploys,
every one verified on both hosts, rule 6 clean throughout. `DOCS/PRODUCT_TRUTH_2026-08-28.md` is
the marketing project's knowledge base and records every disposition.

---

# Previous handoff — 2026-08-27 EOD (Phases 1 AND 2 COMPLETE; kept for the record)

Written to survive a reboot. If the working directory was wiped, see **§7 Recovery** first.

Companion docs: `DOCS/AUDIT_2026-08-20_POST_SWEEP.md` (the defect register — the authority; every
wave's record and every open item lives there), `DOCS/TODO.md` (durable backlog),
`DOCS/INVARIANTS.md` (pre-flight rules — rule 6 now covers the deploy-eviction hazard),
`DOCS/22_PREPAID_VALUE.md` (billing policy, incl. the post-upgrade refund addendum decided today).

---

## 1. Production right now

**Both apps live on `ccb34db`** — the M1 merge, verified at `/health/version` on **BOTH** hosts
(never `/health`; and never trust one host or the workflow list — INVARIANTS rule 6). Today's
merges, in order: `f67a726` (H8+M8) → `f9361e5` (H3) → `21da3e6` (H7) → `7458287` (M13+M2) →
`df79ce5` (M9+M10+M20+print) → `ccb34db` (M1). Six code deploys plus docs follow-ups, every one
verified on both hosts, zero run cancellations.

Smoke after the last deploy: `app.iproadvisers.com/` 200 · `/Account/Login` 200 ·
`admin.iproadvisers.com/` 302 to login · `bahmanmotamed.247advisers.com/` 200.

Production agents:
- **BahmanMotamed #12** — owner, Gold monthly.
- **BobyMot #35** — QA daily harness, PayPal sandbox sub `I-VG1A3CKSK6DX`. Its full lifecycle
  (signup → upgrade → downgrade → cancel → row-exact delete) passed end-to-end 2026-08-20→26.

**PayPal in production is still SANDBOX** (`PayPal__IsSandbox=true`). No money in this system has
ever been real. That is Phase 4's job and it is the single biggest launch risk — see §2b.

Test suite: **375 passed / 0 failed / 0 skipped** — zero skipped for the first time (M1's
skip is closed; the suite grew 336 → 375 today). Every defect fix was verified BOTH ways: the
test observed RED on the pre-fix code first, then green on the fix.

---

## 2. What shipped today — Phases 1 AND 2, both COMPLETE

Phase 1: H8, M8, H3, H7 + the staging decision (below). Phase 2, all six customer-facing
MEDIUMs in three waves, each red/green-proven and verified live on both hosts:

| Wave | Branch | What it closed |
|---|---|---|
| A | `fix/medium-gating-pair` | **M13** — a lapsed agent's public site finally goes offline, as the cancel dialog has always promised (gated in the ONE funnel behind all seven public doors — pages, robots, sitemap, leads, forms, testimonials; paid-through honoured; verdict cached 2 min for the hot path). **M2** — `RebuildRequestMeeting` is SuperAdmin-only with a confirm that names the loss and a disabled (not hidden) button for support admins. |
| B | `fix/medium-storage-trio` | **M9** — article images consult the shared quota (net-change on replace; the "never resets" clause was VOID — no remove path exists). **M10** — all six storage displays use `DisplayLimitMb`, so a blank limit shows the enforced 1024 default instead of "of 0 MB"; a drift-pin greps the six files. **M20** — the reconciliation doc's false "ResumePayment deliberately not guarded" sentence stricken. **Plus the owner-requested Print / Save-PDF button** on Admin invoices (chrome stripped in print; every invoice old or new). |
| C | `fix/overlay-allowlist` | **M1** — the sanitizer's CSS is an ALLOW-list (~120 formatting properties, zero escape mechanisms) with surgical value guards for negative margins/text-indent and viewport units. Ten exploit flavours dead including the register's original reproduction; a pasted-newsletter corpus keeps every declaration. Closed the suite's one skipped test. |

### Phase 1 detail (morning)

| Item | Branch | What it closed |
|---|---|---|
| **H8** | `fix/consent-and-session` | The SendGrid webhook's per-event catch swallowed unsubscribes, group-unsubscribes and spam reports — a DB hiccup was caught, logged, answered 200, and SendGrid never retried. The one event class with legal weight, lost silently. A failed CONSENT event now withholds the 200 and returns 503 so SendGrid retries; stats events still fail soft. |
| **M8** | `fix/consent-and-session` | The agent portal had no `ValidatePrincipal` — a deactivated agent or removed team member kept a working cookie until expiry. `AgentCookieRevalidator` now mirrors the admin one. |
| **H3** | `fix/resolver-split` | `ResolveBillingRuleIdAsync` — the singular resolver behind `GetAccessAsync`, called by nearly every controller — never learned `PaidThroughAt` and fell through to the stale `AgentUser.PackageId`. A cancelled-but-paid-through agent was refused features they had paid for. |
| **H7** | `fix/drip-recovery` | The last open HIGH. SendGrid 401/403 and not-configured classified as PERMANENT, so one key rotation marked every due drip enrollment Failed on its first attempt, and no resume path existed — recovery meant re-enrolling, which re-sends every prior step. Now: account-level rejections are transient (bounded by the existing caps), and `ResumeFailedEnrollments` reactivates Failed rows from the exact step that never went out — no replays, consent still outranks recovery. 10 tests, reverse-run 5 red / 5 green. |
| **Staging decision** | — | RESOLVED: no standing staging before launch; a pre-launch snapshot + restore rehearsal instead (half a day, in/before Phase 4) — the restore has never been tested, the snapshot window closes at launch (owner confirmed production holds only test users), and the rehearsal doubles as the Phase 4 purge dry-run. Three independent reviews + a volume audit are recorded in `DOCS/TODO.md` under "STAGING: second-opinion round". Two live config hazards found there (unguarded job registration; committed `WebAppName: ipro-prod-web`) are logged for fixing regardless. |

**Two test-quality lessons were applied, not just noted.** H8's first tests only pinned the
classifier — the exact C1 shape (a guard the caller never consults). Caught before shipping; two
tests now drive the real action end-to-end with a genuine ECDSA-signed payload. M8 had the same
exposure, so it got a source-walk test proving the revalidator is actually WIRED in `Program.cs`,
not merely present. H3 got five tests enforcing the agreement the two resolvers' comments had only
ever asked for politely — including the reverse direction, so a fix erring permissive fails too.

Suite 331 → 336. Register HIGH count 3 → 1.

**Everything before today** — wave 1, the PayPal billing cluster, the four-auditor A-to-Z billing
audit and its follow-up waves 2–5, the erasure wave, the security+drip wave — is recorded in full
in `DOCS/AUDIT_2026-08-20_POST_SWEEP.md` and `DOCS/TODO.md`. That audit is **fully dispositioned:
0 HIGH / 0 MEDIUM / 0 LOW open.**

---

## 2b. The launch runway — the plan that now drives everything

**Target: Monday 2026-09-21.** Five phases, tracked on a private board the owner ticks off:
`https://claude.ai/code/artifact/fa86d4ad-b0f6-4b2a-9989-59c2c1455a41` (also summarised in
`DOCS/TODO.md` under "LAUNCH RUNWAY", so it survives without the link).

| Phase | Dates | State |
|---|---|---|
| 1 · Close every open HIGH | Aug 27 – Sep 2 | **3 of 5 done** — H8, M8, H3 shipped. Left: **H7** + the staging decision. |
| 2 · Customer-facing MEDIUMs | Sep 3 – 8 | M13, M2, M1, M9, M10, M20 |
| 3 · The front door tells the truth | Sep 9 – 12 | Truth-sweep /Preview + Register + help docs · real screenshots · Azure region. The homepage is ALREADY live and data-driven (#416); roadmap #412's other 14 pages are explicitly after launch. |
| 4 · Go-live mechanics | Sep 13 – 17 | **CRITICAL PATH.** PayPal sandbox → LIVE, real-money pass, ledger purge, SSL renewal, Province audit. |
| 5 · Polish, freeze, launch | Sep 18 – 21 | LOW sweep (the cut line), final auditor pass, freeze, soak, go. |

**The biggest launch risk is Phase 4 and it is not an audit item** — production has run PayPal in
SANDBOX since day one, so no money in this system has ever been real. Plan ids, subscription ids
and the webhook signature are all per-environment and all have to be re-made.

---

## 3. The QA harness — DONE; two follow-ups only

**The full WEB-H-1 lifecycle passed end-to-end 2026-08-20 → 26.** The downgrade applied at midnight
and beat PayPal's charge; the completion re-subscribe waived the setup fee (invoice 000018 =
$45.20); the cancel honored paid-through to Aug 29 with exactly one Cancel row (the fence's first
live run); the delete removed 91 rows / 0 files matching the preview, with financials retained.
Signup, upgrade, downgrade, cancel and row-exact delete are all now proven in production from
`bobymot.247advisers.com`. Detail is in `DOCS/TODO.md` under "QA HARNESS COMPLETE".

**Follow-up 1 — CLOSED 2026-08-27. PayPal did not charge.** The owner's dashboard shows its
newest entry on Aug 26 (-$45.20, the completion re-subscribe with the setup fee correctly waived)
and nothing on Aug 27. The cancel reached PayPal and paid-through-to-Aug-29 held. The surrounding
history matches the harness record: Aug 25 -$101.70 Platinum daily, Aug 24 -$0.45/-$0.31 upgrade
proration, Aug 24 and Aug 23 -$45.20.

**Follow-up 2 — a STRONG erasure test still has no coverage.** Every delete so far was of an agent
with **0 files and no custom domain**, so the blob-shred and hostname-unbind legs have never run
for real. That is exactly the ground H11/M14 covers. Worth one purpose-built agent WITH uploaded
files and a custom domain before any real-customer deletion.

**Standing harness limitations (not defects):** upgrade proration amounts are ~30× understated on
daily packages (frequency mismatch — proration proof lives in the unit tests), and the displayed
"Next billing" can lag until the hourly reconcile corrects >26h drift.

**One-off data task:** existing agents' `Province` values deserve a check against the tax alias
map — "Yukon Territory" (the register dropdown's own label!) was unmapped, so any Yukon signup has
been zero-rated since launch; free-text profile entries ("PEI") may lurk too.

---

## 4. Owner decisions — resolved and open

- **RESOLVED today: post-upgrade annual cancel refund policy** — full unused value at the row's
  rate, capped at everything actually settled in the running cycle across the agent's rows; the
  queue note splits amounts across transactions. DOCS/22 carries the addendum.
- **OPEN: M6** — the credit-note mechanism (`RefundStatus.ConvertedToCredit` is enum-only; the
  CRA tax-by-region figure over-reports after any refund). Feature work, not a defect fix.
- **RESOLVED 2026-08-27: do NOT delete the QA sandbox invoices.** Option A (shipped 08-26)
  already excludes hidden-test-package invoices from Revenue + CSV, and every future harness run
  is auto-excluded. Option B rejected: deletion is irreversible, and since production PayPal has
  run in SANDBOX since day one this was never a 9-row problem — nearly the whole ledger is test
  data, so deleting one agent's rows would leave the rest and fake a clean ledger. **The cleanup
  belongs to the Phase 4 test-ledger purge** at the live cutover, where the whole sandbox ledger
  goes at once immediately before real money exists. The FK-safe statements are in `DOCS/TODO.md`
  as that step's starting point.
- **OPEN: staging environment** — the reminder fired Mon 2026-08-25 09:00; the question is data
  (a real copy = PIPEDA exposure), not cost. Not yet decided.

---

## 5. What is left (all pre-audit register items — the billing audit itself is closed)

**DONE 2026-08-26 (erasure wave, `fix/erasure-wave`):** H11 (Azure unbinds now AFTER the shred
commits), M14 (erase failures audited + reported, locked-out-but-intact), M15 (unresolved-refund
agents refuse deletion). Real-customer deletions are no longer gated.

**Open now (everything else from the audits is closed):**

- **HIGH — 0 left.** Every HIGH from every audit is closed as of `21da3e6`.
- **MEDIUM — 1 left:** M6 credit notes only — feature work and an owner decision, parked by
  choice. M1, M2, M9, M10, M13 and M20 all shipped today.
- **LOW — 11 left**, including L12: content authored before 2026-08-20 still holds live `<form>`
  blocks and overlay CSS until re-saved (a data question, not a code fix).
- **Structural, parked:** A5-H11/H12/H14 blob ownership (design rejected — needs a new one) · the
  EF-migrations-vs-repair duality (snapshot covers 28 of 85 tables).
- **Doc corrections** still owed to `AUDIT_RECONCILIATION_2026-08-17.md` and DOCS/22's Stage-C
  wording.

---

## 6. Standing rules (each one was re-learned the hard way at least once)

Branch before deploying; never push straight to `main` · tests run and pass BEFORE the commit ·
verify at `/health/version` on **BOTH** hosts, never `/health`, never one host · don't push into
an in-flight deploy — the shared concurrency group EVICTS the sibling's pending run (INVARIANTS
rule 6) · schema changes go through `StartupSchemaRepair`, never dotnet-ef (rule 4) · a fix is
done when the production CALLER is right, in BOTH apps, with a test exercising the customer's
path · every fix verified both ways — a test that never failed proves nothing · assertions inside
conditions that are never true pass against broken code (it happened twice today; both caught).

---

## 7. Recovery after a reboot

If this working directory survived: you are on `main`, clean. Production serves the head of
`main` (`ccb34db` + the session-close docs commit). Every feature branch from today is merged
and deleted.

If it was wiped (has happened; only OneDrive-synced folders survived):

1. Everything is on GitHub: `https://github.com/bmipro/IPRO_Modern`. Clone `main` — production
   is its head. **Nothing of value lives outside `main`**; every branch is merged and deleted.
2. Backup zips (`git archive` of the branch HEAD, contents verified by listing inside the zip):
   - **OneDrive** `C:\Users\admin\OneDrive\Codex_Code_Bkup\` — the copy proven to survive resets
   - Local `C:\Users\admin\IPRO_Local_Backups\`
   - Older sets under `C:\Users\admin\Documents\IPRO_Backups\`
   Newest: **`IPRO_Modern_2026-08-27_eod_phase2.zip`** (main HEAD incl. all of Phase 2,
   contents verified by reading the M1 docs and tests back out of the zip).
3. `.claude` memory files have been lost to a reboot before — re-read `DOCS/` rather than trusting
   recalled context. This file and the register are the two that matter.

**Local dev environment:** `ops\Start-LocalEnv.ps1` starts MySQL + Azurite (apps:
`localhost:5100` Web, `localhost:5200` Admin). **MySQL does not survive a reboot** — run the
script before the test suite, or connection errors read as test failures (that misread happened
once already, and produced a false RED). Local-only data: agent 11 (drip tester), `supporttest`
admin.

**Nothing is mid-flight.** Both apps are deployed and verified; no run is queued or in progress;
everything is on `main` and inside both backup zips. The QA harness is complete and needs nothing
from this machine. **Safe to reboot.**
