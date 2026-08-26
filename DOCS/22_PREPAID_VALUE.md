# 22 — Prepaid value: cancellation and downgrade honesty

Decided with the owner 2026-08-20 after the "annual → monthly" brainstorm. This document is the
authority for what happens to money an agent has already paid when they cancel or downgrade.
The register items it closes when fully implemented: UX-CANCEL, BILL-CREDIT (a home for the
credit), and it interlocks with BILL-HST / BILL-BANNER / BILL-ZEROINV / BILL-CREDITPRICE.

## The principle

**The annual discount belongs to those who stay.**

- An agent who **downgrades** keeps their money in the house, so their unused value converts at
  the discounted rate they actually paid.
- An agent who **cancels** did not complete the year that earned the discount, so the months they
  used revert to the monthly price and the remainder is refunded.

## Cancellation

The confirm dialog has always promised "your site will go offline at the end of the billing
period." As of this design, that becomes true.

### Monthly subscription

PayPal is cancelled immediately (no further charges — unchanged, and still PayPal-first so a
failed PayPal cancel never marks the local row cancelled). Access continues to the billing
anniversary (`NextBillingDate`) **plus 2 days grace**, carried by `Billing.PaidThroughAt`.

### Annual subscription — months 1 through 9

Months used revert to the monthly price; the remainder is refunded:

```
monthsUsed = calendar months since period start, partial month rounds UP
refundNet  = max(0, paidNet − monthlyNetPrice × monthsUsed)
refundTax  = refundNet × taxRate actually charged on the original invoice
refundGross= refundNet + refundTax        ← the number typed into PayPal
access     = end of the current billing month + 2 days grace
```

Worked example (Gold, $600/yr, 13% HST, cancel in month 5):
used 5 × $60 = $300 → refund **$300 net, $39 HST, $339 gross**; access to end of month 5.

### Annual — months 10 through 12 (the crossover)

Every package is priced "two months free" annually, so monthly-rate usage equals the annual
payment at month 10 exactly. From month 10 on, the refund is $0 — so instead the agent keeps
access **until the anniversary**. The system always auto-picks whichever outcome favours the
agent; there is no configuration and no judgement call.

### Annual cancel AFTER a mid-year upgrade (owner-decided 2026-08-25)

An annual-to-annual upgrade's new billing only ever captured the prorated difference, and the old
row's payment bought the same year. On cancel: refund = the full unused value at the NEW row's
rate (Amount - monthsUsed x Amount/10), **capped at everything actually settled in the running
cycle across the agent's rows**. The cap keeps every queue instruction executable (never more
than the cycle collected); the queue note splits the amount across the involved transactions when
it exceeds the new row's own capture. Chosen as the least-complicated agent-favouring rule; on
the worked example (Gold $600 renewed, upgraded to Platinum mid-year for $500, cancelled month 4)
it refunds $720 net where perfect cross-row fairness would be $740. A row with NO positive capture
in the running cycle (a convert mid-credit, a deferred start) refunds nothing and keeps its
access to the paid-through date -- money never collected is not refundable.

### Refunds are MANUAL, driven by a SuperAdmin queue

No code moves money. The queue (SuperAdmin → Refunds) lists every cancellation with a refund
due: agent, package/term, months used, **net + HST + gross**, the original PayPal transaction id
to refund against, days remaining in PayPal's ~180-day refund window, and a status the owner
flips: `Pending → Refunded (txn id)` / `ConvertedToCredit` / `Waived`. Rows are financial
records: retained by the eraser like invoices, audit-logged, and Refunded amounts appear as
credit notes against the revenue/tax ledger.

### The 180-day trap (Catch #2)

PayPal will not refund against a transaction older than ~180 days. An annual payment is at day
~150 by month 5 — so **months 6–9, where refunds of $240 → $60 are still owed, cannot be
refunded through the PayPal portal.** For those, the cancel screen leads with the conversion
offer instead ("your remaining $240 = 6 months of Silver"), and the queue shows the window
expired so nobody wastes time trying. Verified at implementation time against PayPal's current
policy; treat 180 as a planning number.

## Downgrade — offer BOTH models

The downgrade confirm screen asks one question:

- **Keep what you paid for** (defer, today's behaviour): keep the current package until the
  paid-through date, then the new package begins.
- **Switch now and stretch your money** (convert): the new package starts today; unused net
  value ÷ new package's net monthly price becomes free time on the new package, computed in
  days, **rounded in the agent's favour**. Mechanism: supersede — create the new PayPal
  subscription with `start_time = today + creditDays`, cancel the old only after approval.
  NOT promo codes: those are a marketing object with their own audit history.

Credit converts at the rate **actually paid** (from invoices, never the current BillingRule
price — SuperAdmin price edits must not reprice history). A later upgrade during a credit
period re-converts the remaining dollars, which is why the dollars are stored, not just a date.

## Interlocked defects that MUST ship with the convert path

| Item | Why it blocks |
|---|---|
| BILL-HST | A $0-up-front subscription resolves TaxRate 0 and bills NET forever — the convert path creates exactly this shape. |
| BILL-ZEROINV | The $0 conversion invoice would later be falsely settled by an unrelated sale. |
| BILL-BANNER | Deferred-start subs need the honest next-charge date (reconcile now syncs from PayPal; the write path must stop clobbering). |
| BILL-CREDITPRICE | Credit must derive from invoices actually paid. |

## Mechanics

- `Billing.PaidThroughAt` (datetime, nullable): access authority for cancelled rows. Gating
  (`IsAccessGatedAsync` AND its bulk sibling — the two must stay logically identical) treats a
  Cancelled row with `PaidThroughAt > now` as access-granting at that row's package.
- `SubscriptionChange` gains `ChangeType.Cancel` and refund columns
  (`RefundNetAmount/RefundTaxAmount/RefundGrossAmount/RefundStatus/RefundPayPalTransactionId/
  RefundWindowEndsAt/RefundResolvedAt/RefundResolutionNote`).
- All math lives in `IPRO.Billing.PrepaidValue` — pure static functions, fully unit-tested.
- Schema via `StartupSchemaRepair` (never dotnet-ef), called from both apps.
- The reconcile job's Cancelled flip does not touch `PaidThroughAt` — verified, must stay true.
- ToS (`Views/Home/Terms.cshtml`) gains the matching refund clause (Catch #3, owner-approved).

## Rollout stages

- **A** — math core + schema + tests (no behaviour change).
- **B** — cancellation flow uses it: PaidThroughAt honored by gating, honest confirm dialog,
  Cancel SubscriptionChange rows with refund computation.
- **C** — SuperAdmin refund queue + ledger credit notes + ToS clause.
- **D** — downgrade Offer-Both / convert path. SHIPPED 2026-08-20 (branch
  `feat/downgrade-offer-both`). During implementation the "interlocked fixes" list was re-verified
  against the code and most of it was ALREADY CLOSED by `1909426` (2026-08-16, "fix all 16
  upgrade/downgrade defects once", pinned by BillingProrationMatrixTests): the proration unit
  mismatch, the false banner (plus the hourly reconcile sync), the tax gross-up skip, and the
  $0-invoice false settle. What Stage D actually added: the convert path itself
  (`ComputeConvertCredit` + supersede with a deferred `start_time`, offered on the Billing page to
  annual subscribers with an estimated free-months label), the shared `ResolvePaidThroughEndAsync`
  (PayPal-verified period end used by scheduling AND credit), the term-switch pending-change guard
  (UX-TERMSWITCH), and the stored-term completion buttons (UX-TERMAPPLY).

Owner's standing rule: every stage tests green BEFORE commit; nothing deploys except by his
merge.
