# Backup and Release Checklist

An internal ops reference, not an agent-facing manual — the actual, currently-followed process for backing up and shipping changes to IPRO Modern. Written to reflect real practice, not an aspirational ideal; gaps are called out explicitly rather than glossed over.

## Backup

### Code and docs (continuous — every commit)
- Every change lands as a commit on `origin/main` (`https://github.com/bmipro/IPRO_Modern.git`). This is the primary, always-current backup: as long as `git status` is clean and the last commit is pushed, code is safe.
- Documentation and decisions are folded into the same commit as the change they describe, not tracked separately:
  - New/changed feature → update the relevant numbered manual in `DOCS/` (see `DOCUMENTATION_STANDARD.md`).
  - Root-caused bug → add an incident entry to `DOCS/09_TROUBLESHOOTING.md`.
  - Roadmap status change or new idea → update `DOCS/IPRO_Project_Status_And_Roadmap.md`.
- **Verify before ending any work session**: `git status` shows a clean tree and the last commit is pushed.

### Snapshot backup (nightly + per milestone)
A dated zip of the exact committed state, kept independent of GitHub as a second copy:
```
git archive --format=zip -o "/c/Users/admin/OneDrive/Codex_Code_Bkup/IPRO_Modern_backup_$(date +%Y%m%d-%H%M%S).zip" HEAD
```
- Uses `git archive`, so it only includes tracked files at `HEAD` — no `bin`/`obj` build output, no local `publish-*-check` scratch folders, no `.git` internals. Typically ~30 MB.
- Lives in `C:\Users\admin\OneDrive\Codex_Code_Bkup\`, matching the existing naming convention (`IPRO_Modern_backup_YYYYMMDD-HHMMSS.zip`) used by every prior backup in that folder.
- Do this at the end of a session and again right after any significant milestone lands (a feature shipped and verified live) — not only at the very end.

**Then a second, local copy** — same archive, different failure mode. OneDrive is one account away from
being unavailable:
```
git archive --format=zip -o "/c/Users/admin/Documents/IPRO_Backups/IPRO_Modern_backup_$(date +%Y%m%d-%H%M%S).zip" HEAD
```
`C:\Users\admin\Documents\IPRO_Backups\` is deliberately **outside** the working tree. A reboot has
already wiped that tree once, so a "local" copy sitting next to the repo would be lost by the same
event it exists to survive. The path was undocumented until 2026-08-08 and no earlier local zip could
be found anywhere on disk, which suggests the step had been silently skipped — hence writing it down.

**Order matters:** reconcile `DOCS/TODO.md` → commit docs → push → OneDrive zip → local zip. The zips
are `git archive HEAD`, so anything uncommitted is simply absent from both.

### Refresh `DOCS/TODO.md` before the snapshot

The open-work list lives in `DOCS/TODO.md` **because that is the only copy that survives anything**.
The assistant's task tracker is tooling state: not a file, not in git, not in any backup here. If it
is lost, everything in it is lost — and this working directory plus the assistant's memory files have
already been wiped once by a reboot.

So before running the snapshot: reconcile `DOCS/TODO.md` against the live task list, mark or delete
what is finished, and commit it. A snapshot of a stale TODO is only marginally better than none —
and a stale "not yet fixed" line actively causes harm, having been re-raised as work on 2026-08-08
weeks after the thing was fixed.

### Database
- The production MySQL server (`ipro-mysql-prod`, resource group `ipro-production`, an Azure Database for MySQL Flexible Server) has **automated backups with 35-day retention** (raised from 7 by the owner on 2026-09-10; confirmed via `az mysql flexible-server show`). The window fills from that date: the earliest restore point was still 2026-09-02 on the day it was raised. Geo-redundant backup is currently **disabled** — the backup only survives a regional Azure outage if that's turned on, which it isn't today. Worth revisiting once real customer data volume justifies the extra cost.
- Point-in-time restore within that 35-day window is available directly through Azure (`az mysql flexible-server restore`). **Rehearsed 2026-09-10** (TODO 473): restore point 22:10 UTC, server Ready after 7 min 14 s, `ipro_crm` present, 27 clients and 2 agents on the copy and on production, the table deployed that afternoon present. Runbook below.

### Runbook: point-in-time restore to a throwaway server

Use this to recover data from any moment in the last 35 days without touching production. Everything here is read-only on production; the copy costs cents per hour and is deleted at the end.

1. **Pick the restore point** in UTC, at least ten minutes in the past and after `backup.earliestRestoreDate`
   (`az mysql flexible-server show -g ipro-production -n ipro-mysql-prod --query backup`).
2. **Restore** (the command returns when the server is Ready; 7 minutes at B1ms with the launch-era data):
   ```
   az mysql flexible-server restore -g ipro-production -n ipro-mysql-rehearsal --source-server ipro-mysql-prod --restore-time 2026-09-10T22:10:00Z
   ```
   The copy keeps the source's admin login and password, SKU and public-access setting. It does **not** copy the firewall rules.
3. **Confirm without a secret**: `az mysql flexible-server db list -g ipro-production -s ipro-mysql-rehearsal --query "[].name" -o tsv` lists `ipro_crm`.
4. **Look at the data** (the owner, with the admin password; the assistant never handles it): add your IP on the copy
   (portal → the server → Networking → Add current client IP → Save), then from a terminal
   ```
   & "C:\Users\admin\ipro-local\mysql-8.0.44-winx64\bin\mysql.exe" -h ipro-mysql-rehearsal.mysql.database.azure.com -u iproadmin -p --ssl-mode=REQUIRED ipro_crm
   ```
   and compare counts (`SELECT COUNT(*) FROM Clients; SELECT COUNT(*) FROM AgentUsers; SELECT MAX(CreatedAt) FROM Clients;`) with production or the Admin figures. Production's own firewall blocks the same client unless a rule is added there; if one is added for a check, remove it afterwards.
5. **Pull what you need.** For one agent's rows: `mysqldump` from the copy with `--where="AgentUserId=<id>"` per table (client-linked tables via `ClientId IN (...)`), then load into production with new ids. The client recycle bin (472) covers the common case, one deleted client, with no restore at all.
6. **Delete the copy** (owner's go): `az mysql flexible-server delete -g ipro-production -n ipro-mysql-rehearsal --yes`, then `az mysql flexible-server list -g ipro-production -o table` shows only `ipro-mysql-prod`.

Total on 2026-09-10: about 35 minutes including the data check.
- **Files** (`iprostorageprod`): blob soft delete and container soft delete (30 days) and blob versioning, all enabled by the owner on 2026-09-10. A file the app deletes can be undeleted in the Azure portal within 30 days.
- **Application level** (472, 2026-09-10): deleting a client moves it to a 30-day recycle bin the agent restores themselves (`ClientRecycleBin`); every other delete in the portal is still immediate. The nightly `client-recycle-bin-purge` job removes expired snapshots and only then their files.
- **Nightly logical dump** (474, 2026-09-11): the `database-dump` job writes a gzipped SQL dump of the whole database (every table's CREATE TABLE and rows; Hangfire's own tables excluded) to the **private** `db-backups` container at 06:15 UTC and removes dumps older than 30 days. It is written in-process (`DatabaseDump`) because Linux App Service has no mysqldump. SuperAdmin → **Backups** lists the files and can take a dump on demand. A file we own, independent of Azure's backups.

### Runbook: restore from a nightly dump

1. Azure portal → `iprostorageprod` → Containers → `db-backups` → download the file you want (they are named `ipro_crm-YYYYMMDD-HHMMSS.sql.gz`, UTC).
2. Unzip it (7-Zip, or `gzip -d`).
3. Create an empty database on a throwaway server (the point-in-time runbook above creates one; or `CREATE DATABASE restore_test CHARACTER SET utf8mb4` on any MySQL 8) and replay:
   ```
   & "C:\Users\admin\ipro-local\mysql-8.0.44-winx64\bin\mysql.exe" -h <host> -u <user> -p --ssl-mode=REQUIRED restore_test < ipro_crm-20260911-061500.sql
   ```
   The script disables foreign-key checks while it runs, so table order does not matter.
4. Query what you need and copy rows into production with new ids, or use the whole database as a rehearsal copy. Never replay a dump into production itself.

## Launch-day domain switch (477)

The old public names -- `www.iproaccountants.com`, `iproaccountants.com`, `www.iproadvisers.com`,
`iproadvisers.com` -- resolve today (2026-09-11) to the old site at 66.102.128.65. At launch they
point at the new site, and the app answers them with a permanent redirect to
the platform: `iproadvisers.com` to the home, `iproaccountants.com` to `/accountants` (and later
`ipromortgages.com` to `/mortgage`), never the old path. The code is in place
(`PlatformAliasHosts`, first middleware in `IPRO.Web`); it does nothing until `App:AliasHosts` is set.

Order on the day, owner's actions marked:

1. **Owner, registrar:** for each of the four names add the App Service verification record
   `TXT asuid.<name>` = the app's custom-domain verification id (Azure portal → ipro-prod-web →
   Custom domains → the id shown there). Touch nothing else on `iproadvisers.com`: its SPF, DKIM and
   MX records carry the ACS email domain.
2. **Owner's go, then CLI or portal:** bind the four hostnames to `ipro-prod-web` and create an App
   Service managed certificate for each (portal: Custom domains → Add → managed certificate).
3. **Owner's go, App Service configuration:** `App__AliasHosts` =
   `www.iproadvisers.com,iproadvisers.com,www.iproaccountants.com=/accountants,iproaccountants.com=/accountants`
   on ipro-prod-web (the app restarts once). Add `www.ipromortgages.com=/mortgage,ipromortgages.com=/mortgage`
   once that domain is registered and bound.
4. **Owner, registrar, the moment of the switch:** `www` names → `CNAME ipro-prod-web.azurewebsites.net`;
   apexes → `A` the app's inbound IP (Custom domains page shows it; 40.89.19.0 on 2026-09-11) --
   or an ALIAS/ANAME record to `ipro-prod-web.azurewebsites.net` if the registrar supports one.
5. **Verify:** `curl -sI https://www.iproaccountants.com/` answers `301` with
   `Location: https://app.iproadvisers.com/accountants`, and the iproadvisers names with `Location: https://app.iproadvisers.com/`; mail from `@iproadvisers.com` still authenticates
   (send one to a Gmail address and check "signed-by").

Rollback is the DNS records back to 66.102.128.65; the bindings and the setting can stay.

Rehearsed 2026-09-12 on ipromortgages.com, end to end (483): GoDaddy's authoritative servers showed
the four records within a minute of saving, `hostname add` verified both names at once, each managed
certificate took a few minutes, the alias setting's restart about a minute. Two things worth knowing
on the day: GoDaddy pre-creates a `www` CNAME (edit it, a name takes one CNAME), and between the DNS
change and the alias setting the app answers the name with its "Website not published yet" page,
which is the normal in-between state, not a fault. And the certificate step: `az webapp config ssl create`
printed a JSON traceback and the certificate list showed nothing for a few minutes, yet both
certificate resources HAD been created (a second create then fails as a duplicate). Read the
resource by name (`.../Microsoft.Web/certificates/<hostname>`) until it carries a thumbprint, then
`az webapp config ssl bind --certificate-thumbprint <thumb> --ssl-type SNI`; the front ends pick the
binding up within a minute or two.

## SuperAdmin behind Microsoft Entra sign-in (482)

Since 2026-09-12 the admin site (`ipro-prod-admin`, resource group `ipro-prod-admin_group`,
admin.iproadvisers.com) sits behind App Service Authentication with the Microsoft identity provider.
A browser reaching any page is sent to login.windows.net for the owner's tenant first; the
SuperAdmin username/password login is the second gate. API-style callers get 401.

**What stays open, and why it matters:** `/health` and `/health/version` are excluded from
authentication. The deploy workflow's verify step (`main_ipro-prod-admin.yml`) and the host watch
read `/health/version` anonymously; with the exclusion missing they answer 401 and the deploy is
declared failed. If the admin site ever answers 401 on that path, check the exclusion before
anything else:

```
az webapp auth show -n ipro-prod-admin -g ipro-prod-admin_group --query properties.globalValidation
MSYS_NO_PATHCONV=1 az webapp auth update -n ipro-prod-admin -g ipro-prod-admin_group --excluded-paths "/health,/health/version"
```

The portal's Edit dialog has no field for excluded paths; the CLI takes them as ONE comma-joined
argument, and under Git Bash the `MSYS_NO_PATHCONV=1` prefix is required or `/health` arrives as
`C:/Program Files/Git/health`. The change takes about a minute to apply; no restart needed.

**Who can pass the first gate:** the enterprise application `IPRO SuperAdmin sign-in` has
Assignment required = Yes and only the owner assigned (Microsoft Entra ID -> Enterprise
applications -> the app -> Properties, then Users and groups). Adding a second administrator means
assigning them there AND creating their SuperAdmin login.

**The client secret expires 2028-09-11.** When it lapses the Microsoft sign-in stops with an error
page and nothing warns beforehand. Before that date: Microsoft Entra ID -> App registrations ->
`IPRO SuperAdmin sign-in` -> Certificates & secrets -> new client secret, then on the admin site
Authentication -> the Microsoft provider -> Edit -> paste the new secret (it is stored in the
`MICROSOFT_PROVIDER_AUTHENTICATION_SECRET` app setting, never in the repo).

**Lock-out recovery (the owner's own account unavailable):** Authentication -> Edit ->
App Service authentication = Disabled removes the first gate; the SuperAdmin login keeps
protecting the site meanwhile. Re-enable once the account is back.

## Release (shipping a change to production)

There is no staging environment — every push to `main` deploys straight to production via GitHub Actions. The discipline below exists to compensate for that.

1. **Build both apps locally before committing**, to catch compile errors before they ever reach CI:
   ```
   dotnet build src/IPRO.Web/IPRO.Web.csproj -c Release
   dotnet build src/IPRO.Admin/IPRO.Admin.csproj -c Release
   ```
2. **Review what's staged** (`git status` after `git add`) — confirm only the intended files are included, and double-check anything that could contain a secret before it's committed.
3. **Commit** with a message describing *why*, not just *what* changed.
4. **Push to `origin/main`** — this triggers two independent GitHub Actions workflows, one per app ("Build and deploy ASP.Net Core app to Azure Web App - ipro-prod-web" / "... - ipro-prod-admin").
5. **Poll until both complete**: `gh run list --limit 2 --json status,conclusion,workflowName`. Don't consider a change shipped until both show `"conclusion":"success"`.
6. **If a schema change was involved**, confirm the new container actually started cleanly rather than assuming success from a green CI run alone — CI success only means the build/publish step worked, not that the app started without crashing on the new schema:
   ```
   az webapp log download --name ipro-prod-web --resource-group ipro-production --log-file weblogs.zip
   ```
   then check the day's `..._docker.log` for `"Site started."` with no `ContainerTimeout`/crash-loop entries in between, and the day's `..._containerStream.log` for any unhandled exception at startup.
7. **For a UI change**, verify it live if at all possible (screenshot, or ask the user to confirm) rather than only trusting a clean build — a Razor view can compile fine and still render wrong.
8. **Update the roadmap doc** to move the item from "not done" to done, in the same commit that ships it (not as an afterthought later).

## Known gaps (honest, not yet addressed)

- **No staging/pre-prod slot.** Every deploy goes directly to the live app. An Azure App Service deployment slot (swap-based) would let a change be verified before it's user-facing — not set up today.
- **No automated rollback procedure.** Today, undoing a bad deploy means reverting the commit and pushing again (which redeploys via the same pipeline), or re-running a previous successful GitHub Actions workflow run from the Actions tab. Neither is scripted or documented step-by-step yet.
- **No automated smoke tests post-deploy.** Verification today is manual (log check + visual check per the steps above), not a scripted health check that runs automatically after every deploy.
- ~~**Database point-in-time restore has never actually been tested** in this project~~ Rehearsed 2026-09-10 (runbook above) — the 35-day automated backup exists, but the restore *procedure* itself is unverified.
