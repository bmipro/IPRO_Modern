# SuperAdmin App Audit
[Agent 3 of 5. Verbatim; pending my verification.]

## 1. CRITICAL — Ledger cascade guard runs BEFORE MigrateAsync → no-op on fresh DB
`IPRO.Admin/Program.cs:180 → :305 vs :268`; `IPRO.Web/Program.cs:398 → :709 vs :487`
FinancialLedgerSchemaGuard.EnsureAsync is called inside EnsureWebsiteTemplateSchemaAsync (FIRST startup statement); MigrateAsync runs ~90 lines later. On a DB with no __EFMigrationsHistory (local rebuild, DR restore, new staging), guard queries information_schema before ledger tables exist, drops nothing, THEN MigrateAsync applies InitialCreate creating FK_Billings_AgentUsers CASCADE + FK_Invoices_Billings CASCADE. First run serves with the 2026-08-14 cascade LIVE; self-heals only on 2nd restart. Reopens on prod any time a future migration recreates a ledger FK.
FIX: move FinancialLedgerSchemaGuard.EnsureAsync(db) to immediately AFTER await db.Database.MigrateAsync() in both Program.cs. [DIRECTLY CONCERNS TODAY'S FIX — verify first.]

## 2. HIGH — Saving a package price diverges DB price from PayPal plan; first invoice records wrong amount+tax
`PackagesController.cs:114-121`; `PayPalBillingService.cs:1083,1089,876-880`
Edit writes MonthlyPrice/AnnualPrice, touches nothing at PayPal; re-sync is separate manual button. Billing.Amount + pending invoice from DB price (:1083); charge from PayPal plan frozen at creation (:1089). Owner raises Gold $60→$75 without sync: PayPal charges $60+HST, IPRO issues $75+HST invoice, HandleSubscriptionPaymentCompletedWebhookAsync sets IsPaid=true (:877) with NO amount comparison. Books record $75 revenue + HST never collected = CRA remittance error. Only first invoice wrong (renewals de-tax real amount) → harder to spot. [OVERLAPS billing #9.]
FIX: store plan-created price; red "plan out of date" banner on Edit; refuse checkout or reconcile pending invoice to webhook amount when differ.

## 3. HIGH — Last SuperAdmin can demote themselves; no in-app recovery
`AdminUsersController.cs:97,111-114`; `Program.cs:607-610`
Edit guards only `id==CurrentAdminId && !isActive`; role has no self-check, no "last SuperAdmin" floor. Sole SuperAdmin sets own role=Support or deactivates the other then demotes self → zero SuperAdmins. Packages/AdminUsers/TaxRates/agent Delete/ResetPassword all [Authorize SuperAdmin] permanently unreachable. Bootstrap re-seed fires only when AdminUsers table empty (`if AnyAsync return`). Locked out until hand-edit MySQL.
FIX: block role change when id==CurrentAdminId; reject Edit leaving zero active SuperAdmins; optionally bootstrap repairs role/active not just insert.

## 4. HIGH — One-time invoice restoration runs on EVERY database, fabricating $67.80 invoice on fresh/local/DR
`FinancialLedgerSchemaGuard.cs:97-127`
RestoreInvoice000008Async guarded only by whether THAT db has the row. Fresh DB: 1st boot throws (no table, swallowed :47-50); 2nd boot inserts Billings + invoice IPRO-2026-000008 $67.80 against AgentUserId 16 (nonexistent there). Every local/restored/new env carries a phantom paid invoice in Revenue chart + tax remittance + CSV. Self-resurrecting if shredded. [THIS IS MY CODE FROM TODAY.]
FIX: restoration served its purpose on prod — delete RestoreInvoice000008Async + call, or gate on one-shot marker (config flag / SchemaRepairHistory row) not on presence of the row it inserts.

## 5. MEDIUM — AgentDataEraser.Map incomplete: 2 missing tables, 1 dead entry, promised test absent
`AgentDataEraser.cs:32-123`
Missing from Map: `WebsiteSpamAttempts` (AgentUserId, IPRODbContext.cs:21) and `ClientClientCategory` (M2M join, :307-308). Both swept by CASCADE today so no orphans, but ErasurePreview + report UNDERCOUNT. Dead entry: `("BannerSlides",...)` at :104 — table never created anywhere (only entity exists), TableExistsAsync silently skips → map unverified. Header :26-27 promises AssertMapCoversAllTables test that does not exist. Line :31 still says "no FKs exist" (the false claim the header corrects).
FIX: add both tables, delete BannerSlides entry, fix :31 comment, add reflection-based AssertMapCoversAllTables test.

## 6. MEDIUM — Deleting an agent never unbinds their custom domain from Azure
`AgentsController.cs:279-387`; `IAzureDomainAutomationService.cs:7`
RemoveDomainAsync has one call site (WebsiteController.cs:435, self-service). Admin Delete cancels PayPal, deletes blobs, erases (drops AgentDomains rows) but never unbinds Azure hostname+managed cert. Binding consumes hostname slot, unreconcilable from DB, cert auto-renew keeps retrying. Code already snapshots domainName at :331 but never uses it.
FIX: read AgentDomains/CustomDomain before erasure, call RemoveDomainAsync per host, best-effort logged like the blob loop.

## 7. MEDIUM — Admin role/active live only in 4-hour cookie; revocation doesn't take effect
`Program.cs:75-91`; `AdminController.cs:56-69`
SignInAdminAsync bakes Role claim, ExpireTimeSpan 4h, no OnValidatePrincipal. IsActive checked only at login. Compromised/dismissed SuperAdmin keeps full authority up to 4h; no "sign out everywhere"; makes #3 worse (self-demotion looks like no-op until expiry).
FIX: OnValidatePrincipal reloads AdminUser (throttled), rejects when IsActive false, refreshes Role claim.

## 8. MEDIUM — Revenue chart buckets by UTC month while ledger prints agent-local dates
`ReportsController.cs:48-53,131-135`; `Revenue.cshtml:138`
Confirms good parts: chart/PaidTotal/TaxByRegion/CSV all from one query (LoadLedgerAsync); deleted agents correctly included. But GroupBy + filter raw UTC (:49,:135) while row renders AgentLocalTime.FromUtc (:138). Invoice at 2026-08-01 02:00 UTC for Vancouver agent prints Jul 31 in row but sits in Aug bar + Aug filter. Eye-reconciled month-end totals won't match.
FIX: one clock — simplest render ledger date in UTC with "UTC" header.

## 9. MEDIUM — SyncPayPalPlans blanks live plan ID when price zero, leaks orphan plans on partial failure
`PayPalBillingService.cs:705-715`
Both fields overwritten unconditionally incl string.Empty. (A) admin zeroes AnnualPrice, syncs to refresh monthly → PayPalAnnualPlanId wiped, not restored by re-setting price. (B) monthly succeeds, annual throws → catch returns failure, SaveChanges never runs, real billable PayPal plan orphaned. [OVERLAPS billing #2/#9.]
FIX: assign plan ID only when new one created; persist each as returned.

## 10. MEDIUM — RebuildResources hard-deletes live agent pages+blocks; any admin (not just SuperAdmin) can run it
`AgentsController.cs:132-182 (esp :174-176)`
Class is [Authorize AdminAccess]=RequireAuthenticatedUser; action adds no SuperAdmin gate (unlike Delete/ResetPassword/ErasurePreview). RemoveRange on WebsiteContentBlocks + WebsitePages then Save. Support admin clicks on real customer → every block agent wrote under Resources destroyed, regenerated from starter. Confirm text says "articles kept" — says nothing about block content deleted. Audit-logged, no undo.
FIX: add [Authorize SuperAdmin]; confirm text states customised Resources content discarded+rebuilt.

## 11. LOW — Agent password-reset confirm states wrong password
`Agents/Details.cshtml:17`; `AgentsController.cs:395,483`
Dialog says "Reset to their last name"; action calls BuildTemporaryPassword→GenerateToken(12), random value only in TempData flash. Admin tells agent "last name", navigates away, flash gone → agent locked out, needs 2nd reset.
FIX: correct confirm text; show generated password on a page surviving refresh.

## 12. LOW — Tax-rate edits audit-logged without recording what changed
`TaxRatesController.cs:56` — logs only "Bulk-updated N province rate(s)". Every other money action names old/new. ON saved 1.3% vs 13% → under-collects HST, log can't reconstruct. FIX: build detail from diffs like PackagesController.

## Overall (agent's words)
Deletion path genuinely good (files-before-rows, cancel-or-refuse, blob logging, pre/post retention+shortfall, preview/erase one predicate). Revenue screen correctly survives agents. Serious residual is the STARTUP ORDERING that arms it: guard runs before MigrateAsync in both apps → no-op on exactly the DBs that need it, comment claims opposite → 2026-08-14 cascade live on any fresh/restored first run. Plus two money-integrity gaps (price/plan divergence with wrong first invoice; decommissioned restoration still firing). Lockout risk real+cheap. Fix 1–4 before next deletion or price change; 5–6 hygiene; rest containment.
