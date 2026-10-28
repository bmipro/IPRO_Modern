# Moving the Azure apps from .NET 8 to .NET 10 -- the runbook (TODO 596)

Written 2026-10-22 for TODO 596, before anything in Azure changed. Microsoft ends .NET 8 support on
2026-11-10; App Service already lists the .NET 8 stack as "Near" its end and offers .NET 10 (LTS,
supported to 2028-11-14).

**Who does what.** Every command here changes an Azure app, so each one is run by the owner, or by the
assistant only on the owner's explicit go, each time. Reading is free. Run them in the portal's
**Cloud Shell (Bash)**: in Windows PowerShell the `|` in the stack name breaks `az`.

## Where things stand (read-only look, 2026-10-22)

| App | Resource group | Stack | Serves |
|---|---|---|---|
| ipro-prod-web | ipro-production | DOTNETCORE 8.0 | app.iproadvisers.com (iPro) |
| ipro-prod-admin | ipro-prod-admin_group | DOTNETCORE 8.0 | admin.iproadvisers.com (SuperAdmin) |
| ipro-staging-web | ipro-builder-staging | DOTNETCORE 8.0 | nothing yet (404) |
| ipro-staging-admin | ipro-builder-staging | DOTNETCORE 8.0 | nothing yet (404) |
| ipro-builder-staging | ipro-builder-staging | DOTNETCORE 8.0 | nothing yet (404) |
| ipro-builder | ipro-builder-staging | DOTNETCORE 8.0 | nothing yet (404) |

Both plans are Basic B2 (ipro-prod-plan, ipro-builder-staging-plan): no deployment slots, so there is no
"swap"; a change restarts the app in place (about 90 seconds of 503).

## Why the order matters

A .NET 10 build does not start on the .NET 8 stack, and the .NET 10 stack has no .NET 8 runtime. The
app setting `DOTNET_ROLL_FORWARD=Major` lets the .NET 8 build that is running now start on .NET 10.
With it, the stack switch becomes a step of its own, tried on the build already running and taken back
with one command, before the .NET 10 build is released.

## A. Staging and the builder apps (any time: no customer sees them)

The builder apps must be on .NET 10 before the customer builder's .NET 10 commit reaches the builder
repository's `main` (its staging workflow deploys every push).

```bash
az webapp config set -g ipro-builder-staging -n ipro-builder-staging --linux-fx-version "DOTNETCORE|10.0"
az webapp config set -g ipro-builder-staging -n ipro-builder --linux-fx-version "DOTNETCORE|10.0"
```

iPro's staging apps may still be given a .NET 8 build before 596 is on `main`, so they get the bridge too:

```bash
az webapp config appsettings set -g ipro-builder-staging -n ipro-staging-web --settings DOTNET_ROLL_FORWARD=Major
az webapp config appsettings set -g ipro-builder-staging -n ipro-staging-admin --settings DOTNET_ROLL_FORWARD=Major
az webapp config set -g ipro-builder-staging -n ipro-staging-web --linux-fx-version "DOTNETCORE|10.0"
az webapp config set -g ipro-builder-staging -n ipro-staging-admin --linux-fx-version "DOTNETCORE|10.0"
```

**Rehearsal.** 585's Azure set-up is done (2026-10-22: the staging apps, `ipro-mysql-staging` on MySQL 8.4
with `ipro_staging`, deploy identities), but nothing is deployed to staging yet and its workflow
(`staging.yml`) is still on the 585 branch. Once 585 is on `main`, every push deploys staging first and
production is released by hand for the commit staging serves; so the first staging deploy that carries
596 is the rehearsal: put the staging apps on .NET 10 (above) before that push, and click staging through
before section B. If 596 has to ship before 585, there is no rehearsal of iPro itself; the bridge below
is what keeps production's switch reversible.

## B. Production (a quiet hour, after the owner's word on 596; about 45 minutes)

Web first, then admin, never both at once: both apps repair the same database at start-up
(INVARIANTS rule 6). After every step, wait until `https://<host>/health/version` answers 200 with the
expected bare SHA before the next.

1. **Note the SHA** both hosts serve now:
   `curl -s https://app.iproadvisers.com/health/version; echo; curl -s https://admin.iproadvisers.com/health/version`
2. **Bridge** (the .NET 8 build keeps running, nothing visible changes):
   ```bash
   az webapp config appsettings set -g ipro-production -n ipro-prod-web --settings DOTNET_ROLL_FORWARD=Major
   # wait: app.iproadvisers.com/health/version, same SHA
   az webapp config appsettings set -g ipro-prod-admin_group -n ipro-prod-admin --settings DOTNET_ROLL_FORWARD=Major
   # wait: admin.iproadvisers.com/health/version, same SHA
   ```
3. **Stack** (the .NET 8 build now runs on .NET 10):
   ```bash
   az webapp config set -g ipro-production -n ipro-prod-web --linux-fx-version "DOTNETCORE|10.0"
   ```
   Check: the same SHA at `/health/version`, a public page and the sign-in page open, and the container
   log has no "framework ... was not found". Then the same for admin
   (`-g ipro-prod-admin_group -n ipro-prod-admin`).
   **Way back from here:** the same command with `"DOTNETCORE|8.0"`.
4. **Release 596** the usual way (`main` fast-forwarded to the gated commit and pushed; or, once 585's
   release-by-hand workflows are on `main`, staging first and then "Run workflow" with the SHA). Verify
   both hosts at `/health/version`, read App Insights errors and BOTH container logs (the start-up seeder
   runs in whichever app starts first).
5. **Keep the bridge about a week**: with it, re-running the previous deploy run puts the .NET 8 build
   back on the .NET 10 stack. Then, at the next quiet hour (one restart each):
   ```bash
   az webapp config appsettings delete -g ipro-production -n ipro-prod-web --setting-names DOTNET_ROLL_FORWARD
   az webapp config appsettings delete -g ipro-prod-admin_group -n ipro-prod-admin --setting-names DOTNET_ROLL_FORWARD
   ```
   and the same for the two staging apps.

## What stays on the old line

The database library stays on EF Core 8 (8.0.32) with Pomelo 8.0.3: Pomelo has no release for EF Core
10. EF Core 8 loses Microsoft support on 2026-11-10 too; moving to EF Core 10 is TODO 596's step 2, with
a rehearsal on a copy of the real database first.
