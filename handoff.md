# Handoff — 2026-08-20 (end of day, post-audit)

Written to survive a reboot. If the working directory was wiped, see **§7 Recovery** first.

Companion docs: `DOCS/AUDIT_2026-08-20_POST_SWEEP.md` (the audit findings — **read this before
touching billing, erasure or the sanitizer**), `DOCS/TODO.md` (durable backlog),
`DOCS/AUDIT_RECONCILIATION_2026-08-17.md` (per-finding status, partly stale — see §4),
`DOCS/22_PREPAID_VALUE.md` (the billing redesign, Stage C partly unshipped).

---

## 1. Production right now

**Both apps live on `94d36c3`**, verified via `/health/version`, all surfaces healthy
(app 200, agent site 200, admin 302). Five deploys shipped today plus a docs commit.

Production agents: **BahmanMotamed #12** (owner, Gold monthly) and **BobyMot #35** (QA daily,
real PayPal sandbox subscription `I-VG1A3CKSK6DX`, charges every day — day 1 was 2026-08-20).

---

## 2. STOP — do not do these until wave 1 ships

1. **Do NOT run QA day-4 (cancel + delete BobyMot).** It trips three live defects at once:
   the post-cancel double-charge state, the blob guard that never runs, and the erasure
   transaction's lock profile.
2. **Do NOT delete any agent** — same blob-guard and lock reasons.
3. **Avoid bulk-re-saving article or drip content** — the sanitizer currently deletes the
   *contents* of `<form>`/`<button>` tags on write.

---

## 3. What the audit found (summary — detail in DOCS/AUDIT_2026-08-20_POST_SWEEP.md)

Six parallel auditors against `94d36c3`. **2 CRITICAL, 15 HIGH, 20 MEDIUM, 12 LOW, 8 doc errors.**
Both criticals were found independently by two auditors:

- **C1 — the shared-file guard has never run in production.** `A5-H12` was marked FIXED on
  2026-08-18 with a passing test; the Admin delete path deletes the *unfiltered* preview list
  before `EraseAsync` (where the guard lives) ever runs. **A5-H12 reverts to NOT FIXED.**
- **C2 — annual cancellation robs renewed subscribers.** The clawback measures from
  `Billing.StartDate`, never advanced on renewal → `$0` refund and instant loss of access for
  anyone past year one. Latent only because no annual subscriber has renewed yet.

**Live right now:** sanitizer content destruction, post-cancel double-charge, the blob guard,
the erasure lock hazard.

**What held up:** 31 of ~45 documented claims verify TRUE; no XSS regression from the AngleSharp
major bump; admin auth fails **closed**; no third transaction-enlistment bug; the `PrepaidValue`
math is exactly as designed.

**The pattern (most important):** the chronic failure moved rather than disappeared — *fixed the
library not the caller*, *fixed one app not both*, *obeyed the invariant in the new path and broke
it in the sibling written the same week*, *asserted the property but tested only the mechanism*.

---

## 4. Documents that are currently WRONG (corrections pending, deliberately not yet applied)

- `AUDIT_RECONCILIATION_2026-08-17.md` — `A5-H12` says FIXED (it is not); the `## Counts` block and
  the "OPEN — 54 distinct defects" heading are stale (**15** are open, not 54); one heading cites
  `1fLB`, not a valid SHA (correct: `1f6cedd`); the Newtonsoft "nothing pins it" bullet is wrong.
- `DOCS/22_PREPAID_VALUE.md` — Stage C is PARTIAL (no ledger credit notes, `ConvertedToCredit`
  unreachable); ToS path is `Views/Shared/_LegalTerms.cshtml`; Stage D added 4 tests, not 6.
- `DOCS/TODO.md` — the medium sweep was **18** fixes, not 16.
- Earlier versions of THIS file claimed "High (actionable): 0" — that was false.

Full correction table: `DOCS/AUDIT_2026-08-20_POST_SWEEP.md` § "Documentation corrections required".

---

## 5. Branch state — IMPORTANT

| Branch | State |
|---|---|
| `main` | `94d36c3` — what production is serving. Clean. |
| `docs/post-sweep-audit` | `e580698` — **the audit findings doc + READ-FIRST pointers. NOT merged.** Pushed to origin. |

All feature branches from today (`fix/audit-medium-seven`, `feat/prepaid-value-honesty`,
`feat/downgrade-offer-both`, `fix/medium-sweep`) are merged into `main` and deployed.

**⚠ The test suite is currently RED on `docs/post-sweep-audit`** — 2 deliberately-failing tests
encode audit findings H1 and M1 (`MediumSweepTests.Removing_form_tags_must_not_destroy_their_inner_content`
and `Overlay_cannot_be_rebuilt_from_the_properties_left_allowed`). They are *supposed* to fail until
wave 1 fixes the sanitizer. `main` itself is green (209/209).

---

## 6. Next steps — the three waves

**Wave 1 — stop the bleeding. Blocks QA day-4.**
H1 sanitizer deletes removed tags' contents (set `KeepChildNodes = true`, or keep the tags and strip
only action/submit attributes) · H2 post-cancel double-charge · C1 make the blob guard actually run
(**needs a design decision** — delete-after-shred with a stranding window, or a pre-shred snapshot;
both options are written up in the audit doc) · H9 move the blob re-check outside the transaction ·
H10 roll back on retention shortfall.

**Wave 2 — correctness.** C2 `StartDate` → `GetCurrentCycleStart` · M3/M4 refund from the invoice,
not today's prices · H3 the resolver split · M5 PayPal-initiated cancels · H5 promo slot release ·
H6 setup-fee waiver · H7 SendGrid 401/403 classification + a resume path for Failed enrollments ·
H8 suppression events must not be swallowed · H12 the convert's pending row · H13/H14 retry bounds ·
H4 SSRF IP pinning + stop echoing `RootLastError` · M2 gate `RebuildRequestMeeting` · M8
agent-portal revalidation · H15/M13 make the promises match the code.

**Wave 3 — truth and prevention.** Apply every correction in §4. Add the tests these findings prove
are missing — starting with a **controller-level** blob test (would have caught C1 on day one), an
annual-cancel-after-renewal fixture, a cancelled-but-paid-through Billing-page test, and a
resolver-agreement test with a divergent `AgentUser.PackageId`.

**Also still open (unchanged by the audit):** the 14 known low-severity items and 7 owner decisions
in the register; the **staging decision returns Mon 2026-08-25 09:00** (scheduled reminder — the
question is data/PIPEDA, not cost).

---

## 7. Recovery after a reboot

If this working directory survived: nothing to do, `git status` should be clean on
`docs/post-sweep-audit`.

If it was wiped (this has happened before — only OneDrive-synced folders survived):

1. Everything is on GitHub: `https://github.com/bmipro/IPRO_Modern` — `main` at `94d36c3`,
   plus the unmerged `docs/post-sweep-audit` at `e580698`.
2. Backup zips (both written 2026-08-20, `git archive` of the audit branch, ~4.1 MB):
   - **OneDrive** `C:\Users\admin\OneDrive\Codex_Code_Bkup\` — the copy proven to survive resets
   - Local `C:\Users\admin\IPRO_Local_Backups\`
3. `.claude` memory files have been lost to a reboot before — re-read `DOCS/` rather than trusting
   recalled context. This file plus `DOCS/AUDIT_2026-08-20_POST_SWEEP.md` are the two that matter.

**Local dev environment:** `ops\Start-LocalEnv.ps1` starts MySQL + Azurite; apps run at
`localhost:5100` (Web) and `localhost:5200` (Admin). Both preview servers are stopped. Local-only
test data: agent 11 (drip tester), `supporttest` admin. Nothing local is needed for production.

**Nothing is deploying and nothing is mid-flight.** Safe to reboot.
