# Ultra-audit consolidated triage — 2026-08-14
5 parallel Opus auditors. 4 completed, 1 (data-layer/schema) hit account usage limit
mid-run (resets 2pm Toronto) — MUST RE-RUN after reset.

Reports on disk: 1-billing.md, 2-webapp.md, 3-admin.md, 4-jobs-email.md
Status legend: [ ] unverified  [V] I verified against code  [FIXED] done+test  [SKIP] not a real issue

## Cross-cutting theme (all 4 agents independently)
Fixes were applied at the specific call site that once failed, never as an INVARIANT.
Two structural fixes retire most of the billing list:
  (A) one guarded subscription entry point: refuse any change lacking a live PayPal
      plan id AND a positive price.
  (B) hourly reconciliation job: ask PayPal the true state of every Active row.
Email theme: same disease — claim-before-send / per-item isolation / consent gate each
exist only where an incident forced them, missing from identical neighbours.

## FIX ORDER (my proposed ranking, criticals first)

### Tier 0 — security, fix same-day
- [FIXED 5013486] WEB C-1: unauth client-portal takeover via omitted `token`. FindByInviteTokenAsync
      rejects null/empty + requires non-null unexpired expiry; expiry cleared on activate+revoke.
      6 controller-level tests (ClientPortalTokenSecurityTests). Pushed + deploying.
- [ ] WEB H-2 / BILLING #1+#12: anonymous promo-code oracle → 100%-off free Platinum.
      AccountController.cs:497-538. Auth+ratelimit+uniform message.

### Tier 1 — money integrity (PayPal still sandbox — window to fix before real money)
- [FIXED dcd89c5] ADMIN #1: guard moved to AFTER MigrateAsync in both Program.cs. Test:
      Guard_never_fabricates_any_ledger_row. Pushed + deploying.
- [FIXED dcd89c5] ADMIN #4: RestoreInvoice000008Async + call deleted entirely (000008 already
      live in prod; job done). Locked by the same no-fabricate test.
- [ ] BILLING #1: time-limited 100%-off promo → free forever, no PayPal sub.
      PayPalBillingService.cs:1118 gate on amounts not on empty plan id.
- [ ] BILLING #2: missing plan id degrades subscription→one-time order→pkg forever for 1 pay.
      :1145 fall-through. Refuse when startsSubscription && plan id empty.
- [ ] BILLING #6: Subscribe accepts any BillingPeriod, no price/plan validation (Quarterly=$0,
      monthly plan id). Reject (pkg,period) with amount<=0 or empty plan id at entry.
- [ ] BILLING #5: invoice marked PAID + receipt on APPROVED, before money. Only set IsPaid
      from PAYMENT.SALE.COMPLETED.
- [ ] BILLING #3: proration uses StartDate never advanced on renewal → upgrades undercharge
      after month 1. Advance StartDate on settled recurring sale.
- [ ] BILLING #4 + ADMIN(reconcile): recurring-sale webhook re-activates Cancelled row; nothing
      expires Active rows. Status guard + hourly PayPal reconcile sweep = structural fix (B).
- [ ] ADMIN #2 / BILLING #9: package price edit diverges DB price from PayPal plan; FIRST
      invoice records wrong amount+tax (CRA). Store plan-created price, banner, refuse/reconcile.
- [ ] BILLING #8: >6h-late webhook retry mints duplicate paid invoice. Dedupe on txn id +
      unique index (BillingId, PayPalTransactionId).
- [ ] BILLING #11: failed-payment webhook mints phantom numbered invoices; success settles
      OLDEST regardless of amount. Log on existing invoice; match by amount.
- [ ] BILLING #10: setup-fee-only promo ignores RestrictedBillingRuleId. Apply when non-null.
- [ ] BILLING #7: tax gross-up skipped when first invoice subtotal 0 → PayPal bills net forever.
      Resolve tax rate from agent not invoice.
- [ ] BILLING #12: capture accepts any 2xx (PENDING treated paid); order-branch never records
      promo redemption (reusable).

### Tier 2 — account/lockout + consent
- [ ] ADMIN #3: last SuperAdmin can demote self, no in-app recovery. Floor check.
- [ ] ADMIN #7: admin role/active only in 4h cookie; revocation ineffective. OnValidatePrincipal.
- [ ] JOBS #1: drip campaigns bypass consent end-to-end (EmailChannel has no drip member).
      Add channel + gate + cancel-in-SuppressAll + spamreport case. (Likely shared-IP spam source.)
- [ ] JOBS #4: spam/unsubscribe don't set EmailOptOutAt outside newsletter. One suppression call.
- [ ] JOBS #2: three reminder jobs duplicate on Hangfire retry (send-then-persist-once).
- [ ] JOBS #3: send stuck "Sending" forever + manual reset double-sends. Incremental persist + sweep.

### Tier 3 — entitlement/consistency (mostly latent; patterns codebase already gets right elsewhere)
- [ ] WEB M-1: Client Portal (Platinum feature) enforced in one action only.
- [ ] WEB M-2: SyncPrimaryDomainAsync re-parents AgentDomains row w/o ownership filter (TOCTOU).
- [ ] WEB M-3: storage quota bypassed on UploadImage + UploadPortalDocument.
- [ ] WEB M-4: replayable captcha + unrated SubmitCustomForm + anon mail-to-arbitrary (DYK).
- [ ] WEB M-5: raw X-Forwarded-For re-read, bypasses validated pipeline (visitor-count inflation).
- [ ] ADMIN #5: AgentDataEraser.Map missing WebsiteSpamAttempts + ClientClientCategory; dead
      BannerSlides entry; :31 stale "no FKs" comment; add AssertMapCoversAllTables test.
- [ ] ADMIN #6: agent delete never unbinds Azure custom domain (RemoveDomainAsync uncalled).
- [ ] ADMIN #9: SyncPayPalPlans blanks live plan id when price 0; orphan plan on partial fail.
- [ ] ADMIN #10: RebuildResources hard-deletes live agent content; any admin (not SuperAdmin).
- [ ] JOBS #5-11, WEB L-1/L-2, ADMIN #8/#11/#12: mediums/lows — see per-agent reports.

### STILL PENDING
- [ ] DATA-LAYER/SCHEMA agent (#5) — re-run after 2pm reset. Was auditing: entity↔schema drift,
      repair drift between apps, delete-behavior catalog, eraser coverage, constraint gaps.
      This is the most important remaining pass given the cascade incident.
