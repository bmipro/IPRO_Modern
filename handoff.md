# Handoff — 2026-08-25, end of day (five waves shipped; billing audit fully dispositioned)

Written to survive a reboot. If the working directory was wiped, see **§7 Recovery** first.

Companion docs: `DOCS/AUDIT_2026-08-20_POST_SWEEP.md` (the defect register — the authority; every
wave's record and every open item lives there), `DOCS/TODO.md` (durable backlog),
`DOCS/INVARIANTS.md` (pre-flight rules — rule 6 now covers the deploy-eviction hazard),
`DOCS/22_PREPAID_VALUE.md` (billing policy, incl. the post-upgrade refund addendum decided today).

---

## 1. Production right now

**Both apps live on the head of `main`** — verify which SHA at `/health/version` on **BOTH** hosts
(never `/health`; and never trust one host or the workflow list — INVARIANTS rule 6). Today's code
merges, in order: wave 1 `d21ef09` → billing wave `a827bda` → wave 3 `d222826` → wave 4 `8624609`
→ wave 5 `5debbe4`. Six deploys, every one verified on both hosts, zero run cancellations.

Production agents:
- **BahmanMotamed #12** — owner, Gold monthly.
- **BobyMot #35** — QA daily harness, PayPal sandbox sub `I-VG1A3CKSK6DX`, **Platinum Daily** with
  a **scheduled downgrade to Silver Daily (`billingRuleId` 7) effective Aug 26** — it fires up to
  6 hours EARLY (the lead window is 25% of a daily cycle; harness artifact, not a bug).

Test suite: **306 passed / 0 failed / 1 skipped** (the skip is deliberate — audit M1's CSS
allow-list, `[Fact(Skip=...)]` with the reason inline). The suite grew **216 → 281 today**; every
defect fix in every wave was verified BOTH ways (test fails on pre-fix code, passes on the fix —
or the fix stash-reverted and the greens observed to go red).

---

## 2. What shipped today — five waves

| Wave | Branch | What it closed |
|---|---|---|
| 1 | `fix/audit-wave-1` | The five live-exposure findings: H1 sanitizer content destruction, C1 blob guard never ran, H9 lock profile, H10 retention veto, H2 post-cancel double-charge. |
| Billing | `fix/billing-wave` | The register's PayPal cluster, 13 findings: C2 renewed-annual clawback, M3/M4 refund tax+amount from what was paid, M5 PayPal-initiated cancels, H5 promo slots, H12 convert survives the sweeper, H6 waiver door, H15/New A truthful copy, M7/M16/M19 batch isolation, H13/H14 email retry bounds. |
| 2 | `fix/billing-wave-2` | The four-auditor audit's 4 HIGHs (three wave-created): the drip tracker-clear duplicate-send regression, deferred-start phantom refunds, the refund double-mint race (new `BillingCancellationClaims` fence table), the sweep's PayPal blindness + the captured-after-end refund net; plus Expired paid-through honored, month-end drift, slot ordering, txn-ref truncation, reconcile isolation. |
| 3 | `fix/billing-wave-3` | Audit F2 (renewals keep the sold-at tax rate; `Billing.Amount` survives a province move), F3 (Profile province dropdown + missing aliases — **"Yukon Territory" was never in the tax map**), F5 (plan sync clears frozen promo plans), and the **owner-decided refund policy**: full unused value capped at cycle-wide settled money (DOCS/22 addendum). |
| 4 | `fix/billing-wave-4` | The audit's last four MEDIUMs: F6 terminal-state relabels, state-F5 Keep-My-Plan vs in-flight converts, F3c/jobs-4 superseded rows never mint a second outcome (+ H6/M16 protected from the race row), jobs-5 stage-3 isolation. |
| 5 | `fix/billing-wave-5` | The LOW sweep, all ten in the owner-approved ranking — invariant-culture webhook parsing, the ClientInvoices Quebec-precision sibling, converts derive credit only from paid money, Resume keeps the convert flavor, webhook-cancelled checkouts void completely, dunning copy/ordering. |

**Between the billing wave and wave 2 sat a four-auditor A-to-Z billing audit** (money math, state
machine, jobs, truth) against the deployed code. It confirmed all 13 billing-wave fixes HOLD at
their production call sites — no C1-class green-but-useless test — and found the 4 HIGHs above
that the tests could not see.

**End state: the four-auditor billing audit is fully dispositioned — 0 HIGH / 0 MEDIUM / 0 LOW
open** (one recorded hardening residue: the DYK counter's server-side increment, bounded by its
cap either way). The register's "no known open defects" claim from mid-day was DISPROVED the same
day by that audit and is kept in the register as a lesson.

---

## 3. The QA harness — what happens next

**ALL THREE EVENTS BELOW COMPLETED 2026-08-26 — the harness is DONE.** The downgrade applied at
midnight and beat PayPal's charge; the completion re-subscribe waived the fee (invoice 000018 =
$45.20); the cancel honored paid-through to Aug 29 with exactly one Cancel row (the fence's first
live run); the delete removed 91 rows / 0 files matching the preview, financials retained. The
one outstanding check: PayPal must show NO $45.20 charge on Aug 27. Original plan kept below for
the record.

1. **Aug 26 (early, up to 6h before the boundary): BobyMot's downgrade APPLIES.** Expect: the
   Platinum PayPal sub cancelled, billing row Cancelled, an "Action needed: complete your plan
   change" email naming **Silver Daily (monthly billing)** (wave-5 #9 copy), and the account
   pausing at the Billing page — exactly what the banner disclosed. This is the first live run of
   the fence, the supersede guard, and the apply path post-waves.
2. **Owner re-subscribes to Silver Daily** from the Billing page. The setup fee should be
   **WAIVED** (H6's legitimate completion branch). An unexpected fee = a real finding; report it.
3. **Day 4: cancel, then DELETE BobyMot #35** — exercises C1/H9/H10 erasure end-to-end.
   ⚠ **Before running the delete, strongly consider fixing H11/M14 + M15 first** (see §5) — the
   erasure ordering is only PARTLY fixed (Azure unbinds still precede the shred; `EraseAsync` has
   no try/catch) and nothing guards deleting an agent owed an unresolved refund — an exposure the
   waves WIDENED, since more refund rows are minted now.

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
- **OPEN: staging environment** — the reminder fired Mon 2026-08-25 09:00; the question is data
  (a real copy = PIPEDA exposure), not cost. Not yet decided.

---

## 5. What is left (all pre-audit register items — the billing audit itself is closed)

**DONE 2026-08-26 (erasure wave, `fix/erasure-wave`):** H11 (Azure unbinds now AFTER the shred
commits), M14 (erase failures audited + reported, locked-out-but-intact), M15 (unresolved-refund
agents refuse deletion). Real-customer deletions are no longer gated.

**Then:** H3 resolver split · H4 SSRF pinning + `RootLastError` echo · H7 SendGrid 401/403
classification + drip resume path · H8 webhook suppression swallow · M1 overlay CSS allow-list
(the skipped test) · M2 `RebuildRequestMeeting` gate · M8 agent-portal `ValidatePrincipal` ·
M9–M13 · M17 IPv6 prefixes · M18 blob registry containers · the 12 pre-audit LOWs · wave-3 doc
corrections still owed to `AUDIT_RECONCILIATION_2026-08-17.md` and DOCS/22 Stage-C wording.

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

If this working directory survived: `git status` clean on `main`; nothing else to do.

If it was wiped (has happened; only OneDrive-synced folders survived):

1. Everything is on GitHub: `https://github.com/bmipro/IPRO_Modern`. **Clone `main` — nothing of
   value lives outside it**; `git branch -r --no-merged origin/main` is empty.
2. Backup zips (`git archive` of `main`, ~4 MB, contents verified at write time):
   - **OneDrive** `C:\Users\admin\OneDrive\Codex_Code_Bkup\` — the copy proven to survive resets
   - Local `C:\Users\admin\IPRO_Local_Backups\`
   - Older sets under `C:\Users\admin\Documents\IPRO_Backups\`
   Newest: `IPRO_Modern_2026-08-25_eod_d418a91.zip`.
3. `.claude` memory files have been lost to a reboot before — re-read `DOCS/` rather than trusting
   recalled context. This file plus the register are the two that matter.

**Local dev environment:** `ops\Start-LocalEnv.ps1` starts MySQL + Azurite (apps:
`localhost:5100` Web, `localhost:5200` Admin). **MySQL does not survive a reboot** — run the
script before the test suite or connection errors read as test failures (that misread happened
once already). Local-only data: agent 11 (drip tester), `supporttest` admin.

**Nothing is mid-flight.** All five waves merged and deployed; the harness's next event (the
downgrade apply) runs on PayPal's clock and our production Hangfire — this machine is not needed
for it. Safe to reboot.
