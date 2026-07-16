# Troubleshooting and Deployment Checks

## A Recent Change Is Not Visible

1. Confirm the commit was pushed to `main`.
2. Open GitHub Actions.
3. Confirm the correct Web or Admin workflow completed successfully.
4. Wait for Azure restart to finish.
5. Refresh with `Ctrl+F5` or use a private browser window.
6. Confirm the URL points to the correct Azure app.

## HTTP 500 or Application Error

1. Open the Azure App Service.
2. Select **Diagnose and solve problems**.
3. Open container startup/exit details and application logs.
4. Find the first application exception rather than certificate-update warnings.
5. Record the exception type, message, controller/action, and database error.
6. Correct the code, package, environment setting, or schema problem.
7. Redeploy and restart.

## HTTP 400 After Login or PayPal Return

Check that the login return URL remains local and that the application's data-protection/authentication state has not been invalidated by an incomplete deployment. Sign in again after the corrected deployment.

## Website Image Does Not Remain Selected

1. Confirm the latest Web deployment completed.
2. Edit the page.
3. Select the destination block.
4. Click **Use this image**.
5. Confirm the image selector and preview change.
6. Click **Save Block**.
7. Refresh and confirm the selector still shows the image.

Local image paths under `/images` and `/uploads` are valid. Do not paste a filesystem path such as `C:\...` into an image URL.

## Domain Shows Azure 404

1. Confirm `www` is a CNAME to `ipro-prod-web.azurewebsites.net`.
2. Confirm IPRO reports DNS ready.
3. Confirm Azure custom-domain binding is complete.
4. Recheck from Super Admin.
5. Clear local DNS/browser cache.

## Domain Is Not Secure

1. Confirm DNS and Azure hostname binding are complete.
2. Confirm the managed certificate exists.
3. Confirm SNI SSL binding is attached to the hostname.
4. Wait for certificate provisioning and retry HTTPS.

## Azure Domain Automation Errors

- `unauthorized_client`: verify Tenant ID and Client ID are real values, not placeholders.
- Invalid subscription: replace the placeholder with the Azure Subscription ID.
- `403 hostNameBindings/write`: assign Website Contributor or sufficient role to the IPRO Domain Automation service principal at the required scope.
- Certificate/serverfarm permission errors: grant the required role on the App Service plan/resource group.
- Empty JSON response: the Azure API may have returned success without a body; deploy the current response-handling code.

After changing credentials or role assignments, restart Web and Admin, wait for Azure propagation, then click **Recheck**.

## PayPal Invalid Client

1. Confirm `PayPal__IsSandbox` matches the credential type.
2. Confirm Client ID and Secret came from the same PayPal REST application.
3. Use sandbox business credentials for the seller integration and a different sandbox personal account for the buyer.
4. Restart the app after changing Azure settings.

## SendGrid 403 Sender Identity

The From address must match a verified SendGrid sender or authenticated domain. Correct the Azure email sender settings or complete domain authentication.

## SendGrid Deferred

A deferred response means the recipient server temporarily throttled delivery. SendGrid retries automatically. Review the event response and wait before resending.

## Newsletter Open Tracking Does Not Update

1. Confirm SendGrid event webhook points to the IPRO newsletter event endpoint.
2. Enable delivered, open, click, bounce, deferred, and dropped events.
3. Confirm open tracking is enabled in SendGrid.
4. Remember that privacy tools and image blocking can affect open detection.

## Incident: Both Apps Down After Deploy — "Connection must be Open; current state is Closed"

**2026-07-16.** Commit `00ad45d` ("Harden public contact/lead forms...") took both `ipro-prod-web` and `ipro-prod-admin` down immediately on deploy. Both apps showed Azure's generic `:( Application Error` page and the Azure platform log showed repeated `ContainerTimeout` / cold-start-failure entries, which looks like an infrastructure problem but was not one.

**Root cause**: a new startup schema-repair method (`EnsureWebsiteLeadSchemaAsync` in both `Program.cs` files) called `EnsureTableColumnAsync`, which builds a raw ADO.NET command via `db.Database.GetDbConnection().CreateCommand()`. That path requires the connection to be explicitly opened first — EF Core does not auto-open it the way it does for `ExecuteSqlRawAsync`/normal LINQ queries. The existing `EnsureWebsiteTemplateSchemaAsync` already wraps all of its `EnsureTableColumnAsync` calls in `await db.Database.OpenConnectionAsync(); try { ... } finally { await db.Database.CloseConnectionAsync(); }` for exactly this reason — the new method was added as a sibling call without that wrapper, so the connection was closed by the time it ran. Every request to start the app threw:

```
Unhandled exception. System.InvalidOperationException: Connection must be Open; current state is Closed
   at MySqlConnector.MySqlConnection.get_Session()
   at ... EnsureTableColumnAsync(...)
```

**Fix**: commit `865c25e`, wrapping the new method in the same `OpenConnectionAsync`/`CloseConnectionAsync` pattern. Deployed and confirmed both apps serving real pages (not the crash screen) roughly 11 minutes after the bad deploy landed.

**Diagnosis method, for next time**: GitHub Actions reporting deploy success does **not** mean the app started — that only confirms the build/publish/upload step. To find the real cause, pull the Azure runtime log archive directly:

```
az webapp log download --name ipro-prod-web --resource-group ipro-production --log-file /tmp/weblogs.zip
```
(substitute `ipro-prod-admin` for the admin app.) Extract it and check the newest `LogFiles/*_containerStream.log` — that is the actual stdout/stderr of the .NET process, including any unhandled startup exception with a full stack trace. The `*_docker.log` file only shows container orchestration events (pulling image, mounting volumes, `ContainerTimeout`) and will not contain the .NET exception itself.

**Prevention rule**: any new schema-repair code that uses `db.Database.GetDbConnection()` directly (rather than `db.Database.ExecuteSqlRawAsync(...)`) must run inside its own `OpenConnectionAsync`/`CloseConnectionAsync` scope, or be added inside the existing `EnsureWebsiteTemplateSchemaAsync` scope rather than as a new sibling call after it.

## Release Build Commands

From the repository root:

```powershell
dotnet build src/IPRO.Web/IPRO.Web.csproj -c Release
dotnet build src/IPRO.Admin/IPRO.Admin.csproj -c Release
```

If packages are already restored and the local NuGet config is inaccessible:

```powershell
dotnet build src/IPRO.Web/IPRO.Web.csproj -c Release --no-restore
dotnet build src/IPRO.Admin/IPRO.Admin.csproj -c Release --no-restore
```

