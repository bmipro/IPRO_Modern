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

