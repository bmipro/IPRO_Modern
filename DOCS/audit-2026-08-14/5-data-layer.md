# Data layer / schema integrity audit — 2026-08-14 (agent 5 of 5)

This is the auditor that ran out of account quota mid-run on 2026-08-14 and was relaunched; it
finished 2026-08-15. It is the most important of the five, because it looked at the layer the
Bob2Mot invoice loss actually lived in.

Its method was three-way: entity source, DDL source (migrations + both `Program.cs` + `WebsiteContentSchema.cs`
+ `EmailDeliverySchema.cs`), and the **live local MySQL schema**. Where it contradicts an earlier
assumption, the live schema is the tiebreaker.

Findings are reproduced below as written. Statuses added on 2026-08-15 after independent checks.

---

## Status summary

| ID | Severity | Finding | Status |
|---|---|---|---|
| F1 | CRITICAL | 28 migrations invisible to EF Core since 2026-07-11; `MigrateAsync()` is a no-op for them | **OPEN — do NOT fix blind.** Independently confirmed. See the warning below. |
| F2 | LOW | Model snapshot covers 28 of 85 tables | OPEN — do with F1 |
| F3 | HIGH | Neither app can boot against an empty database; the DR path is unreachable | OPEN |
| F4 | MEDIUM | `EnsureBillingRuleSchemaAsync` exists only in IPRO.Web (INVARIANTS rule 4) | OPEN |
| F5 | CRITICAL | Deleting one Client turns a scheduled one-to-one newsletter into a broadcast | OPEN |
| F6 | CRITICAL | Deleting a Client orphans 12 tables; one permanently kills a daily job for every agent | **PARTLY FIXED 2026-08-15** — the job-killing part is fixed; the other 11 tables are open |
| F7 | HIGH | Deleting a ClientCategory: same widening, plus a silent under-send on polls | OPEN |
| F8 | MEDIUM | Eraser map: 2 missing tables, 1 phantom | **FIXED 2026-08-15** (`d7c65c9`) |
| F9 | HIGH | `WebsiteTemplates.TemplateKey` unique in the model, absent from the database | OPEN |
| F10 | HIGH | `BillingRules.PackageName` is a de-facto natural key with no unique index | OPEN |
| F11 | MEDIUM | `AdminUsers.Username` has no unique index despite being the login identifier | OPEN |
| F12 | MEDIUM | Invoice-number generation is an unlocked read-modify-write on a global sequence | OPEN |
| F13 | LOW | `NewsLetterService.DeleteAsync` is unreferenced and would destroy delivery history | OPEN |
| F14 | MEDIUM | ~40 repair-created tables carry no FK constraints while the model declares Cascade | OPEN — this is the general form of F6 |

---

## Independent verification of F1 (2026-08-15)

The claim is large enough that it was re-checked by hand rather than taken on trust:

```
migration classes on disk .................. 43
of those, carrying [DbContext(typeof(...))]  15   (the ones with a .Designer.cs)
rows in __EFMigrationsHistory .............. 15
newest applied ............................. 20260710155952_AddNewsletterUnsubscribeTokens
newest on disk ............................. 20260726220000_AddWebsiteSidebarPositionOverride
```

The correlation is exact. EF Core's `MigrationsAssembly.Migrations` filters on the `[DbContext]`
attribute, so the 28 hand-written migrations added since 2026-07-11 have never been discovered and
`MigrateAsync()` has never run them.

**This is almost certainly why the ~1,200-line schema-repair layer exists.** Migrations stopped
working, the repairs quietly became the real schema authority, and nobody noticed because the
repairs kept the schema correct — the auditor confirmed **zero** entity-to-column drift across all
1,042 mapped scalar properties and all 85 tables. Production is fine today.

### Why F1 must not be fixed casually

Adding `[DbContext]` to the 28 classes makes EF discover them **and immediately try to apply them**
against databases that already contain every object they create. Several `Up()` bodies contain real
`CREATE TABLE` / `ADD COLUMN` DDL. The likely result of a naive fix is a crash-loop on both
production App Services at startup.

The safe sequence is:
1. Audit each of the 28 `Up()` bodies for idempotency against a populated database.
2. Either make each one idempotent, or insert the 28 rows into `__EFMigrationsHistory` directly so
   EF treats them as already applied, then add the attribute.
3. Rebuild the model snapshot (F2).
4. Add a startup assertion that the on-disk migration count and
   `GetPendingMigrationsAsync()` agree, so this can never go quiet again.

Do it as its own change, on a restored copy of production first, with the test suite green either
side. It is not urgent — nothing is broken today — and it is the single riskiest change in the
backlog.

---

## Findings as reported

### F1 — CRITICAL — 28 migrations have been invisible to EF Core since 2026-07-11

Every migration from `20260711010200_AddWebsiteTemplateManagement` onward carries `[Migration(...)]`
but **no `[DbContext(typeof(IPRODbContext))]`**, and has no `.Designer.cs` (which is what supplies
that attribute on the first 15). EF Core's `MigrationsAssembly.Migrations` filters on
`t.GetCustomAttribute<DbContextAttribute>()?.ContextType == contextType`, so all 28 are excluded
from discovery.

- Evidence: `src/IPRO.DataAccess/Migrations/20260711143000_RepairWebsiteTemplateColumns.cs:8`
  (`[Migration]` only), vs `20260710155952_AddNewsletterUnsubscribeTokens.Designer.cs`
  (`[DbContext]` + `[Migration]`).
- **Failure scenario:** anyone who adds a migration the same way gets a green deploy, `MigrateAsync()`
  returns instantly, and the DDL never runs — the schema-layer twin of the RUN_FROM_PACKAGE problem.
- **Fix:** see the sequencing warning above.

### F2 — LOW — model snapshot covers 28 of 85 tables

`IPRODbContextModelSnapshot.cs` has 28 `ToTable()` entries (same cutoff as F1). `dotnet ef migrations add`
today would scaffold `CreateTable` for ~57 tables that already exist. Regenerate once F1 lands.

### F3 — HIGH — neither app can boot against an empty database

The repair pass runs **before** `MigrateAsync()` (`Web/Program.cs:398` vs `:487`;
`Admin/Program.cs:180` vs `:268`) and unconditionally `ALTER`s tables that only migrations create.
`EnsureTableColumnAsync` (`Web/Program.cs:1806-1862`) checks `INFORMATION_SCHEMA.COLUMNS` — which
returns 0 for a *missing table* — then runs the ALTER and catches **only**
`MySqlErrorCode.DuplicateFieldName` (1060). A missing table raises 1146, which is unhandled.

```
ALTER TABLE `NoSuchTable_AuditProbe` ADD COLUMN `X` int NOT NULL DEFAULT 0;
ERROR 1146 (42S02): Table 'ipro_crm.nosuchtable_auditprobe' doesn't exist
```

- First crash point in Web: `Program.cs:645` → `EnsureBillingRuleSchemaAsync` → `:694`.
- First crash point in Admin: `Program.cs:297` → `ALTER TABLE WebsiteTemplates`.
- **Failure scenario:** a DR restore into an empty schema, or a new region/environment. Both App
  Services crash-loop before a single table is created. This also contradicts
  `FinancialLedgerSchemaGuard.cs:20-22`, whose "a fresh database gets its FKs recreated by
  MigrateAsync and immediately stripped here" sequence cannot currently be reached.
- **Fix:** extend the `when` filter to also swallow `MySqlErrorCode.NoSuchTable`, or gate each repair
  on `TableExistsAsync` (the pattern `EmailDeliverySchema.cs:73` and `AgentDataEraser.cs:316` already
  use correctly).

### F4 — MEDIUM — `EnsureBillingRuleSchemaAsync` exists only in IPRO.Web

Web calls it at `Program.cs:645`; Admin inlines only 7 of the 17 `BillingRules` ALTERs at
`Admin/Program.cs:300-306`. Ten columns Admin cannot self-heal: `MonthlyPrice, QuarterlyPrice,
AnnualPrice, SetupFee, PayPalMonthlyPlanId, PayPalAnnualPlanId, MaxClients, MaxNewsletters, IsActive,
CreatedAt`. All ten are created by applied migrations, so this is a resilience gap rather than a live
outage — but it breaches `DOCS/INVARIANTS.md:75-76`. Move the function into `IPRO.DataAccess` and call
it from both.

### F5 — CRITICAL — deleting one Client turns a scheduled newsletter into a broadcast

`FK_NewsLetterSends_Clients_ClientId` is **ON DELETE SET NULL**. The dispatcher then falls through:

```csharp
// src/IPRO.Email/NewsLetterDispatcher.cs:194-200
query = send.AudienceType switch
{
    NewsLetterAudienceType.AccountType when send.ClientCategoryId.HasValue => …,
    NewsLetterAudienceType.IndividualClient when send.ClientId.HasValue    => …,
    _ => query          // <-- ALL subscribers
};
```

- **Failure scenario:** agent schedules a newsletter to one client for Monday, deletes that client on
  Friday. MySQL nulls `ClientId`. Monday the dispatcher mails the agent's **entire** list.
  `AudienceLabel` still shows the old narrow audience, so the send history lies about what happened.
- **Fix:** make the fall-through fail closed — if `AudienceType != AllSubscribers` and the target id
  is null, mark the send `Failed` ("audience no longer exists") instead of widening. Same for
  `PollDispatcher.cs:182-187`.

### F6 — CRITICAL — deleting a Client orphans 12 tables

`ClientService.DeleteAsync` (`src/IPRO.Business/Services/ClientService.cs:35-42`) loads the client with
**no `Include`** and calls `Remove`, so EF's client-side cascade covers nothing. These tables have a
`ClientId` and no FK, so their rows survive: `ClientLifeEvents, PortalMessages, PortalDocuments,
PortalAppointmentRequests, DidYouKnowEmailQueueItems, ClientInvoices (+lines), RecurringInvoiceSchedules
(+lines), ECardRecipients, ELetterRecipients, PollRecipients, PollSends, TestimonialSubmissions`.

Ranked by harm:

1. **`ClientLifeEvents` → system-wide job outage.** `ClientLifeEventReminderJob.cs:45` runs
   `events.Select(e => e.Client.AgentUserId)` **outside** the per-row try/catch. An orphan gives
   `e.Client == null` → NRE before the loop. `LastCheckedAt` is never written, so the orphan keeps the
   oldest timestamp and stays in the `Take(500)` window forever — life-event *and* birthday reminders
   stop for **every agent in the system, permanently**, from one client deletion.
   **FIXED 2026-08-15:** the query now filters `e.Client != null` server-side.
2. **`PortalDocuments` → client PII left in blob storage indefinitely.** The client-delete path never
   calls `_blob.DeleteAsync`, and the rows holding the `BlobUrl` are orphaned, so nothing can find them.
3. **`RecurringInvoiceSchedules` → silent daily failure.** Caught by the per-schedule try; logs and
   retries forever.
4. `ClientInvoices` — the agent's own AR ledger keeps dangling `ClientId`s.

All four email dispatchers **do** guard, so a deleted client is never emailed.

- **Fix:** make `ClientService.DeleteAsync` delete through a declarative map the way `AgentDataEraser`
  does (ideally reusing it), or add the missing FKs.
- Live orphan scan across all 12 tables returns **0 rows** — latent, not realised.

### F7 — HIGH — deleting a ClientCategory widens newsletters and silently under-sends polls

`ClientsController.DeleteAccountType` (`:537-548`) removes the category with no check for scheduled
sends. `FK_NewsLetterSends_ClientCategoryId` is SET NULL → the F5 broadcast. `PollSends.ClientCategoryId`
has no FK → keeps pointing at a deleted category, `PollDispatcher.cs:184` matches nobody, and the poll
"sends" to zero recipients while reporting success. Three tables model the same relationship and all
three behave differently on delete.

### Delete paths confirmed correct (not findings)

- **`WebsitePage`** — `WebsitePagesController.cs:696-719` re-parents children before delete; blocks,
  leads and pageviews all covered by FKs in `WebsiteContentSchema.cs`.
- **`PollSurvey`** — `PollsController.cs:352-376` is Draft-only; a Draft has no sends or recipients.
- **`AgentWebsite`** — no user-facing delete path; only `AgentDataEraser`.

### F8 — MEDIUM — eraser map gaps **[FIXED 2026-08-15, `d7c65c9`]**

`WebsiteSpamAttempts` and `ClientClientCategory` were missing; `BannerSlides` names a table that has
never existed. **Impact correction:** both missing tables cascade from `AgentUsers`/`Clients` in the
live DB, so no rows were orphaned — the defect was that `PreviewAsync` undercounted and the erasure
report omitted tables that were in fact wiped. No other gaps: the 21 DbSets absent from the map are
all global/reference data and correctly excluded.

Fixed, plus the `AssertMapCoversAllTables` test the class header had promised since day one now
exists as `tests/IPRO.IntegrationTests/AgentDataEraserCoverageTests.cs`.

### F9 — HIGH — `WebsiteTemplates.TemplateKey`: unique in the model, absent from the database

`IPRODbContext.cs:565` declares `HasIndex(t => t.TemplateKey).IsUnique()`. The two migrations that
create it are both among the 28 invisible ones (F1), and no repair function creates it. Live DB:
`websitetemplates` has only `PRIMARY`. A duplicate key makes template resolution non-deterministic.
Fix with `EnsureUniqueIndexAsync` in both `Program.cs`.

### F10 — HIGH — `BillingRules.PackageName` is a natural key with no unique index

Seven call sites resolve a package by name, including `PackageEntitlementSeeder.cs:52` (runs at every
startup of both apps) and `PackageEntitlementService.cs:203-205` (maps legacy package numbers to
"IPro Silver"/"Gold"/"Platinum"). `PackagesController.Create:88` has no duplicate-name guard. A second
"IPro Gold" means agents may resolve to the wrong row — wrong features, wrong price, wrong PayPal plan.

### F11 — MEDIUM — `AdminUsers.Username` has no unique index

`Admin/Program.cs:575-586` creates the table with `PRIMARY KEY (Id)` only. All three auth paths use
`FirstOrDefaultAsync(u => u.Username == …)`. The bootstrap insert at `:608-632` is a check-then-insert
**not** wrapped in `SeedGuard`, contrary to `DOCS/INVARIANTS.md:77`. A duplicate username makes the
second account unable to log in and audit attribution ambiguous.

### F12 — MEDIUM — invoice-number generation is an unlocked read-modify-write

`PayPalBillingService.cs:1615-1632` derives `IPRO-{yyyy}-NNNNNN` from committed rows with no lock.
The unique index turns a race into a duplicate-key **exception** rather than a duplicate number — but
that exception lands in the PayPal path *after* the money is captured (`:1569`), and the invoice row
is never written. Same end state as the 2026-08-14 incident, different route. Mitigating: only
IPRO.Web runs a Hangfire server, so the exposure is a webhook racing `SubscriptionBillingJob`, or Web
scaled past one instance. Fix: `GET_LOCK` around generate+insert, or a counter row with
`SELECT … FOR UPDATE`; and catch the duplicate at `:1569` and retry rather than losing the invoice.

### F13 — LOW — `NewsLetterService.DeleteAsync` is unreferenced and dangerous

`src/IPRO.Business/Services/NewsLetterService.cs:71-74`, no callers. If wired up,
`FK_NewsLetterRecipients_NewsLetters_NewsLetterId ON DELETE CASCADE` would erase every recipient row —
the open/click/bounce record *and* the `UnsubscribeToken` for mail already delivered, breaking
unsubscribe links sitting in inboxes (CASL/CAN-SPAM exposure). Delete the method or soft-delete.

### F14 — MEDIUM — ~40 repair-created tables have no FK constraints while the model declares Cascade

E.g. `IPRODbContext.cs:156-165` declares Cascade on `AgentDomain → AgentUser` and `→ AgentWebsite`;
the live DB has no FK on `agentdomains` at all. Same for all Poll, Portal, ECard/ELetter, ClientInvoice,
Form and SupportTicket tables. The general form of F6: the model's delete semantics are enforced only
for the 36 FK-bearing tables, and only when EF has loaded the dependents. Pick one authority — add the
constraints in the repair `CREATE TABLE` bodies (as `WebsiteContentSchema.cs` already does), or add a
startup assertion that fails loudly when the model declares a relationship the schema doesn't enforce.

---

## The auditor's overall assessment

> The entity-to-column layer is, surprisingly, **clean** — all 1,042 mapped scalar properties have a
> live column, all 85 DbSet tables exist, and the two apps' repair sets differ in exactly one
> non-cosmetic place. The divergence that remains is one level down, and it is structural rather than
> incremental: **EF migrations silently stopped being applied on 2026-07-11**, which is very likely the
> unexamined reason the 30-function hand-written repair layer had to grow in the first place. The
> consequence is that the schema now has two authorities that disagree about who owns referential
> integrity — 36 tables carry real FKs, ~40 carry none, while `OnModelCreating` cheerfully declares
> Cascade for both groups. The 2026-08-14 invoice loss was the *first* of these two failure modes (a DB
> cascade the code didn't expect); the ones still live are mostly the *second* (a code-expected cascade
> the DB doesn't perform), plus one genuinely dangerous SET NULL that widens a scheduled newsletter's
> audience instead of narrowing it. Nothing here has materialised yet — the live orphan scan is clean
> across all twelve at-risk tables — but `ClientService.DeleteAsync` is one click away from a permanent
> system-wide reminder-job outage, and no environment can currently be rebuilt from an empty database.
> I would sequence the fixes as: F3 (bootstrap, blocks DR), F5/F6 (client deletion), F1 (restore
> migration discovery, which then unblocks F9), then the constraint gaps.
