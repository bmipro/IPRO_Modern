# Billing subsystem audit — IPRO_Modern (2026-08-14)
[Agent 1 of 5 — billing/money. Verbatim report; findings pending my verification before fixing.]

## CRITICAL

### 1. A time-limited 100%-off promo grants the package free *forever*, with no PayPal subscription
`src/IPRO.Billing/PayPalBillingService.cs:1118`

The free-activation short-circuit checks only the *amounts*, while the decision that produced those amounts (`isFullyComped`, line 132) additionally requires `promo.RecurringDurationCycles == null`:

```csharp
if (changeType == SubscriptionChangeType.Subscribe && promotionCodeId.HasValue && billing.Amount <= 0 && setupFee <= 0)
```

Failure path: admin creates a perfectly ordinary promo — "first 3 months free, setup fee waived" (`RecurringDiscountType=PercentOff 100`, `RecurringDurationCycles=3`, setup fee 100% off or the package's `SetupFeeWaived` already on). Admin validation accepts it (`PromotionCodesController.cs:77` only rejects permanent 100% codes). At subscribe: `overrideAmount = 0` (line 129), `isFullyComped = false` (duration is not null), so `GetOrCreatePromoPlanIdAsync` correctly builds a real 3-cycle-then-full-price plan and `billing.PayPalPlanId` is non-empty. But line 1118 fires *before* the subscription-creation block at 1145 → `ActivateSubscriptionBillingAsync` → `Status = Active`, invoice marked paid, "no cost" message. No PayPal subscription is ever created, and nothing bills anyone. The agent holds the package permanently for $0.

Fix direction: gate on the same condition that produced the empty plan id — `string.IsNullOrWhiteSpace(billing.PayPalPlanId)` (or re-check `RecurringDurationCycles == null`), not on the amounts.

### 2. A missing PayPal plan id silently degrades a subscription into a one-time order → package forever for one payment
`PayPalBillingService.cs:1145`, falling through to `1191-1194`, completed at `302-308`

```csharp
if (startsSubscription && !string.IsNullOrWhiteSpace(billing.PayPalPlanId))   // 1145
...
order = await CreatePayPalOrderAsync(invoice, ...);                            // 1194
```

When the second conjunct is false the code falls to `CreatePayPalOrderAsync` — a single capture. `CapturePaymentAsync` then sets `billing.Status = Active` (line 302) with an empty `PayPalSubscriptionId`. This is exactly the C-2 defect the comments at 417-425 and 1127-1141 claim to have closed; this door is still open. Two concrete triggers, neither requiring an attacker:

- **A package whose plans were never synced.** `SyncPayPalPlansAsync` is a manual admin button (`PackagesController.cs:139`) and packages are created with empty plan ids. Every subscriber to a new, unsynced package pays once and keeps the package indefinitely.
- **Annual on a package with no annual price.** `SyncPayPalPlansAsync:709-711` deliberately writes `PayPalAnnualPlanId = string.Empty` when `AnnualPrice <= 0`. The Billing view hides the Annually button (`Views/Billing/Index.cshtml:229`) and Register guards the radio, but the server never validates — a posted `period=Annually` yields recurring $0 + setup fee → a one-time order for the setup fee → permanent Active access.

Fix direction: refuse the change when `startsSubscription && plan id is empty` (fail loudly rather than fall back), and delete the order path for subscription changes entirely.

## HIGH

### 3. Proration is computed against a `StartDate` that is never advanced on renewal — every upgrade after month 1 undercharges
`PayPalBillingService.cs:189-193` reading a value only written at `306`, `359`, `968`

`billing.StartDate` is set on *activation* only; the recurring-sale webhook advances `NextBillingDate` but leaves `StartDate` untouched (`965-972`, the assignment is inside `if (billing.Status != Active)`). `CalculateRemainingFraction(now, StartDate, NextBillingDate)` therefore uses the whole elapsed lifetime as the denominator.

Scenario: Silver $40 → Gold $60, monthly, activated Aug 10. First renewal Sep 10 → `NextBillingDate = Oct 10`, `StartDate` still Aug 10. Agent upgrades Sep 11: span = 61 days, remaining = 29 → fraction 0.48 instead of ~0.97. `amountDue` = $9.60 instead of $19.33. After twelve renewals the fraction is ~1/13. The QA runs (TODO 402/403) never caught this because each upgrade resets `StartDate`; only a renewed-but-not-upgraded subscription exhibits it.

Fix direction: advance `StartDate` on every settled recurring sale, or compute the fraction from `NextBillingDate` minus one period rather than from `StartDate`.

### 4. A recurring-sale webhook re-activates a Cancelled subscription, and nothing ever expires an Active row
`PayPalBillingService.cs:965-969`

```csharp
if (billing.Status != BillingStatus.Active) { billing.Status = BillingStatus.Active; billing.StartDate = DateTime.UtcNow; }
```

No status guard: `Cancelled`, `Expired` and `Failed` rows are all resurrected. Concrete path — the renewal sale fires Aug 1, verification call blips (`VerifyWebhookSignatureAsync:2617` returns false → controller returns `Unauthorized`, `BillingController.cs:243`), PayPal queues a retry; the agent cancels Aug 2 (row → Cancelled, PayPal subscription genuinely stopped); PayPal's retry lands Aug 3 and flips the row back to `Active` with `NextBillingDate` a month out.

That state is permanent: `BillingStatus.Expired` is written *only* by the EXPIRED webhook (line 678), nothing sweeps Active rows whose `NextBillingDate` has passed, and `IsAccessGatedAsync` (`PackageEntitlementService.cs:62-66`) grants full access on the mere existence of an Active row. `ReconcileDuplicateActiveSubscriptionsAsync` only fires for agents with *two* Active rows. The same gap means any lost CANCELLED/SUSPENDED webhook, or a buyer cancelling directly inside PayPal, leaves the agent with free access indefinitely.

Fix direction: refuse to activate a row that was cancelled/expired; add an hourly sweep that queries PayPal for every Active row whose `NextBillingDate` is more than a grace period in the past.

### 5. Invoices are marked PAID and receipts emailed on *approval*, before any money is confirmed
`PayPalBillingService.cs:233-239` + `2673-2675`, `792-814`, `333-337`, enabled by `2144`

`IsPayPalSubscriptionApproved` accepts `"APPROVED"` — PayPal's post-buyer-consent, pre-payment state — and `ActivateSubscriptionBillingAsync` unconditionally does `invoice.IsPaid = true` and sends the "Your payment has been received" receipt (`379-382`). The `BILLING.SUBSCRIPTION.ACTIVATED` webhook (`810-813`) does the same. The plans are created with `setup_fee_failure_action = "CONTINUE"` (line 2144), which makes PayPal activate a subscription *despite* a failed setup-fee charge. So a declined setup fee still produces: Active package, invoice stamped paid, a paid-invoice email, and a permanently wrong ledger.

Contradiction noted: `CancelPayPalSubscriptionAsync:2444-2449` explicitly reasons that APPROVED is *not* a settled state, while the activation path treats it as payment.

Fix direction: activate access on APPROVED/ACTIVATED if you must, but only set `IsPaid` (and send a receipt) from `PAYMENT.SALE.COMPLETED`.

### 6. `Subscribe` accepts any `BillingPeriod` with no price/plan validation
`Web/Controllers/BillingController.cs:95` → `PayPalBillingService.cs:87-214`, `2653-2664`

`period` is model-bound from the POST body and never validated. `GetAmount(package, Quarterly)` returns `QuarterlyPrice`, which the admin UI hard-forces to 0 (`Views/Packages/Edit.cshtml:51`), while `GetPayPalPlanId(Quarterly)` returns the **monthly** plan id (line 2663). Posting `period=Quarterly` for any package yields: `Billing.Amount = 0`, a $0 signup invoice, `NextBillingDate = +3 months`, and a real PayPal subscription on the **monthly** plan — priced net, with no tax gross-up (see #7), since a $0 invoice has `TaxRate = 0`. The $0 `Billing.Amount` then poisons every later proration (`GetAmount` at 191-192) and the de-tax snap (947).

Combined with #1, a `Quarterly` post plus any valid promo code satisfies `billing.Amount <= 0 && setupFee <= 0` → free package on *any* tier, including the seeded 0-priced "Broker Package" (`PackageEntitlementSeeder.cs:47`), which is `IsActive` and passes `GetPackagesAsync`'s filter.

Fix direction: reject any (package, period) whose `GetAmount` is `<= 0` or whose plan id is empty, at the top of `CreateSubscriptionAsync`.

## MEDIUM

### 7. The tax gross-up is skipped whenever the first invoice's subtotal is zero — PayPal then bills net forever
`PayPalBillingService.cs:1934-1942` reading `invoice.TaxRate` produced at `1452-1469`

`CreateInvoiceAsync` emits a `$0 subscription adjustment` line when both amounts are zero, so `CalculateTaxAsync(userId, 0)` returns `(0, 0, "No tax")` (line 1557) and `invoice.TaxRate = 0`. `CreatePayPalSubscriptionAsync` gates the entire tax-inclusive machinery on `if (invoice.TaxRate > 0)` — so the subscription is created with no `taxes` block and no `billing_cycles` override, and PayPal charges the plan's **net** price for the life of the subscription. Reachable via #6, via a duration-limited promo whose first cycles are $0, and via a zero-due upgrade (the path the comment at 1127-1141 deliberately routes through here). The agent's later invoices are then back-computed from the net charge (937-950), so the books balance while the tax is simply never collected.

Fix direction: resolve the tax rate from the agent, not from the invoice, when building the subscription payload.

### 8. A webhook retry more than 6 hours late mints a duplicate paid invoice and a second receipt
`PayPalBillingService.cs:903-906`

The absorb rule matches only on `IssuedAt > UtcNow.AddHours(-6)`; there is no dedupe on transaction id, and the only unique index on `Invoice` is `InvoiceNumber` (`IPRODbContext.cs:355`). PayPal retries a non-2xx delivery over ~3 days, and this endpoint returns `Unauthorized` on any transient verification failure (`BillingController.cs:243`). First delivery creates and settles the invoice but the response is lost or the app restarts; the retry arrives at hour 7+, finds no unpaid invoice, misses the 6-hour window, and creates a second numbered invoice for the same sale plus a second "invoice paid" email.

Fix direction: before creating, check whether any invoice for this billing already carries `transactionId`; ideally add a unique index on `(BillingId, PayPalTransactionId)`.

### 9. Editing a package's price does not invalidate its PayPal plan — charged and invoiced diverge silently
`Admin/Controllers/PackagesController.cs:100-121` (`ApplyRuleFields` at ~292)

`Edit` writes `MonthlyPrice`/`AnnualPrice` and leaves `PayPalMonthlyPlanId`/`PayPalAnnualPlanId` pointing at the old plan; re-syncing is a separate manual button. After a price change, a new subscriber's invoice is built from the **new** price (`BeginPaidChangeAsync:1083`, `1099`) while `BuildTaxInclusiveCycleOverridesAsync:2044` reads the **old** net back from PayPal and charges that. Promo plan cache: `PromotionCodesController` clears them when the *promo* changes (139-143) but not when the underlying package price changes.

Fix direction: blank the plan ids (or auto-resync) whenever a price field changes, and refuse to start a subscription whose plan net does not match the package price.

### 10. A setup-fee-only promo code ignores its package restriction
`PayPalBillingService.cs:2172`

```csharp
if (promo.RecurringDiscountType != PromoDiscountType.None && promo.RestrictedBillingRuleId != billingRuleId) return null;
```

`RestrictedBillingRuleId` is enforced *only* for recurring discounts (admin validation likewise, `PromotionCodesController.cs:67`). A code created as "100% off setup fee — Silver only" waives Platinum's $400 setup fee for anyone who types it.

Fix direction: apply `RestrictedBillingRuleId` whenever it is non-null, regardless of discount type.

### 11. Failed-payment webhooks mint real numbered invoices; the next success settles the oldest one regardless of amount
`PayPalBillingService.cs:837-858` and `873-881`

`HandleSubscriptionPaymentFailedWebhookAsync` creates a full `Invoice` (consuming an invoice number) for a charge that never happened, on every delivery — and PayPal retries up to `payment_failure_threshold = 3` (line 2145), so one bad cycle yields up to three phantom unpaid invoices. Each drives dunning email (`NotifyBillingIssuesAsync:587-603`) and the `GetBillingIssueAsync` banner. When a payment finally succeeds, `pendingInvoice` picks `.OrderBy(i => i.IssuedAt).FirstOrDefault()` — the *oldest* unpaid invoice — and marks it paid with the new transaction id and **no amount comparison**.

Fix direction: record failures as a status/log on the existing invoice rather than issuing a new numbered one, and match a sale to an unpaid invoice by amount, not by age.

## LOW

### 12. Payment capture accepts any 2xx; plus two soft edges around promo codes
`PayPalBillingService.cs:2535-2551`

`CapturePayPalOrderAsync` returns `response.IsSuccessStatusCode` without inspecting the capture `status`, so a `PENDING` capture (review hold, e-check) is treated as paid. Related: `Web/Controllers/AccountController.cs:497-499` exposes `ValidatePromoCode` anonymously with `[IgnoreAntiforgeryToken]` and no rate limiting (promo-code enumeration oracle); `AccountController.cs:653` lets any signed-in agent write an arbitrary string into their own `agent.PromotionCode`, the exact field `CreateSubscriptionAsync:109` trusts. Also `CapturePaymentAsync`'s order branch (302-313) never calls `RecordPromoRedemptionAsync`, so promos redeemed through that path never increment `RedemptionCount` and can be reused.

---

## Overall assessment (agent's words)

This subsystem has clearly been through several rounds of hard-won, incident-driven fixes; the supersede/cancel contract, tax-inclusive gross-up, and absorb rule are carefully thought through for the paths they were written for. The weakness is systematic: correctness is enforced at the specific call sites that once failed, not by invariants. Dominant theme: **an Active Billing row is treated as unconditional proof of a paying customer** while at least three paths can produce one with no PayPal subscription behind it (#1, #2, #4), and nothing ever revalidates that belief. Second theme: **money-shaped inputs are never validated at the boundary** (period, plan id, plan price, promo restrictions). Two structural changes retire most of the list: (a) a single guarded entry point that refuses any subscription change lacking a live plan id and a positive price, and (b) an hourly reconciliation job that asks PayPal the state of every Active subscription.
