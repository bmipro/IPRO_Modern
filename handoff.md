# Handoff — 2026-08-24 (wave 1 shipped)

Written to survive a reboot. If the working directory was wiped, see **§8 Recovery** first.

Companion docs: `DOCS/AUDIT_2026-08-20_POST_SWEEP.md` (the audit findings — **read this before
touching billing, erasure or the sanitizer**), `DOCS/TODO.md` (durable backlog),
`DOCS/INVARIANTS.md` (pre-flight rules for routing/hosts/auth/billing),
`DOCS/AUDIT_RECONCILIATION_2026-08-17.md` (per-finding status, partly stale — see §5),
`DOCS/22_PREPAID_VALUE.md` (the billing redesign, Stage C partly unshipped).

---

## 1. Production right now

**Both apps live on the head of `main`.** The wave-1 *code* merge is **`d21ef09`**; anything on
`main` after it is documentation only, so `d21ef09` is the SHA that matters for behaviour. Verified
via `/health/version` (never `/health`, which answers before the new build is serving).

Production agents:
- **BahmanMotamed #12** — owner, Gold monthly.
- **BobyMot #35** — QA daily harness, real PayPal sandbox subscription `I-VG1A3CKSK6DX`, charges
  every day. Day 1 was 2026-08-20. Currently **Platinum Daily** (package 9).

Test suite on `main`: **216 passed, 0 failed, 1 skipped** (the skip is deliberate — audit M1, below).

---

## 2. What shipped today (wave 1) — the five live-exposure findings

Full detail in `DOCS/AUDIT_2026-08-20_POST_SWEEP.md` § "Wave 1". Summary:

| | What was wrong | What it means now |
|---|---|---|
| **H1** | The sanitizer deleted a removed tag's *contents*, so a pasted `<button>Book a call</button>` became empty and `<form><p>real text</p></form>` lost the paragraph. Sanitisation runs on WRITE, so the loss was permanent and invisible. | Two passes — dangerous elements die with their bodies (`<script>` cannot leak as text); form controls are **unwrapped**, so the words survive. Bulk re-saving article/drip content is safe again. |
| **C1** | The cross-agent shared-file guard **had never run in production** — Admin deleted the unfiltered preview list before `EraseAsync` (where the guard lives) was ever called. | Ordering reversed to ROWS BEFORE FILES. Only `report.Blobs` (the filtered list) is deleted. |
| **H9** | The blob re-check ran *inside* the erasure transaction, holding write locks across ~66 tables while running ~30 queries per file. | Moved after commit — it needs the rows gone, not the locks. |
| **H10** | A detected retention violation was committed anyway. | Now a veto: rollback, nothing deleted, agent left deactivated but whole (Admin → Activate restores). |
| **H2** | Cancelling left a state where re-subscribing double-charged, and the Billing page showed a stale past-dated "Trial active" banner. | New subscription starts at `PaidThroughAt`; the page says "cancelled — you keep X until \<date\>". |

**Every fix was verified BOTH ways** — the new test run against the pre-fix code and observed to
FAIL, then against the fix and observed to PASS. That two-way check is exactly what was missing on
2026-08-18, when C1 shipped green while protecting nothing.

Three wrong turns happened on H1 and all three were caught by tests rather than by review. One of
them tempted me to weaken an existing security assertion to fit the fix; the right answer satisfied
both rules and left that test **unmodified**. Recorded here because the discipline is the point.

**Deliberately NOT fixed: audit M1 (CSS overlay).** Its test carries `[Fact(Skip=...)]` with the
reason inline. A deny-list cannot close it — the real fix is a CSS *allow*-list, which needs care not
to break existing newsletter formatting. Wave 2.

---

## 3. The STOP list is lifted

The three blocks from 2026-08-20 are gone: **QA day-4 (cancel + delete BobyMot) is now unblocked**,
agent deletion is safe, and bulk content re-saves no longer destroy prose.

---

## 4. QA harness — next steps and one important limitation

**2026-08-25: Platinum → Silver downgrade.** Console POST to `/Billing/Subscribe` with
`billingRuleId=7` (Silver Daily) and `period=Monthly`.

**Correction to an earlier note in this file: do NOT expect the Offer Both flow.** `downgradeMode
="convert"` is refused for anything that is not `BillingPeriod.Annually`
(`PayPalBillingService.cs:356`), and BobyMot's period is Monthly. Platinum→Silver therefore takes
the plain scheduled path: `ScheduleDowngradeAsync`, `AmountDue = 0`, no PayPal redirect, and the
message "Your downgrade to <package> is scheduled for <date>." Offer Both can only be exercised by
an annual subscriber, which the daily harness cannot be.

**Second harness artifact, same class as the proration one:** `DowngradeApplyLeadWindow` is 6 hours
(`PayPalBillingService.cs:1971`) — a due downgrade fires up to 6h early so the PayPal cancel always
beats the next charge. On a monthly plan that is 0.8% of the period; on the DAILY harness plan it is
**25% of the whole billing period**, so BobyMot will visibly lose about a quarter of a paid day.
That is the harness's compressed cycle, not a production defect.

**Then: day 4 — cancel, then delete BobyMot #35.** That exercises C1/H9/H10 end to end against real
data, which is the whole reason wave 1 had to land first.

### The proration limitation — read before judging any upgrade amount

The daily harness **cannot validate upgrade proration amounts**. `GetCurrentCycleStart` subtracts one
*month* because `BillingPeriod` is Monthly, but the daily plan's synced `NextBillingDate` is tomorrow
— so the denominator is roughly 30x too large and every prorated charge comes out roughly 30x too
small. Observed live today: Silver→Gold billed **$0.27** (invoice IPRO-2026-000015, $0.31 with tax)
and Gold→Platinum **$0.40** ($0.45, IPRO-2026-000016), where the correct monthly-equivalent figures
are ~$8.33 and ~$12.

**This is a harness artifact, not a production defect** — only packages 7/8/9 have the daily/Monthly
frequency mismatch. But it cuts both ways: the 2026-08-11 note claiming "prorated $19.33 verified"
was overstated for the same reason. Proration correctness has to be proven by unit test, not by the
daily harness.

Everything else in the harness *is* trustworthy: upgrades applied, invoices issued with both
transaction ids, emails delivered, and every redirect landed back on `bobymot.247advisers.com`.

---

## 5. Documents that are currently WRONG (corrections pending — wave 3)

- `AUDIT_RECONCILIATION_2026-08-17.md` — `A5-H12` says FIXED; it was not, and wave 1 is what actually
  fixed it (as C1). The `## Counts` block and the "OPEN — 54 distinct defects" heading are stale.
  One heading cites `1fLB`, not a valid SHA (correct: `1f6cedd`). The Newtonsoft "nothing pins it"
  bullet is wrong.
- `DOCS/22_PREPAID_VALUE.md` — Stage C is PARTIAL (no ledger credit notes, `ConvertedToCredit`
  unreachable); the ToS path is `Views/Shared/_LegalTerms.cshtml`; Stage D added 4 tests, not 6.
- `DOCS/TODO.md` — the medium sweep was **18** fixes, not 16.

Full correction table: `DOCS/AUDIT_2026-08-20_POST_SWEEP.md` § "Documentation corrections required".

---

## 6. Branch state

| Branch | State |
|---|---|
| `main` | wave 1 merged (`d21ef09`) plus docs. Clean. **Nothing is unmerged — `git branch -r --no-merged origin/main` returns empty.** |
| `fix/audit-wave-1` | `37b0d91` — merged. Safe to delete. |
| `docs/wave-1-handoff` | `e789ac3` — merged. Safe to delete. |
| `docs/post-sweep-audit` | `092b49f` — superseded; its content reached `main` via the wave-1 merge. Safe to delete. |

---

## 7. What is left

**Wave 2 — correctness.** C2 `Billing.StartDate` → `GetCurrentCycleStart` (annual cancellation robs
renewed subscribers; latent only because no annual subscriber has renewed yet) · M1 the CSS
allow-list · M3/M4 refund from the invoice, not today's prices · H3 the resolver split · M5
PayPal-initiated cancels · H5 promo slot release · H6 setup-fee waiver · H7 SendGrid 401/403
classification + a resume path for Failed enrollments · H8 suppression events must not be swallowed ·
H12 the convert's pending row · H13/H14 retry bounds · H4 SSRF IP pinning + stop echoing
`RootLastError` · M2 gate `RebuildRequestMeeting` · M8 agent-portal revalidation · H15/M13 make the
promises match the code.

**Wave 3 — truth and prevention.** Apply every correction in §5. Add the tests these findings prove
are missing: an annual-cancel-after-renewal fixture, a cancelled-but-paid-through Billing-page test,
and a resolver-agreement test with a divergent `AgentUser.PackageId`. (The controller-level blob test
that would have caught C1 on day one now exists — `AgentDeleteBlobOrderingTests`.)

**Also open:** the 14 known low-severity items and the owner-decision list in the register; the
**staging decision reminder fires Mon 2026-08-25 09:00** — the open question is data (a real copy
means PIPEDA exposure), not cost.

---

## 8. Recovery after a reboot

If this working directory survived: nothing to do, `git status` should be clean on `main`.

If it was wiped (this has happened before — only OneDrive-synced folders survived):

1. Everything is on GitHub: `https://github.com/bmipro/IPRO_Modern`. **Clone `main` — nothing of
   value lives outside it.** (Wave-1 code = `d21ef09`; later commits on `main` are docs.)
2. Backup zips — `IPRO_Modern_2026-08-24_wave1_d21ef09.zip`, 4.1 MB, 887 files, tree-identical to
   `main` (verified, not assumed):
   - **OneDrive** `C:\Users\admin\OneDrive\Codex_Code_Bkup\` — the copy proven to survive resets
   - Local `C:\Users\admin\IPRO_Local_Backups\`
   - Older sets also exist under `C:\Users\admin\Documents\IPRO_Backups\`
3. `.claude` memory files have been lost to a reboot before — re-read `DOCS/` rather than trusting
   recalled context. This file plus `DOCS/AUDIT_2026-08-20_POST_SWEEP.md` are the two that matter.

**Local dev environment:** `ops\Start-LocalEnv.ps1` starts MySQL + Azurite; apps run at
`localhost:5100` (Web) and `localhost:5200` (Admin). **MySQL does not survive a reboot** — after
restarting, run that script before the test suite, or you will read connection errors as test
failures. Local-only test data: agent 11 (drip tester), `supporttest` admin.

**Deploy hazard found 2026-08-24 (now `DOCS/INVARIANTS.md` rule 6):** admin and web share one
Actions concurrency group, which holds only ONE pending run — so pushing to `main` while a deploy is
in flight **cancels one of the two apps' runs before it starts**, leaving the hosts on different
SHAs with nothing reporting a failure. Always check `/health/version` on **both** hosts after a push;
if they disagree, `gh run list` and `gh run rerun <id>`. Don't push into an in-flight deploy.

**Standing rules that keep being re-learned:** branch before deploying, never push straight to `main`
without asking · tests must run and pass BEFORE the commit · verify a deploy at `/health/version` for
the pushed SHA, never `/health` · a fix is not done when the library is right, it is done when the
production **caller** is right, in **both** apps, with a test that exercises the path a customer takes.

**Nothing is mid-flight.** Safe to reboot.
