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
5. **Confirm SendGrid's Signed Event Webhook is actually enabled** (`GET /v3/user/webhooks/event/settings/signed` — a non-empty `public_key` means it's on). This bit the project for real on 2026-07-26: see the incident below — steps 1-4 alone won't catch it, since the webhook can be fully, correctly configured per steps 1-3 and still have every single event silently rejected.

## Incident: Delivered/Opened Tracking Silently Broken Since the Signature-Verification Fix Shipped (2026-07-26)

A Drip Campaign step showed `Sent: 1, Delivered: 0, Opened: 0` even though the recipient had genuinely received and opened the email. Confirmed via Azure App Insights that SendGrid's webhook (`/Newsletter/SendGridEvents`) was being called repeatedly (every 30-90s, a retry backlog) — every single call returned **401**.

**Root cause**: the webhook handler correctly requires a valid ECDSA signature on every incoming event (added earlier in the project as a security hardening measure, so a spoofed request can't fabricate delivery/open events) and rejects anything unsigned. But SendGrid's own **Signed Event Webhook** toggle — a separate setting from the basic Event Webhook URL/event-type configuration — had never actually been enabled on the SendGrid account (confirmed via `GET /v3/user/webhooks/event/settings/signed` returning an empty `public_key`). SendGrid was therefore never sending a signature at all, and the app correctly refused every event as a result. This had been silently broken since the signature-verification code shipped — affecting Newsletter open/click tracking too, not just the new Drip Campaign feature that happened to surface it.

**Fix**: `PATCH /v3/user/webhooks/event/settings/signed` with `{"enabled": true}` to turn on signing (SendGrid generates a new public key), then update the `Email:SendGridEventWebhookPublicKey` Azure App Setting to match — an App Setting change triggers an automatic app restart, no manual redeploy needed. Confirmed fixed by checking SendGrid's Email Activity API for a subsequent send: `status: "delivered"`, `opens_count` incrementing.

**Diagnosis method, for next time**: `az monitor app-insights query --app ipro-prod-web-insights -g ipro-production --analytics-query "requests | where name has 'SendGridEvents' | project timestamp, resultCode | order by timestamp desc" -o json` shows exactly what SendGrid is receiving as a response — **always pass `-o json`, not `-o table`**, for this CLI: a table-format query silently rendered as empty output for a genuinely non-empty result set during this investigation, which looked identical to "no requests are arriving at all" and briefly led toward the wrong conclusion (that the webhook wasn't configured, rather than configured-but-rejected).

**Prevention rule**: the basic Event Webhook (`/v3/user/webhooks/event/settings` — URL, enabled event types) and the Signed Event Webhook (`/v3/user/webhooks/event/settings/signed` — the actual signing key) are two independent SendGrid settings. Enabling signature verification in application code is only half the change; confirm the SendGrid-side signing toggle is on too, in the same sitting, or tracking silently breaks with no error visible anywhere in the app's own logs (only in the account's outbound webhook delivery history, which most people never think to check).

## Incident: Custom Form Blocks Never Rendered On A Real Published Page (2026-07-26)

The Custom Form website block (`WebsiteBlockTypes.Form`) worked perfectly in the page editor's "Preview without saving," but a saved, visible block never actually appeared on the real public URL — confirmed by fetching the live page's HTML directly and finding zero trace of the block's heading/body/fields, even though an adjacent block on the same page rendered its own updated content correctly.

**Root cause**: `PublicFormBlockData` lookups (`PublicFormBuilder.BuildAsync`) were only ever wired into `WebsitePagesController.BuildPreviewViewModelAsync` (the preview path). The real public render path, `PublicWebsiteController.BuildWebsiteViewAsync`, built `PollResultsByBlockId` via the equivalent `PollResultsBuilder.BuildAsync` call but never made the matching call for Forms — so `PublicWebsiteViewModel.FormsByBlockId` was always an empty dictionary on the live site, and every template's `Form`-block render branch (`if (Model.FormsByBlockId.TryGetValue(block.Id, out var formData))`) silently found nothing, every time, for every agent. This had been broken since the Custom Form feature first shipped — the preview path being correct made it look like the feature worked in testing.

**Fix**: one additional line in `BuildWebsiteViewAsync`, mirroring the existing Poll call: `var formsByBlockId = await PublicFormBuilder.BuildAsync(_db, website.AgentUserId, currentPage);`, threaded into the returned `PublicWebsiteViewModel`. Verified by fetching the live page's HTML before and after the fix.

**Prevention rule**: when a per-block-type data dictionary needs threading into `PublicWebsiteViewModel`, it has **two** call sites that both need it — the preview path (`WebsitePagesController.BuildPreviewViewModelAsync`) and the real public path (`PublicWebsiteController.BuildWebsiteViewAsync`) — and only testing via "Preview without saving" will never catch the second one being missed, since preview is the one path that was actually wired correctly.

## Incident: Optional Text Fields Rejected As Required Across The Whole App (2026-07-26)

Building a Custom Form, leaving the (explicitly labeled "optional") Placeholder field blank on a Text field produced "The Placeholder field is required." Filling it back in and resubmitting still failed the same way for other optional fields (HelpText, the top-level Description) — and a Section-header field (which never shows Placeholder/HelpText inputs at all) could never be saved under any circumstances.

**Root cause**: two independent ASP.NET Core behaviors compounding. (1) With Nullable Reference Types enabled project-wide and `SuppressImplicitRequiredAttributeForNonNullableReferenceTypes` never set, MVC automatically treats every non-nullable `string` property as implicitly `[Required]` — with no `[Required]` attribute ever written anywhere in the codebase. (2) ASP.NET Core's model binder converts an **empty submitted form value to `null`**, not `""`. Combined: any optional string property a user genuinely leaves blank binds to `null`, which the framework's own implicit-required check then rejects — independent of, and invisible from, any custom validation logic actually written in the controller (`FormsController.ValidateBuilder` never once mentions "Placeholder"; the error came entirely from framework-level model binding, before the action method ever ran).

**Diagnosis method, for next time**: don't guess from reading the model — build an isolated repro. A throwaway minimal ASP.NET Core project (no DB, no auth) with the same POCO shape and a bare action that returns `ModelState` as JSON, POSTed to directly with `curl`, proved definitively (a) that a filled-in value passed and a blank one failed, and (b) that adding `SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true` fixed every failing case at once — before touching the real codebase at all.

**Fix**: one line added to `AddControllersWithViews()` in **both** `IPRO.Web/Program.cs` and `IPRO.Admin/Program.cs`: `.AddMvcOptions(o => o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true)`. This matches how validation is actually meant to work in this codebase (explicit checks in controllers, like `ValidateBuilder`), not implicit framework inference — and fixes the bug class everywhere at once, not just for Forms.

**Prevention rule**: any new optional `string` property on a POST-bound view model in this codebase was silently affected by this until the fix landed — if a future symptom looks like "an optional field with no `[Required]` attribute anywhere is being rejected as required," this is almost certainly the same root cause recurring in a project that ever re-enables the default (e.g. a future .NET upgrade, or a copy-pasted `Program.cs` scaffold from a different project).

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

## Incident: Admin 500 Was A Deploy-Restart False Alarm, And The Nav Fix Itself Had A Bug

**2026-07-18.** Two follow-ups from the "Agent Portal Nav Never Highlighted" deploy (commit `ddb573d`) above, found while verifying it live immediately after deploy.

**False alarm — `/Admin/Login` returned 500 twice in a row right after deploy.** This broke the previously-documented "transient cold-start 500 resolves on retry" pattern (a retry normally fixes it), so it looked like the new Hangfire registration had broken Admin's startup. Downloading the Azure log bundle (`az webapp log download --name ipro-prod-admin --resource-group ipro-prod-admin_group --log-file <path>.zip`) and reading `LogFiles/*_docker.log` showed the container simply cycling through two back-to-back restarts a few minutes apart during the deploy window — no application exception appeared anywhere in `*_containerStream.log` for that period. By the time a clean request landed a few minutes later it returned 200, and the site has been healthy since. **Lesson**: a 500 that survives one retry isn't automatically a code bug — check the docker log's container start/stop timeline before assuming the deploy itself is broken, since a deploy can trigger multiple platform-level container recycles in quick succession.

**Real bug — the new nav active-state fix highlighted the wrong link on the Follow-ups page.** On `/Clients/FollowUps?status=open`, "Clients" stayed highlighted instead of "Follow-ups". The nav check compared `currentAction` against the literal string `"FollowUps"`, but the link's URL is actually served by `ClientsController.FollowUpQueue` (mapped via `[HttpGet("Clients/FollowUps")]`) — a *different*, separately-named action from the per-client `ClientsController.FollowUps(int id, ...)` action used elsewhere. `ViewContext.RouteData.Values["action"]` reflects the real C# method name, not the URL path segment, so the check silently never matched.

**Fix** (commit `ce77bdc`): updated both the "Clients" tab's exclusion check and the "Follow-ups" tab's own check in `src/IPRO.Web/Views/Shared/_Layout.cshtml` to accept either `FollowUpQueue` or `FollowUps` as the active action.

**Prevention rule**: this is a variant of the recurring "action name vs. URL path" trap (see the `id`-parameter incidents above) — it now also applies to nav active-state checks, not just routing. When writing a `currentAction == "X"` check for a nav link, verify `X` against the actual action method name the link's URL resolves to (especially for controllers with multiple attribute-routed actions on similar paths), not against the URL segment text.

## Incident: My Website Publish Button Silently Failed, And A 100%-Off Promo Crashed Subscribe

**2026-07-19.** Two bugs found by the user during live testing of a new promo code.

**Bug 1 — the "Publish" button at the bottom of My Website did nothing, but the one in the top-right corner worked.** Both post to the same `WebsiteController.Publish` action, but the bottom button lives inside the big "Website Settings" `<form>` (`asp-action="Save"`) via an HTML `formaction`/`formmethod` override, while the top-right button is its own small standalone form. That settings form has a `required` Site Title input; for a brand-new site with no title saved yet, clicking the bottom Publish button triggered the browser's native "please fill out this field" validation on Site Title — completely unrelated to publishing — and silently blocked the submit with an easy-to-miss browser tooltip. The top-right button's form has no other fields, so it always worked.

**Fix**: added `formnovalidate` to the bottom Publish button (`src/IPRO.Web/Views/Website/Index.cshtml`) so it always bypasses the settings form's unrelated required-field checks, matching the top-right button's behavior.

**Bug 2 — subscribing with a promo code named `SAVEFREE` (100% off, permanent) 500'd on `/Billing/Subscribe`.** Root cause: PayPal's Subscriptions API rejects a `$0.00` price on a permanent (`REGULAR`) billing cycle outright — only a temporary `TRIAL` cycle may be free, and a permanent 100%-off code has no `RecurringDurationCycles`, so it always built a single `REGULAR` cycle at `$0.00`, which PayPal always rejects with `UNPROCESSABLE_ENTITY`. This wasn't a fluke or a transient PayPal issue — a permanent, fully-discounted recurring promo code can never work against PayPal's Subscriptions API.

**Fix**: two layers. (1) `PromotionCodesController.Edit` (`src/IPRO.Admin`) now blocks *saving* a permanent recurring discount that would bring the restricted package's monthly or annual price to $0 or less, with a message suggesting a limited duration (e.g. `1` = "first cycle free") or a smaller discount instead. (2) `PayPalBillingService.CreateSubscriptionAsync` now catches a PayPal plan-creation failure and returns a normal failed-result message instead of letting the exception reach the unhandled-exception middleware as a raw 500 — defense in depth for any promo code already saved in a bad state before this fix, or any other PayPal plan-creation edge case.

**Prevention rule**: when a discount or price override feeds an external payment provider's API, validate the *worst-case computed price* server-side against that provider's actual constraints at save time (not just "is the input greater than zero") — a percent-based discount can silently produce $0 depending on which package it's later applied to, and provider-side rejections should never be allowed to surface as an unhandled exception to the end user.

## Incident: Preview Looked Broken Because the Image Was Never Saved

**2026-07-23.** A real agent's report, the same day the Preview feature shipped: selected an image for a block, clicked Preview, and the image wasn't there. Investigated by reproducing the exact save via a direct HTTP request (not the UI) to isolate cause. First attempt through Git Bash's `curl` gave a false result — Git Bash silently rewrites any command-line argument starting with `/` into a Windows path (`/images/starter-banners/x.jpg` became `C:/Program Files/Git/images/starter-banners/x.jpg`), which made `NormalizeUrl` correctly reject it as not-http(s) and looked exactly like a broken save. Re-ran with `MSYS_NO_PATHCONV=1` to get a clean request and confirmed: **saving an image works correctly.** The real cause was the same gap as the incident below this one, restated for the specific Image field: picking an image from the dropdown only updates the on-page thumbnail preview via JS — nothing is sent to the server until **Save Block** is clicked. The agent clicked Preview first, so Preview correctly rendered the still-unsaved (imageless) block. Not a bug in the save path; a one-extra-step trap in the UI.

**Fix**: added a **Save & Preview** button next to every block's **Save Block** button (`Views/WebsitePages/Edit.cshtml`, `WebsitePagesController.UpdateBlock`) — a second submit button (`name="preview" value="true"`, `formtarget="_blank"`) that saves the block and redirects straight to the new Preview action in one click, instead of requiring Save then a separate Preview click.

**Prevention rule for testing this app specifically**: never pass a value starting with `/` as a raw Git Bash command-line argument to a Windows-native tool (`curl.exe`, `mysql.exe`, etc.) without `MSYS_NO_PATHCONV=1` — Git Bash's automatic POSIX-path translation will silently mangle it before the tool ever sees it, producing a false bug report. Use `--data-urlencode` plus this env var, or write the payload to a file and reference it, when testing any endpoint that accepts a root-relative URL.

## Incident: Website Block Editor Showed Image/Button Fields That Did Nothing

**2026-07-23.** Found by an agent (via a real QA report, not testing here): a Poll Results block had an image selected in the block editor — visibly previewed there — but it never appeared anywhere on the live site. The agent had set it up "a couple of days ago" and it never worked, which is what made this worth digging into rather than dismissing as user error.

**Root cause**: `Views/WebsitePages/Edit.cshtml`'s block editor shows the same generic form (Heading, Subheading, Body, Image, Button Text, Button Link) for *every* block type, but the public-site renderers (`_ModernManagedPage.cshtml`, `_ClassicManagedPage.cshtml`, `_EditorialManagedPage.cshtml`) only ever read `block.ImageUrl` for **Hero** and **Text** blocks, and only ever read `block.ButtonText`/`block.ButtonUrl` for **Hero**, **Call to Action**, **Review Badge**, and **Text** blocks — confirmed by grepping all three template files directly, not assumed. For every other block type (Services, Contact Form, Newsletter Signup, Testimonial Submission Form, Poll Results, Lead Magnet Download, Agent Info Card), those two fields are complete no-ops: an agent can pick an image, watch it preview correctly right there in the edit form, click Save, and it will never render anywhere. This predates this session's work — it's not something introduced by any of the newer blocks, just never caught before because nothing was checking whether a form field's value was actually consumed downstream.

**Fix**: `Edit.cshtml` now only renders the Image field for Hero/Text blocks, and only renders the Button Text/Link fields for Hero/CallToAction/Reviews/Text blocks, matching exactly what each template actually reads. No data migration — blocks that already had a now-hidden `ImageUrl`/`ButtonText` saved keep that value in the database (harmless, simply unused), the field is just no longer offered where it can't do anything.

**Prevention rule**: a generic, one-size-fits-all edit form across many content types is only honest if every field on it is wired all the way through to every renderer that type can hit. When adding a new field to a shared form, grep the actual render paths for whether it's consumed by *every* type the form covers — an unused form field with no downstream reader looks identical to a working one until someone loses real time to it.

## Incident: Agent Photo Upload Silently Did Nothing (Saved the Wrong Form Instead)

**2026-07-23.** Found by the user during live testing the morning after the Agent Photo feature (item 29) shipped: clicking **Upload Photo** on the My Profile page showed a green "Profile updated." success banner, no error, but no photo — and the file picker reset to "No file selected."

**Root cause**: `Views/Account/Profile.cshtml` nested the small Upload Photo `<form>` (and Remove Photo `<form>`) *inside* the page's main Profile-save `<form>`, so that both cards could sit in the same visual column. Nested `<form>` elements are invalid HTML; browsers silently drop the inner `<form>` start tag during parsing rather than creating an actual nested form, which left the file input and its submit button as plain children of the *outer* Profile form. Clicking "Upload Photo" therefore submitted the whole page to `/Account/Profile` (explaining the "Profile updated." message) instead of `/Account/UploadPhoto` — the file was silently discarded since `AgentProfileViewModel` has no field bound to it. Confirmed on production by checking Azure Storage directly: the `agent-photos` container had never been created, meaning no upload request had ever reached `AzureBlobStorageService.UploadAsync`.

**Why local testing missed this**: local verification posted directly to `/Account/UploadPhoto` with curl, which bypasses HTML parsing entirely and exercises the controller action correctly — but never rendered or submitted the actual page, so the nested-form markup bug never had a chance to surface. The controller logic was never wrong; the page markup around it was.

**Fix**: moved both small forms (`uploadPhotoForm`, `removePhotoForm`) to sit *before* the outer Profile `<form>` opens, each carrying its own `@Html.AntiForgeryToken()`. Their visible controls (file input, Upload/Remove buttons) stay exactly where they were inside the Photo card — nested inside the outer form for layout purposes — but are bound to their real form via the HTML5 `form="uploadPhotoForm"` / `form="removePhotoForm"` attribute, which associates a control with any `<form>` in the document regardless of DOM nesting. Verified in a real browser (not curl) by driving the actual page: attached a `File` via `DataTransfer` to the real `<input type="file">`, clicked the real "Upload Photo" button, and confirmed the response was "Photo updated." (the correct message) rather than "Profile updated."

**Prevention rule**: never nest one `<form>` inside another, even visually/structurally — browsers do not reject this at parse time, they silently drop the inner tag, so the bug produces no error and no console warning, only a wrong-looking success message. When a page needs two independently-submittable actions that must sit inside a shared layout container, keep both `<form>` elements as siblings (or place one outside the other entirely) and use the `form=""` attribute to keep controls visually wherever they need to be. And: **curl-based verification of a controller action is not a substitute for driving the actual rendered page** — it proves the endpoint works, not that the page's markup correctly routes a real click to it.

## Feature: Fully-Comped Promo Codes Bypass PayPal Entirely

**2026-07-19.** Follow-up to the `SAVEFREE` 500 incident above. Rather than only blocking a permanent 100%-off code from being saved, a permanent promo code that discounts **both** the recurring price and the setup fee to $0 (a genuine "free forever" comp) now activates the package directly, without ever creating a PayPal plan, subscription, or order — there is nothing to check out.

**Why not just force PayPal to accept it**: PayPal's Subscriptions API has no representation for a permanent $0 recurring plan (see the incident above); padding the price to $0.01 or similar would be a hack, not a fix, and would misrepresent the transaction. A genuinely free-forever account is more correctly modeled as a comped subscription with no PayPal object attached at all.

**Implementation**: `PayPalBillingService.CreateSubscriptionAsync` now detects this exact case (`RecurringDurationCycles == null` and both the discounted recurring price and effective setup fee are ≤ $0) and skips `GetOrCreatePromoPlanIdAsync` entirely. `BeginPaidChangeAsync` then short-circuits before ever attempting a PayPal call and calls the same `ActivateSubscriptionBillingAsync` helper a real PayPal payment confirmation would call — so invoice creation, promo redemption recording, and paid-invoice email all go through the identical path a paid subscription uses, just without waiting on a PayPal webhook. `PromotionCodesController`'s validation was loosened to match: a permanent 100%-off code can now be saved, but only when the setup fee is *also* fully discounted; a permanent code that zeroes the recurring price while a setup fee remains due is still blocked, since that combination genuinely can't be represented as one PayPal plan.

**Known consequence, by design**: since the agent never goes through PayPal checkout for a fully-comped code, no payment method is ever collected. That's correct for a true free-forever comp — but if that code is later revoked or the agent is meant to convert to paying, they have no card on file and must go through a normal, non-promo Subscribe flow once to attach one.

**Scope note**: this bypass only applies to `SubscriptionChangeType.Subscribe` (a fresh signup). A *temporary* 100%-off code (e.g. "first month free, then full price") is unaffected and still goes through PayPal's `TRIAL` → `REGULAR` billing-cycle mechanism as before, since that path still needs a real payment method on file for PayPal to auto-charge once the trial ends.

## Feature: Portal Appointment Requests Now Create Real Calendar Entries

**2026-07-19.** Explaining "how does the Calendar get populated" to the user surfaced a real, already-documented gap: the Agent Portal Calendar is driven entirely by `ClientFollowUp` rows (`ClientsController.Calendar`), and `PortalRequestsController.SetStatus` marking a Client Portal appointment request "Scheduled" only flipped a status enum — it never created anything the Calendar could show, and the client never learned what time was actually agreed.

**Fix**: `PortalAppointmentRequest` gained `ScheduledAt` and `ClientFollowUpId`. `SetStatus` was replaced with two actions: `Schedule(id, scheduledAt)` lets the agent confirm/adjust the exact date and time (prefilled from the client's preferred date when given, not auto-accepted) and creates a real `ClientFollowUp` linked back to the request, so the appointment now genuinely appears on the Calendar and in Dashboard/Follow-up counts; `Decline(id)` is unchanged in effect but now its own explicit action. Both email the client via the existing `IEmailService.SendDetailedAsync` (same pattern already used for invoice-sent and ticket-reply notifications) — confirming the scheduled time, or a polite decline notice. The client's own Appointments page now shows the confirmed date/time instead of a bare "Scheduled" badge.

**Explicit scope boundary**: rescheduling or cancelling an already-scheduled appointment isn't a new flow — it reuses the existing follow-up edit/delete tools on the client's Details page, since the appointment *is* a follow-up under the hood.

## Feature: Google Calendar Two-Way Sync (Per-Agent, Opt-In)

**2026-07-19.** Follow-up to the appointment-scheduling fix above: agents who live in Google Calendar can now connect it to the Agent Portal Calendar for a full two-way sync, gated by a new togglable `GoogleCalendarSync` package feature (default off, Super Admin enables it per package).

**Architecture**: `GoogleCalendarConnection` stores one encrypted OAuth connection per agent (`IDataProtectionProvider.CreateProtector("IPRO.Web.GoogleCalendar.Tokens.v1")` — same API already used for the public-site captcha token, just a new purpose string). `IGoogleCalendarService` (`src/IPRO.Utility`) is a thin, token-based HttpClient wrapper over the Calendar REST v3 API — no Google SDK dependency, matching how `PayPalBillingService` already hand-rolls its own HTTP calls rather than pulling in a provider SDK. `GoogleCalendarController` (`src/IPRO.Web`) handles the Authorization Code OAuth flow (`Connect`/`Callback`/`Disconnect`); `GoogleCalendarSyncJob` (`src/IPRO.Scheduler`) is a Hangfire recurring job (every 15 minutes) doing the actual two-way reconciliation: new IPRO follow-ups get pushed to Google, and Google's incremental `events.list` (`syncToken`) surfaces anything changed on the Google side since the last run.

**Deliberate design choices worth knowing**:
- Deletes are pushed to Google **immediately** at delete-time (`ClientsController.DeleteFollowUp`), not left to the next poll — a follow-up disappearing from the agent's calendar should feel instant.
- If a Google event linked to a follow-up is deleted directly in Google, IPRO **unlinks** the follow-up (clears `GoogleEventId`) rather than deleting it — a follow-up is CRM history tied to a client, not just a calendar block, so it shouldn't vanish because the calendar side changed.
- Non-client Google events (personal appointments, other meetings) are cached into a separate `ExternalCalendarEvent` table purely for Calendar-view display — they're never forced into the client-scoped `ClientFollowUp` model, which keeps "mark complete," Dashboard counts, and the Follow-up Queue meaningful (only real client follow-ups appear there).
- Editing a follow-up's date/title from within IPRO after it's already synced does not currently propagate to Google — there's no "edit a follow-up" UI in this codebase yet (only add/complete/delete), so that gap doesn't apply in practice; if an edit flow is ever added, it will need to also push the update to Google.

**Requires setup outside IPRO before it can be tested live**: a Google Cloud project with the Calendar API enabled, an OAuth consent screen, and a Web-application OAuth Client ID (redirect URI `https://ipro-prod-web.azurewebsites.net/GoogleCalendar/Callback`) with its Client ID/Secret placed in Azure App Settings as `GoogleCalendar:ClientId`/`GoogleCalendar:ClientSecret`. Google also requires app-review/verification for the Calendar scope before agents outside a manually-added test-user list can connect without an "unverified app" warning — this can take Google days to weeks, independent of when the code itself ships.

**Incident: "not syncing" after OAuth setup looked complete (2026-07-19).** The user finished the OAuth client setup, connected successfully (email showed correctly), then reported neither direction of sync was actually happening. Two separate, sequential root causes, both entirely on the Google Cloud Console side (no code was wrong):
1. **The Google Calendar API itself was never enabled** for the project — creating an OAuth Client ID does *not* enable the underlying API; that's a separate step (APIs & Services → Library → search "Google Calendar API" → Enable). Confirmed via `az webapp log download` + grepping the container log for `GoogleCalendarSyncJob`: the job was running correctly every 15 minutes, but every Google API call failed with `403 SERVICE_DISABLED` / "Google Calendar API has not been used in project ... or it is disabled." The job's per-connection `try/catch` meant this failed silently from the user's perspective — no app-level error, no crash, just nothing happening.
2. **After enabling the API, a second, different error appeared**: `403 PERMISSION_DENIED` / `ACCESS_TOKEN_SCOPE_INSUFFICIENT` on `calendar.v3.Events.Insert`. Root cause: the OAuth consent screen's **Data Access** page (Google Auth Platform → Data Access) had zero scopes registered — "Your sensitive scopes" showed "No rows to display." Requesting a scope in the app's own authorization URL is not sufficient; Google also requires that exact scope (`https://www.googleapis.com/auth/calendar`) to be explicitly added via **Add or remove scopes** on this page before it will actually grant it, regardless of what the OAuth request asks for. Once added and saved, reconnecting (Disconnect → Connect) showed the calendar permission on Google's consent screen for the first time, and both sync directions started working immediately.

**Takeaway for any future OAuth-based integration on this codebase**: Google Cloud OAuth setup has (at least) three independent, easy-to-miss steps beyond creating the OAuth Client ID itself — enabling the actual API, registering the scope on the consent screen's Data Access page, and (for sensitive scopes) adding test users while in Testing publishing status. Missing any one of them fails silently or with a generic-looking error, not an obvious "you forgot step X" message.

## Feature: Poll/Survey System

**2026-07-20.** New **Polls** area (see `DOCS/16_POLLS_AND_SURVEYS.md`): agents build a single-choice poll, send it to the same subscriber base and audience picker newsletters already use, and recipients answer via a one-time public link, no login required. Follow-up work after the user's first live test on production surfaced two real bugs and added two enhancements, all shipped the same day.

**Incident: poll (and every newsletter) link uses the raw `azurewebsites.net` hostname instead of a real domain.** Root cause: `App:BaseUrl` (read by both `PollDispatcher.BuildVoteUrl` and the pre-existing `NewsLetterDispatcher.BuildUnsubscribeUrl`) was never actually set in Azure App Settings for `ipro-prod-web` — it silently fell back to a hardcoded `https://ipro-prod-web.azurewebsites.net`. This was a pre-existing gap on the newsletter side too, just never noticed until the poll feature made a visible link land in a real inbox. **Fix**: bound a real custom domain (`app.iproadvisers.com`, same CNAME + TXT `asuid.` DNS pattern used for `admin.iproadvisers.com`) to `ipro-prod-web` and set `App__BaseUrl=https://app.iproadvisers.com` in Azure App Settings. **Prevention rule**: any time a new outbound-email feature is built, check whether `App:BaseUrl` is actually configured in the target environment — don't assume it is just because the fallback code exists and "looks handled."

**Bug: Results link disappears even after real responses come in.** `Preview.cshtml`'s Results button was gated on `poll.TotalSent > 0` — but `TotalSent` only increments on a *successful* synchronous SendGrid API call, not whenever a send was attempted. A recipient can still receive and answer a poll even if the delivery API call itself reported a non-success (e.g. a transient SendGrid issue), leaving `TotalSent` at 0 while `TotalResponded` climbs — and the button simply never rendered, with no error shown to the agent. **Fix**: gate the Results link on whether any `PollSend` record exists at all (`sends.Any()`), not on a successful-delivery counter. **Prevention rule**: don't gate a "view what happened" UI element on a success-only counter when failure states of the same action can still produce viewable data — gate on "did this happen at all."

**Enhancement: post-vote redirect to the agent's own website.** After voting (or reopening an already-used link), the visitor now sees a few-second countdown before being taken to the agent's published site, with an immediate "go now" link. `PollVoteController.ResolveAgentSiteUrlAsync` prefers a bound custom domain (`AgentDomain` where `IsPrimary && AzureBindingStatus == Bound`) and otherwise falls back to `AgentUser.DomainName` — note this field already stores the **full** temporary domain (e.g. `janedoe.247advisers.com`), not just a subdomain slug; a first draft of this feature appended `.247advisers.com` a second time and was caught in local testing before it shipped.

**Enhancement: Poll Results website block.** Agents can add a "Poll Results" block to any page and pick a sent poll to display; it stays hidden on the live site until that poll clears 10 responses (an explicit anonymity threshold, not a bug — confirmed live when the user reported a freshly-added block "not showing" for a poll that only had 1 response). Follows the same per-block `SettingsJson` config pattern as the Hero block's layout settings, and the same package-gated/hidden-until-ready UX as the Testimonial Submission Form block.

## Feature: Lead-Magnet Download Block, And Two Bugs Found Verifying It Locally

**2026-07-20.** New **Lead Magnet Download** website block (see `DOCS/05_DOMAINS_AND_LEADS.md`): reuses the `WebsiteLead` pipeline and `AgentDocuments` library end to end. Local verification (upload a real file, download it, hit the public unlock link) surfaced two real, pre-existing bugs unrelated to the new feature's own code — both fixed the same day.

**Bug: local blob downloads/deletes fail with "The specified container does not exist" against Azurite, even though the container is right there.** Root cause: `AzureBlobStorageService.DownloadAsync`/`DeleteAsync` parsed a blob URL by splitting its path on the first `/`, assuming real Azure's virtual-hosted-style URLs (`https://account.blob.core.windows.net/container/blob` — account name in the *hostname*). Azurite instead uses path-style URLs (`http://127.0.0.1:10000/devstoreaccount1/container/blob` — account name as an extra *path segment*), so the parser grabbed `devstoreaccount1` as the "container name" instead of the real one. Never surfaced before because nothing had exercised a local blob **download** end-to-end prior to this feature (uploads worked fine — `EnsureContainerAccessAsync`/`UploadAsync` don't parse an existing URL). **Fix**: `ParseBlobUrl` now strips the `BlobServiceClient`'s own base path (`_client.Uri.AbsolutePath`) before splitting — empty for real Azure (no behavior change there), `/devstoreaccount1` for Azurite. **Prevention rule**: a blob-download code path that's only ever been tested against real Azure Storage should be explicitly re-tested against Azurite before trusting "it already works" — upload success doesn't imply download success.

**Bug: a new anonymous public GET endpoint (`DownloadLeadMagnet`) 404'd/showed "Website not published" on a real agent domain, despite working on `localhost`.** Root cause: `Program.cs` has a custom-domain routing middleware that rewrites *every* unrecognized GET path on a non-`localhost`/non-`azurewebsites.net` host into a page-slug lookup (`/PublicWebsite/Page?slug=...`) — it only special-cases a short hardcoded whitelist (`PublicWebsite`, `PublicWebsite/Page`, `PublicWebsite/Page/{slug}`). A brand-new `PublicWebsiteController` GET action isn't in that whitelist by default, so on a real agent domain it never reaches the controller — it gets swallowed and treated as a nonexistent page slug instead. POST actions are unaffected (the middleware only intercepts GET). **Fix**: added an explicit passthrough branch for `PublicWebsite/DownloadLeadMagnet`. **Prevention rule**: any new anonymous **GET** action added to `PublicWebsiteController` needs a matching branch in this middleware, or it will silently break on every real/temporary agent domain while appearing to work fine on `localhost:5000` during dev testing — test new public GET endpoints via Host-header spoofing (`curl -H "Host: agent.247advisers.com" http://127.0.0.1:5000/...`) or the real domain, not just bare localhost.

**Local-dev-only wrinkle, not a bug**: this environment's Azurite version enforces a newer storage API version than the `az storage` CLI defaults to, failing with `The API version ... is not supported by Azurite`. Fixed by adding `--skipApiVersionCheck` to Azurite's `runtimeArgs` in `.claude/launch.json`. Only affects manually driving `az storage blob upload`/`container create` against the local emulator for testing — the app's own Azure SDK calls were never affected.

## Feature: Three Small Follow-ups (Overdue Reminders, Portal Preferences, Targeted Testimonial Requests)

**2026-07-20.** See item 23 in `DOCS/IPRO_Project_Status_And_Roadmap.md` for what shipped. Nothing broke — noting here only because two tables involved (`ClientInvoices`, `TestimonialSubmissions`) are schema-repaired via raw SQL at startup rather than real EF migrations (established pattern for several tables in this codebase — see `EnsureClientInvoiceSchemaAsync`/`EnsureTestimonialSubmissionSchemaAsync` in both `IPRO.Web/Program.cs` and `IPRO.Admin/Program.cs`). Any future column added to either table needs the matching `EnsureTableColumnAsync` call added in **both** apps' `Program.cs`, not a migration — verified locally this time by starting both apps twice and confirming the `ALTER TABLE` calls are no-ops on the second run.

## Incident: Duplicate Success Banners, And A Silent Admin-Role Trap

**2026-07-21.** User reported two things at once: a "Your reply was sent" banner appearing twice stacked on Support tickets in both portals, and the color-palette picker "gone" from IPRO.Admin's Template Editor.

**Bug: TempData Success/Error banners rendered twice on several pages, in both portals.** Root cause: both `_Layout.cshtml` files (Web and Admin) render `TempData["Success"]`/`["Error"]`/`["Warning"]` unconditionally right before `@RenderBody()` — this is the single, correct place for it. But 8 individual views (`Support/Index.cshtml`, `Support/Details.cshtml`, `SupportTickets/Index.cshtml`, `SupportTickets/Details.cshtml`, `WebsiteTemplates/Index.cshtml`, `PromotionCodes/Index.cshtml`, `NewsletterTemplates/Index.cshtml`, `AdminUsers/Index.cshtml`) also had an identical copy-pasted check-and-render block at the top of their own markup, left over from before the layout centralized this. Since `TempData`'s indexer can be read more than once within the same request without clearing, both the layout's render and the view's render fired for the same message — two identical stacked alerts. **Fix**: deleted the redundant blocks from all 8 views, leaving the layout as the sole render location. **Prevention rule**: never add a `TempData["Success"]`/`["Error"]` render block to an individual view — both `_Layout.cshtml` files already handle it globally; a view-level copy is always a duplicate, not a fallback.

**Bug (really a UX trap, not a code bug): new admin accounts silently defaulted to the restricted "Support" role.** `AdminUsersController.Create()` (GET) pre-populated the new-admin form with `Role = AdminRoles.Support`, and the `<select>` in `Create.cshtml` listed Support first with no explicit `selected` — so any SuperAdmin creating a teammate without consciously changing the dropdown got a Support-role account with no visible warning. Support-role admins can't see the "Templates" nav link and are denied `/WebsiteTemplates` outright (`[Authorize(Policy = "SuperAdmin")]` on the whole controller, added in `bc8f359` 2026-07-17 when the two-role admin model was introduced) — which is where the color-palette picker lives (`WebsiteTemplates/Edit.cshtml`), so "the palette is gone" for that account even though the markup was never touched. **Fix**: the dropdown now opens on an empty "-- Choose a role --" placeholder instead of pre-selecting Support, and submitting without an explicit choice is rejected server-side ("The Role field is required."). **Prevention rule**: a role/permission `<select>` should never silently pre-select the more restrictive option — force an explicit choice, or default to the least-surprising option, but never let "forgot to change the dropdown" produce a silently-degraded account.

If a specific admin account is still stuck on Support and needs Templates access, a SuperAdmin can fix it via `/AdminUsers` → edit that user's role directly (no code change needed for that part — this fix only prevents *new* accounts from falling into the same trap).

**Follow-up, same day: "the color palette is gone" turned out to mean something else entirely.** After the Support-role fix shipped, the user's own admin account checked out fine — `Super Admin` badge, `Templates`/`Admin Users` nav links, and the Template Editor's color-swatch picker all rendered correctly in production. The actual ask, clarified with a screenshot, was that the **Agent Portal** (`IPRO.Web`) sidebar has a small 6-swatch accent-color picker (Ocean Blue/Sunset Orange/Forest Green/Slate Gray/Burgundy/Royal Purple) that lets each agent re-theme their own portal chrome (`AgentUser.PortalAccentColor`, set via `AccountController.SetPortalAccentColor`, baked into the login cookie's claims and applied through a `--portal-accent` CSS variable). IPRO.Admin had no equivalent — the user wanted the same self-service theme picker for the SuperAdmin portal, not a bug fix. **Shipped**: mirrored the identical pattern onto `AdminUser.PortalAccentColor`, a `SetPortalAccentColor` action in `AdminController`, and the same swatch row + CSS variable overrides in `IPRO.Admin/Views/Shared/_Layout.cshtml`. One gotcha hit during local verification: `EnsureAdminUserSchemaAsync` called the shared `EnsureTableColumnAsync` helper without first calling `db.Database.OpenConnectionAsync()` — that helper uses the raw ADO.NET connection directly (unlike `ExecuteSqlRawAsync`, which opens/closes on its own), so it threw `Connection must be Open` on a fresh database. Fixed by wrapping the call in the same `OpenConnectionAsync`/`try`/`finally CloseConnectionAsync` pattern already used elsewhere in this file (e.g. `EnsureNewsLetterClickTrackingSchemaAsync`). **Prevention rule**: any new `Ensure*SchemaAsync` function that calls `EnsureTableColumnAsync` must open the connection first — `CREATE TABLE`/`ExecuteSqlRawAsync` calls don't need it, but `EnsureTableColumnAsync` always does.

## Feature: LLM-Composed Reason Line + AI Usage Tracking (2026-07-21)

Shipped the first real LLM call in IPRO (item 26 in the roadmap doc): `AiDailyDigestJob` now asks Claude Haiku 4.5 for a one-sentence "why this matters" line per agent per day, plus a SuperAdmin page (`/AiUsage`) tracking estimated spend against a self-recorded funded balance, with a low-balance reminder banner.

- **Pricing was verified live, not assumed.** Fetched `platform.claude.com/docs/en/about-claude/pricing` directly rather than trusting a remembered figure from an earlier planning conversation — confirmed Haiku 4.5 base rate is $1/MTok input, $5/MTok output (2026-07-21). Cost estimates in `AiUsageDailyLogs` are computed from these constants against the real `input_tokens`/`output_tokens` Anthropic returns per call — not a flat per-call estimate.
- **The Anthropic API key** is an Azure App Service setting (`Ai__AnthropicApiKey` on `ipro-prod-web` only — that's the only app running the Hangfire server that executes the job). Set via `az webapp config appsettings set`, never written to `appsettings.json` or committed.
- **Balance tracking is self-reported, not synced.** There is no read access to Anthropic's actual account balance without a separate Admin API key (different from a regular API key), so `/AiUsage`'s "Total funded" is whatever the SuperAdmin has manually recorded via the "Record a top-up" form — it does not verify against Anthropic's real ledger. If the two ever diverge (e.g. a top-up recorded here didn't actually go through on Anthropic's side, or vice versa), this page will be wrong until corrected by hand.
- **Diagnostic limitation hit this session, worth remembering**: a direct read-only `mysql` query against the production database (`ipro-mysql-prod.mysql.database.azure.com`, using credentials pulled from Azure App Settings) was blocked by the auto-mode permission classifier, even though an *earlier* identical-style query in the same session had been allowed. Don't assume one successful direct-DB read means the pattern is reliably available for the rest of a session — have a fallback plan (in this case: trigger the job and read results back through the app's own new UI instead of querying the table directly).
- **Verification path that worked**: trigger `ai-daily-digest` from `/hangfire/recurring` (checkbox → "Trigger now", both need genuine `.click()` calls, not just setting `.checked`/state — see the earlier Hangfire note in this file), then confirm real token usage landed on `/AiUsage`, then check the actual agent's Dashboard for the rendered reason line. All three layers (job execution, cost tracking, UI rendering) need to check out independently — a green Hangfire "succeeded" status alone doesn't prove the AI call itself succeeded, since the job is designed to swallow AI failures silently and still report success.

## Incident: "Connection must be Open" Recurred, Same Bug, Different Method (2026-07-24)

A new `EnsureTableColumnAsync` call added to `EnsureAgentDailyInsightSchemaAsync` (for the AI Daily Assistant staleness fix, see below) crash-looped `ipro-prod-web` with the exact same failure as the 2026-07-16 incident above — that method had only ever used the self-managing `ExecuteSqlRawAsync` before, so it had never needed the `OpenConnectionAsync`/`CloseConnectionAsync` wrapper, and the existing prevention rule wasn't consulted before adding the new call. Same root cause, same fix (commit `7bff99f`), found the same way (pulling the log archive directly) — this time via `az webapp log download` + reading `LogFiles/StartupLogs/*_failure.log`, since `az webapp log tail` only showed container-orchestration noise for the failure window, not the app's own stdout. **Sharper prevention rule**: before adding an `EnsureTableColumnAsync` call to an *existing* `Ensure*SchemaAsync` method, check whether that specific method already opens a connection — copying the shape of a working example elsewhere in the file is not enough, since not every existing method needs the wrapper.

## Incident: "Connection must be Open" Recurred a Third Time (2026-07-26)

Same bug class as the 2026-07-16 and 2026-07-24 incidents above, a third time: the new `SocialPostDrafts.ScheduledAt` column-add (for the Marketing Calendar feature) called `EnsureTableColumnAsync` inside `EnsureSocialPostSchemaAsync` without the `OpenConnectionAsync`/`CloseConnectionAsync` wrapper — that method had only ever used the self-managing `ExecuteSqlRawAsync` (for its `CREATE TABLE IF NOT EXISTS`) before, exactly the same shape as the 2026-07-24 recurrence. Both `ipro-prod-web` and `ipro-prod-admin` crash-looped (exit code 134) and Azure auto-blocked both sites after repeated cold-start failures. Root-caused via `az webapp log download` + `LogFiles/StartupLogs/*_failure.log` (live-tail via `az webapp log tail` replays stale history first and briefly showed an unrelated-looking 404 before the real 503 crash-loop appeared, which could have been mistaken for a routing bug instead of a startup crash). Fixed by adding the wrapper (commit `f623232`), matching the existing pattern exactly; verified via a fresh log download showing both apps starting cleanly with no further crashes.

**This is now three occurrences of an identical mistake, each caught by the "sharper prevention rule" written after the previous one, but not prevented by it.** A documentation-only prevention rule has now failed twice to stop this from recurring a third time. The actual fix that would make this structurally impossible — having `EnsureTableColumnAsync` open and close its own connection internally, so no caller ever needs to remember the wrapper — was not made here (it touches every existing call site across both `Program.cs` files, a larger change than this incident's fix, and wasn't asked for). Recorded as a real backlog item rather than a third repeat of the same "remember the rule" advice: see "Make `EnsureTableColumnAsync` self-contained" under Recommended Next Tasks.

**Resolved, same day**: `EnsureTableColumnAsync`/`EnsureUniqueIndexAsync` now check `db.Database.GetDbConnection().State` and open/close the connection themselves only if it isn't already open, so a bare unwrapped call — this exact recurring mistake — is now safe by construction rather than by convention. None of the existing wrapped call sites needed to change; the guard just makes their wrapper redundant-but-harmless. See item 42 in the roadmap doc.

## Incident: Agents Locked Out Of Their Own Portal On Their Own Domain (2026-07-24)

A user-reported chain of four related bugs, all on the same day, around the "agents manage their whole portal from their own domain (temporary `*.247advisers.com` or a custom domain like `www.4ipro.com`), not an Azure URL" requirement.

**Bug 1 — the actual root cause.** `Program.cs`'s domain-rewrite middleware (`ShouldRouteToPublicWebsite`) only ever exempted `/Account/*` paths from being treated as a public-website page-slug lookup. A prior fix (see "Fix agent-portal sign-in link" in the roadmap doc) made the site footer's "sign in" link point at `/Account/Login` on whatever domain served the page — which worked for the login page itself, but every *other* real portal route (`/Dashboard`, `/Clients`, `/WebsiteAnalytics`, all of it) was still swallowed into a 404-as-"website not published" once an agent actually signed in on their own domain. Reported directly with a screenshot of a broken "Website not published yet" page after clicking a left-nav link.

**First fix attempt was wrong.** Redirected the login page itself to the platform's own base URL whenever reached from a non-platform host, so the credential POST (and its cookie) would land on a host where the portal actually worked. This "fixed" the crash but directly reversed the stated product requirement — the user caught this immediately: "why are you going back after agreeing to something and implementing it." Fully reverted within the hour.

**Real fix**: replaced the single hand-maintained `/Account/*` exemption with a set built once at startup by reflecting over every MVC controller in the assembly — respecting a controller's own `[Route]` override where one exists (e.g. `TestimonialRequestController` → `"testimonial"`, `PollVoteController` → `"Poll"`, `ClientDocumentController` → `"invoice"`) rather than assuming the class-name convention always holds. Any request whose first path segment matches a real controller's route prefix now falls through to normal MVC routing regardless of host; only genuinely unmatched paths fall back to the public-website lookup. Verified the derived set directly against the built assembly (a throwaway console app referencing `IPRO.Web.csproj`) before deploying, rather than trusting the logic by inspection alone.

**Bug 2, found immediately after the "real fix" deployed**: login succeeded and every explicit portal path worked, but the post-login redirect itself landed the agent back on their own public *homepage*. Cause: `AccountController`'s success-path redirects used `RedirectToAction("Index", "Dashboard")` — and since `Dashboard`/`Index` are literally the *default values* of the app's default route (`{controller=Dashboard}/{action=Index}/{id?}`), ASP.NET Core's URL generator collapses the generated URL down to `"/"`, which on a non-platform host is reserved for the public site homepage by design. The reflection-based fix handled every *explicit* path correctly; this was a distinct bug about never handing the browser a bare `"/"` when a portal destination is meant. Fixed 4 occurrences in `AccountController` plus one hardcoded `href="/"` brand link in the portal's own `_Layout.cshtml` to explicit `/Dashboard` paths (which can't collapse the same way).

**One confusing near-miss along the way**: a reproduction attempt landed between deploying the redirect fix and the user's next real test still showed the old broken behavior. Every layer was re-verified (routing exemption, POST handling, the redirect code itself) and nothing was wrong — the likely explanation, never fully confirmed, is that the attempt landed in the propagation window of the deploy (the same kind of brief post-deploy instability seen with the schema-repair incident above), not a remaining bug. The very next attempt, on both a temporary and a genuine custom domain, worked.

**Prevention rule**: when building anything host- or route-based, prefer deriving the "is this a real app route" answer from the actual route table (reflection, in this case) over a hand-maintained allowlist — a single missed entry in a list like that is exactly what caused Bug 1, and it's the kind of gap that fails silently (looks like "page not found," not an error) rather than loudly.

## Incident: Google Calendar Reconnect Failed With redirect_uri_mismatch (2026-07-28)

Related to the incident above (same underlying "agent portal is reachable from the agent's own custom/temporary domain" fact), but a distinct root cause. Reported as a recurring Google Calendar reconnect failure for agent 22 — the earlier fix that session (adding the redirect URI to Google Cloud Console) didn't hold.

**Root cause**: `GoogleCalendarController.Connect()`/`Callback()` built the OAuth `redirect_uri` via `Url.ActionLink(...)`, which reflects whatever host the request actually arrived on. The agent was on `raniahmotamed.247advisers.com/Account/Profile` (confirmed by asking directly what URL was in the browser's address bar) — a host Google's OAuth client had never had registered, since only the canonical portal host and the `azurewebsites.net` fallback were. Every other feature that builds an absolute portal URL already goes through `PortalUrlHelper.GetAgentPortalBaseUrl` (a fixed, configured base URL) for exactly this reason; `GoogleCalendarController` was the one remaining holdout still deriving it from the live request.

**Why the obvious fix isn't free**: simply swapping in the fixed canonical URL would break session continuity, not just redirect_uri matching. The portal's auth cookie has no explicit `Cookie.Domain` set, so it's host-only — a session established on `raniahmotamed.247advisers.com` doesn't carry over to `app.iproadvisers.com`. If `Callback()` landed on the canonical host, the agent would simply be logged out there.

**Fix**: added a canonical-host bounce at the top of `Connect()` only — if the current host isn't the canonical portal host (or the `azurewebsites.net` fallback), redirect there first (preserving path+query) before doing anything else. This means: if already authenticated on the canonical host, it proceeds straight through; if not, `[Authorize]` naturally bounces to `/Account/Login` there (a one-time re-login, standard behavior for any protected page), and by the time `Url.ActionLink` builds the redirect_uri, `Request.Host` is already correct. `Callback()` needed no changes — Google will only ever hit it at the now-always-canonical registered URI.

**Prevention rule**: any new feature that builds an absolute portal callback/redirect URL (a future OAuth integration, a webhook) should default to `PortalUrlHelper`'s fixed base URL, not `Request.Host`/`Url.ActionLink` — and if the action is `[Authorize]`-gated and could legitimately be reached from a non-canonical host (as this one could), add the same canonical-host bounce rather than assuming the fixed URL alone is sufficient.

## Incident: Hangfire Security Fix Crashed IPRO.Web ("JobStorage instance has not been initialized") (2026-07-24)

Fixing a real finding from the security audit below (`IPRO.Web`'s `/hangfire` dashboard had no explicit authorization filter) caused a genuine production outage, not the false-alarm kind seen in earlier incidents.

**The mistake**: the first fix attempt wrapped `app.MapHangfireDashboard(...)` in `if (app.Environment.IsDevelopment())`, on the reasoning that IPRO.Admin already exposes the same underlying Hangfire storage to authenticated SuperAdmins, so IPRO.Web didn't need its own dashboard in Production at all. This looked safe — `dotnet build` succeeded, and the change only *removed* a route mapping. It crashed the app on every single startup instead: `RecurringJob.AddOrUpdate<T>(...)` (the static Hangfire API, called for every recurring job right below the dashboard line) throws `System.InvalidOperationException: Current JobStorage instance has not been initialized yet` if `JobStorage.Current` isn't set — and in this app, that only gets initialized as a side effect of `MapHangfireDashboard` actually running. Skipping it in Production left every static Hangfire call with nothing to initialize it, and `Program.cs`'s `Main` throws an unhandled exception before the app ever starts listening — exit code 134, every time, deterministically.

**Why the usual verification didn't catch it before deploying**: `dotnet build` only checks compilation, and there's no local dev database available to actually run the app end-to-end before pushing (a standing constraint this whole project). The failure is a runtime-only, framework-internal wiring dependency that isn't visible from reading the diff.

**Found via**: the standard health-check `curl` after deploy returned 500 initially, which was first (correctly, based on the timing) attributed to a normal container warm-up window from the *previous* deploy — but a second, later check still failed with 503. `az webapp log download` + reading `LogFiles/StartupLogs/.sources/*_containerStream.log` showed the actual unhandled-exception stack trace pointing at `Program.cs`'s `RecurringJob.AddOrUpdate` line, well past the container-orchestration noise `docker.log` alone shows.

**Real fix**: restore the unconditional `MapHangfireDashboard` call (so Hangfire's static wiring initializes exactly as before), and instead pass a `DashboardOptions.Authorization` filter that denies access outside `IsDevelopment()` — the same *security* outcome, achieved by adding a filter (the supported, documented way to restrict a Hangfire dashboard) instead of conditionally skipping the mapping call itself. Mirrors the shape `IPRO.Admin` already uses successfully (`SuperAdminDashboardAuthorizationFilter`). Commit `9508c81`.

**Same-night side discovery**: while diagnosing this, found that health checks earlier in the session had been silently checking `app.iproadvisers.com` under the label "admin" — that domain is actually `ipro-prod-web`'s custom domain, not `ipro-prod-admin`'s (which is `admin.iproadvisers.com`, with login at `/Admin/Login` rather than `/Account/Login`). This meant "admin" checks were unknowingly re-checking Web a second time, and Admin itself was never independently verified during that window — it turned out to be healthy throughout, but the gap in the checking process was real. `DOCS/README.md`'s Portal Addresses section has been corrected with both custom domains and their distinct login routes to prevent a repeat.

**Prevention rule**: a fix aimed at *removing* exposure (skipping a route mapping, disabling a feature outright) needs the same "did I verify this actually still starts" scrutiny as one that adds code — "the diff only deletes/guards something" is not a safety guarantee when the removed call has framework-internal side effects that aren't visible from the call site alone. When in doubt, prefer restricting *access* to a capability (an authorization filter) over conditionally skipping the code that wires it up.

## Incident: Website Image Library Let Agents Apply Images To Blocks That Never Render Them (2026-07-28)

An agent applied an uploaded image to a **Contact Form** block via the page editor's "Apply uploaded image to" picker. The admin UI showed the image as successfully applied (no error, checkmark shown), but the image never appeared anywhere on the live public page.

**Root cause**: `WebsitePagesController.ApplyImageToBlock` sets `block.ImageUrl` unconditionally for any block type, and the "Apply uploaded image to" / starter-banner dropdowns (`Views/WebsitePages/Edit.cshtml`) listed **every** block on the page as a valid destination with no filtering. But across all 3 public templates (`_ModernManagedPage`, `_ClassicManagedPage`, `_EditorialManagedPage`), only the **Hero** and **Text** block types ever read `block.ImageUrl` when rendering. Contact Form, Testimonials, Reviews, Polls, Custom Form, Lead Magnet, Did You Know, Article Content, Services, Maps, Call To Action, Newsletter Signup, and Agent Info all silently ignore it — an agent could apply an image to any of these and it would save with no error, then never show up anywhere.

**Fix**: both destination pickers (and their per-item "Use this image"/"Use this banner" buttons) now only list Hero and Text blocks, with a plain message when a page has none — commit `c6707ed`.

**Related discovery — the Image Library is site-wide, not page-scoped, and "Remove" permanently deletes the file**: `WebsitePagesController.DeleteImage` scopes by `AgentWebsiteId`, not the current page, and blanks `ImageUrl` on **every block on every page** that references the same uploaded file, then calls `_blob.DeleteAsync` — an irreversible deletion of the actual file, not just an unlink. An agent removed a stock photo from one page's Image Library (to undo the dead-end case above) and it silently disappeared from a completely different page (`/underdrop1`) that happened to reuse the same upload. The confirm dialog ("Remove this image from the library and any content blocks using it?") does warn about this, but doesn't make clear it's site-wide — worth revisiting the wording if this recurs.

**Prevention rule**: when adding a new website block type, check whether it needs an `ImageUrl` before assuming the Image Library "just works" for it — the picker's filter (`Views/WebsitePages/Edit.cshtml`, `imageDestinationBlocks`) needs updating in lockstep with any template partial that starts reading `block.ImageUrl` for a new block type, or the same dead-end returns for that type.

## Incident: Hero/Text Images Render As Tiny Thumbnails In Split/Image-Left Layouts (2026-07-28)

After fixing the above, an agent applied a real image to a Hero block (Classic template, `layout-split`) and it did display — but as a small ~150x100px thumbnail floating next to the text instead of filling its half of the section, confirmed live on `raniahmotamed.247advisers.com/about`.

**Root cause**: the Hero/Text-image grid layouts (`.cp-hero.layout-split`, `.cp-hero.layout-image-left`, `.cp-text-grid` in Classic; `.mp-split` in Modern) use `<img>` as a **direct CSS Grid item** with `grid-template-columns` in `fr` units. A grid item's `min-width` defaults to `auto`, not `0` — for a replaced element like `<img>`, this interferes with `fr`-track distribution during layout, and the computed column widths came out badly skewed (measured `240px 160px` instead of the intended roughly-proportional split of the available width) with the image column collapsing toward the image's own minimum content size rather than filling its `1.2fr` share. Modern's and Editorial's **Hero** blocks were unaffected because they wrap the image in a container `<div>` (`.mp-hero-media`, `.ep-hero-visual`) that is the actual grid item, with the `<img>` inside sized via explicit `width:100%; height:100%` — a `<div>` grid item doesn't have the same replaced-element minimum-size interaction.

**Fix**: added `min-width:0` to Classic's `.cp-image` (shared by Hero and Text image-left/right) and Modern's `.mp-split > img` (Text image-left/right) — commit `dcb44fe`. This is the standard fix for "flex/grid item won't shrink or fill its track" when the item is a replaced element (img/video) rather than a block container.

**Diagnosis method, for next time**: the public site needs no login, so `javascript_tool` against the live URL is faster than guessing from CSS source — `document.querySelector('.cp-hero-image').getBoundingClientRect()` plus `getComputedStyle(sectionEl).gridTemplateColumns` immediately showed the actual (wrong) column widths and confirmed the fix's mechanism before writing any code.

**Prevention rule**: any future grid-based image-beside-text layout should either (a) wrap the `<img>` in a container `<div>` and size the img to `width:100%; height:100%` inside it (the pattern already used safely for both Hero blocks in Modern/Editorial), or (b) if the `<img>` must be a direct grid/flex item, always pair its `width:100%` with `min-width:0`.

## Incident: SuperAdmin Card/Letter Feature Crashed Both Apps On Startup (2026-07-29)

**Symptom.** Commit `378a5e6` deployed cleanly and then both `ipro-prod-web` and `ipro-prod-admin`
failed to start — exit code **134** (SIGABRT), container never reached a listening state. Azure
blocked both sites after repeated cold-start failures, so they stayed down (~25 min) instead of
self-healing.

**Root cause, found the next morning (2026-07-30) — evidenced, not guessed:**

- `ECardDesignSeeder` and `ELetterTemplateSeeder` both use a check-then-act pattern:
  `AnyAsync()` to see if the table is empty, then `AddRange` + `SaveChangesAsync` if so.
- Both `ipro-prod-web` and `ipro-prod-admin` run **every** seeder on **every** startup, against the
  **same shared MySQL database** (`ipro_crm` — confirmed directly from each app's connection
  string), triggered by the **same git push** — the two GitHub Actions workflows (`on: push:
  branches: [main]`) have no dependency on each other and no coordination with the database.
- `ECardDesigns.Key` and `ELetterTemplates.Key` both carry a **new UNIQUE INDEX**, added in the
  same commit. Two seeders racing past the `AnyAsync()` check on a genuinely-empty new table both
  proceed to insert; whichever commits second hits a duplicate-key violation.
- That exception was unhandled in the original commit. **An unhandled .NET exception escaping
  `Main` on Linux exits via SIGABRT (128+6=134) by design of the CoreCLR PAL** — this is the
  ordinary signature for "something threw before `app.Run()`", not evidence of a native crash or
  memory corruption. This resolves what looked like a scary, exotic failure mode into a mundane
  platform fact: any unhandled exception on Linux looks exactly like this.
- The check-then-act shape already existed, unguarded, in two *older* seeders
  (`NewsLetterTemplateSeeder`, `WebsiteStarterContentSeeder`) for as long as this project has had
  two apps sharing one database. Neither of their tables has a unique key, so the identical race
  there produces silently duplicated rows instead of a crash — this bug class is not new, the
  unique index just gave it, for the first time, a way to surface loudly.
- *Ruled out by evidence, not assumption:* EF's pending-model-changes check. It throws in EF 9;
  this repo is on EF **8.0.0** where it only warns, and DbSet-without-migration is the established
  pattern here (ECards, ELetters, Polls, Forms all ship that way).
- *Found and decompiled while investigating, but NOT the cause:* `Hangfire.Storage.MySql`
  (`2.1.0-beta`)'s own `MySqlObjectsInstaller.Install()` has the *exact same* TOCTOU shape (a bare
  `SHOW TABLES LIKE` check, then a bare `CREATE TABLE` with no `IF NOT EXISTS` and no advisory
  lock — confirmed by decompiling the DLL with `ilspycmd`), called unguarded from `MySqlStorage`'s
  constructor on every app startup. Its sibling `Upgrade()` path, in the same class, *is* correctly
  guarded with a named `GET_LOCK`-based `ResourceLock`. This is a real latent risk in that
  dependency, but very unlikely to be this incident's cause, since Hangfire's own tables have
  existed in this database for months without issue — `Install()` short-circuits to a no-op the
  moment its tables already exist.

**Diagnostic dead ends, for the next time this happens:**
- Application Insights had **no exception and no trace** in the crash window. A same-day control
  query, run hours later while both apps were demonstrably healthy and serving traffic, showed **zero
  requests, zero traces — nothing at all** for either app's resource, even in steady state. The
  connection string, instrumentation key, and `AppId` were all confirmed to match the correct
  resource, and the SDK is wired in code (`AddApplicationInsightsTelemetry()` in `Program.cs`), not
  just via the codeless agent. **Telemetry for these two apps is not flowing at all, for reasons
  still unknown** — treat "App Insights is empty" as a known blind spot for this project, not as
  evidence about whatever you're investigating.
- The container docker log carries orchestrator lines only; the app's own stdout/stderr is not in it.
- No Docker and no MySQL on the dev machine, so startup could not be reproduced locally.

**Recovery.** Revert (`9fac47a`), then **re-run the web deploy** — its first run had independently
failed at `azure/login` with "Failed to fetch federated token from GitHub", so admin recovered and
web did not. Check both workflow runs, not just one.

**Fix (the durable part).** Starter-content seeding and the new schema repair are now wrapped: a
failure logs to `ILogger` *and* stderr and the app boots anyway. This is the isolation H-8/M-11/M-12
already gave the background jobs, finally applied to startup. Structural seeders (entitlements, tax
rates, website templates) are deliberately left unwrapped — an agent cannot function without those.

**Prevention.** A clean build and a locally-rendered component do not exercise *application startup
against a real database*, which is where this failed. Anything touching both `Program.cs` files plus
new tables deserves more than a build check. And no optional-content seeder should ever be able to
abort the process.

**The actual fix (2026-07-30), once the mechanism was known.** `SeedGuard` (`IPRO.DataAccess`) wraps
a seeder's check-and-insert in a MySQL advisory lock (`GET_LOCK`/`RELEASE_LOCK`, keyed per seeder
name), so only one process performs it at a time — the same primitive Hangfire's own `Upgrade()`
path already uses correctly elsewhere in this dependency tree. Applied to all four seeders with the
check-then-act shape: `ECardDesignSeeder`, `ELetterTemplateSeeder`, `NewsLetterTemplateSeeder`,
`WebsiteStarterContentSeeder`. The structural seeders (entitlements, tax rates, website templates)
use a different per-row upsert pattern and were left alone.

One self-caught mistake worth recording: the first draft of `SeedGuard` checked `result is long and
1` to read `GET_LOCK`'s return value, assuming the driver boxes a MySQL integer scalar as a CLR
`long`. That assumption was never verified against a live connection (no local MySQL to test
against), and if wrong, the lock would **silently never acquire** — worse than no lock at all, since
seeding would then quietly stop working on every future deploy with no error anywhere. Rewritten to
`Convert.ToInt64(result) == 1L`, which is correct regardless of whether the driver boxes it as `int`
or `long`.

**Verification, precisely stated.** Every touched seeder's content (all 14 card designs, 4 letter
templates, 4 newsletter templates, all starter-page content) was diffed byte-for-byte against the
pre-change version via a normalized line-multiset comparison — confirmed only the wrapper structure
changed, no seeded content was altered. The lock's actual `GET_LOCK`/`RELEASE_LOCK` round-trip against
live MySQL could **not** be tested before shipping — no local MySQL, and running a program with
production DB credentials was correctly blocked by the environment's safety classifier. It was
instead observed on the real deploy: both apps came up healthy and **neither logged the
`[StarterContentSeeding] FAILED` stderr line**, meaning the new SQL executed without throwing in both
apps, against production, for real — the closest available substitute for a pre-deploy test.

## Incident: Three Cross-App Assumptions, One Feature (2026-07-29)

All three surfaced within an hour of the card/letter admin screens going live, and all three share a
shape that a build and a local render are blind to: **one app serves it, another app consumes it.**

1. **`IBlobStorageService` was never registered in IPRO.Admin.** Artwork upload would have thrown a
   DI error on first use. Worse, `AzureBlobStorageService` throws in its *constructor* when the
   connection string is absent, so injecting it normally would have killed the whole designs screen
   including list and retire — neither of which touches storage. Now resolved lazily, on the upload
   path only.
2. **Blank artwork thumbnails in Admin.** Artwork lives in IPRO.Web's `wwwroot` and `ImageUrl` is
   stored site-relative, so admin resolved it against its own host. Confirmed with curl: all ten
   files 200 on `app.iproadvisers.com`, 404 on `admin.iproadvisers.com`.
   `ECardDesign.AbsoluteImageUrl(baseUrl)` now owns that rule.
3. **Fixing the URL was not enough** — admin's CSP was `img-src 'self' data:`, so the browser blocked
   the cross-origin image anyway. Correct URL, HTTP 200 at the other end, **no server-side error and
   nothing in any log**; the failure existed only in the browser. `img-src` now allows the agent
   portal origin (config-driven) plus blob storage.

**Prevention.** When one app displays an asset another app serves, check three things on *both*
sides: the URL, the credential, and the CSP. Also still outstanding:
`Azure__StorageConnectionString` / `Azure__StorageAccountName` are set on `ipro-prod-web` but **not**
on `ipro-prod-admin`, so uploading new artwork reports storage is not configured.

## Incident: Two Misleading Admin Readouts Sent Investigations The Wrong Way (2026-07-29)

Neither was a functional bug; both cost real time by stating something false.

- **E-Card Designs list.** Two designs had been retired, so the agent picker offered 12 of 14 — which
  looked like a query bug. The status was only visible as a small badge per row. The list now shows
  a count: *"14 design(s), 12 offered to agents."*
- **Audit Log.** The header read *"0 total entries · every action a Super Admin or Support user takes
  across the portal"* while a filter was active, but that count is computed **after** filtering. A
  search that matched nothing was indistinguishable from an empty audit log. The page now shows both
  numbers when filtered and the empty state says which case it is.

**Also fixed:** retiring a design or template fired from a single unguarded click. Both now use the
`js-confirm-submit` / `data-confirm-message` convention this codebase already applies to every other
destructive admin action.

## Feature: Designation Renders Before The Name, Everywhere (2026-07-29)

Reported on an e-card as "Raniah Motamed  Ms."; on the websites the designation sat on the line
below the name.

The complication: `AgentUser.Designation` holds two kinds of value with **opposite** placement rules.
`Ms.` is an honorific and belongs before the name; `CFP` is a credential and belongs after it,
comma-separated. Moving the field unconditionally would have produced "CFP Raniah Motamed".

`AgentNameFormatter.FullName` (IPRO.Entities) recognises honorifics — Mr/Mrs/Ms/Miss/Mx/Dr/Prof/Rev/
Sir/Dame plus M/Mme/Mlle — and places those first; anything else is appended after a comma.
Punctuation is preserved exactly as typed: adding a period produces "Miss." and "Sir.", and "Ms"
without one is correct British style, so the helper decides placement only, never spelling.

Applied at every render site so no template decides this for itself: e-card contact block, e-letter
signature, newsletter footer (which previously showed no designation at all), the Modern/Classic/
Editorial Agent Info blocks, `_ModernProfessional`, `_ClassicSidebar`, `_WebsiteSidebarRail` (this
last file was deleted 2026-07-30 — see "Incident: Sidebar-As-Navigation" below).
Admin → Agents → Details is deliberately untouched — that is a labelled field readout, not a name.

## Incident: Sidebar-As-Navigation Shipped Wrong Twice, Then Turned Out To Be The Wrong Feature (2026-07-30)

**Symptom, first report, with a screenshot.** A "Site Menu" feature (a page-tree sidebar, meant to
sit alongside the header) shipped showing every page link **twice** — two logos, two Home, two About,
two Services, one set in the header and an identical set again in the new sidebar.

**Root cause.** The sidebar was built additively, next to the existing header nav, instead of the two
being coordinated. This was not a small coding slip — it came from misreading what the user actually
wanted from reference material that showed *both* a working top nav and a working side nav on the
same legacy site, without confirming first whether the new sidebar was meant to complement the top nav
or partly replace it. **First fix:** `_PublicNavigation.cshtml` hides its own page links whenever a
sidebar position is active, and the sidebar rail stopped repeating the logo the header already shows.

**Symptom, second report, same day, with a screenshot.** With the duplication fixed, a large blank gap
appeared above all page content on any sidebar-enabled site — hero, services, everything — with the
sidebar itself rendering correctly but visually disconnected above the gap.

**Root cause, evidenced via `getBoundingClientRect`/`getComputedStyle`, not assumed from the
screenshot.** A genuine CSS Grid footgun: `.site-sidebar-rail` had an explicit `grid-column` in the
sidebar-shell grid; its sibling (the actual page content) was left to CSS Grid's automatic placement.
Only one grid item having an explicit position can make the auto-placed sibling land in a whole new
implicit row instead of the empty column beside it. **First fix attempt** — giving the sibling an
explicit `grid-column` too — was deployed, then **re-verified live before being reported as fixed**:
`getBoundingClientRect()` on both elements showed different `top` values and `grid-template-rows`
still computed as two rows, not one. That failure was caught and told to the user directly rather than
being reported as resolved. **Second, correct fix:** pin `grid-row: 1` explicitly on both grid
children, removing all ambiguity for the placement algorithm. Verified afterward the same way —
`getBoundingClientRect().top` matched on both elements, `grid-template-rows` computed as a single row.

**The actual resolution, one day later.** Neither bug fix was the real fix. After living with the
(by-then genuinely working) sidebar for another day, the user's own conclusion — arrived at with
outside input — was that a sidebar acting as a second, parallel navigation system was never the right
shape: top nav should be the only navigation surface, full stop. Two rounds of patching a feature that
kept producing visible defects turned out to be a signal about the feature's fundamental shape, not
about needing a third patch. The whole sidebar-as-navigation mechanism (the Position picker on both
the agent-facing My Website page and the SuperAdmin template editor, `_WebsiteSidebarRail.cshtml`, and
its CSS — including the hard-won `grid-row: 1` fix above) was retired the same week in favor of
top-nav-only navigation, plus a new "Resources" top-nav dropdown built on the page tree's existing
`ParentPageId`/`ChildPages` mechanism, which needed no new navigation code at all.

**A third, independently-found bug, while removing the retired mechanism.** Grepping for every file
that referenced the Position/sidebar plumbing turned up a previously unnoticed instance of the exact
same wrapping-bug class in `_ClassicManagedPage.cshtml`: an extra, unstyled, **never-closed** `<div>`
wrapper around the block-rendering loop, sitting *inside* Classic's own separate, always-on hardcoded
sidebar. Classic could have rendered two nested sidebars whenever a Position override was also set —
never reported by the user, found only by systematically checking every file the retirement touched
rather than assuming the bug pattern was unique to the two files already known to have it.

**Prevention.**
- Don't assume how a new feature "inspired by" reference material relates to existing working
  behavior — confirm explicitly before implementing, especially when the implementation would change
  or remove something that already works. (See `feedback_confirm_before_replacing_working_nav` in the
  assistant's own memory system for the durable version of this lesson.)
- Verify layout claims against real DOM measurements (`getBoundingClientRect`, `getComputedStyle`)
  before reporting a fix as done — a screenshot alone had already produced one false "fixed" claim
  earlier the same day; the second fix attempt was caught as still-broken *before* it reached the user
  a second time, purely because it was re-measured rather than re-screenshotted.
- Repeated bug reports on the same feature, especially from the same root cause class, are worth
  treating as a possible signal about the feature's design, not only as more bugs to patch.

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

