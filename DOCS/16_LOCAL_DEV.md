# Local Development Environment

Stood up 2026-08-07 (review step 2). Until this existed, **no change had ever been run outside
Azure** — every fix was tested on production. The independent review of 2026-08-07 traced all three
of that week's new-breakage findings to exactly that gap.

## What runs where

| Piece | Location / port | Notes |
|---|---|---|
| MySQL 8.0.44 (portable ZIP, no service) | `C:\Users\admin\ipro-local\`, `127.0.0.1:3306` | Prod is 8.0.21 — same major version. Bound to loopback only. |
| Database | `ipro_crm`, user `ipro_local` / `ipro-local-dev-2026` | Local-only credentials; root has empty password but is loopback-bound. |
| Azurite (blob emulator) | `127.0.0.1:10000` | Preinstalled global npm package. Data under `ipro-local\azurite`. |
| IPRO.Web | `http://localhost:5100` | |
| IPRO.Admin | `http://localhost:5200` | |

Start everything: `powershell -ExecutionPolicy Bypass -File ops\Start-LocalEnv.ps1`, then the two
`dotnet run` commands it prints.

## Config isolation — why a local run cannot touch real services

The committed `appsettings.json` is a placeholder template (real secrets live only in Azure App
Service configuration). The local overrides live in `appsettings.Development.json` in each app —
**gitignored**, so they never reach the repo. They set:

- `DefaultConnection` → local MySQL
- `Azure:StorageConnectionString` → `UseDevelopmentStorage=true` (Azurite)
- SendGrid / PayPal / Anthropic / Google Maps keys → **empty string**. Verified behaviour: empty is
  treated as *not configured* (`SendGridEmailService.IsConfigured`, `HasPayPalSettings()`), so email
  and billing paths no-op gracefully instead of calling out with garbage credentials.
- `App:PlatformDomains` → `localhost` and `127.0.0.1` prepended, otherwise the middleware treats
  localhost as an agent's custom domain and routes everything to the public-site slug lookup.
- `AzureDomainAutomation:Enabled` → false.

To recreate the file, copy `appsettings.json`, apply the five changes above, and set
`Admin:Username`/`Admin:Password` + `AdminPreview:SharedSecret` to any local-only values (keep them
identical across the two apps).

## How the schema was created (important — read before recreating)

1. `dotnet ef database update` applied the **15 discoverable migrations** (July 1–10). The
   connection string comes from the `ConnectionStrings__DefaultConnection` **environment variable**,
   read by `IPRODbContextFactory` (which otherwise falls back to a hardcoded dead string).
2. **The July 11+ migration files are dead code.** They are hand-written single files without the
   `[DbContext]` attribute, so EF never discovers them — locally *or in production*. Every schema
   change after July 10 actually ships via the raw-SQL `EnsureXxxSchemaAsync` functions in both
   `Program.cs` files. Do not "fix" the dead migrations without understanding this: prod's
   `__EFMigrationsHistory` has only the 15.
3. First app boot ran the whole Ensure suite against the July-10 baseline and created the remaining
   ~68 tables. Total: 97 tables + Hangfire's.

## Known first-boot quirk (found by this environment on day one)

`ECardDesignSeeder`/`ELetterTemplateSeeder` run **before** `EnsureECardDesignSchemaAsync`
(Program.cs ~line 338 vs ~395), so on a truly empty database the first boot logs
`[StarterContentSeeding] FAILED: Table 'ecarddesigns' doesn't exist` and skips those two seeders.
The failure is caught and non-fatal; the **second boot** seeds them (verified: 14 designs, 4
templates). Harmless locally, invisible in prod (tables pre-exist), queued as a review-step-6
ordering fix.

## Automated tests (regression suite)

```
dotnet test tests/IPRO.IntegrationTests
```

Needs local MySQL running (see above) — each test creates its own throwaway `ipro_test_*` database
with the real schema, runs, and drops it. Root access on 127.0.0.1:3306 is assumed; override with
the `IPRO_TEST_DB_TEMPLATE` environment variable (`{0}` = database name). Battery 1 covers agent
deletion/retention and the financial-ledger cascade guard — the 2026-08-14 invoice-loss class of
bug (TODO items 417/420). **Every bug fixed from now on should land with a test here that pins it.**

## Local test accounts

| What | Value |
|---|---|
| Agent portal (`:5100`) | `LocalTester` / `LocalDev-Agent-2026!` |
| SuperAdmin (`:5200`) | `superadmin` / password in `appsettings.Development.json` |

`LocalTester` has a **simulated** Active Silver subscription — a `billings` row inserted directly
(`Status=1`, empty PayPal ids, the free/promo-agent shape) because there is no PayPal locally. Real
PayPal-sandbox testing still needs sandbox credentials placed in `appsettings.Development.json`
(planned for review step 5).

## What this environment has already proven it can do

- Reproduced the production Follow-ups sidebar 404 (review finding H-7) with an authenticated
  agent: `/portal/Clients/FollowUps` → 404, `/Clients/FollowUps` → 200.
- Caught the seeder-ordering defect above within minutes of first boot.
- Full signup → forced password change → billing gate → simulated activation → portal, all local.
