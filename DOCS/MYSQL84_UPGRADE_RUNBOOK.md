# MySQL 8.0 to 8.4 upgrade runbook (TODO 590)

Written 2026-11-09 evening. Azure's standard support for MySQL 8.0 ends **2026-12-31**; extended-support
charges start 2027-01-01. This runbook takes the production database `ipro-mysql-prod` to 8.4 in three parts.

| Part | What | Who | Time | State |
|---|---|---|---|---|
| 1 | Both iPro database accounts to `caching_sha2_password` (8.4 turns the old method off) | owner's two pastes | 15 min | **DONE 2026-11-09 evening** (TODO 590) |
| 2 | Rehearsal: the upgrade on a throwaway copy of production | assistant + owner's pastes | about 1 hour, the owner about 15 min | planned 2026-11-10 |
| 3 | The real upgrade, at a quiet hour, after a fresh backup | owner's pastes, assistant checks | about 1 hour; iPro down for the time part 2 measures | after a clean part 2 |

## What is known (2026-11-09)

- **Production:** `ipro-mysql-prod`, resource group `ipro-production`, MySQL 8.0.46, Standard_B1ms (Burstable),
  20 GB, backups kept 35 days, no high availability, no read replicas, firewall `AllowAzureServices` only.
  `sql_mode` = ONLY_FULL_GROUP_BY, STRICT_TRANS_TABLES, NO_ZERO_IN_DATE, NO_ZERO_DATE, ERROR_FOR_DIVISION_BY_ZERO
  (none of the modes Azure says to remove first); `lower_case_table_names` = 1; `default_authentication_plugin`
  = mysql_native_password (a setting 8.4 no longer has; no iPro account uses that method any more).
- **Accounts:** `iproadmin` (the portal AND SuperAdmin: each app's `ConnectionStrings__DefaultConnection`, the
  same password) and `ipro_builder` (the live builder, app `ipro-builder` in resource group `ipro-builder-staging`,
  `ConnectionStrings__Builder`). Both on `caching_sha2_password` since part 1.
- **The code already runs on 8.4:** staging's `ipro-mysql-staging` was created on 8.4 (8.4.9) on 2026-10-22, and
  staging's portal, SuperAdmin and builder have run the same code on it since. What the rehearsal adds is
  production's DATA going through Azure's in-place upgrade, and how long that takes.
- **Azure's rules** (learn.microsoft.com/azure/mysql/flexible-server/how-to-upgrade, read 2026-11-09): the
  upgrade is in place (same server, name, address and data) and **cannot be undone**; going back means restoring
  a backup taken before it to a NEW server. Azure takes no backup of its own first: take an on-demand one. A
  Burstable server is first moved to General Purpose automatically, paid only for that time; afterwards the
  server can stay General Purpose or go back to Burstable, and the page says the default is **B2s**, so check
  the size afterwards and set **Standard_B1ms** again. Downtime "varies based on the size of the database and the
  number of tables"; client connections stop while the server upgrades. No open XA transactions allowed. A
  Validate button (preview for 8.0 to 8.4) is on the portal's Upgrade panel. A restored copy does NOT get the
  source's firewall rules.

## How the commands are run

Database commands run in **Azure Cloud Shell (Bash)**, pasted by the owner: the assistant may not type a
database sign-in there. Nobody types a password: the setup paste reads each one from the app that uses it,
prints only its length, and every later line uses it from memory. The session is ephemeral (opened with "No
storage account required"), so nothing is kept after it closes; if Cloud Shell times out (about 20 idle
minutes), paste the setup again. Azure resource commands that do not sign in to a database (restore, upgrade of
the COPY, delete of the copy, status checks) are run by the assistant from this PC.

### The setup paste (first, every new Cloud Shell session)

```bash
{
P=ipro-mysql-prod.mysql.database.azure.com
C=ipro-mysql-rehearsal.mysql.database.azure.com
pw() { az webapp config appsettings list -g "$1" -n "$2" --query "[?name=='$3'].value" -o tsv | sed -n 's/.*[Pp]assword=\([^;]*\).*/\1/p'; }
A=$(pw ipro-production ipro-prod-web ConnectionStrings__DefaultConnection)
B=$(pw ipro-builder-staging ipro-builder ConnectionStrings__Builder)
sq() { MYSQL_PWD="$A" mysql -h "$1" -u iproadmin --default-auth=caching_sha2_password -N "${@:2}" 2>&1 | grep -iv deprecated; }
try() { MYSQL_PWD="$3" mysql -h "$1" -u "$2" --default-auth=caching_sha2_password -N -e "SELECT CURRENT_USER()" 2>&1 | tail -1; }
counts() { sq "$1" ipro_crm -e "SET SESSION group_concat_max_len=1000000; SELECT GROUP_CONCAT(CONCAT('SELECT ', QUOTE(table_name), ', COUNT(*) FROM ', CHAR(96), table_name, CHAR(96)) ORDER BY table_name SEPARATOR ' UNION ALL ') FROM information_schema.tables WHERE table_schema='ipro_crm' AND table_type='BASE TABLE'" | sq "$1" ipro_crm; }
info() {
  sq "$1" -e "SELECT CONCAT('version ', VERSION()); SELECT CONCAT('routines ', COUNT(*)) FROM information_schema.routines WHERE routine_schema='ipro_crm'; SELECT CONCAT('views ', COUNT(*)) FROM information_schema.views WHERE table_schema='ipro_crm'; SELECT CONCAT('triggers ', COUNT(*)) FROM information_schema.triggers WHERE trigger_schema='ipro_crm'; SELECT CONCAT('account ', user, ' ', plugin) FROM mysql.user WHERE user LIKE 'ipro%'; SELECT 'open XA transactions (none listed = none):'; XA RECOVER;"
  echo "sign-in iPro: $(try "$1" iproadmin "$A")   sign-in builder: $(try "$1" ipro_builder "$B")"
}
summary() { counts "$1" > "$2"; echo "$(wc -l < "$2") tables, $(awk '{s+=$2} END {print s}' "$2") rows, fingerprint $(md5sum < "$2" | cut -c1-8)"; }
echo "ready: password lengths ${#A} ${#B} (expect 12 32)"
}
```

`info <host>` prints the version, any stored routines, views and triggers (expected 0, 0, 0), both accounts' sign-in
method, any open XA transaction, and a fresh sign-in by both accounts. `summary <host> <file>` counts the rows of
every table exactly, keeps the list in the file, and prints one line: tables, rows, and an 8-character
fingerprint of the whole list. Tested 2026-11-09 against the local database (132 tables, the same fingerprint
twice).

## Part 2: the rehearsal

1. **Assistant: make the copy** from a moment about ten minutes back (on 2026-09-10 the same restore was Ready
   after 7 minutes):
   `az mysql flexible-server restore -g ipro-production -n ipro-mysql-rehearsal --source-server ipro-mysql-prod --restore-time <UTC>`
   Then `az mysql flexible-server firewall-rule list -g ipro-production -n ipro-mysql-rehearsal`; if
   `AllowAzureServices` is missing, add it on the COPY only (0.0.0.0 to 0.0.0.0) so Cloud Shell reaches it.
2. **Owner: before.** Setup paste, then `info $C; summary $C ~/copy_before.txt`. Expected: version 8.0.46,
   0/0/0, both accounts caching_sha2_password, no XA, both sign-ins. Note the fingerprint line.
3. **Owner, optional: Validate** in the portal: `ipro-mysql-rehearsal` -> Overview -> Upgrade -> Validate (8.4).
   Its result is what production's Validate will say.
4. **Assistant: upgrade the copy, timed:** `az mysql flexible-server upgrade -g ipro-production -n ipro-mysql-rehearsal --version 8.4 --yes`,
   with the server's state read every 30 seconds while it runs. Record: total time, the time the state was not
   Ready, and the size (`sku`) afterwards.
5. **Owner: after.** `info $C; summary $C ~/copy_after.txt`. Expected: version 8.4.x, everything else as in
   step 2, and **the same fingerprint** (nothing writes to the copy). A different fingerprint: `diff ~/copy_before.txt ~/copy_after.txt`.
6. **Assistant, on the owner's go: delete the copy:** `az mysql flexible-server delete -g ipro-production -n ipro-mysql-rehearsal --yes`,
   then `az mysql flexible-server list -g ipro-production -o table` shows only `ipro-mysql-prod`.
7. Write the measured times into part 3 below and into TODO 590.

Not in the rehearsal, on purpose: clicking through iPro on the copy. Staging has run the same code on 8.4 since
2026-10-22; pointing staging at a copy of production data would mean swapping and restoring staging's settings
and running real customers' rows (their Google connections among them) on a second app, for little gain.

## Part 3: the real upgrade

At a quiet hour (late evening Eastern; first read the last hour's requests in Application Insights). Announce
nothing to customers: the downtime is the time part 2 measured.

1. **Owner (Cloud Shell): backup and before.** Setup paste, then
   `az mysql flexible-server backup create -g ipro-production -n ipro-mysql-prod --backup-name before-mysql84-$(date -u +%Y%m%d%H%M)`
   and wait for it to finish; then `info $P; summary $P ~/prod_before.txt`.
2. **Owner: upgrade:** `az mysql flexible-server upgrade -g ipro-production -n ipro-mysql-prod --version 8.4 --yes`
   (or the portal's Upgrade button if part 2 showed the size choice is only offered there). iPro shows errors
   while the server is upgrading.
3. **Owner: the size back**, if `az mysql flexible-server show -g ipro-production -n ipro-mysql-prod --query sku`
   does not say Standard_B1ms / Burstable:
   `az mysql flexible-server update -g ipro-production -n ipro-mysql-prod --tier Burstable --sku-name Standard_B1ms`
   (one more short restart).
4. **Owner: after.** `info $P; summary $P ~/prod_after.txt; diff ~/prod_before.txt ~/prod_after.txt`. Only tables
   written by the live apps in those minutes may differ (Hangfire's, logs, visits).
5. **Assistant: checks.** Both hosts' `/health/version` and a customer site answer 200; the builder answers;
   Application Insights shows no database errors in the next 30 minutes; the Hangfire dashboard shows jobs
   succeeding again; `sql_mode`, `lower_case_table_names`, backup retention (35 days) and the firewall rule are
   as before; the server says 8.4. Then TODO 590 is closed and the open-work list updated.

**If iPro cannot work on 8.4** (not expected: staging runs on it): there is no downgrade. Restore the step-1
backup (or the moment just before step 2) to a NEW server, e.g. `ipro-mysql-prod-80`, add `AllowAzureServices`
on it, and change the server name in the three connection settings (portal, SuperAdmin, builder; each app
restarts once). About 30 minutes; anything written between the backup and the switch is on the 8.4 server only.

## Cost

The copy: a B1ms server for an hour or two, cents. The upgrade itself: General Purpose compute for its duration,
about a dollar. Production stays on Standard_B1ms afterwards.
