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

### Database
- The production MySQL server (`ipro-mysql-prod`, resource group `ipro-production`, an Azure Database for MySQL Flexible Server) has **automated backups with 7-day retention** (confirmed via `az mysql flexible-server list`). Geo-redundant backup is currently **disabled** — the backup only survives a regional Azure outage if that's turned on, which it isn't today. Worth revisiting once real customer data volume justifies the extra cost.
- Point-in-time restore within that 7-day window is available directly through Azure (`az mysql flexible-server restore`) if ever needed — this hasn't been exercised/tested in this project yet.

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
- **Database point-in-time restore has never actually been tested** in this project — the 7-day automated backup exists, but the restore *procedure* itself is unverified.
