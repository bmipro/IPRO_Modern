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

## Incident: Public Contact/Newsletter Leads Silently Not Saving

**2026-07-17.** An agent reported that submitting their own site's contact form (`www.4ipro.com/contact`, and the equivalent temp domain) produced a validation error every time, and separately that a successful-looking submission redirected to a blank/unexpected page. Investigation surfaced **three separate, compounding bugs**, all inside the "harden public contact/lead forms" feature shipped 2026-07-16. Because these failed before a lead row was ever created, **none of them left any trace anywhere in the product** — not a saved lead, not a logged blocked-spam-attempt, nothing. The only way to find them was direct log inspection plus live reproduction against the actual custom domain.

### Bug 1 — every legitimate submission failed model validation (the critical one)

`IPRO.Web.csproj` has `<Nullable>enable</Nullable>`. With that set, ASP.NET Core MVC treats **non-nullable `string` properties as implicitly `[Required]`** during model binding/validation — with no explicit `[Required]` attribute needed to trigger it. `WebsiteLeadFormViewModel.HoneypotField` was declared as a plain non-nullable `string`. The honeypot field is *always* submitted empty for a real visitor (that's the entire point of a honeypot) — so this implicit rule rejected **every** contact/newsletter submission, unconditionally. The same non-nullable-string pattern also affected `LastName`, `Phone`, and `Message` (all logically optional, and always absent from the DOM entirely for newsletter-type submissions), so even without the honeypot issue, any visitor who left phone/message blank — or any newsletter signup at all — would also have failed.

Confirmed via a diagnostic log line added specifically to catch this (`_logger.LogWarning` on the ModelState-invalid branch in `PublicWebsiteController.SubmitLead`):
```
Public lead submission rejected by validation on www.4ipro.com/contact. ConsentGiven=True. Invalid fields: HoneypotField: The HoneypotField field is required.
```
**Fix** (commit `385cb48`): changed `HoneypotField`, `LastName`, `Phone`, `Message`, `CaptchaToken`, `CaptchaAnswer` to nullable (`string?`). The controller already handled all of these null-safely (`model.X?.Trim() ?? string.Empty`), so no other code changed.

A second, smaller instance of the same class of bug: `ConsentGiven` used `[Range(typeof(bool), "true", "true")]` to enforce "must be checked" — `RangeAttribute` is not reliably designed for `bool` comparisons. The controller already had an explicit `!model.ConsentGiven` check as a backup, making the attribute redundant and a plausible source of its own false rejections. Removed (commit `5fdb353`).

### Bug 2 — successful submissions redirected to the wrong page with no confirmation

The routing middleware in `Program.cs` that maps a custom/temp domain's `/contact` to the internal `PublicWebsiteController.Page` action **rewrites `context.Request.Path`** before MVC ever sees it (to `/PublicWebsite/Page`, moving the real slug into the query string). The lead form's hidden `ReturnPath` field read `@Context.Request.Path` directly — which by then was the *rewritten* internal path, not the path the visitor's browser actually showed. Confirmed live: the hidden field on the real production site literally read `/PublicWebsite/Page`. Every post-submit redirect therefore sent visitors to that path, which itself gets rewritten again on the next request and lands on the site's home page — never back to Contact, and never showing a success message (which only renders on the page it's configured for).

**Fix** (commit `4aad54e`): the middleware now stores the original, pre-rewrite path in `context.Items["IproPublicPath"]`; the lead form reads that instead of `Context.Request.Path`.

### Bug 3 — the success confirmation still didn't show, even on the right page

After fixing Bug 2, the URL correctly showed `/contact?submitted=contact` post-submit, but the page still rendered the empty form instead of the green confirmation banner. The same routing middleware **also replaces `context.Request.QueryString` wholesale** with just `?slug=<path>` on every request through the `else` branch — silently discarding whatever query string was already there, including `submitted=contact`, which the lead-form partial depends on (`@if (submitted == expectedResult)`) to know a submission just succeeded.

**Fix** (commit `3ccc8b0`): the middleware now merges `slug` into the existing query string (`existingQuery.Add("slug", ...)`) instead of replacing it outright.

**Verification**: all three fixes were confirmed against live production before *and* after deploying, by inspecting the actual rendered hidden-field values and page content via the browser tool (not just by reading the code) — e.g. navigating directly to `.../contact?submitted=contact` post-fix and confirming the success text rendered. A real end-to-end submission (`forthtest@gbssurveillance.com`) was confirmed to appear as a new CRM client afterward.

**Prevention rules**:
- Any new field on a public form model that is legitimately allowed to be empty (honeypots, optional contact details) must be declared nullable (`string?`) in a project with `<Nullable>enable</Nullable>` — a non-nullable `string` with no visible `[Required]` attribute is still implicitly required by MVC's validation, which is easy to miss in review.
- Prefer plain, explicit boolean checks (`!model.SomeFlag`) over `[Range(typeof(bool), ...)]` for "must be true" checkbox validation — the latter is a known-fragile pattern.
- Any middleware that rewrites `context.Request.Path` and/or `context.Request.QueryString` must preserve (not replace) whatever was already present, unless discarding it is a deliberate, documented choice — silent data loss here broke both page identity and success-state signaling.
- A validation failure that happens *before* a domain entity is created leaves no audit trail by default. Consider whether public-form validation failures deserve the same "blocked attempt" visibility that spam/honeypot/captcha rejections already get in Super Admin's Website Leads screen.

## Incident: New Agents Published With No Nav/Pages, And Some Couldn't Publish At All

**2026-07-17.** Two compounding bugs surfaced while live-testing a brand-new test agent's first publish.

**Bug 1 — the Publish button silently didn't render.** `Views/Website/Index.cshtml` gated both Publish buttons (top of page and bottom of the settings form) on `Model != null`, where `Model` is the agent's `AgentWebsite` row. A brand-new agent who hadn't saved website settings yet had no row, so `Model` was `null` and the button never appeared — even though the `Publish` controller action already knew how to create a default website on the fly. The agent had no visible way to publish at all.

**Fix** (commit `fb914ba`): changed both button conditions from `Model != null && !isPublished` to just `!isPublished`, since `isPublished` is already `false` when `Model` is null.

**Bug 2 — publishing directly (without visiting Manage Pages first) produced an empty nav and blank homepage.** Starter pages (Home/About/Services/Contact, with their nav-visibility flags) are seeded by `EnsureStarterPagesAsync`, but that method was only ever called from `WebsitePagesController.Index`/`Navigation` — never from `WebsiteController.Publish`. An agent who selected a template, saved, and clicked **Publish** directly on **My Website** got a live site with zero `WebsitePage` rows: an empty top nav and no home content. The moment they visited **Manage Pages**, the same seeding ran and pages "magically" appeared — which looked like an unrelated edit had fixed it, but visiting that screen was the actual trigger.

**Fix** (commit `0b824dd`): extracted the seeding logic into a shared `WebsiteStarterPagesHelper.EnsureStarterPagesAsync(db, website, agentId)` and call it from `WebsiteController.Publish` too, so starter pages always exist the moment a site goes live, regardless of which screen the agent visits first.

**Prevention rule**: any "first-run" seeding step (starter pages, default settings, etc.) tied to a specific screen should also run from every other path that can make the underlying record live (here: Publish) — not just the screen where a developer happened to add it first.

## Incident: My Website Template Preview Buttons Threw a 500

**2026-07-17.** Clicking **Preview** on any template card in **My Website** returned an Azure 500 error page. Root cause found via fresh Azure container logs (`az webapp log download`):

```
System.InvalidOperationException: The partial view '_ClassicSidebar' was not found. The following locations were searched:
/Views/Website/_ClassicSidebar.cshtml
/Views/Shared/_ClassicSidebar.cshtml
```

`WebsiteController.PreviewTemplate` renders `~/Views/PublicWebsite/Index.cshtml` directly via an absolute path, but that view (and the shell partials it selects between — `_ClassicSidebar`, `_EditorialVisual`, `_ModernProfessional`) reference each other by simple name (`Html.PartialAsync("_ClassicSidebar", ...)`). ASP.NET Core's default Razor view location search is based on the **ambient route's controller name** ("Website"), not the folder the already-resolved parent view lives in ("PublicWebsite") — so the partials were never found. This was a latent bug in the Preview feature itself, not a regression from any same-day change; it just hadn't been clicked before.

**Fix** (commit `4de495e`): registered a `PublicWebsiteViewLocationExpander` (`IPRO.Web/Infrastructure/PublicWebsiteViewLocationExpander.cs`) in `Program.cs` via `services.Configure<RazorViewEngineOptions>(...)`, adding `/Views/PublicWebsite/{0}.cshtml` as an extra fallback search path app-wide.

**Prevention rule**: rendering a view via an absolute `~/Views/{OtherController}/...` path from a controller whose name doesn't match that folder will break any simple-name partial lookup inside it (and inside anything it includes, transitively) — either register a view-location fallback for that folder, or route the request through the controller that actually owns the view.

## Incident: Support Help Article Links 404'd

**2026-07-17.** Every "Read article" link on the agent Support Center 404'd. `Views/Support/Index.cshtml` links to `/Support/Article/@article.Slug`, and the app's conventional route is `{controller=Dashboard}/{action=Index}/{id?}` — that last segment binds to a route value literally named `id`. `SupportController.Article`'s parameter is named `slug`, not `id`, so MVC model binding never populated it; `HelpDocsService.FindArticle(null)` returned nothing and the action returned `NotFound()`.

**Fix** (commit `7c57fd2`): added an explicit `[HttpGet("Support/Article/{slug}")]` attribute route on the action so the URL segment binds to `slug` directly.

**Prevention rule**: an action parameter name must match the route template's placeholder name (or the route must be adjusted) — the conventional default route's placeholder is `id`; any action using a differently-named identifier parameter reached via that route needs its own explicit route attribute.

## Incident: Client Portal Activation Page Missing Company Name, And Agent Message Thread 404'd

**2026-07-18.** Live verification of the newly-built Client Portal feature surfaced two bugs.

**Bug 1 — the activation page never showed the inviting company's name.** `ClientPortalAccountController.Activate(string token)` (GET) queried `_db.Clients.FirstOrDefaultAsync(c => c.PortalInviteToken == token)` without including the `AgentUser` navigation, so `client.AgentUser?.CompanyName` was always null and the page read "has invited you to their client portal" with a blank company name.

**Fix** (commit `66c76ef`): added `.Include(c => c.AgentUser)` to the query.

**Bug 2 — clicking into a client's conversation from the agent's Portal Messages inbox 404'd.** Same bug class as the earlier Support Article incident above: `PortalMessagesController.Thread(int clientId)` had no attribute route, and the app's conventional route (`{controller=Dashboard}/{action=Index}/{id?}`) only binds a route segment literally named `id`. The inbox linked to `/PortalMessages/Thread/{clientId}`, but since the action's parameter is named `clientId` (not `id`), it never bound — silently defaulting to `0`, which the ownership-scoped lookup then correctly (but unhelpfully) turned into a blank `NotFound()`.

**Fix** (commit `66c76ef`): added `[HttpGet("PortalMessages/Thread/{clientId}")]` above the action, mirroring the `SupportController.Article` fix.

**Prevention rule**: this is now a recurring pattern in this codebase — any action reached via a positional URL segment (not a query string or form field) needs either a parameter literally named `id`, or its own explicit attribute route naming the actual parameter. Grep for `int \w+\)` action parameters reached via link hrefs with a trailing `/{value}` segment when adding a new controller, and default to adding the attribute route up front rather than relying on the conventional route.

## Incident: Portal Documents Accepted Any File With No Validation

**2026-07-18.** A security review of the Client Portal's document-sharing feature (both the agent-side `ClientsController.UploadPortalDocument` and client-side `ClientPortalDocumentsController.Upload`) found no extension allowlist, no content-type validation, and no magic-byte inspection — any file, regardless of actual content, was accepted and stored as long as it was under the 20 MB cap. The uploaded file's browser-supplied `Content-Type` was also trusted and stored as-is. Separately, the underlying Azure Blob container (`portal-documents`) was created public-read (`PublicAccessType.Blob`) with a bare HTTPS URL and no SAS token, meaning a leaked or guessed blob URL could bypass the authenticated Download action entirely.

One initially-suspected issue turned out to already be handled correctly: both Download actions already call `File(stream, contentType, fileName)`, and ASP.NET Core's 3-argument overload sets `Content-Disposition: attachment` automatically — there was no stored-XSS-via-inline-render risk, contrary to first appearances.

**Fix**: added `PortalDocumentValidator` (`src/IPRO.Utility/PortalDocumentValidator.cs`), an extension allowlist (PDF, Word, Excel, JPG/PNG/GIF/WebP, TXT, CSV) paired with a magic-byte signature check per type, modeled directly on `WebsitePagesController.UploadImage`'s existing image-signature validation. The upload controllers no longer trust `IFormFile.ContentType` at all — the content-type stored is always derived from the validated extension. `IBlobStorageService.UploadAsync` gained an explicit `isPrivate` parameter; the `portal-documents` container is now created/kept private (`PublicAccessType.None`), while `agent-logos`/`website-media` (used for the public agent website) remain public, since those must stay directly viewable by anonymous visitors. The container's access policy is re-asserted on every upload and once at app startup, so an already-public container from before this fix gets locked down automatically without a manual Azure step.

**Prevention rule**: any new file-upload endpoint must validate both the file extension against an explicit allowlist AND the file's actual byte signature, and must never trust a client-supplied `Content-Type` header for anything that gets stored or re-served. A blob container should default to private unless the stored content is specifically meant to be publicly, anonymously accessible (e.g. content embedded in a public website).

**Explicitly out of scope**: antivirus/malware scanning of uploaded files (e.g. Azure Defender for Storage) was not added — it requires enabling a paid Azure service, which is a cost/ops decision for the business to make separately, not something to add silently.

## Incident: Portal Documents Had No Delete, And Newsletter "Use This Template" 404'd

**2026-07-18.** Two unrelated bugs, both found by the user during live testing.

**Bug 1 — no way to remove a shared Portal Document.** `ClientsController` and `ClientPortalDocumentsController` had Upload and Download actions but no Delete action, and neither view rendered a delete control — this was missing from the feature's original build, not a regression.

**Fix**: added a `DeletePortalDocument` action to `ClientsController.cs` (agent side — can delete any document on their own clients) and a `Delete` action to `ClientPortalDocumentsController.cs` (client side — scoped to `UploadedByClient == true`, so a client can only remove their own uploads, never a document the agent shared with them). Both call the existing `IBlobStorageService.DeleteAsync` before removing the database row. Delete buttons were added next to each Download link in `Views/Clients/Details.cshtml` and `Views/ClientPortalDocuments/Index.cshtml`.

**Bug 2 — every "Use this template" click on the Newsletter Create page 404'd.** Same bug class as the earlier Support Article and Portal Messages Thread incidents: `NewsletterController.CreateFromTemplate(int templateId)` had no attribute route, so the app's default convention route (`{controller=Dashboard}/{action=Index}/{id?}`) bound the URL's third segment to `id`, not `templateId` — the parameter silently defaulted to `0`, which never matches a real seeded template, so the action always returned `NotFound()`.

**Fix**: added `[HttpGet("Newsletter/CreateFromTemplate/{templateId}")]` above the action.

**Prevention rule**: this is now the third time this exact routing mistake has shipped in this codebase. When adding any GET action reached via a positional URL segment, either name the parameter `id` or add an explicit attribute route — do not rely on the default convention route with a differently-named parameter.

## Incident: Agent Portal Nav Never Highlighted, And Job Scheduler 404'd

**2026-07-18.** Two more UI bugs found by the user during live testing, both in navigation.

**Bug 1 — the agent portal's top nav never showed which page was active.** Unlike the Super Admin sidebar (which had per-item active-state logic, aside from the Reports section bug fixed earlier the same day), `src/IPRO.Web/Views/Shared/_Layout.cshtml`'s top nav had no active-state logic at all — every `<a class="nav-link">` used a static class with no conditional, so Bootstrap's `.nav-link.active` styling never triggered no matter what page was open.

**Fix**: added `currentController`/`currentAction` lookups and a `NavActive(params string[] controllers)` helper to the layout, applied per nav item. Three items (**Clients**, **Follow-ups**, **Calendar**) all route through the same `ClientsController` with different actions, so they needed action-level differentiation (`ClientsSubActive(action)` for the two sub-tabs, with the main **Clients** tab active whenever the action is neither of those) rather than a simple controller-name check.

**Bug 2 — "Job Scheduler" in Super Admin's System section 404'd.** `src/IPRO.Admin` never referenced Hangfire at all — no package, no `AddHangfire`, no dashboard route — so the `/hangfire` link in its own sidebar pointed at a route that simply didn't exist there. Hangfire is fully configured only in `src/IPRO.Web/Program.cs`, which runs the actual background jobs. Initially repointing the link to `https://ipro-prod-web.azurewebsites.net/hangfire` seemed like the fix, but testing that URL directly revealed a **403 Forbidden** — Hangfire's dashboard defaults to `LocalRequestsOnlyAuthorizationFilter` unless a custom `Authorization` array is supplied, and IPRO.Web's `MapHangfireDashboard` call never supplied one, so the dashboard was never actually reachable from any browser, in either app.

**Fix**: added a dashboard-only Hangfire registration to `IPRO.Admin` (same MySQL storage/table prefix as IPRO.Web, but no `AddHangfireServer` — Admin only views/manages the shared queue, it never runs jobs), gated by a custom `SuperAdminDashboardAuthorizationFilter` that checks the same `Role`/`SuperAdmin` claim already used by the existing `"SuperAdmin"` authorization policy. Job Scheduler now lives at `/hangfire` inside the Admin app itself, restricted to the Super Admin role (a Support-role admin gets denied, same as every other Super-Admin-only screen).

**Prevention rule**: when a nav item is added to a shared layout, always apply the same active-state pattern already established for the rest of that nav — don't let a new item silently skip it. And before wiring a cross-app link to another app's route, confirm that route is actually reachable by an external request, not just that it's registered — Hangfire's secure-by-default local-only dashboard is an easy trap since it fails the same way (looks fine in local dev, where requests genuinely are local) as it does in an environment where it's silently broken for everyone.

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

