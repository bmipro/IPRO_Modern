# Launch-blocker fix plans — 2026-08-17

Code-grounded implementation plans for the six launch-blocking defects from
`DOCS/AUDIT_RECONCILIATION_2026-08-17.md`, each adversarially challenged before implementation.

**Read the MUST-NOT-BREAK section before touching any of this.** The single most likely way to
cause an outage here is a blanket "make URLs host-aware" sweep: 19 call sites must stay canonical.

## LB-1 — cross-host checkout never activates (WEB-H-1)

### Root cause

PortalUrlHelper is documented as the host-aware URL builder but is implemented host-blind, so every browser-facing absolute URL it produces is pinned to the canonical origin regardless of which host the session actually lives on.

1) The helper. `src/IPRO.Web/Infrastructure/PortalUrlHelper.cs:7-8` is a 2-line pass-through: `GetAgentPortalBaseUrl(IConfiguration) => WebAppUrlHelper.GetWebAppBaseUrl(configuration)`. It takes no `HttpRequest` and cannot know the current host. `WebAppUrlHelper.GetWebAppBaseUrl` (`src/IPRO.Utility/WebAppUrlHelper.cs:17-33`) resolves `App:BaseUrl` -> `App:PortalBaseUrl` -> hardcoded `https://ipro-prod-web.azurewebsites.net`. DOCS/INVARIANTS.md:59-62 states the contract the helper is supposed to satisfy - "any absolute URL that must stay authenticated ... has to be built for the host the agent is actually on (PortalUrlHelper), or bounce through the canonical host first" - and the helper only ever implements the first half's name, never its behaviour.

2) The cookie. `src/IPRO.Web/Program.cs:97-108` configures the agent cookie with HttpOnly/Secure/SameSite=Lax and **no `Cookie.Domain`** (a repo-wide grep for `Cookie.Domain` returns nothing), so `.AspNetCore.Cookies` is host-only. The session cookie (`Program.cs:143-148`) is host-only too, and it carries the signup verify code (`AccountController.cs:24` `RegistrationVerifyCodeSessionKey`, validated at `AccountController.cs:313-317`).

3) The hosts. Agent hosts genuinely serve these routes: `IsNeverShadowedPrefix` (`Program.cs:598-625`, line 600) reserves `account` and `billing` on every host, and `/portal/...` is reserved unconditionally (`Program.cs:641-644`). This is not an edge case the templates discourage - the public site partials deliberately push prospects into signup on the agent's own host: `src/IPRO.Web/Views/PublicWebsite/_ClassicSidebar.cshtml:28`, `_EditorialVisual.cshtml:24`, `_ModernProfessional.cshtml:27`, and `src/IPRO.Web/Views/Preview/Show.cshtml:6` all build `"/Account/Register?firstName=..."` as a relative link.

4) The two money paths that hand that canonical URL to PayPal:
   - `src/IPRO.Web/Controllers/BillingController.cs:31` `BuildBillingActionUrl(string action) => $"{PortalUrlHelper.GetAgentPortalBaseUrl(_configuration)}/Billing/{action}"`, used at :105-106 (`Subscribe`) and :154-155 (`ResumePayment`).
   - `src/IPRO.Web/Controllers/AccountController.cs:426-432`, which does **not** call `BuildBillingActionUrl` - it re-derives `$"{portalBase}/Billing/PayPalReturn"` and `$"{portalBase}/Billing/Cancel"` by hand. This is the codebase's signature defect shape: one bug, two independent copies.

5) The consequence. Both signup and upgrade now take the real PayPal *Subscriptions* path (`src/IPRO.Billing/PayPalBillingService.cs:1462-1516`; `CreatePayPalSubscriptionAsync` at :2416 stamps `return_url`/`cancel_url` at :2480-2481), so buyer approval activates and charges at PayPal server-side. The buyer is then returned to the canonical host where their cookie does not exist; `BillingController` is `[Authorize]` (`BillingController.cs:14`), so `CapturePaymentAsync` (`BillingController.cs:125` -> `PayPalBillingService.cs:289`) never runs. `/Billing/Cancel` is broken identically: `CancelPendingPaymentByOrderAsync` (`BillingController.cs:141`) never runs, leaving a Pending Billing row plus an unpaid Invoice.

Precision on "never activates": there are two accidental backstops, and the plan should not pretend otherwise. (a) `BILLING.SUBSCRIPTION.ACTIVATED` is handled (`PayPalBillingService.cs:850`, :972-1013) and will activate the row - but only if the webhook is registered and delivered, and INVARIANTS.md:117 confirms webhook activation is unverifiable outside production. (b) The cookie handler's `LoginPath` is relative (`Program.cs:100`), so the buyer lands on `/Account/Login?ReturnUrl=%2FBilling%2FPayPalReturn%3Ftoken%3D...` on the canonical host, and `AccountController.cs:81-84` would replay it if they log in right there. Both are fragile: the buyer sees an unexplained login wall with no confirmation and TempData lost; navigating away burns the token; and `AccountController.cs:80` (`MustChangePassword`) discards `returnUrl` entirely. The system should not be depending on either.

### Approach

RECOMMEND FIX (i): preserve the originating host through checkout, owned by PortalUrlHelper. Reject (ii) (bounce to canonical before checkout).

Why (i) and not (ii):
- INVARIANTS.md:59-62 already designates PortalUrlHelper as the mechanism and "built for the host the agent is actually on" as the preferred branch. (i) implements the documented invariant; (ii) would add a second mechanism next to it, which is precisely how the /portal slug collision survived four attempts (Program.cs:199-202, :629-632 both record that failure).
- (ii) is structurally impossible for signup. The checkout starts from the `Register` **POST** (`AccountController.cs:266`, checkout at :427). You cannot bounce a cross-host POST without losing the body, and the form's verify code lives in a host-only session cookie (`AccountController.cs:313-317`, `Program.cs:143-148`). Bouncing earlier - at the `Register` GET (`AccountController.cs:220`) - would defeat the four templates that intentionally sell signup inside the agent's brand (`_ClassicSidebar.cshtml:28`, `_EditorialVisual.cshtml:24`, `_ModernProfessional.cshtml:27`, `Preview/Show.cshtml:6`).
- (ii) is a bad trade for upgrade. The agent is authenticated only on their own host, so bouncing to canonical lands them on `/Account/Login` - the exact failure being fixed, merely moved to before the money instead of after. `GoogleCalendarController.cs:35-52` accepts that cost only because Google enforces a fixed registered `redirect_uri` allowlist. PayPal imposes no such constraint: `return_url`/`cancel_url` are per-order fields (`PayPalBillingService.cs:2480-2481`, :3141-3142). `Admin/Controllers/PayPalSetupController.cs:100` confirms the team already treats these as "Generated by app during checkout".
- (ii) also loses TempData. `BillingController.cs:128-133`, :142-144 write TempData on return; cookie/session TempData is host-scoped, so under (ii) the confirmation is written on a host the buyer's flow did not start on.

Which helper owns it: **PortalUrlHelper**, extended - not a new type, and not the private canonical-bounce helper inside GoogleCalendarController. Give it three members plus keep the existing one:

```
// unchanged - canonical origin, for OUT-OF-BAND links (email, jobs, webhooks) where no request exists
public static string GetAgentPortalBaseUrl(IConfiguration configuration)

// NEW - the single "is this a host we serve" predicate. Takes a bare string so it is
// testable with zero ASP.NET types.
public static bool IsAppHost(string host, IConfiguration configuration)

// NEW - the origin the CURRENT session lives on: the host the host-only cookie was issued for.
// Falls back to GetAgentPortalBaseUrl when the request host is not one of ours.
public static string GetSessionBaseUrl(HttpRequest request, IConfiguration configuration)

// NEW - async overload that additionally accepts a BOUND custom domain, consulted only when the
// config allowlist misses, so the canonical-host case never touches the database.
public static Task<string> GetSessionBaseUrlAsync(HttpRequest request, IConfiguration configuration, IPRODbContext db)

// MOVED here verbatim from GoogleCalendarController - the bounce, for callers whose third party
// requires a pre-registered redirect URI. Now sharing ONE allowlist with the above.
public static string? CanonicalRedirectUrlIfNeeded(HttpRequest request, IConfiguration configuration)
```

`IsAppHost` allowlist, normalised with the same `Trim().Trim('.').ToLowerInvariant()` shape `Program.cs:627` uses:
- equals `new Uri(GetAgentPortalBaseUrl(config)).Host`
- ends with `.azurewebsites.net`
- is in `App:PlatformDomains` (same key read at `Program.cs:668-673`)
- ends with `"." + App:TemporarySiteRootDomain` (default `247advisers.com`, `Program.cs:682-685`)
- is `localhost` / `127.0.0.1` / `::1` (the local dev env at :5100 must keep working - MEMORY: every change runs locally first)

`GetSessionBaseUrl` composes `$"{request.Scheme}://{request.Host.Value}"` - `Host.Value` **including the port**, so `http://localhost:5100` round-trips in the local env - but matches on `request.Host.Host` (portless). Anything not on the allowlist returns canonical, so a forged `Host:` header can never reach PayPal's `return_url` (`appsettings.json:111` still sets `AllowedHosts: "*"`, so HostFiltering is not doing this for us).

`GetSessionBaseUrlAsync` adds bound custom domains, reusing the health predicate already written at `PublicWebsiteController.cs:94-98`: match `AgentDomains` on `DomainName` / `RootDomain` / `WwwDomain` with `AzureBindingStatus == AgentDomainStatus.Bound`. Deliberately **not** gated on `SslStatus` - the buyer's browser is already on that host over TLS, so an SslStatus row lagging reality must not silently divert the return URL.

Then, critically, collapse the duplicate:
- `BillingController.cs:31` becomes `private async Task<string> BuildBillingActionUrlAsync(string action) => $"{await PortalUrlHelper.GetSessionBaseUrlAsync(Request, _configuration, _db)}/Billing/{action}";` (inject `IPRODbContext _db`; the ctor at :23 currently takes `IBillingService, IUnitOfWork, IConfiguration, IPackageEntitlementService` and `IUnitOfWork` has no `AgentDomains` - I checked `IUnitOfWork.cs:11-54`). Call sites :105-106 and :154-155 await it.
- `AccountController.cs:426-432` **deletes** its hand-rolled pair and calls the same producer. The cleanest form is to make `PortalUrlHelper` own the two action paths too - e.g. `PortalUrlHelper.BuildBillingActionUrlAsync(request, config, db, "PayPalReturn")` - so the literal strings `"/Billing/PayPalReturn"` and `"/Billing/Cancel"` exist in exactly one file. `AccountController` already has `_db` (`AccountController.cs:44`), so no ctor change there.
- `GoogleCalendarController.cs:40-52` deletes its private `RedirectToCanonicalHostIfNeeded` and calls `PortalUrlHelper.CanonicalRedirectUrlIfNeeded` at :56. It keeps the bounce (Google's fixed allowlist), but stops carrying a second, narrower answer to "which hosts are ours".

No `/portal` prefix is needed on the return path: `billing` is never-shadowed on every host (`Program.cs:600`), and a gated agent is exempt for `/Billing/...` (`Program.cs:271-272`), so `https://<originating-host>/Billing/PayPalReturn?token=...` routes and authorises correctly on an agent host.

House-rule compliance: no inline scripts are added, so `Context.GetCspNonce()` (`SecurityHeadersMiddleware.cs:73-75`) is not implicated. No prices are touched - `BillingRule`/`IsSetupFeeWaivedOn` are untouched; only the two URL strings passed into `CreateSubscriptionAsync`/`ResumePaymentAsync` change. No schema change, so no startup repair function and no `dotnet-ef` scaffold. No job is modified, so per-item error isolation is unaffected.

### Files to change

- `C:/Users/admin/Documents/Codex/2026-06-30/ca/work/iPro_Project/iPro_Project/IPRO_Modern/src/IPRO.Web/Infrastructure/PortalUrlHelper.cs` — Owns the fix. Keep GetAgentPortalBaseUrl (line 7-8) exactly as-is for out-of-band links. Add: IsAppHost(string host, IConfiguration) - the single allowlist (canonical host from GetAgentPortalBaseUrl, *.azurewebsites.net, App:PlatformDomains, *.App:TemporarySiteRootDomain, localhost/127.0.0.1/::1), normalised the way Program.cs:627 does it; GetSessionBaseUrl(HttpRequest, IConfiguration) returning $"{Scheme}://{Host.Value}" (port INCLUDED, for localhost:5100) when IsAppHost(Host.Host) else canonical; GetSessionBaseUrlAsync(HttpRequest, IConfiguration, IPRODbContext) which additionally accepts a bound custom domain by matching AgentDomains on DomainName/RootDomain/WwwDomain with AzureBindingStatus == AgentDomainStatus.Bound (same predicate as PublicWebsiteController.cs:94-98, but NOT gated on SslStatus), queried only when the config allowlist misses; CanonicalRedirectUrlIfNeeded(HttpRequest, IConfiguration) moved verbatim from GoogleCalendarController.cs:40-52; and BuildBillingActionUrlAsync(HttpRequest, IConfiguration, IPRODbContext, string action) so the literals "/Billing/PayPalReturn" and "/Billing/Cancel" exist in exactly one file. Add a header comment citing DOCS/INVARIANTS.md rule 3 and stating which method is for in-session redirects vs which is for email/jobs.
- `C:/Users/admin/Documents/Codex/2026-06-30/ca/work/iPro_Project/iPro_Project/IPRO_Modern/src/IPRO.Web/Controllers/BillingController.cs` — Inject IPRODbContext into the ctor (line 23 - IUnitOfWork has no AgentDomains, confirmed at IUnitOfWork.cs:11-54). Replace BuildBillingActionUrl (line 31) with an async wrapper over PortalUrlHelper.BuildBillingActionUrlAsync. Await it at the four call sites: :105, :106 (Subscribe) and :154, :155 (ResumePayment). Nothing else in the controller changes - CapturePaymentAsync at :125 and CancelPendingPaymentByOrderAsync at :141 already do the right thing once the buyer arrives authenticated.
- `C:/Users/admin/Documents/Codex/2026-06-30/ca/work/iPro_Project/iPro_Project/IPRO_Modern/src/IPRO.Web/Controllers/AccountController.cs` — Delete the duplicated URL construction at lines 426-432 (var portalBase = PortalUrlHelper.GetAgentPortalBaseUrl(...) plus the two interpolated strings) and call PortalUrlHelper.BuildBillingActionUrlAsync(Request, _configuration, _db, "PayPalReturn"/"Cancel") instead. _db is already injected (line 44). Leave line 105 (password-reset email) alone - it is correctly canonical.
- `C:/Users/admin/Documents/Codex/2026-06-30/ca/work/iPro_Project/iPro_Project/IPRO_Modern/src/IPRO.Web/Controllers/GoogleCalendarController.cs` — Delete the private RedirectToCanonicalHostIfNeeded (lines 40-52); at line 56 call PortalUrlHelper.CanonicalRedirectUrlIfNeeded and Redirect to it when non-null. Keep the bounce semantics and keep the existing explanatory comment (lines 35-39) - it is the reason this caller does NOT take the preserve-the-host branch. Add one line noting Callback (line 67) is only ever reached on the canonical host because that is what is registered with Google.
- `C:/Users/admin/Documents/Codex/2026-06-30/ca/work/iPro_Project/iPro_Project/IPRO_Modern/DOCS/INVARIANTS.md` — Rule 3 (lines 54-67) currently describes the intended behaviour as if it exists. Tighten it into a decision rule now that the code can satisfy it: in-session redirects and third-party return URLs use PortalUrlHelper.GetSessionBaseUrlAsync; email/job/webhook links use PortalUrlHelper.GetAgentPortalBaseUrl; bounce to canonical ONLY when a third party enforces a pre-registered redirect URI (Google OAuth), and never for PayPal, whose return_url is per-order.
- `C:/Users/admin/Documents/Codex/2026-06-30/ca/work/iPro_Project/iPro_Project/IPRO_Modern/tests/IPRO.IntegrationTests/CheckoutHostPreservationTests.cs` — New xUnit file. Covers the helper's allowlist and composition, the bound/unbound custom-domain branch against TestDatabase, the two controller call sites via a recording fake IBillingService, and a source-level guard against a future third copy of the return-URL literals. Detailed in the test plan.
- `C:/Users/admin/Documents/Codex/2026-06-30/ca/work/iPro_Project/iPro_Project/IPRO_Modern/DOCS/AUDIT_RECONCILIATION_2026-08-17.md` — Update the WEB-H-1 entry (lines 74-76) when the fix lands, and record the two facts the entry currently omits: BILLING.SUBSCRIPTION.ACTIVATED (PayPalBillingService.cs:850, :972-1013) is a partial backstop, and the login ReturnUrl replay is a fragile second one - neither of which the flow should depend on.
- `C:/Users/admin/Documents/Codex/2026-06-30/ca/work/iPro_Project/iPro_Project/IPRO_Modern/DOCS/TODO.md` — Per MEMORY (ipro_todo_file_is_the_durable_backlog), reconcile the WEB-H-1 line here in the same commit, and add the separate NewsletterController.cs:606 test-send finding as its own bullet rather than letting it disappear into this fix.

### Sibling call sites

MUST CHANGE - identical defect, browser-facing URL handed back to a host-scoped session:
1. `src/IPRO.Web/Controllers/BillingController.cs:31` `BuildBillingActionUrl` - the reported one. Four emissions: :105 and :106 (`Subscribe` -> `CreateSubscriptionAsync`), :154 and :155 (`ResumePayment` -> `ResumePaymentAsync`). Note :155 (`Cancel`) matters independently: without it `CancelPendingPaymentByOrderAsync` (:141) never runs and the Pending billing + unpaid invoice are orphaned.
2. `src/IPRO.Web/Controllers/AccountController.cs:426-432` - the post-registration checkout. This is the sibling that would be missed: it never calls `BuildBillingActionUrl`, it re-derives `$"{portalBase}/Billing/PayPalReturn"` and `$"{portalBase}/Billing/Cancel"` from `PortalUrlHelper.GetAgentPortalBaseUrl` at :426. It is also the *higher*-volume path (every prospect who signs up from an agent site) and the one where the buyer has no canonical-host session at all.

MUST BE CONSOLIDATED - second, narrower answer to "which hosts are ours" living beside the new one:
3. `src/IPRO.Web/Controllers/GoogleCalendarController.cs:40-52` `RedirectToCanonicalHostIfNeeded`, called at :56 (`Connect`), with `Url.ActionLink(nameof(Callback))` at :62 depending on the bounce having happened. Its allowlist is canonical-host + `.azurewebsites.net` only - it does not know `App:PlatformDomains` or `App:TemporarySiteRootDomain`. No live bug proven (in Development `App:BaseUrl` is `http://localhost:5100`, so the equality check at :45 covers local dev), but leaving two allowlists is the exact drift `Program.cs:629-632` was written to warn about. Also note `Callback` at :67 does not call the bounce; harmless today because Google only ever calls the registered canonical URI, but it should be documented rather than left implicit.
4. `src/IPRO.Web/Program.cs:633-688` `ShouldRouteToPublicWebsite` - do **not** merge it. It answers a different question (public site vs portal) and `Program.cs:629-632` explicitly records that a near-copy of these host checks was deleted for drifting. The new `IsAppHost` should read the same three config keys and reuse the `NormalizeHostForLookup` normalisation shape (`Program.cs:627`) so the two stay legible side by side, and a comment in each should point at the other.

MUST NOT CHANGE - canonical is correct here; every one of these is an out-of-band link with no request or session context, and a careless "make it host-aware" sweep would break them:
5. `src/IPRO.Web/Controllers/AccountController.cs:105` - password-reset email.
6. `src/IPRO.Web/Controllers/ClientsController.cs:148` - client portal activation email.
7. `src/IPRO.Web/Controllers/TestimonialsController.cs:170` - testimonial request link.
8. `src/IPRO.Web/Controllers/ClientInvoicesController.cs:36` - public token document/invoice link.
9. `src/IPRO.Web/Controllers/PublicWebsiteController.cs:969` and :1004 - lead-notification email to the agent.
10. `src/IPRO.Business/Services/EmailConsentService.cs:108` - unsubscribe link.
11. `src/IPRO.Email/NewsLetterDispatcher.cs:164`, `src/IPRO.Email/ECardDispatcher.cs:52`.
12. `src/IPRO.Scheduler/OverdueInvoiceReminderJob.cs:81`.
13. `src/IPRO.Billing/PayPalBillingService.cs:1871` `BuildBillingPageUrl`, used only at :1856 in the downgrade-completion email sent from a job - no HttpRequest exists there.
14. `src/IPRO.Admin/Controllers/ECardDesignsController.cs:46`, `src/IPRO.Admin/Middleware/SecurityHeadersMiddleware.cs:22` - Admin app, different host by design.

OPPOSITE-DIRECTION SIBLING - flag, do not fix in this change:
15. `src/IPRO.Web/Controllers/NewsletterController.cs:606` `GetRequestBaseUrl() => $"{Request.Scheme}://{Request.Host}"`, used at :178 (preview, fine) and :234 (**test-send email**). A test send composed while the agent is on their custom domain bakes that host into image and link URLs inside a real delivered email. Per MEMORY (`project_ssl_custom_domains_pending`), a lapsed custom-domain cert then breaks images in mail already sitting in inboxes. Same family, opposite direction, separate defect - raise it, don't bundle it.

### Risks

1. Host-header injection into PayPal's return_url. This is the risk (i) introduces and (ii) does not, so it must be closed in the same commit. `appsettings.json:111` sets `AllowedHosts: "*"`, so ASP.NET HostFiltering is not screening anything - the allowlist in `IsAppHost` is the only gate. Mitigation: fall back to canonical for any unrecognised host, and compose the URL from the parsed `HostString` (`request.Host.Value`), never from the raw header. Impact is bounded even if bypassed (the attacker can only redirect their own session), but a merchant account emitting arbitrary return domains is a phishing primitive and must not ship.

2. Custom-domain TLS. The buyer now returns to the custom domain rather than the canonical host. MEMORY (`project_ssl_custom_domains_pending`) records that certs expire 2026-10-19 and renewal is manual-but-monitored; `PublicWebsiteController.cs:71-79` documents that the 247advisers subdomain is deliberately kept as the fallback when a cert lapses. Window is small (checkout starts and finishes on the same host, minutes apart) but non-zero. This is why `GetSessionBaseUrlAsync` should gate on `AzureBindingStatus == Bound` and NOT on `SslStatus`: the browser is already on that host over TLS, so a lagging SslStatus row must not divert the return URL to a host with no cookie - that would silently reintroduce the exact bug.

3. Async ripple in BillingController. `BuildBillingActionUrl` becomes async and `BillingController` gains an `IPRODbContext`. `Subscribe` (:99) and `ResumePayment` (:149) are already async, so the ripple is contained, but the ctor signature change (:23) will break any direct construction of the controller in tests.

4. Over-application. The single most likely way to get this wrong is sweeping every `GetAgentPortalBaseUrl` caller to the new host-aware method. That would break password-reset emails (`AccountController.cs:105`), unsubscribe links (`EmailConsentService.cs:108`), invoice links (`OverdueInvoiceReminderJob.cs:81`) and newsletters (`NewsLetterDispatcher.cs:164`) by baking whichever host happened to serve the triggering request into mail that outlives it. The sibling list above splits change/don't-change explicitly for exactly this reason; the XML doc comments on the two methods must state it too.

5. Production config. `appsettings.json:5-10` ships `App:BaseUrl = "https://yourdomain.com"`, which `WebAppUrlHelper.IsConfigured` (`WebAppUrlHelper.cs:35-39`) rejects, so the canonical fallback is `https://ipro-prod-web.azurewebsites.net` unless `App__BaseUrl` is set in Azure. `IsAppHost` derives the canonical host from the same helper, so a missing Azure setting silently narrows the allowlist. Verify `App__BaseUrl` in Azure before deploying, and log at Warning when a checkout falls back to canonical because the request host was not recognised - that log line is how you find a custom domain missing from `AgentDomains`.

6. Local dev. `App:BaseUrl` in `appsettings.Development.json:7` is `http://localhost:5100` (with a port). If `GetSessionBaseUrl` composes from `Host.Host` instead of `Host.Value`, the port is dropped and the local env at :5100 breaks. Explicit test below.

7. Not a full fix for money-in-flight. This closes the host mismatch only. A buyer who approves at PayPal and then closes the tab still leaves an activated PayPal subscription with a Pending local row, healed only by the ACTIVATED webhook. Worth a follow-up backlog item (a reconcile sweep over Pending Billings with a non-empty PayPalSubscriptionId); explicitly out of scope here.

8. Deploy verification. Per MEMORY (`ipro_deploy_green_is_not_live`), confirm the pushed commit via `/health/version`, never `/health`, before believing production is running this change.

### Test plan

The test project already references IPRO.Web and drives real controllers with ASP.NET MVC types (`ClientPortalTokenSecurityTests.cs:1-40`), so `DefaultHttpContext`, `HostString` and `ConfigurationBuilder` are all available. Designing `IsAppHost` to take a plain `string` keeps the core logic testable with no web host at all.

New file: `tests/IPRO.IntegrationTests/CheckoutHostPreservationTests.cs`

A. Pure helper tests (no database, no HTTP) - config built with `new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{ ["App:BaseUrl"]="https://app.iproadvisers.com", ["App:PlatformDomains"]="ipro-prod-web.azurewebsites.net,app.iproadvisers.com,www.iproadvisers.com,iproadvisers.com", ["App:TemporarySiteRootDomain"]="247advisers.com" }).Build()`:
 1. `A_temporary_agent_subdomain_is_kept` - Host `bob.247advisers.com` -> `https://bob.247advisers.com`.
 2. `The_canonical_host_is_kept` - Host `app.iproadvisers.com` -> `https://app.iproadvisers.com`.
 3. `A_platform_domain_is_kept` - Host `www.iproadvisers.com` -> itself, not rewritten to the canonical.
 4. `The_azurewebsites_fallback_host_is_kept`.
 5. `Localhost_keeps_its_port` - scheme `http`, Host `localhost:5100` -> `http://localhost:5100`. This is the local-dev regression (MEMORY: every change runs locally before any deploy) and the one a `Host.Host`-instead-of-`Host.Value` slip breaks.
 6. `An_unknown_host_falls_back_to_canonical` - Host `evil.example.com` -> `https://app.iproadvisers.com`. The security test; make it `[Theory]` over `evil.example.com`, `247advisers.com.evil.com` (suffix-confusion), `bob.247advisers.com.evil.com`, and an empty host.
 7. `Host_matching_ignores_case_and_a_trailing_dot` - `BOB.247Advisers.com.` -> matched (mirrors `Program.cs:627` normalisation).

B. Custom-domain branch, against `TestDatabase.CreateAsync(applyLedgerGuard: false)` (real MySQL schema, the pattern `ClientPortalTokenSecurityTests` uses):
 8. `A_bound_custom_domain_is_kept` - seed an `AgentDomain` with `DomainName = "theirfirm.com"`, `AzureBindingStatus = AgentDomainStatus.Bound`; `GetSessionBaseUrlAsync` with Host `theirfirm.com` -> `https://theirfirm.com`.
 9. `A_bound_www_or_root_alias_is_kept` - seed `WwwDomain = "www.theirfirm.com"` / `RootDomain = "theirfirm.com"` and assert both match, mirroring `PublicWebsiteController.cs:94-98`.
 10. `An_unbound_custom_domain_falls_back_to_canonical` - `AzureBindingStatus = AgentDomainStatus.BindingPending` -> canonical.
 11. `A_bound_domain_with_a_lagging_SslStatus_is_still_kept` - `AzureBindingStatus = Bound`, `SslStatus = BindingPending`. Locks in the deliberate decision not to gate on SslStatus; without this test someone will "tighten" it later and silently restore the defect.
 12. `The_canonical_host_never_queries_the_database` - pass a disposed/throwing context (or count with a logging interceptor) and assert the canonical path short-circuits.

C. Controller call sites - the sibling coverage, with a recording fake `IBillingService` that captures `returnUrl`/`cancelUrl` and returns `new BillingChangeResult { Success = true, RequiresPayment = true, ApprovalUrl = "https://paypal.test/approve" }`:
 13. `Subscribe_from_an_agent_host_returns_to_that_host` - build `BillingController` with a `DefaultHttpContext` whose `Request.Scheme = "https"`, `Request.Host = new HostString("bob.247advisers.com")` and an authenticated `ClaimsPrincipal` carrying `ClaimTypes.NameIdentifier` (required by `BillingController.cs:21`); call `Subscribe(billingRuleId, BillingPeriod.Monthly)`; assert captured return == `https://bob.247advisers.com/Billing/PayPalReturn` and cancel == `https://bob.247advisers.com/Billing/Cancel`.
 14. `ResumePayment_from_an_agent_host_returns_to_that_host` - same assertion via `ResumePayment(invoiceId)`, covering `BillingController.cs:154-155`. This is the call site most likely to be forgotten because the reported symptom is signup.
 15. `Subscribe_from_an_unknown_host_returns_to_the_canonical_host` - the injection case end-to-end through the controller.

D. Sibling guard - the test that makes the "fixed the reported call site, missed the identical sibling" pattern fail loudly. `AccountController.Register` POST is impractical to construct (eleven ctor dependencies, `AccountController.cs:36`), so cover it at the source level instead, in the same spirit as `ops/Test-NoBarePortalLinks.ps1`:
 16. `Only_PortalUrlHelper_may_build_a_PayPal_return_or_cancel_url` - walk `src/**/*.cs`, fail on any occurrence of the literals `/Billing/PayPalReturn` or `/Billing/Cancel` outside `src/IPRO.Web/Infrastructure/PortalUrlHelper.cs`. This is what catches `AccountController.cs:426-432` if it is ever re-hand-rolled.
 17. `GetAgentPortalBaseUrl_is_not_used_to_build_an_in_session_redirect` - assert `PortalUrlHelper.GetAgentPortalBaseUrl` has no remaining callers in `BillingController.cs`, `AccountController.cs`'s registration path, or `GoogleCalendarController.cs`.

E. Live verification (MEMORY: `feedback_verify_before_claiming_fixed` - test via real interaction, not code-reading):
 18. Local env (`ops\Start-LocalEnv.ps1`, Web on :5100): sign in, hit `/portal/Billing`, POST Subscribe, and confirm from `preview_logs`/the PayPal sandbox request that `return_url` is `http://localhost:5100/Billing/PayPalReturn`. Also add a hosts-file entry for a fake `bob.247advisers.com -> 127.0.0.1` and repeat to prove host preservation without needing production.
 19. `./ops/Test-RoutingInvariants.ps1 -AgentHost <agent>.247advisers.com -AgentUser <u> -AgentPassword <p>` - it already probes `/Billing` on an agent host (script lines 211-212); confirm still green.
 20. Production buyer pass. INVARIANTS.md:117 says webhooks cannot reach localhost, so subscription *activation* is only verifiable in production - and the whole point of this defect is what happens after PayPal returns. Run one full sandbox signup starting from an agent host (`someagent.247advisers.com/Account/Register`) and one upgrade started from `/portal/Billing` on that same host, and confirm for each: PayPal returns to the agent host, the buyer stays authenticated, `CapturePaymentAsync` runs, the Billing row goes Active, and the TempData success banner renders. Then repeat the cancel leg and confirm the Pending billing is closed by `CancelPendingPaymentByOrderAsync`. Check `/health/version` for the pushed commit first, per MEMORY (`ipro_deploy_green_is_not_live`).

## LB-2 — consent + dispatcher integrity (JOBS-4, A5-H6, A5-H7/JOBS-3)

### Root cause

THREE ROOT CAUSES, ONE SHAPE: the four send paths each own a private copy of a decision that should have exactly one home.

(a) JOBS-4 — the consent READ is centralized; the consent WRITE never was.
`EmailConsentService.IsSuppressed` (src/IPRO.Business/Services/EmailConsentService.cs:56-85) is genuinely the single decision point and all five senders DO call it: ECardDispatcher.cs:71, ELetterDispatcher.cs:60, PollDispatcher.cs:59, DidYouKnowEmailDispatchJob.cs:109, DripCampaignJob.cs:65 (NewsLetterDispatcher expresses the same rule in SQL at NewsLetterDispatcher.cs:206-210, documented as a deliberate exception at :201-205). So the reported symptom is NOT a missing gate. The gate reads `client.EmailOptOutAt` for every channel (EmailConsentService.cs:64) and additionally `IsNewsletterSubscribed` for Newsletter only (:82). The defect is that only ONE code path in the entire system writes `EmailOptOutAt`: `EmailPreferencesController.SuppressAllAsync` (src/IPRO.Web/Controllers/EmailPreferencesController.cs:146-187). Every other opt-out signal writes only the newsletter flag, which `IsSuppressed` applies to Newsletter alone:
  - SendGrid webhook, newsletter recipients: NewsLetterService.cs:220-235 handles `spamreport`/`unsubscribe`/`group_unsubscribe` and sets `client.IsNewsletterSubscribed = false` (:230). Never `EmailOptOutAt`.
  - SendGrid webhook, everything else: EmailDeliveryTracker.Map (src/IPRO.Business/Services/EmailDeliveryTracker.cs:40-49) maps `spamreport` to `Outcome.Failed` (:47) — it stamps the recipient row and stops. `unsubscribe` and `group_unsubscribe` fall to `_ => Outcome.Ignored` (:48) and are discarded at :58. This is the path for ecard, eletter, poll and didyouknow (:62-79). A spam complaint on any of those four suppresses nothing at all, on any channel.
  - Newsletter footer link: NewsletterController.Unsubscribe (src/IPRO.Web/Controllers/NewsletterController.cs:250-301) sets `IsNewsletterSubscribed = false` at :272 only.
Concrete CASL failure: client presses "this is spam" on a newsletter → IsNewsletterSubscribed=false → next month's birthday e-card (ECard channel, EmailOptOutAt still null) passes IsSuppressed and is delivered. The e-letter too. Poll only stops by accident, because PollDispatcher.cs:201 re-implements the newsletter flag in its audience SQL — the exact re-implementation EmailConsentService.cs:16-19 forbids, and it makes PollDispatcher.cs:60-66 log "0 suppressed" while silently excluding people.
Two artefacts already assert the fixed behaviour as if it existed: src/IPRO.Entities/Client.cs:41-44 ("NewsletterController.Unsubscribe and the RFC 8058 one-click endpoint both set EmailOptOutAt AND clear IsNewsletterSubscribed together") and DOCS/INVARIANTS.md rule 7 ("The unsubscribe sets both it and EmailOptOutAt"). Both are false today for the NewsletterController path.

(b) A5-H6 — the claim is read-then-write in all four dispatchers, and the correct pattern already exists next door.
NewsLetterDispatcher.cs:50-51 reads `send.Status != Scheduled` then writes Sending at :62-64. ECardDispatcher.cs:29-30 then :48-49. ELetterDispatcher.cs:26-27 then :32-33. PollDispatcher.cs:29-30 then :35-37 (it flips two rows — PollSend and PollSurvey). Between the read and the `SaveChangesAsync` there is no lock and no conditional predicate, so two runners both observe Scheduled. All four jobs are `Cron.Minutely` (src/IPRO.Web/Program.cs:376-378, 389-390) and Hangfire does not skip a tick while the previous run is executing, so a send slower than 60s is re-selected by the next tick (NewsLetterDispatchJob.cs:21-23, ECardDispatchJob.cs:24-26, ELetterDispatchJob.cs:24-26, PollDispatchJob.cs:22-24 all select `Status == Scheduled`). The second racer is worse for newsletters: DispatchSendAsync rebuilds the recipient list from scratch (NewsLetterDispatcher.cs:84-101) and mails all of it again. There is a second racer in the request thread too — "Send now" dispatches inline (NewsletterController.cs:353, ECardsController.cs:119, ELettersController.cs:112, PollsController.cs:287) against a row already written as Scheduled, so the minutely tick can collide with the agent's own click. DidYouKnowEmailDispatchJob.cs:79-89 already solves exactly this with a conditional `ExecuteUpdateAsync` + affected-rows check, and its comment block at :51-78 is the design rationale. Nothing generalized it to the other four.

(c) A5-H7 / JOBS-3 — 'Sending' is a terminal black hole and per-recipient results are buffered in memory.
No code anywhere selects `Status == Sending` (verified: the only Sending writes are the four dispatcher lines above plus PollSurvey; EmailActivityController.cs:206 only renders a label). So a crash, deploy or worker recycle after the Sending write leaves the row stuck forever, invisible, and the jobs' `Status == Scheduled` predicates will never pick it up again. Compounding it, every dispatcher mutates recipient rows in the loop but calls SaveChanges once after it: NewsLetterDispatcher.cs:104-152 then :158; ECardDispatcher.cs:61-125 then :135; ELetterDispatcher.cs:43-109 then :116; PollDispatcher.cs:87-134 then :147. An interruption therefore loses the record of who was already mailed, so any resume re-mails them. PollDispatcher's audience-failure path (:45-52) also leaves `PollSurvey.Status` at Sending permanently — it sets the PollSend to Failed and never unwinds the survey row it changed at :36. DidYouKnowEmailQueueItem.ClaimedAtUtc (src/IPRO.Entities/DidYouKnowEmailQueueItem.cs:10-17) plus the stale-claim cutoff (DidYouKnowEmailDispatchJob.cs:36, :42, :82) is the already-proven answer; the four send tables have no equivalent column.

### Approach

ONE CHANGE, TWO NEW SHARED PRIMITIVES, ZERO PARALLEL HELPERS. Both primitives are the missing counterpart of something that already exists, and each puts all four (five) siblings in one file so a missing one is visible on screen.

=== PRIMITIVE 1: the consent WRITE (fixes a) ===
Add to the existing IEmailConsentService (src/IPRO.Business/Services/EmailConsentService.cs) — the file that already owns the READ, so the pair cannot drift:

    Task<SuppressionResult> SuppressAllAsync(Client client, string source);
    Task ResubscribeAsync(Client client);
    public readonly record struct SuppressionResult(bool WasAlreadySuppressed, int QueuedItemsRetired, int EnrollmentsCancelled);

Body is EmailPreferencesController.SuppressAllAsync:146-187 moved verbatim: set EmailOptOutAt/GreetingsOptInAt=null/IsNewsletterSubscribed=false, retire unsent DidYouKnowEmailQueueItems (:156-164), cancel Active DripCampaignEnrollments (:170-177). Make it idempotent — if `EmailOptOutAt.HasValue`, return `WasAlreadySuppressed: true` and skip the sweeps, because the webhook fires repeat spamreports and Index:87 already relies on that check.

Agent notification: IPRO.Business CANNOT reference IPRO.Email (IPRO.Email → IPRO.Business, src/IPRO.Email/IPRO.Email.csproj:13), so `IEmailService` is out of reach. Declare `IUnsubscribeNotifier { Task NotifyAgentAsync(Client client); }` in IPRO.Business, implement it in IPRO.Email wrapping the body of EmailPreferencesController.NotifyAgentAsync:192-220, register in both Program.cs DI, inject it into EmailConsentService and fire only when `!WasAlreadySuppressed`. That is what makes webhook-triggered suppressions notify the agent too — today only the preferences page does.

Rewire every writer (see sibling list). Two shape changes worth calling out:
- EmailDeliveryTracker: add `Unsubscribed` to the `Outcome` enum, map `"unsubscribe" or "group_unsubscribe" => Outcome.Unsubscribed` and keep `spamreport => Outcome.Failed`; add both to `IsTerminal` (:53). Then change the four `Record*Async` methods (:82, :108, :134, :163) to RETURN `int?` clientId, and make the single suppression call once in `RecordAsync` (:55-79) after the switch, for `Outcome.Failed && normalized == "spamreport"` or `Outcome.Unsubscribed`. One call site, four feeders — a fifth sender added to the switch cannot forget it.
- PollDispatcher.cs:201: delete `&& c.IsNewsletterSubscribed` from the audience query. It is a re-implementation of a rule IsSuppressed does not apply to Poll, it over-suppresses, and it makes the suppressed-count log at :60-66 report zero. IsSuppressed(Poll) at :59 becomes the only gate.

=== PRIMITIVE 2: the atomic claim + the sweep (fixes b and c together) ===
They are one mechanism: the claim predicate IS the sweep. New file src/IPRO.DataAccess/SendClaims.cs, sitting beside EmailDeliverySchema.cs for the same reason that file gives at :12-17 — one list both apps agree on. IPRO.DataAccess is referenced by IPRO.Email, IPRO.Scheduler and both apps.

    public static class SendClaims
    {
        public static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(15);   // same window as DidYouKnowEmailDispatchJob.cs:36
        public const int MaxAttempts = 3;

        public static Task<bool> TryClaimNewsletterSendAsync(IPRODbContext db, int id, DateTime now);
        public static Task<bool> TryClaimECardAsync(IPRODbContext db, int id, DateTime now);
        public static Task<bool> TryClaimELetterAsync(IPRODbContext db, int id, DateTime now);
        public static Task<bool> TryClaimPollSendAsync(IPRODbContext db, int id, DateTime now);
        public static Task HeartbeatNewsletterSendAsync(...);   // + one per table
        public static IQueryable<NewsLetterSend> DueNewsletterSends(IPRODbContext db, DateTime now);  // + one per table
        public static Task<int> RetireExhaustedAsync(IPRODbContext db, DateTime now, ILogger log);
    }

Claim body (newsletter shown; the other three are the same eight lines against their table):

    var staleCutoff = now - ClaimTimeout;
    var claimed = await db.NewsLetterSends
        .Where(s => s.Id == id
                 && s.ClaimAttempts < MaxAttempts
                 && (s.Status == NewsLetterSendStatus.Scheduled
                     || (s.Status == NewsLetterSendStatus.Sending
                         && s.ClaimedAt != null && s.ClaimedAt < staleCutoff)))
        .ExecuteUpdateAsync(u => u
            .SetProperty(s => s.Status, NewsLetterSendStatus.Sending)
            .SetProperty(s => s.ClaimedAt, now)
            .SetProperty(s => s.ClaimAttempts, s => s.ClaimAttempts + 1));
    return claimed == 1;

Why this is correct on MySQL via EF Core, concretely: `ExecuteUpdateAsync` emits a single `UPDATE ... WHERE ...` outside the change tracker in its own autocommit statement. InnoDB takes an exclusive row lock; the second runner's UPDATE blocks on that lock, and when the first commits it re-evaluates the WHERE against the now-current row (semi-consistent read), sees Status=Sending with a fresh ClaimedAt, matches nothing, and returns 0. MySQL settles the race, not application timing — the same argument DidYouKnowEmailDispatchJob.cs:51-64 makes. The `ClaimAttempts + 1` term is load-bearing beyond the retry budget: Pomelo 8.0.2 pins MySqlConnector 2.x where `UseAffectedRows` defaults to true (changed rows, not matched rows), so a conditional UPDATE whose SET is a no-op returns 0 and would silently drop the send. Incrementing an integer guarantees at least one column genuinely changes on every successful claim, including a stale reclaim where Status is already Sending.

Ordering rule for every dispatcher: CLAIM FIRST, LOAD SECOND. `_uow` and `_db` are the same scoped IPRODbContext (Program.cs:37, :62; UnitOfWork.cs:13), so an ExecuteUpdate against a row already materialised leaves a stale tracked copy. Claim before any `GetByIdAsync`/`FirstOrDefaultAsync` on the send row, then load fresh.

Two new columns per send table: `ClaimedAt datetime(6) NULL`, `ClaimAttempts int NOT NULL DEFAULT 0` on NewsLetterSends, ECards, ELetters, PollSends. Added as a new `SendClaimColumns` array inside the EXISTING EmailDeliverySchema.EnsureAsync (src/IPRO.DataAccess/EmailDeliverySchema.cs:56-63 is the pattern; :65-112 the loop) — never a `dotnet-ef` scaffold, and never a hand-edited copy in each Program.cs. EmailDeliverySchema.EnsureAsync is already called from both apps (src/IPRO.Web/Program.cs:462 and :547; src/IPRO.Admin/Program.cs:237 and :323), so extending it satisfies the both-apps rule with zero Program.cs edits. Add matching properties to the four entities. Do NOT touch the PollSends CREATE TABLE DDL at Program.cs:1727-1748 (Web) / :1470 (Admin) — the ALTER path covers new and existing databases and editing two copies is the failure mode that file exists to prevent.

The sweep is the job's due-predicate, not a new job:

    Status == Scheduled && ScheduledAt <= now
    || Status == Sending && ClaimedAt != null && ClaimedAt < staleCutoff && ClaimAttempts < MaxAttempts

Each of the four jobs replaces its inline `.Where(...)` with the shared `Due*` query so selection and claim can never disagree. `RetireExhaustedAsync` runs once per job pass, flips Sending rows past the cutoff with `ClaimAttempts >= MaxAttempts` to Failed and logs an error each — the "nobody is told" half of JOBS-3.

Heartbeat: a 5,000-recipient newsletter can outlive a 15-minute cutoff and get stolen mid-run. Every 50 recipients call `Heartbeat*Async` (one `ExecuteUpdateAsync` setting ClaimedAt=now WHERE Id==x AND Status==Sending). Negligible next to a SendGrid round-trip per recipient.

=== INCREMENTAL PERSIST + RESUME (the rest of c) ===
Per dispatcher, in the send loop, after mutating the recipient and in the catch block, `await SaveChangesAsync()` before the next iteration. Cost is one local write per SendGrid HTTP round-trip — irrelevant.
Resume rules so a re-claim never re-mails:
- ECardDispatcher / ELetterDispatcher: already select only `Status == Queued` (ECardDispatcher.cs:56, ELetterDispatcher.cs:38) and their recipient rows are created by the controller. Incremental save alone makes them resumable — no other change.
- NewsLetterDispatcher: guard the build-and-insert block at :84-101 with `if (!await _db.NewsLetterRecipients.AnyAsync(r => r.NewsLetterSendId == send.Id))`; on a resume, load the existing rows and work only `Status == Queued`. Set TotalRecipients only on first build.
- PollDispatcher: same guard around :68-83 on `PollSendId == send.Id`; on resume, work the existing Queued rows. Also make the survey-counter block at :141-145 additive-once (it currently does `+=` and would double-count on resume) and unwind `PollSurvey.Status` on the audience-failure path at :45-52, which today leaves the survey Sending forever.
Terminal writes: after the loop, set the final Status and clear `ClaimedAt` to NULL so a completed row can never look like a live claim.

Order of work: (1) entity properties + EmailDeliverySchema columns; (2) SendClaims.cs; (3) four dispatchers claim/heartbeat/resume/incremental-save; (4) four jobs onto the shared Due predicate + RetireExhaustedAsync; (5) SuppressAllAsync on EmailConsentService + IUnsubscribeNotifier; (6) rewire every consent writer; (7) delete PollDispatcher's IsNewsletterSubscribed filter; (8) correct the two docs that already claim the fixed behaviour.

No CSP nonce surface is touched (no inline scripts) and no billing figure is read or written, so those two house rules are satisfied by not applying.

### Files to change

- `src/IPRO.DataAccess/SendClaims.cs` — NEW. The single home for the atomic claim, the stale sweep and their policy. ClaimTimeout = 15min (matching DidYouKnowEmailDispatchJob.cs:36), MaxAttempts = 3. Four TryClaim*Async methods (NewsletterSend, ECard, ELetter, PollSend), each a conditional ExecuteUpdateAsync matching (Scheduled) OR (Sending AND ClaimedAt < staleCutoff), setting Status=Sending, ClaimedAt=now, ClaimAttempts=ClaimAttempts+1, returning affected==1. Four Heartbeat*Async. Four Due* IQueryable predicates so job selection and claim can never disagree. RetireExhaustedAsync flips exhausted stale rows to Failed and logs. All four siblings live side by side in this one file so a missing one is visible on screen.
- `src/IPRO.DataAccess/EmailDeliverySchema.cs` — Add a SendClaimColumns array — (NewsLetterSends|ECards|ELetters|PollSends) x (ClaimedAt datetime(6) NULL, ClaimAttempts int NOT NULL DEFAULT 0) — following the ConsentColumns pattern at :56-63, and loop it inside the existing EnsureAsync (:65-112). This is the ONLY schema change: the method is already invoked from both apps (Web Program.cs:462/:547, Admin Program.cs:237/:323), so no Program.cs edit and no dotnet-ef scaffold. Optionally add an index on (Status, ClaimedAt) per table for the sweep predicate.
- `src/IPRO.Entities/NewsLetterSend.cs` — Add DateTime? ClaimedAt and int ClaimAttempts, with a comment pointing at DidYouKnowEmailQueueItem.cs:10-17 for why the claim marker is separate from the status.
- `src/IPRO.Entities/ECard.cs` — Add DateTime? ClaimedAt and int ClaimAttempts to ECard (not to ECardStatuses).
- `src/IPRO.Entities/ELetter.cs` — Add DateTime? ClaimedAt and int ClaimAttempts to ELetter.
- `src/IPRO.Entities/PollSend.cs` — Add DateTime? ClaimedAt and int ClaimAttempts.
- `src/IPRO.Email/NewsLetterDispatcher.cs` — DispatchSendAsync: replace the read-then-write at :50-51 and :62-64 with SendClaims.TryClaimNewsletterSendAsync BEFORE any GetByIdAsync (the tracker shares the context). Guard the recipient build-and-insert at :84-101 behind an AnyAsync on NewsLetterSendId so a resumed claim reuses rows instead of rebuilding the list; work only Status==Queued. Add await _uow.SaveChangesAsync() at the end of each loop iteration and in the catch (:104-152), replacing the single save at :158. Heartbeat every 50 recipients. On the terminal write (:154-158) and the audience-failure path (:71-76) also clear ClaimedAt. Point DispatchAsync:34-38 at SendClaims.DueNewsletterSends.
- `src/IPRO.Email/ECardDispatcher.cs` — Replace :29-30 guard and :48-49 write with SendClaims.TryClaimECardAsync before loading the card. Recipient rows already exist and :55-57 already filters Queued, so resume needs only the per-iteration SaveChangesAsync inside the loop (:61-125) instead of the single save at :135. Heartbeat every 50. Clear ClaimedAt on the terminal write (:131-135) and on the unknown-design failure path (:41-45).
- `src/IPRO.Email/ELetterDispatcher.cs` — Identical treatment: claim replaces :26-27 and :32-33; per-iteration save inside :43-109 replacing the single save at :116; heartbeat; clear ClaimedAt on the terminal write at :112-116.
- `src/IPRO.Email/PollDispatcher.cs` — Claim replaces :29-30 and :35-37. Guard recipient creation at :68-83 behind an AnyAsync on PollSendId for resume. Per-iteration save inside :87-134 replacing :147; heartbeat; clear ClaimedAt on the terminal write at :136-147. Make the survey counters at :141-145 additive-once so a resume does not double-count. Unwind PollSurvey.Status on the audience-failure path at :45-52, which today strands the survey in Sending. Separately, DELETE '&& c.IsNewsletterSubscribed' from GetAudienceClientsAsync:201 — it is a forbidden re-implementation of IsSuppressed and it makes the suppressed-count log at :60-66 report zero.
- `src/IPRO.Scheduler/NewsLetterDispatchJob.cs` — Replace the inline Scheduled-only selection at :21-23 with SendClaims.DueNewsletterSends (which also picks up stale Sending rows — this is the sweep), and call RetireExhaustedAsync once per pass. Per-item try/catch at :27-35 already satisfies the isolation rule; keep it.
- `src/IPRO.Scheduler/ECardDispatchJob.cs` — Same: :24-26 becomes SendClaims.DueECards, plus RetireExhaustedAsync.
- `src/IPRO.Scheduler/ELetterDispatchJob.cs` — Same: :24-26 becomes SendClaims.DueELetters, plus RetireExhaustedAsync.
- `src/IPRO.Scheduler/PollDispatchJob.cs` — Same: :22-24 becomes SendClaims.DuePollSends, plus RetireExhaustedAsync.
- `src/IPRO.Business/Services/EmailConsentService.cs` — Add the WRITE counterpart to IsSuppressed in the file that already owns the READ: SuppressAllAsync(Client, string source) returning SuppressionResult(WasAlreadySuppressed, QueuedItemsRetired, EnrollmentsCancelled), plus ResubscribeAsync. Body is EmailPreferencesController.SuppressAllAsync:146-187 moved verbatim (opt-out fields, DYK retire, drip cancel), made idempotent on EmailOptOutAt.HasValue. Inject an optional IUnsubscribeNotifier and fire it only when newly suppressed. Extend the file header comment at :8-19 to state that this file owns both directions.
- `src/IPRO.Business/Interfaces/IUnsubscribeNotifier.cs` — NEW, tiny. Declares NotifyAgentAsync(Client) in IPRO.Business so EmailConsentService can notify without referencing IPRO.Email — IPRO.Email already depends on IPRO.Business (IPRO.Email.csproj:13), so the reverse reference is impossible.
- `src/IPRO.Email/EmailUnsubscribeNotifier.cs` — NEW. Implements IUnsubscribeNotifier using IEmailService; body is EmailPreferencesController.NotifyAgentAsync:192-220 moved, including its best-effort try/catch (a notification failure must never fail the unsubscribe). Registered in both apps' DI.
- `src/IPRO.Business/Services/EmailDeliveryTracker.cs` — The biggest consent gap. Add Outcome.Unsubscribed; map 'unsubscribe' or 'group_unsubscribe' to it in Map:40-49 (they currently hit the Ignored default at :48 and are discarded at :58) and add it to IsTerminal:53. Change the four Record*Async methods (:82, :108, :134, :163) to return int? clientId, and make ONE suppression call in RecordAsync:55-79 after the switch for spamreport/unsubscribe/group_unsubscribe. Inject IEmailConsentService. One call site fed by four recorders is what stops the fifth sender from forgetting.
- `src/IPRO.Business/Services/NewsLetterService.cs` — RecordRecipientEventAsync: replace the client write at :225-234 (IsNewsletterSubscribed only) with _consent.SuppressAllAsync. Re-express CancelSendAsync:119-131 as a conditional ExecuteUpdateAsync so a cancel cannot race a claim. Also give RecordDripStepEventAsync (:268+) the same spamreport/unsubscribe suppression while the file is open — it is the fifth sibling of this method.
- `src/IPRO.Web/Controllers/EmailPreferencesController.cs` — Delete the private SuppressAllAsync:146-187 and NotifyAgentAsync:192-220; OneClick:70 and Index:89 call the service instead. Route the resubscribe branch at :108-114 through ResubscribeAsync so opt-in has one home too. Behaviour must be byte-identical — this controller is the one path that is correct today.
- `src/IPRO.Web/Controllers/NewsletterController.cs` — Unsubscribe: replace client.IsNewsletterSubscribed = false at :272 with _consent.SuppressAllAsync, making Client.cs:41-44 and INVARIANTS.md rule 7 true for the first time. Add '&& c.EmailOptOutAt == null' to whatever feeds the subscriber count at :610. No change needed to SendGridEvents:501-604 or TrackedRecipientArgs:490-496 once suppression lives inside the recorders.
- `src/IPRO.Web/Controllers/PollsController.cs` — CancelSend:297-309: re-express the Status = Cancelled write at :309 as a conditional UPDATE guarded on Status == Scheduled, so cancelling cannot stomp a send a job has already claimed.
- `src/IPRO.Web/Controllers/PublicWebsiteController.cs` — Newsletter signup at :227 and :245 sets IsNewsletterSubscribed = true on a client who may still have EmailOptOutAt set, silently creating a subscriber IsSuppressed will never mail. Route through ResubscribeAsync (a form submission is fresh express consent) or refuse and say so.
- `src/IPRO.Business/Services/ClientService.cs` — GetNewsletterSubscribersAsync:66 filters IsNewsletterSubscribed only. Add '&& c.EmailOptOutAt == null' so the count an agent is shown matches the audience the dispatcher will actually build (NewsLetterDispatcher.cs:206-210).
- `src/IPRO.Web/Controllers/CampaignsController.cs` — SubscriberCount at :547 has the same omission as ClientService.cs:66; same fix.
- `src/IPRO.Utility/ContactImporter.cs` — Line 158 exports Subscribed = c.IsNewsletterSubscribed, ignoring the global opt-out — an export becomes a CASL problem in whatever tool consumes it. Export the effective consent, not the newsletter flag.
- `src/IPRO.Web/Program.cs` — Register IUnsubscribeNotifier -> EmailUnsubscribeNotifier alongside the existing IEmailConsentService registration. NO schema edits here — EmailDeliverySchema.EnsureAsync at :462 and :547 already covers the new columns, and the PollSends DDL at :1727-1748 is deliberately left alone.
- `src/IPRO.Admin/Program.cs` — Same single DI registration, mirroring Web. EmailDeliverySchema.EnsureAsync at :237 and :323 already runs; the PollSends DDL at :1470 is deliberately left alone.
- `src/IPRO.Entities/Client.cs` — The comment at :41-44 states that NewsletterController.Unsubscribe already sets EmailOptOutAt. It does not. Update it in the same commit that makes it true, and extend it to name the webhook paths as writers too.
- `DOCS/INVARIANTS.md` — Rule 7 currently promises 'the unsubscribe sets both'. Restate it as: EmailConsentService owns BOTH directions — IsSuppressed is the only read, SuppressAllAsync the only write — and list the webhook as a first-class opt-out source. Add a short rule that a scheduled send is claimed by conditional UPDATE with an affected-rows check, never read-then-write.
- `DOCS/TODO.md` — Per the durable-backlog convention, reconcile LB-2's three entries (JOBS-4, A5-H6, A5-H7/JOBS-3) and carry forward the adjacent siblings this change deliberately does NOT close: DripCampaignJob/DispatchDripStepAsync have the same unclaimed read-then-write shape, and TestimonialsController still sends with no consent check. Do not drop those bullets silently.
- `tests/IPRO.IntegrationTests/SendClaimRaceTests.cs` — NEW. Two concurrent contexts racing TryClaim across all four tables as a [Theory], stale-reclaim, MaxAttempts retirement, heartbeat, and the explicit UseAffectedRows regression guard. See the test plan.
- `tests/IPRO.IntegrationTests/SendResumeTests.cs` — NEW. Stub IEmailService that dies mid-list; asserts per-recipient results are durable, a resume does not re-mail Sent recipients, a resumed newsletter/poll does not build a second recipient list, and a failed poll leaves neither send nor survey in Sending.
- `tests/IPRO.IntegrationTests/EmailConsentChannelTests.cs` — EXTEND. The existing tests only prove the READ (:44, :57, :68-75). Add the WRITE: a spamreport/unsubscribe/group_unsubscribe on ANY channel sets EmailOptOutAt and suppresses all seven EmailChannel members ([Theory] over all five senders); the newsletter footer link does the same; suppression is idempotent; it retires queued DYK items and cancels drip enrollments; resubscribe is the exact inverse.
- `tests/IPRO.IntegrationTests/ScheduledSendAudienceTests.cs` — EXTEND. Pin the PollDispatcher.cs:201 removal in both directions (a non-newsletter-subscriber is now in the poll audience; an opted-out client is excluded AND counted as suppressed), and assert the agent-facing subscriber count equals the audience the dispatcher builds.

### Sibling call sites

Grouped by which primitive each one must adopt. Every one of these was read; the reported call site is marked REPORTED.

--- A. CONSENT-WRITE siblings (must call SuppressAllAsync) ---
1. src/IPRO.Web/Controllers/EmailPreferencesController.cs:146-187 SuppressAllAsync — the only correct writer today. Delete the private copy, call the service. Callers at :61-71 (RFC 8058 OneClick) and :75-94 (:87-90 link landing) need no change.
2. src/IPRO.Web/Controllers/EmailPreferencesController.cs:99-129 Save — the INVERSE. The resubscribe branch at :108-114 must become `ResubscribeAsync` so opt-in has one home too; the greetings-only branch at :116-122 stays.
3. src/IPRO.Web/Controllers/NewsletterController.cs:250-301 Unsubscribe, client write at :267-276 — MISSING. Replace `client.IsNewsletterSubscribed = false` (:272) with SuppressAllAsync. This is what makes Client.cs:41-44 true.
4. src/IPRO.Business/Services/NewsLetterService.cs:220-235 RecordRecipientEventAsync, spamreport/unsubscribe/group_unsubscribe case — MISSING. Replace the :225-234 block (IsNewsletterSubscribed only) with SuppressAllAsync. REPORTED (webhook, newsletter half).
5. src/IPRO.Business/Services/EmailDeliveryTracker.cs:40-49 Map — MISSING ENTIRELY. `spamreport` → Failed (:47) stamps the row and stops; `unsubscribe`/`group_unsubscribe` hit `_ => Outcome.Ignored` (:48) and die at :58. REPORTED (webhook, all four non-newsletter channels).
6-9. src/IPRO.Business/Services/EmailDeliveryTracker.cs:82 RecordECardAsync, :108 RecordELetterAsync, :134 RecordPollAsync, :163 RecordDidYouKnowAsync — four sibling methods, each holding the ClientId the suppression needs (ECardRecipient.ClientId, ELetterRecipient.ClientId, PollRecipient.ClientId (nullable), DidYouKnowEmailQueueItem.ClientId). Make each return `int?` and suppress once in RecordAsync:55-79.
10. src/IPRO.Business/Services/NewsLetterService.cs:268+ RecordDripStepEventAsync — the drip webhook recorder, same switch shape, no suppression. Adjacent (JOBS-1) but it is the fifth sibling of the same method and should be done in this change while the file is open.
11. src/IPRO.Web/Controllers/NewsletterController.cs:490-496 TrackedRecipientArgs and :501-604 SendGridEvents — the routing table that decides which recorder sees an event. Newsletter at :574-579, drip at :581-586, everything else at :592-599. No code change needed if the suppression lives inside the recorders, but this is the file to check when adding a sender.
12. src/IPRO.Web/Controllers/PublicWebsiteController.cs:227 and :245 — website newsletter signup sets `IsNewsletterSubscribed = true` on a client who may still have EmailOptOutAt set. Today that silently creates a "subscriber" IsSuppressed will never mail. Route through ResubscribeAsync (a form submission IS fresh express consent) or refuse loudly.
13. src/IPRO.Web/Controllers/ClientPortalPreferencesController.cs:43 — client portal newsletter toggle, writes IsNewsletterSubscribed only. Correct as a newsletter-only toggle, but the page must not present it as "all email"; verify the view copy.
14. src/IPRO.Web/Controllers/ClientsController.cs:880 — agent editing a client writes IsNewsletterSubscribed only. Correct (agent-side newsletter flag) — leave, but do not let anyone "harmonize" it into a global opt-out later.

--- B. CONSENT-READ siblings (re-implementations of IsSuppressed, per EmailConsentService.cs:16-19) ---
15. src/IPRO.Email/PollDispatcher.cs:201 — `IsNewsletterSubscribed` in the poll audience SQL. Delete it; IsSuppressed(Poll) at :59 is the gate. Fixes the "0 suppressed" log lie at :60-66.
16. src/IPRO.Email/NewsLetterDispatcher.cs:206-210 — the same rule as SQL, with an explicit justification at :201-205 (it runs over the whole client list). Legitimate; leave it, but it must stay in lockstep with EmailConsentService.cs:64/:82 — if the opt-out rule changes, this is the second place.
17. src/IPRO.Business/Services/ClientService.cs:66 GetNewsletterSubscribersAsync — `IsNewsletterSubscribed` only, no `EmailOptOutAt == null`. Feeds the subscriber count at NewsletterController.cs:610, so the number an agent sees before sending is larger than what will actually go out.
18. src/IPRO.Web/Controllers/CampaignsController.cs:547 SubscriberCount — same omission, same lie.
19. src/IPRO.Utility/ContactImporter.cs:158 — exports `Subscribed = c.IsNewsletterSubscribed`, ignoring the global opt-out. An export handed to another tool becomes a CASL problem outside this system.
20. TestimonialsController (per DOCS/audit-2026-08-14/4-jobs-email.md:45) — sends to client.Email with no IsSuppressed, no channel, no listUnsubscribeUrl. Out of LB-2's scope but it is the same omission; `EmailChannel.TestimonialRequest` already exists unused at EmailConsentService.cs:28.
21. Deliberately exempt, name them so nobody "fixes" them: ClientInvoicesController.cs:331, OverdueInvoiceReminderJob.cs:62, ClientsController.cs:159 (portal invite) — transactional, not marketing.

--- C. CLAIM siblings (read-then-write of a send status) ---
22. src/IPRO.Email/NewsLetterDispatcher.cs:50-51 guard + :62-64 write. REPORTED.
23. src/IPRO.Email/NewsLetterDispatcher.cs:34-38 DispatchAsync — a SECOND read-then-write: it picks the earliest Scheduled send by query, then hands the id to DispatchSendAsync. Harmless once DispatchSendAsync claims, but point it at the shared Due predicate.
24. src/IPRO.Email/ECardDispatcher.cs:29-30 guard + :48-49 write.
25. src/IPRO.Email/ELetterDispatcher.cs:26-27 guard + :32-33 write.
26. src/IPRO.Email/PollDispatcher.cs:29-30 guard + :35-37 write — TWO rows (PollSend and PollSurvey). The survey row is the one nothing unwinds on failure (:45-52).
27. src/IPRO.Scheduler/DidYouKnowEmailDispatchJob.cs:79-89 — already correct; this is the model to copy, not a site to change. Its rationale at :51-78 should be referenced rather than re-written in four places.
28-31. Job selection: NewsLetterDispatchJob.cs:21-23, ECardDispatchJob.cs:24-26, ELetterDispatchJob.cs:24-26, PollDispatchJob.cs:22-24 — all four select `Status == Scheduled` only and all four need the shared Due predicate. Per-item try/catch already present in all four (NewsLetterDispatchJob.cs:27-35 etc.), so the isolation rule is already satisfied.
32-35. Inline "send now" racers, second runner against the minutely tick: NewsletterController.cs:353, ECardsController.cs:119, ELettersController.cs:112, PollsController.cs:287. No change needed once the dispatchers claim — but they are why the race fires in ordinary use, not just on deploys, and they must be exercised by the test.
36. src/IPRO.Business/Services/NewsLetterService.cs:119-131 CancelSendAsync — reads then writes Cancelled, guarded on `Status == Scheduled`. Must be re-expressed as a conditional UPDATE too, or an agent cancelling at the instant a job claims can flip a row that is already mailing.
37. src/IPRO.Web/Controllers/PollsController.cs:297-309 CancelSend — same, writes `Status = Cancelled` at :309.
38. src/IPRO.Scheduler/DripCampaignJob.cs:31-40 selection and :86-99 enrollment advance — identical read-then-write shape on DripCampaignEnrollment with no claim at all, and DispatchDripStepAsync (NewsLetterDispatcher.cs:248-304) has no claim either. Out of LB-2's four, but it is the sixth sibling of the same defect and should be tracked so the next audit does not report it as new.

--- D. SCHEMA / WIRING siblings ---
39. src/IPRO.DataAccess/EmailDeliverySchema.cs:27-33, :35-42, :56-63 — extend with SendClaimColumns; it is already called from both apps at Web Program.cs:462, :547 and Admin Program.cs:237, :323.
40. src/IPRO.Web/Program.cs:1727-1748 and src/IPRO.Admin/Program.cs:1470 — duplicated PollSends CREATE TABLE. Deliberately NOT edited; the ALTER path covers both. Listed so nobody adds a third copy of the column.
41. Docs asserting behaviour that does not exist yet and must be updated in the same commit: src/IPRO.Entities/Client.cs:41-44 and DOCS/INVARIANTS.md rule 7 (line ~127 onward, the "sets both" bullet).

### Risks

1. MySqlConnector affected-rows semantics is the sharpest edge. Pomelo 8.0.2 (Directory.Packages.props:28) resolves MySqlConnector 2.x, where `UseAffectedRows` defaults to true — the driver reports CHANGED rows, not MATCHED rows. Any conditional claim whose SET writes values the row already holds returns 0 and the send is silently dropped. The `ClaimAttempts + 1` term guarantees a real change on every claim including a stale reclaim. Do not "simplify" it away, and do not write a claim that only sets ClaimedAt.

2. Stale-claim theft on long sends. A 15-minute cutoff against a multi-thousand-recipient newsletter means a healthy run gets stolen mid-flight unless the heartbeat lands. If the heartbeat is dropped or the interval set too high, the failure mode is a partial duplicate blast — the exact thing being fixed. Mitigated by: heartbeat every 50 recipients, and by the resume rule (a thief only mails rows still Queued, so the damage is bounded to in-flight recipients rather than the whole list).

3. Change-tracker staleness. `_uow` and `_db` are the same scoped IPRODbContext (Program.cs:37/:62, UnitOfWork.cs:13). An ExecuteUpdateAsync against a row already loaded leaves the tracked copy showing the old Status, and later code reads that stale value. Enforce claim-before-load in all four dispatchers. DidYouKnowEmailDispatchJob.cs:30-31 avoids this by going AsNoTracking + ExecuteUpdate throughout; the four dispatchers cannot, because they mutate recipient entities through the tracker.

4. Bounded duplicates remain possible. A crash between SendGrid returning 202 and the per-recipient save re-sends that one recipient on resume. This is the same trade DidYouKnowEmailDispatchJob.cs:65-78 already documents and accepts — a rare single duplicate beats a silent loss. Say so in the code comment rather than implying exactly-once.

5. ClaimAttempts exhaustion can retire a healthy send. Three deploys landing inside one send's window would mark it Failed with recipients still Queued. MaxAttempts=3 is a guess; the retire path must log loudly enough (agent-visible, not just ILogger) that a wrongly-failed send is noticed, and the Failed row must remain resumable by hand.

6. Suppression is now genuinely global, and agents will feel it. One spam complaint on a newsletter stops that client's e-cards, e-letters, polls and Did-You-Know mail permanently. That is the CASL-correct behaviour and INVARIANTS.md rule 7 already promises it — but it is a visible product change, and the IUnsubscribeNotifier path is what keeps it from looking like a bug to the agent. Also make sure the greetings exemption still survives: EmailConsentService.cs:66-77 requires BOTH SendAfterUnsubscribe and GreetingsOptInAt, and SuppressAllAsync clears GreetingsOptInAt — so a spam complaint correctly kills birthday cards too.

7. Webhook replay. SendGrid re-delivers events on its own schedule, so SuppressAllAsync will be called repeatedly for the same client. Without the `WasAlreadySuppressed` short-circuit, every replay re-runs the DYK and drip sweeps and re-fires the agent notification. Idempotency is not optional here.

8. Deleting `IsNewsletterSubscribed` from PollDispatcher.cs:201 WIDENS the poll audience: clients who never opted into the newsletter but have not opted out will now receive polls. That is what IsSuppressed(Poll) says should happen and it removes a forbidden re-implementation, but it is a live behaviour change on the next poll send and the owner should be told before it ships, not after.

9. Test-harness blind spot. TestDatabase.cs:40-47 uses `EnsureCreatedAsync` off the entity model, so the new columns appear in tests automatically and the EmailDeliverySchema ALTER path is NEVER exercised by the suite. The repair must be verified by hand against the local dev MySQL (ops\Start-LocalEnv.ps1, per memory) on a database that predates the columns — and against one that already has them, to confirm the ColumnExistsAsync guard.

10. Two long-lived docs assert the fixed behaviour today (Client.cs:41-44, INVARIANTS.md rule 7). If the code lands and they are not updated in the same commit, the next auditor reads them as evidence and the defect goes back to "already fixed" — the exact pattern DOCS/AUDIT_RECONCILIATION_2026-08-17.md line 55 describes.

11. Per-recipient SaveChanges multiplies write volume by the recipient count. Negligible against one SendGrid HTTP call per recipient, but it does mean a failing database now fails the send loudly mid-run instead of at the end. That is the intended trade.

### Test plan

All in tests/IPRO.IntegrationTests. The harness (TestDatabase.cs:22-51) creates a throwaway REAL MySQL database per test from the entity model, so genuine concurrency, row locks and affected-rows semantics are all testable — InvoiceNumberRaceTests.cs is the precedent for a race test against real MySQL and should be the template for style.

NEW: SendClaimRaceTests.cs — covers (b), the headline.
- `Two_concurrent_claims_of_the_same_send_produce_exactly_one_winner` — parameterized [Theory] over all four tables. Seed one row in Scheduled. Open TWO separate IPRODbContexts from the same TestDatabase (separate connections — this is what makes it a real race, not a change-tracker artifact). Fire both `SendClaims.TryClaim*Async` calls with `Task.WhenAll`. Assert exactly one returns true, the stored Status is Sending, and ClaimAttempts == 1. Running this as a Theory over all four is the structural defence against the "fixed one, missed the sibling" pattern: adding a fifth sender without a claim fails a test that already exists.
- `A_claim_whose_row_is_already_Sending_and_fresh_is_refused` — ClaimedAt = now, expect false.
- `A_claim_whose_row_is_Sending_past_the_stale_cutoff_succeeds_and_increments_attempts` — ClaimedAt = now - 16min, expect true and ClaimAttempts == 2. This is the (c) sweep.
- `A_stale_row_at_MaxAttempts_is_refused_and_retired` — ClaimAttempts = 3, expect TryClaim false and RetireExhaustedAsync flips it to Failed.
- `A_reclaim_of_a_row_already_Sending_still_reports_one_affected_row` — the explicit UseAffectedRows regression guard. Assert the ExecuteUpdate returns 1 even though Status is unchanged by the SET, proving ClaimAttempts is doing its job. Without this test the MySqlConnector footgun is invisible until production.
- `Heartbeat_moves_ClaimedAt_forward_only_while_Sending` — and is a no-op on a Sent row.

NEW: SendResumeTests.cs — covers (c), the persist half. Uses a stub IEmailService that succeeds for the first N recipients then throws, simulating a mid-flight kill.
- [Theory] over ecard/eletter/poll/newsletter: dispatch to 5 recipients with the stub failing at #3; assert recipients 1-2 are persisted as Sent IN THE DATABASE (re-read on a fresh context, not the tracked one), not just in memory. Today all four lose them.
- `A_resumed_send_does_not_re_mail_recipients_already_marked_Sent` — after the interruption, force the row stale, re-claim, re-dispatch, and assert the stub received exactly 3 more sends (not 5) and no recipient row has two SentAt values.
- `A_resumed_newsletter_send_does_not_create_a_second_recipient_list` — the mass-duplicate case: assert `NewsLetterRecipients.Count(r => r.NewsLetterSendId == id)` is still 5, and TotalRecipients is not doubled. Same for PollRecipients by PollSendId.
- `A_poll_send_whose_audience_vanished_leaves_neither_the_send_nor_the_survey_in_Sending` — pins the PollDispatcher.cs:45-52 / :36 unwind.

EXTEND: EmailConsentChannelTests.cs — covers (a). It already proves IsSuppressed is right for every channel (:44, :57) and the greetings exemption (:68-75); the gap is that nothing tests the WRITE.
- `A_spam_complaint_on_any_channel_suppresses_every_channel` — [Theory] over ecard/eletter/poll/didyouknow/newsletter: seed a subscribed client, drive the real webhook recorder (EmailDeliveryTracker.RecordAsync / NewsLetterService.RecordRecipientEventAsync) with a `spamreport` event carrying that channel's recipient id, then assert `EmailOptOutAt` is set AND `IsSuppressed` returns true for all seven EmailChannel members. This is the test that would have caught JOBS-4, and it fails today for all five inputs.
- Same [Theory] for `unsubscribe` and `group_unsubscribe`, which are currently dropped entirely at EmailDeliveryTracker.cs:48/:58.
- `The_newsletter_footer_link_suppresses_every_channel` — drive NewsletterController.Unsubscribe's logic with a valid UnsubscribeToken, assert EmailOptOutAt is set. Fails today (NewsletterController.cs:272).
- `Suppressing_twice_is_idempotent` — second call reports WasAlreadySuppressed, does not re-retire DYK items, does not re-cancel enrollments, does not re-notify.
- `Suppression_retires_queued_DidYouKnow_items_and_cancels_active_drip_enrollments` — pins the behaviour being moved out of EmailPreferencesController:156-177 so the move is provably lossless.
- `Resubscribing_clears_the_opt_out_and_the_newsletter_flag_together` — the inverse, so opt-in cannot drift from opt-out.

EXTEND: ScheduledSendAudienceTests.cs — it already mirrors the newsletter audience SQL at :77.
- `A_poll_audience_no_longer_filters_on_the_newsletter_flag` — a client with IsNewsletterSubscribed=false and EmailOptOutAt=null IS in the poll audience and IS counted as eligible; an opted-out client is excluded AND appears in the suppressed count. Pins the PollDispatcher.cs:201 removal in both directions.
- `The_subscriber_count_an_agent_sees_matches_what_will_be_sent` — ClientService.GetNewsletterSubscribersAsync (ClientService.cs:66) vs the dispatcher's own audience query, on a fixture containing one opted-out subscriber. Fails today.

MANUAL, on the local dev environment (ops\Start-LocalEnv.ps1, localhost:5100/5200) — required because the harness bypasses the repair path and because of the standing "verify by real interaction, not code-reading" rule:
- Run BOTH apps against a database snapshot taken before this change and confirm EmailDeliverySchema adds ClaimedAt/ClaimAttempts to all four tables exactly once; restart both again and confirm the ColumnExistsAsync guard makes the second pass a no-op.
- Schedule a newsletter to a handful of seeded clients, kill the Web process mid-send, restart, and watch the next minutely tick reclaim it after the stale window and finish only the Queued remainder.
- Click "Send now" on an e-card at the same second the minutely tick fires and confirm exactly one delivery per recipient in the SendGrid sandbox / Email Activity screen.
- Fire a signed spamreport at /Newsletter/SendGridEvents carrying an `ecard_recipient_id` and confirm the client's EmailOptOutAt is written, the agent notification arrives, and a subsequent e-letter to that client is suppressed.

## LB-3 — promo-code oracle + Newtonsoft pin (WEB-H-2, DEP)

### Root cause

=== (a) WEB-H-2 — /Account/ValidatePromoCode promo-code oracle ===

The endpoint is `AccountController.ValidatePromoCode` at C:/Users/admin/Documents/Codex/2026-06-30/ca/work/iPro_Project/iPro_Project/IPRO_Modern/src/IPRO.Web/Controllers/AccountController.cs:497-538. Three separate things combine into the defect:

1. NO AUTH, NO ANTIFORGERY. Lines 497-499 are `[HttpPost]` + `[IgnoreAntiforgeryToken]` with no `[Authorize]`. Note a subtlety that matters for the fix: `[IgnoreAntiforgeryToken]` is currently a *no-op*, because IPRO.Web never registers a global antiforgery filter — Program.cs:139-140 is only `AddControllersWithViews().AddMvcOptions(o => o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true)`, and every other protected POST in the app opts in individually (`[HttpPost, ValidateAntiForgeryToken]`, e.g. AccountController.cs:59, 96, 176, 265, 603). So deleting `[IgnoreAntiforgeryToken]` alone changes nothing; `[ValidateAntiForgeryToken]` must be added explicitly.

2. NO PER-ENDPOINT RATE LIMIT. src/IPRO.Web/appsettings.json:74-106 declares the AspNetCoreRateLimit `IpRateLimiting.GeneralRules` list. Rules exist for Login (5m/10, line 78), Register (1h/5, line 80), ForgotPassword/ResetPassword (5m/5, lines 81-82), SubmitLead/SubmitCustomForm/SubmitTestimonial (5m/10, lines 83-85) — and each has a `/portal`-prefixed twin at lines 91-102. There is no rule for ValidatePromoCode in either form, so only the catch-all `{ "Endpoint": "*", "Period": "1m", "Limit": 120 }` (line 105) applies: 7,200 guesses/hour/IP.

3. THE RESPONSE IS A FULLY ITEMIZED ORACLE. Lines 513-537 build the reply from the `PromotionCode` row itself — "50% off the recurring price for the life of your subscription and 100% off the setup fee". Worse, there are three distinguishable outcomes: "Choose a valid package first." (line 504, invalid packageId), "That code is not valid for the selected package…" (line 510, invalid code), and the itemized accept (line 537). That is a clean enumeration channel for both `code` and `packageId`.

The payoff is real money, not just information: a discovered fully-comped code fed into registration reaches `PayPalBillingService.CreateSubscriptionAsync` → `ValidatePromotionCodeAsync` (src/IPRO.Billing/PayPalBillingService.cs:125) → `isFullyComped` (line 148) → an active subscription with no PayPal step at all (AccountController.cs:438-442).

The deeper root cause is architectural: a *pre-payment convenience check* for a form the visitor is already looking at was implemented as an unauthenticated public API with a verbose response, rather than as a page-scoped helper.

=== (b) DEP-Newtonsoft.Json ===

Newtonsoft.Json is a purely transitive dependency — `grep -rn "Newtonsoft" --include=*.cs --include=*.cshtml src/ tests/` (excluding obj/bin) returns ZERO hits, and no .csproj declares a `PackageReference` for it. It arrives only via Hangfire.

Hangfire.Core 1.8.23 declares `"Newtonsoft.Json": "11.0.1"` (src/IPRO.Web/obj/project.assets.json:179-183). NuGet's lowest-applicable-version rule then resolves 11.0.1 in every project where Hangfire is the *only* asker. Two unrelated packages accidentally float it higher elsewhere:
- AspNetCoreRateLimit 5.0.0 → `"Newtonsoft.Json": "13.0.2"` (src/IPRO.Web/obj/project.assets.json:38-44)
- SendGrid 9.28.1 → `"Newtonsoft.Json": "13.0.1"` (src/IPRO.Web/obj/project.assets.json:1776-1780)

Actual resolved versions today (from the checked-in restore output):
- **11.0.1 (VULNERABLE — the three projects):**
  - src/IPRO.Business/obj/project.assets.json:1376 and :3715
  - src/IPRO.DataAccess/obj/project.assets.json:726 and :2177
  - src/IPRO.Utility/obj/project.assets.json:528 and :1444
- 13.0.1: src/IPRO.Billing (:1376), src/IPRO.Email (:1376), src/IPRO.Scheduler (:1364)
- 13.0.2: src/IPRO.Web (:1725), src/IPRO.Admin (:1712), tests/IPRO.IntegrationTests (:2007)

That is exactly why the July "gone entirely" claim survived — DOCS/SECURITY_AUDIT_2026-07-24.md:267 verified only the two app projects, which are the only ones AspNetCoreRateLimit rescues. Drop or downgrade AspNetCoreRateLimit and SendGrid and both apps fall back to 11.0.1 silently.

Answering the two explicit questions:
- **Central package management: ALREADY IN USE.** Directory.Packages.props:3 `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>`; every .csproj uses versionless `<PackageReference Include="..." />`.
- **Transitive pinning: ALREADY ENABLED.** Directory.Packages.props:4 `<CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>`. Nothing needs enabling — the single `PackageVersion` line takes effect immediately. There is direct precedent in the same file: System.Text.Json (line 39), System.Security.Cryptography.Xml (40), System.Formats.Asn1 (41) and Microsoft.Extensions.Caching.Memory (42) are all transitive-only advisory pins under the comment at lines 34-38, and none of them is a `PackageReference` in any project.

### Approach

=== (a) WEB-H-2: three cheap layers, none of which breaks signup ===

The legitimate flow is: an anonymous visitor loads /Account/Register (GET), types a code into #promoCodeInput, clicks Apply, and gets a yes/no before proceeding to PayPal. Every layer below is invisible to that visitor.

LAYER 1 — Antiforgery (removes the "anyone, from anywhere" property without removing anonymity).
`[Authorize]` is the wrong tool: it would break the flow outright, since the account does not exist yet (see the comment at AccountController.cs:2696-2699 explaining why registration-time validation passes no agentId). `[ValidateAntiForgeryToken]` gets most of the benefit for free — the caller must have fetched a real Register page and be carrying its antiforgery cookie+token pair. The token is already on the page (Register.cshtml:203) and the Apply button is inside that same form, so this costs one attribute and one line of JS. Data protection is already relied on for the same class of token elsewhere (the 422g captcha protector, PublicWebsiteController.cs:39), and the Register POST at AccountController.cs:265 already uses antiforgery on the same page — so no new infrastructure and no new failure mode.

LAYER 2 — A named rate-limit rule, declared exactly like the existing ones.
The precedent is not attribute-based: this app uses AspNetCoreRateLimit (Program.cs:123-138, `app.UseIpRateLimiting()` in the pipeline), configured entirely by `IpRateLimiting.GeneralRules` in appsettings.json, keyed `"{VERB}:{path}"`, with `EnableEndpointRateLimiting: true`. Every endpoint that got hardened in the 422g/SubmitCustomForm work is a JSON row, not a C# attribute — POST:/PublicWebsite/SubmitCustomForm at line 84 with its /portal twin at line 97. So the correct fix is two JSON rows at 5m/5, matching the ForgotPassword tier, and NOT a new mechanism.

LAYER 3 — Stop the response from being worth harvesting.
Layers 1 and 2 are per-IP and per-session; a distributed attacker still gets some attempts. What removes the incentive is that a hit no longer reveals anything: collapse the two rejection messages into one string, and replace the itemized discount description with "Code accepted. Your discount is applied at checkout." A harvested code then has to be spent through registration to learn its value, which is rate-limited (1h/5), antiforgery-protected, verify-code-gated (AccountController.cs:313-317) and permanently attributable to a created account.

Layer 3 is the only visibly product-affecting change, so treat it as an explicit owner decision rather than a silent one — the house pattern is to ask before changing working UI. If the owner wants the terms text kept, ship layers 1+2 now and note in DOCS that WEB-H-2 is partially closed with the disclosure accepted, rather than reporting it fixed.

What I deliberately did NOT do:
- No new rate-limiting mechanism, no ASP.NET Core 8 `AddRateLimiter`/`[EnableRateLimiting]` policies. That would be a parallel system alongside AspNetCoreRateLimit.
- No new promo-validation helper. All three promo paths already funnel through `PayPalBillingService.ValidatePromotionCodeAsync` (src/IPRO.Billing/PayPalBillingService.cs:2681); only the *presentation* changes, and that is extracted to one internal helper with one caller.
- No loosened CSP; the JS edit lives inside the existing `nonce="@Context.GetCspNonce()"` block at Register.cshtml:436.
- No schema change, so the both-apps startup-repair rule does not apply here.

=== (b) DEP-Newtonsoft.Json: literally one line ===

Add to Directory.Packages.props, inside the existing advisory-pin block:

    <PackageVersion Include="Newtonsoft.Json" Version="[13.0.2]" />

That is the whole fix. Central package management is already on (line 3) and central transitive pinning is already on (line 4), so a `PackageVersion` for a package that no project references directly is precisely how the four existing advisory pins on lines 39-42 work. NuGet lifts the transitive Hangfire.Core → Newtonsoft.Json 11.0.1 edge to a pinned 13.0.2 in all ten projects, including the three currently at 11.0.1.

There is no compile-surface risk to weigh: no source file in src/ or tests/ references Newtonsoft.Json at all. The only consumer is Hangfire.Core's internal job serializer, which already runs against 13.0.2 in both deployed apps today. So this makes explicit and durable what production already does by accident, and simultaneously fixes the three library projects that a scanner reads.

### Files to change

- `C:/Users/admin/Documents/Codex/2026-06-30/ca/work/iPro_Project/iPro_Project/IPRO_Modern/Directory.Packages.props` — (b) THE ONE-LINE FIX. Insert into the existing advisory-pin block (comment at lines 34-38), immediately after line 42 (`Microsoft.Extensions.Caching.Memory`) and before `</ItemGroup>` on line 43:

    <PackageVersion Include="Newtonsoft.Json" Version="[13.0.2]" />

Exact-bracket notation matches the four sibling pins already in that block. No other change is required: ManagePackageVersionsCentrally (line 3) and CentralPackageTransitivePinningEnabled (line 4) are both already true, so this promotes the transitive dependency to a pinned direct reference in all ten projects at once. Extend the existing comment block with one sentence naming Hangfire.Core 1.8.23 as the 11.0.1 source, so the next reader does not repeat the July mistake of checking only the two app projects.

Version choice: 13.0.2, not 13.0.3. 13.0.2 is what IPRO.Web/IPRO.Admin already ship, so production bits do not change; and the local NuGet cache (C:\Users\admin\.nuget\packages\newtonsoft.json) holds only 11.0.1, 11.0.2, 13.0.1, 13.0.2 — pinning 13.0.3 forces an online restore before the local dev env (ops\Start-LocalEnv.ps1) will build. 13.0.2 already clears GHSA-5crp-9r3c-p9vr, which is the advisory scanners raise against 11.0.1.
- `C:/Users/admin/Documents/Codex/2026-06-30/ca/work/iPro_Project/iPro_Project/IPRO_Modern/src/IPRO.Web/appsettings.json` — (a) Add the missing named rate-limit rules to IpRateLimiting.GeneralRules. Two lines, following the file's own canonical + /portal-twin convention:

  after line 82 (POST:/Account/ResetPassword):
      { "Endpoint": "POST:/Account/ValidatePromoCode", "Period": "5m", "Limit": 5 },
  after line 95 (POST:/portal/Account/ResetPassword):
      { "Endpoint": "POST:/portal/Account/ValidatePromoCode", "Period": "5m", "Limit": 5 },

5-per-5-minutes matches the ForgotPassword/ResetPassword tier (lines 81-82, 94-95) and takes the ceiling from 7,200/hr/IP to 60/hr/IP. The legitimate flow clicks Apply once or twice per signup, so this has enormous headroom.

STRONGLY RECOMMENDED IN THE SAME COMMIT — the identical sibling oracle (see sibling_call_sites): add rules for the anonymous trial-invite-code check, which today is covered only by the `*` rule:
      { "Endpoint": "GET:/Account/Register", "Period": "5m", "Limit": 20 },
      { "Endpoint": "GET:/portal/Account/Register", "Period": "5m", "Limit": 20 },
      { "Endpoint": "GET:/pub/register.aspx", "Period": "5m", "Limit": 20 },
(20/5m, not 5/5m: a real prospect legitimately reloads and back-buttons the signup page.)

Why the /portal twin is mandatory and not cosmetic: AspNetCoreRateLimit 5.0.0 leaves EnableRegexRuleMatching false (the key is absent from this file), so IsUrlMatch degrades to a case-insensitive Contains against the literal string "{VERB}:{path}". `POST:/portal/account/validatepromocode` does NOT contain `POST:/Account/ValidatePromoCode`, because `POST:` is immediately followed by `/portal`. That is precisely why every existing rule in this file is written twice.
- `C:/Users/admin/Documents/Codex/2026-06-30/ca/work/iPro_Project/iPro_Project/IPRO_Modern/src/IPRO.Web/Controllers/AccountController.cs` — (a) Three edits to the ValidatePromoCode action at lines 497-538.

1. Line 498: replace `[IgnoreAntiforgeryToken]` with `[ValidateAntiForgeryToken]`. (Deleting it is not enough — there is no global antiforgery filter; see root_cause.) This binds the request to a rendered Register page, which is exactly the legitimate flow, and kills scripted enumeration that never fetches the form.

2. Collapse the 3-way oracle to 2-way. Line 504's "Choose a valid package first." and line 510's "That code is not valid for the selected package, or has expired/reached its redemption limit." become one identical string, so an invalid packageId is indistinguishable from an invalid code. Keep the client-side guard at Register.cshtml:449 so a genuinely unselected package still gets its friendly hint without a round trip.

3. Replace the itemized terms (lines 513-536) with a non-disclosing confirmation, e.g. "Code accepted. Your discount is applied at checkout." This is the half that actually stops bulk harvesting: with it, a discovered code yields no information about its value. It does not break the flow — the visitor still learns before payment that the code is good, and the real figures appear at the PayPal approval step built by PayPalBillingService.CreateSubscriptionAsync (src/IPRO.Billing/PayPalBillingService.cs:119-160) from BillingRule.EffectiveSetupFee / IsSetupFeeWaivedOn (src/IPRO.Entities/BillingRule.cs:45-50). It also removes the last hand-formatted money strings from this controller, which is the direction the no-hardcoded-prices rule points.

Extract the message decision into a pure, testable helper on the same class so the response shape can be unit-tested without constructing the 11-dependency controller:

    internal static (bool Valid, string Message) BuildPromoValidationResponse(PromotionCode? promo);

and have the action return `Json(new { valid, message })` from it. One helper, one call site, no parallel implementation.

DO NOT touch the Register POST promo check at lines 319-326 — it already validates through the same shared `_billing.ValidatePromotionCodeAsync` and already emits a generic message. Leave PayPalBillingService.ValidatePromotionCodeAsync (src/IPRO.Billing/PayPalBillingService.cs:2681-2708) alone; it is the correct shared validator and all three promo paths already go through it.
- `C:/Users/admin/Documents/Codex/2026-06-30/ca/work/iPro_Project/iPro_Project/IPRO_Modern/src/IPRO.Web/Views/Account/Register.cshtml` — (a) Update the fetch inside the existing nonced script block (line 436 is already `<script nonce="@Context.GetCspNonce()">`, so no new script tag and no CSP change — `connect-src 'self'` at src/IPRO.Web/Middleware/SecurityHeadersMiddleware.cs:57 already permits this same-origin fetch).

1. Send the antiforgery token. The token is already rendered by `@Html.AntiForgeryToken()` at line 203, inside the same `<form>` that spans lines 202-433 and contains the promo input/button at lines 371-373. In the click handler (lines 445-473) read it and append it to the urlencoded body:
     var tokenEl = document.querySelector('input[name="__RequestVerificationToken"]');
     ... body: 'code=' + ... + '&packageId=' + ... + '&__RequestVerificationToken=' + encodeURIComponent(tokenEl ? tokenEl.value : '')
   (Header form `RequestVerificationToken` works too; the body field is simpler given the existing x-www-form-urlencoded content type at line 456.)

2. Handle 429 explicitly. AspNetCoreRateLimit returns text/plain on block, so `r.json()` at line 459 rejects and the user gets the misleading "Could not check that code right now." from the catch at line 469. Add `if (r.status === 429) { ... 'Too many attempts. Please wait a few minutes and try again.'; return; }` before parsing.

3. Prefer a path-relative fetch over the hardcoded '/Account/ValidatePromoCode' at line 454 — use `@Url.Action("ValidatePromoCode", "Account")` so the page keeps working identically whether it was served at /Account/Register or /portal/Account/Register. (Both currently work because POSTs are never diverted to the public site — Program.cs:635 returns false for non-GET, and "account" is a never-shadowed prefix at Program.cs:600 — but Url.Action removes the need to know that.)
- `C:/Users/admin/Documents/Codex/2026-06-30/ca/work/iPro_Project/iPro_Project/IPRO_Modern/src/IPRO.Web/IPRO.Web.csproj` — (a) Add an InternalsVisibleTo item group so the extracted helper is testable, using the precedent already in src/IPRO.Billing/IPRO.Billing.csproj:22-26:

  <ItemGroup>
    <InternalsVisibleTo Include="IPRO.IntegrationTests" />
  </ItemGroup>

tests/IPRO.IntegrationTests already ProjectReferences IPRO.Web (IPRO.IntegrationTests.csproj:12-14, added for exactly this class of controller-level security test).
- `C:/Users/admin/Documents/Codex/2026-06-30/ca/work/iPro_Project/iPro_Project/IPRO_Modern/tests/IPRO.IntegrationTests/PromoCodeOracleTests.cs` — (a) NEW. Behavioural + attribute regression tests. No MySQL needed (unlike TestDatabase-based suites), so it runs anywhere. See test_plan for the assertions.
- `C:/Users/admin/Documents/Codex/2026-06-30/ca/work/iPro_Project/iPro_Project/IPRO_Modern/tests/IPRO.IntegrationTests/RateLimitRuleCoverageTests.cs` — (a) NEW. The structural guard against this codebase's recurring defect: parses src/IPRO.Web/appsettings.json and asserts that EVERY non-`*` rule has a matching /portal twin (with an explicit, documented exemption list containing only "POST:/pub/register.aspx", which is a literal legacy route that does not exist under /portal). This is the test that would have caught the class of miss, not just this instance. See test_plan.
- `C:/Users/admin/Documents/Codex/2026-06-30/ca/work/iPro_Project/iPro_Project/IPRO_Modern/tests/IPRO.IntegrationTests/DependencyPinTests.cs` — (b) NEW (optional but cheap). Parses Directory.Packages.props and asserts: CentralPackageTransitivePinningEnabled is true; a PackageVersion for Newtonsoft.Json exists at >= 13.0.1; and no .csproj under src/ or tests/ declares an inline `PackageReference Include="Newtonsoft.Json" Version=...` (which would bypass CPM). Pure file parsing, no DB, no restore.
- `C:/Users/admin/Documents/Codex/2026-06-30/ca/work/iPro_Project/iPro_Project/IPRO_Modern/.github/workflows/main_ipro-prod-web.yml` — (b) OPTIONAL hardening, and the thing that makes the July failure non-repeatable. After the existing `dotnet restore IPRO.sln` (line 26) add:

      - name: Fail on vulnerable packages
        run: dotnet list IPRO.sln package --vulnerable --include-transitive

plus a grep/exit-code gate. Because the workflow already restores and builds the whole solution (lines 26-28), this covers all ten projects — the coverage gap that hid this defect. Mirror it in main_ipro-prod-admin.yml. Do NOT flip `<NuGetAuditMode>all</NuGetAuditMode>` with warnings-as-errors without first suppressing the deliberately-deferred AngleSharp 0.17.1 advisory documented at Directory.Packages.props:34-38, or the build breaks on a known, accepted risk.
- `C:/Users/admin/Documents/Codex/2026-06-30/ca/work/iPro_Project/iPro_Project/IPRO_Modern/DOCS/AUDIT_RECONCILIATION_2026-08-17.md` — Update the WEB-H-2 entry (lines 94-96) and the DEP-Newtonsoft.Json entry (lines 98-100), and the open question at line 287. Per the durable-backlog convention, reconcile DOCS/TODO.md in the same pass. Record honestly which half of WEB-H-2 shipped: if the itemized-terms removal is deferred pending owner sign-off, say so rather than marking the item closed — that partial-fix-reported-as-complete pattern is exactly what this reconciliation document exists to correct (line 58).

### Sibling call sites

Naming every one, because "fixed the reported call site, missed the identical sibling" is this codebase's signature defect.

--- (a) Same action, second route (MUST fix in the same commit) ---

1. POST /portal/Account/ValidatePromoCode. src/IPRO.Web/Program.cs:367 registers `app.MapControllerRoute("portal", "portal/{controller=Dashboard}/{action=Index}/{id?}")` as an additive alias over the default route at line 369. The exact same action is therefore reachable at the /portal prefix, and — as proven by AspNetCoreRateLimit's Contains-based matching with EnableRegexRuleMatching left false — the unprefixed rule does not cover it. Every one of the twelve existing rules in appsettings.json is written twice for this reason (lines 78-89 vs 91-102). A one-rule fix here would be a bypass shipped on day one.

--- (a) The identical oracle, different code type (SAME SHAPE, currently unreported) ---

2. GET /Account/Register?trialCode=… — AccountController.cs:219-236, delegating to `ResolveTrialInviteAsync` at AccountController.cs:466-481. This is the promo oracle again with a different table: anonymous, no antiforgery (it's a GET), no dedicated rate-limit rule, and it returns FOUR distinguishable messages that separate "not a code" from "a real code that is inactive / expired / capped out" — a strictly richer enumeration channel than ValidatePromoCode's. It also discloses the invited package via ViewBag.TrialPackage (line 230), rendered into the form. A trial code is worth an entire free package (BillingRule.IsTrialPackage, checked at line 478).
3. GET /portal/Account/Register?trialCode=… — the /portal twin of #2, same action via Program.cs:367.
4. GET /pub/register.aspx?trialCode=… — the legacy alias registered at src/IPRO.Web/Program.cs:321-324, mapped to the same Account/Register action. appsettings.json:103 rate-limits only `POST:/pub/register.aspx`; the GET is uncovered. (The legacy template hardcodes controller+action, so it cannot reach ValidatePromoCode — this sibling belongs to #2, not to #1.)
   → Recommendation: add the three GET rules listed under the appsettings.json entry now (one-line each, zero behavioural risk). Collapsing ResolveTrialInviteAsync's four messages for the anonymous caller is a second, slightly larger decision — call it out explicitly in DOCS rather than doing it silently.

--- (a) Other promo call sites: reviewed, deliberately unchanged ---

5. POST /Account/Register — AccountController.cs:319-326 validates the promo anonymously through the same shared validator. Already `[HttpPost, ValidateAntiForgeryToken]` (line 265), already rate-limited 1h/5 (appsettings.json:80 and :93), and its message at line 324 is already generic. No change; but any future edit must keep it generic or it becomes the next oracle.
6. POST /Account/Profile — AccountController.cs:602-604, 653 lets an authenticated agent write an arbitrary `agent.PromotionCode`. It performs no validation and returns no signal, so it is not an oracle. Do not "helpfully" add validation feedback here.
7. PayPalBillingService.CreateSubscriptionAsync — src/IPRO.Billing/PayPalBillingService.cs:119-160 reads `agent.PromotionCode` and calls the shared validator at line 125. Authenticated, no oracle. This is the redemption path the harvested codes were headed for (`isFullyComped`, line 148).
8. PayPalBillingService.ValidatePromotionCodeAsync — src/IPRO.Billing/PayPalBillingService.cs:2681-2708. THE shared helper. All promo checks already go through it. Extend it if validation logic ever changes; do not add a parallel validator in the Web layer.
9. IPRO.Admin PromotionCodesController — src/IPRO.Admin/Controllers/PromotionCodesController.cs:10 is `[Authorize(Policy = "SuperAdmin")]`. Not exposed. Admin's own rate-limit list (src/IPRO.Admin/appsettings.json:5-12) needs no change.
10. Views/Account/Register.cshtml:436-474 is the sole client of the endpoint — I grepped the whole repo (including e2e/) for "ValidatePromoCode" and this fetch at line 454 plus the action at AccountController.cs:499 are the only two hits. So adding antiforgery cannot break an unseen caller.

--- (b) The three projects, plus every project the pin touches ---

11. src/IPRO.Business/IPRO.Business.csproj — resolves Newtonsoft.Json 11.0.1 (obj/project.assets.json:1376). Hangfire.Core is its only asker (csproj lines 12-13).
12. src/IPRO.DataAccess/IPRO.DataAccess.csproj — resolves 11.0.1 (obj/project.assets.json:726). Hangfire.Core only (csproj lines 10-11).
13. src/IPRO.Utility/IPRO.Utility.csproj — resolves 11.0.1 (obj/project.assets.json:528). Hangfire.Core only (csproj lines 19-20).
14. src/IPRO.Billing (13.0.1), src/IPRO.Email (13.0.1), src/IPRO.Scheduler (13.0.1) — rescued only by SendGrid 9.28.1's floor.
15. src/IPRO.Web (13.0.2), src/IPRO.Admin (13.0.2), tests/IPRO.IntegrationTests (13.0.2) — rescued only by AspNetCoreRateLimit 5.0.0's floor. These are the only three the July check looked at, which is why it reported clean.
16. src/IPRO.Entities — no Newtonsoft edge at all (no PackageReferences).
   The single pin covers 11-15 simultaneously; there is no per-project edit.

### Risks

=== (a) ===

1. ANTIFORGERY BREAKS AN UNSEEN CALLER. Mitigated by evidence: a repo-wide grep for "ValidatePromoCode" returns exactly two hits (Register.cshtml:454, AccountController.cs:499) — no e2e script, no second view, no external integration. Still, it is the highest-blast-radius item: if the token is not sent, Apply returns 400 for every visitor and the observable symptom is "Could not check that code right now." on the signup page. Ship JS and controller in one commit; never the attribute alone.

2. THE 429 BODY IS NOT JSON. AspNetCoreRateLimit returns text/plain, so `r.json()` at Register.cshtml:459 rejects and the visitor sees the wrong message. Handle status 429 explicitly or the rate limit degrades the UX in a confusing way — which is precisely the "papered over confusing behaviour" failure mode this project has been burned by before.

3. RATE-LIMIT COUNTERS ARE IN-MEMORY AND SINGLE-INSTANCE. Program.cs:107 uses MemoryCacheRateLimitCounterStore, so every deploy/restart resets the window, and the limit is per-instance if the app is ever scaled out. Acceptable (the 422g captcha nonce accepts the same trade-off, PublicWebsiteController.cs:1047-1048) but it means layer 3 — the non-disclosing message — is the durable part of the fix, not the rate limit.

4. IP RATE LIMITING DOES NOT STOP A DISTRIBUTED ATTACK. 5/5m per IP against a botnet is still meaningful friction but not a wall. Do not report WEB-H-2 as closed on the strength of the rule alone.

5. ARRAY-INDEX SHIFT IN CONFIGURATION. Inserting rules renumbers `IpRateLimiting:GeneralRules:N`. Any Azure App Setting of the form `IpRateLimiting__GeneralRules__3__Limit` would silently retarget a different endpoint. I grepped ops/, infra/, scripts/ and e2e/ for "GeneralRules"/"IpRateLimiting" and found none, and neither appsettings.Development.json nor appsettings.Production.json overrides the section — so this is clean today. Check the live App Service configuration before deploy anyway.

6. REMOVING THE ITEMIZED TERMS IS A USER-VISIBLE PRODUCT CHANGE. It is the security-load-bearing part, but it changes what a prospect sees on the signup page. Get an explicit yes rather than assuming; and if it is deferred, say in DOCS that WEB-H-2 is partially closed instead of reporting it fixed.

7. FALSE SENSE OF SECURITY FROM `[IgnoreAntiforgeryToken]`. Anyone reading the diff may assume deleting that attribute restores protection. It does not — there is no global antiforgery filter in IPRO.Web (Program.cs:139-140). The positive attribute must be present. Worth a code comment.

8. SCOPE CREEP. The trial-invite oracle (siblings 2-4) is genuinely the same defect and its rate-limit half is three JSON lines. Its message-collapsing half is a separate UX call. Do not silently expand WEB-H-2 into a rewrite of the trial-invite flow; name it, fix the cheap half, and log the rest.

=== (b) ===

9. EXACT-VERSION BRACKETS CAN BLOCK A FUTURE RESTORE. `[13.0.2]` means "exactly 13.0.2". If any future package demands >= 13.0.3, restore fails with NU1107/NU1608 rather than resolving. That is the same trade-off the four existing pins on Directory.Packages.props:39-42 already accepted, and a hard failure is the desired behaviour for an advisory pin — but it must be a conscious choice, and the fix is to bump the pin, never to delete it.

10. OFFLINE RESTORE. 13.0.3 is NOT in the local NuGet cache (only 11.0.1, 11.0.2, 13.0.1, 13.0.2 are present), so pinning 13.0.3 makes the first local build require network. 13.0.2 keeps ops\Start-LocalEnv.ps1 working offline and keeps the production bits byte-identical to what ships today.

11. RUNTIME BEHAVIOUR OF HANGFIRE JOB SERIALIZATION. Newtonsoft 11 → 13 changes the assembly version (11.0.0.0 → 13.0.0.0) under Hangfire.Core's serializer. Both deployed apps already run 13.0.2, so nothing in production changes; the delta is confined to the three library projects' compile-time graph, and no source file in the repo references Newtonsoft at all (zero grep hits in src/ and tests/). Residual risk is close to nil, but Hangfire job payloads already persisted in MySQL are worth a smoke check after deploy.

12. TRANSITIVE PINNING MAKES IT A DIRECT DEPENDENCY EVERYWHERE. Newtonsoft.Json will now appear as a direct entry in all ten projects' deps.json and in `dotnet list package` output without `--include-transitive`. Expected and harmless; do not "clean it up" by removing the pin.

13. TIGHTENING NuGetAudit WILL BREAK THE BUILD ON A KNOWN ACCEPTED RISK. If the CI hardening in the workflows is taken further to `NuGetAuditMode=all` with warnings-as-errors, the deliberately-deferred AngleSharp 0.17.1 advisory (Directory.Packages.props:34-38, and DEP-AngleSharp in the reconciliation doc) will fail every build. Suppress that specific advisory ID first, or keep the audit as a reporting step.

14. THE VERIFICATION METHOD IS ITSELF THE RISK. The July claim was wrong because the check only looked at the two app projects. Verify with `dotnet list IPRO.sln package --include-transitive` across all ten, or by re-reading the three obj/project.assets.json files named above — not by inspecting the published output of ipro-prod-web.

### Test plan

All new tests go in tests/IPRO.IntegrationTests. Note: unlike the existing suites, none of these needs MySQL — TestDatabase.CreateAsync (tests/IPRO.IntegrationTests/TestDatabase.cs:23-51) requires a live local server, and these tests deliberately avoid it so they run in CI unchanged.

=== (a) Automated ===

TEST FILE 1 — tests/IPRO.IntegrationTests/PromoCodeOracleTests.cs

  T1.1 Antiforgery is enforced (attribute reflection over the real controller; the test project already ProjectReferences IPRO.Web at IPRO.IntegrationTests.csproj:12-14):
     var m = typeof(IPRO.Web.Controllers.AccountController).GetMethod("ValidatePromoCode");
     Assert.NotEmpty(m.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true));
     Assert.Empty(m.GetCustomAttributes(typeof(IgnoreAntiforgeryTokenAttribute), true));
   Rationale: this is exactly the regression that would silently reappear, and it pins the load-bearing detail that the positive attribute — not the absence of the negative one — is what protects the endpoint.

  T1.2 The accepted response discloses no terms. Drive the extracted internal helper with a maximally revealing promo:
     new PromotionCode { RecurringDiscountType = PercentOff, RecurringDiscountValue = 50, RecurringDurationCycles = null,
                         SetupFeeDiscountType = FlatAmountOff, SetupFeeDiscountValue = 400 }
     var (valid, message) = AccountController.BuildPromoValidationResponse(promo);
     Assert.True(valid);
     Assert.DoesNotContain('%', message); Assert.DoesNotContain('$', message);
     Assert.DoesNotContain(message, c => char.IsDigit(c));
     Assert.DoesNotContain("setup", message, StringComparison.OrdinalIgnoreCase);
     Assert.DoesNotContain("life of your subscription", message, StringComparison.OrdinalIgnoreCase);
   This is the test that actually encodes the security property, and it is why the helper is worth extracting.

  T1.3 The rejection messages are indistinguishable. Assert the invalid-package string and the invalid-code string are reference-identical (drive both branches through the same helper / shared const). Guards the 3-way oracle from creeping back one "helpful" message at a time.

TEST FILE 2 — tests/IPRO.IntegrationTests/RateLimitRuleCoverageTests.cs

  Locate the repo root by walking up from AppContext.BaseDirectory until IPRO.sln is found; parse src/IPRO.Web/appsettings.json with System.Text.Json (already pinned, Directory.Packages.props:39).

  T2.1 The rule exists and is tight:
     a rule with Endpoint == "POST:/Account/ValidatePromoCode" exists, Period == "5m", Limit <= 5.
  T2.2 The /portal twin exists with identical Period and Limit.
  T2.3 THE STRUCTURAL GUARD — for every rule whose Endpoint is not "*" and does not contain "/portal/", assert a twin exists with "/portal" inserted after the verb, except for an explicit, commented exemption set { "POST:/pub/register.aspx" }. Today this passes for all twelve existing pairs and would have failed the moment a ValidatePromoCode rule was added without its twin. This is the test that generalises past the reported instance.
  T2.4 (if the trial-invite rules ship) assert GET:/Account/Register, GET:/portal/Account/Register and GET:/pub/register.aspx rules exist with Limit <= 20.

Not attempted, and why: a full end-to-end HTTP test of the 429 and the 400 would need Microsoft.AspNetCore.Mvc.Testing (not referenced; would need a new PackageVersion) and would boot Program.cs, which runs the schema-repair functions and registers fifteen Hangfire recurring jobs (Program.cs:376-393, 395+). Constructing AccountController directly is also impractical — eleven constructor dependencies (AccountController.cs:37) versus the two that made ClientPortalTokenSecurityTests.cs:57 clean. The reflection + pure-helper + config-parsing split gets the regression coverage without that cost. Say this out loud in the commit message rather than implying full E2E coverage.

=== (a) Manual, on the local dev environment — required before any deploy ===

Start ops\Start-LocalEnv.ps1 (Web on localhost:5100). Real interaction, not code-reading:
  M1. Load http://localhost:5100/Account/Register, enter a known-good promo code, click Apply → expect "Code accepted. Your discount is applied at checkout." with no figures. Confirm in DevTools that the POST carries __RequestVerificationToken and returns 200.
  M2. Enter a garbage code → expect the single generic rejection. Enter a good code with a bogus packageId (edit the select value in DevTools) → expect the SAME string, byte for byte.
  M3. Click Apply six times in under five minutes → the sixth returns 429 and the page shows "Too many attempts. Please wait a few minutes and try again." (not the generic error).
  M4. Replay the bypass an attacker would use — a bare curl with no antiforgery token:
      curl -i -X POST http://localhost:5100/Account/ValidatePromoCode -d "code=TEST&packageId=1"
      → expect HTTP 400. Repeat against http://localhost:5100/portal/Account/ValidatePromoCode → also 400, and after six attempts, 429. The /portal twin is the step most likely to be skipped; do it explicitly.
  M5. Complete a full signup with a valid promo end-to-end and confirm the discount actually lands at the PayPal step (PayPalBillingService.CreateSubscriptionAsync, src/IPRO.Billing/PayPalBillingService.cs:136-160) — the point of layer 3 is that the code still WORKS, it just stops advertising itself.
  M6. Repeat M1 against /portal/Account/Register to confirm the Url.Action change did not break the prefixed render.

=== (b) ===

  B1. Baseline, before the edit — record the current state:
      dotnet list IPRO.sln package --include-transitive | Select-String Newtonsoft
      → expect 11.0.1 for IPRO.Business, IPRO.DataAccess, IPRO.Utility; 13.0.1 for Billing/Email/Scheduler; 13.0.2 for Web/Admin/IntegrationTests.
      dotnet list IPRO.sln package --vulnerable --include-transitive
      → expect the Newtonsoft.Json 11.0.1 HIGH advisory (GHSA-5crp-9r3c-p9vr) on the three projects. This baseline is the proof the July verification never produced.
  B2. Apply the one-line pin, then: dotnet restore IPRO.sln && dotnet build IPRO.sln -c Release
  B3. Re-run both commands from B1 → expect 13.0.2 in all ten projects and zero Newtonsoft rows in the --vulnerable output. Also confirm the AngleSharp advisory is still the only remaining known one, i.e. nothing else changed.
  B4. Spot-check the restore graph directly, the way the July check should have: grep '"Newtonsoft.Json/' in src/IPRO.Business/obj/project.assets.json, src/IPRO.DataAccess/obj/project.assets.json and src/IPRO.Utility/obj/project.assets.json → all must now read 13.0.2. These three files are the ground truth; do not verify from the published output of the two app projects.
  B5. DependencyPinTests.cs (new) — parse Directory.Packages.props: assert CentralPackageTransitivePinningEnabled is true, assert a Newtonsoft.Json PackageVersion exists at >= 13.0.1, and assert no .csproj under src/ or tests/ declares an inline versioned PackageReference for it.
  B6. Runtime smoke on the local dev env: bring up Start-LocalEnv.ps1, open the Hangfire dashboard at /hangfire, trigger one recurring job manually (e.g. dispatch-newsletters, Program.cs:376) and confirm the job payload deserializes and the job runs — Hangfire.Core's serializer is the only actual consumer of the package. Confirm nothing was already enqueued-and-broken by checking for failed jobs after the restart.

## ADVERSARIAL CHALLENGE — verdicts and corrections

All paths below are relative to C:/Users/admin/Documents/Codex/2026-06-30/ca/work/iPro_Project/iPro_Project/IPRO_Modern.

=== PLAN 1 (crossHost) — ROOT CAUSE CORRECT; FIX CLOSES IT; 3 REAL PROBLEMS ===

Verified true: PortalUrlHelper.cs:7-8 is a host-blind pass-through to WebAppUrlHelper.cs:17-33; no `Cookie.Domain` anywhere, so the agent cookie (Program.cs:97-108) and session cookie (Program.cs:143-148) are host-only; `account`/`billing` are never-shadowed on every host (Program.cs:598-625, line 600) and a gated agent is exempt for `/Billing/` (Program.cs:271-272), so the return path really does route and authorise on an agent host; `BuildBillingActionUrl` at BillingController.cs:31 with four emissions at :105-106 and :154-155; the hand-rolled duplicate in AccountController (actual lines **425-431**, not 426-432 — `var portalBase` is :425, the two interpolated strings are :429-430); PayPal `return_url`/`cancel_url` are per-order fields (PayPalBillingService.cs:2480-2481 subscriptions, :3141-3142 orders), so option (ii) is correctly rejected; `IUnitOfWork` genuinely has no `AgentDomains` (grep of IUnitOfWork.cs returns nothing), so injecting `IPRODbContext` into BillingController.cs:23 is right; `AllowedHosts: "*"` at appsettings.json:111 confirmed, so `IsAppHost` really is the only gate.

Approach (i) over (ii) is the right call and the reasoning holds. Problems:

P1-1. **Sibling-guard test D.16 is vacuous.** BillingController.cs:105-106/:154-155 already use `nameof(PayPalReturn)`/`nameof(Cancel)`, so the literal `"/Billing/PayPalReturn"` exists today only at AccountController.cs:429-430. After the fix, `PortalUrlHelper.BuildBillingActionUrlAsync` composes `$"/Billing/{action}"`, so that literal exists in **zero** files and the grep passes vacuously forever. Replace with: fail on any `"/Billing/` string literal outside PortalUrlHelper.cs, AND on any `GetAgentPortalBaseUrl` call inside `src/IPRO.Web/Controllers/` (test 17 generalised). Test 17 should be the primary guard, not the secondary.

P1-2. **Local-dev verification step 18 cannot actually run as written.** `o.Cookie.SecurePolicy = CookieSecurePolicy.Always` (Program.cs:105) means the agent auth cookie is not issued over plain `http://localhost:5100`, and PayPal rejects a non-https `return_url` in `application_context`. Test 18 ("confirm from the PayPal sandbox request that return_url is http://localhost:5100/Billing/PayPalReturn") therefore cannot exercise the round trip. Split it: assert the composed string in the unit tests (A.5 already does), and do the real PayPal round-trip only on an https host.

P1-3. **Missed sibling in the same family: PollDispatcher.cs:153-162 `BuildVoteUrl`.** It re-implements the canonical base URL by hand — reads `App:BaseUrl` directly, does its own `yourdomain.com` placeholder check, and hardcodes its own `https://ipro-prod-web.azurewebsites.net` fallback — bypassing `WebAppUrlHelper` entirely. It belongs in the MUST-NOT-CHANGE (canonical, out-of-band) group, but it is exactly the drift WebAppUrlHelper.cs:5-13 was written to eliminate and the sibling sweep missed it. Add it to the list as "collapse onto WebAppUrlHelper, keep canonical".

P1-4. Two smaller corrections to `IsAppHost`: (a) it should mirror the **admin-host exclusion** at Program.cs:678-680 (`App:AdminDomain`, `admin.` prefix) or state why not — in Development `App:AdminDomain` is `localhost:5200` while `App:PlatformDomains` contains bare `localhost` (appsettings.Development.json:6,10), so the two predicates would disagree; (b) the custom-domain lookup should not be described as "the same predicate as PublicWebsiteController.cs:94-98" — that one is scoped by `AgentWebsiteId == website.Id` and handles the legacy `"SslBound"` string (PublicWebsiteController.cs:95-99). Gating on `AzureBindingStatus == AgentDomainStatus.Bound` only is right (AgentDomain.cs:8), but confirm no legacy binding-status values exist in real data the way `SslBound` does.

=== PLAN 2 (dispatchers) — ROOT CAUSES CORRECT; ATOMICITY CORRECT BUT JUSTIFIED WRONG; 6 REAL DEFECTS INTRODUCED ===

Root causes all verified. (a) `IsSuppressed` (EmailConsentService.cs:56-85) is the only read, and a repo-wide grep confirms `EmailOptOutAt` is **written in exactly two places**: EmailPreferencesController.cs:110 (clear) and :148 (set). NewsLetterService and NewsletterController.cs:272 write only the newsletter flag; EmailDeliveryTracker.cs:47 maps `spamreport` to `Failed` and :48 drops `unsubscribe`/`group_unsubscribe`. (b) verified at NewsLetterDispatcher.cs:50-51/62-64, ECardDispatcher.cs:29-30/48-49, PollDispatcher.cs:29-30/35-37. (c) verified: single trailing SaveChanges at NewsLetterDispatcher.cs:158, ECardDispatcher.cs:135, PollDispatcher.cs:147, and nothing selects `Status == Sending`.

**Atomicity: YES.** A single `UPDATE … WHERE` under InnoDB is atomic; the second racer blocks on the X lock and, because UPDATE does a *current read*, re-evaluates its WHERE against the newly committed row and matches nothing. But the plan's stated reasons are wrong in two ways and one of them produces a worthless test:
- **`UseAffectedRows` defaults to FALSE in MySqlConnector, not true.** It was changed to `false` in MySqlConnector 1.0 to match Connector/NET, i.e. the driver asks the server for FOUND_ROWS, so a conditional UPDATE that changes nothing still reports 1. In-repo proof: DidYouKnowEmailDispatchJob.cs:81-84 claims with a *single* `SetProperty(q => q.ClaimedAtUtc, …)` and demonstrably works. Consequence: keep `ClaimAttempts + 1` (it earns its place as the retry budget, and it makes the design survive someone adding `UseAffectedRows=True` to the connection string later) but **fix the rationale**, and delete/replace the test `A_reclaim_of_a_row_already_Sending_still_reports_one_affected_row` — it passes either way and proves nothing. If you want a real guard, assert on the persisted row state after a reclaim, or add `UseAffectedRows=True` to a dedicated test connection string.
- "semi-consistent read" is a READ COMMITTED optimisation; MySQL's default is REPEATABLE READ. Right conclusion, wrong mechanism — state it as "UPDATE performs a current read".

Defects the plan would introduce:

P2-1. **Do NOT add `Unsubscribed` to `IsTerminal` (EmailDeliveryTracker.cs:53).** `IsTerminal` feeds :95-99 (and the ELetter/Poll/DYK twins) where it sets `recipient.Status = …Failed` and `FailureReason`. An unsubscribe means the mail was delivered and read. This would flip successfully-delivered recipients to Failed and silently corrupt the "Delivered" column that this whole class exists to fix (see its own header, EmailDeliveryTracker.cs:8-16). Correct: map `unsubscribe`/`group_unsubscribe` to a new **non-terminal** outcome that writes only `LastEvent`, is a no-op in `ApplyTimestamps` (:191-206), and triggers suppression from `RecordAsync`. Leave `spamreport => Failed` alone.

P2-2. **Do NOT route PublicWebsiteController.cs:227/:245 through `ResubscribeAsync`.** `ResubscribeAsync` clears `EmailOptOutAt` globally, and that controller is an **unauthenticated public form**. Anyone who knows a client's email address could resurrect all marketing to someone who globally unsubscribed — a worse CASL failure than the one being fixed, plus a harassment vector. Correct: at PublicWebsiteController.cs:245, if `client.EmailOptOutAt.HasValue`, do not set `IsNewsletterSubscribed = true` and do not clear the opt-out; record the lead and say so. `ResubscribeAsync` must be reachable only from the token-authenticated EmailPreferencesController.cs:99-129.

P2-3. **Deleting `&& c.IsNewsletterSubscribed` from PollDispatcher.cs:201 is a consent regression, not a fix.** PublicWebsiteController.cs:227 creates lead-form contacts with `IsNewsletterSubscribed = false, EmailOptOutAt = null`; ClientsController.cs:880 and ContactImporter.cs:158 produce the same shape. Every one of those people starts receiving polls. Risk 8 notes the widening but calls it correct — it isn't. The plan's own governing rule (EmailConsentService.cs:16-19: "A dispatcher that needs a new exception changes this file") gives the right answer: move the rule INTO `IsSuppressed` — make `IsNewsletterSubscribed` the marketing-consent gate for `Poll` (and decide explicitly for ECard/ELetter/DidYouKnow, which ignore it today) — **then** delete the SQL copy. Deleting the filter without relocating the rule is the drift the file warns against.

P2-4. **Claim-before-load is insufficient: the JOBS load the row tracked into the same scoped context first.** ECardDispatchJob.cs:24-26 does `_db.ECards.Where(…).ToListAsync()` with no `AsNoTracking()`, and the dispatcher shares that scoped `IPRODbContext` (Program.cs:37; UnitOfWork.cs:13). By the time `SendClaims.TryClaimECardAsync` runs, the entity is already tracked with `Status = Scheduled`, and ECardDispatcher.cs:29's `FirstOrDefaultAsync` returns that stale tracked instance — EF identity resolution does not refresh a tracked entity. NewsLetterDispatcher.cs:50 has the same problem via `_uow.NewsLetterSends.GetByIdAsync`. Fix: make all four jobs `AsNoTracking().Select(x => x.Id)` (matching DidYouKnowEmailDispatchJob.cs:29-30), and `Reload()`/`ChangeTracker.Clear()` in the dispatcher after a successful claim.

P2-5. **The resume design corrupts the terminal counters on ECard/ELetter/Poll.** ECardDispatcher.cs:131-133 sets `card.Status = sentCount > 0 ? Sent : Failed` and `card.TotalSent = sentCount` where `sentCount` counts only the current pass. A reclaimed card whose remaining Queued recipients all fail is marked **Failed with TotalSent overwritten**, erasing a first pass that sent hundreds. Same at ELetterDispatcher.cs:112-116 and PollDispatcher.cs:136-139. The plan catches the `+=` double-count on PollSurvey (:143-145) but misses the `=` overwrite on the send rows. Fix: derive terminal status and counters from the recipient table (`COUNT(Status == Sent)` / `Failed`) after the loop.

P2-6. **PollSurvey is never claimed and its counters are read-modify-write.** PollDispatcher.cs:141-145 uses `+=` through the tracker on a row shared by every PollSend of that survey. The per-PollSend claim does not serialise it, so two concurrent sends of the same survey lose updates — and the incremental-save change widens the window. Fix: SQL-side increments (`SetProperty(x => x.TotalSent, x => x.TotalSent + n)`), and unwind `survey.Status` on the audience-failure path at :45-52 (plan catches the unwind only).

P2-7. **Breaks an existing passing test at compile time.** EmailConsentChannelTests.cs:18-19 does `new EmailConsentService(null!, config)`. Adding a required `IUnsubscribeNotifier` ctor parameter breaks the whole file. Make it `IUnsubscribeNotifier? = null`, or update the factory in the same commit.

P2-8. **`src/IPRO.DataAccess/SendClaims.cs` will not compile as specified.** IPRO.DataAccess.csproj has no `Microsoft.Extensions.Logging.Abstractions` PackageReference (only EF Tools, Pomelo, Hangfire.AspNetCore, Hangfire.Core + IPRO.Entities), so `RetireExhaustedAsync(…, ILogger)` needs that line added. The version is already central (Directory.Packages.props:19).

P2-9. `MaxAttempts = 3` × a 15-minute cutoff means ~45 minutes before a healthy-but-slow send is retired as Failed. Only consume an attempt on a **stale reclaim**, not on the first Scheduled→Sending transition, so ordinary sends never burn budget.

P2-10. Idempotency: always re-apply the three field writes in `SuppressAllAsync` (opt-out timestamp, clear `GreetingsOptInAt`, clear `IsNewsletterSubscribed`) and short-circuit only the two sweeps and the notification. Otherwise a client re-flagged `IsNewsletterSubscribed = true` by ClientsController.cs:880 after an earlier opt-out keeps that flag through a replayed spam complaint.

**Transactional mail: SAFE, verified.** `EmailOptOutAt` is read in exactly two places (EmailConsentService.cs:64, NewsLetterDispatcher.cs:210). Password reset (AccountController.cs:105), overdue-invoice reminders (OverdueInvoiceReminderJob.cs:62), invoice/document links (ClientInvoicesController.cs:331), portal invitations (ClientsController.cs:159) and all billing mail never consult it. And `EmailDeliveryTracker.RecordAsync` dispatches only on `ecard|eletter|poll|didyouknow` (:62-79), so a spam complaint on transactional mail cannot reach the new write path. This invariant is now load-bearing and should be stated in INVARIANTS.md rule 7 and pinned by a test, or a future "track transactional mail too" change silently starts suppressing password resets.

=== PLAN 3 (quick) — (b) FULLY CORRECT; (a) CORRECT BUT OVERSTATES LAYER 1 ===

(b) Independently confirmed every claim by reading the restore graphs: 11.0.1 in src/IPRO.Business, src/IPRO.DataAccess, src/IPRO.Utility; 13.0.1 in Billing/Email/Scheduler; 13.0.2 in Web/Admin/tests. Zero `Newtonsoft` references in any .cs/.cshtml/.csproj under src or tests. Directory.Packages.props:3-4 both already true. The one-line pin is exactly right and matches the four sibling advisory pins at :39-42. Only nuance: GHSA-5crp-9r3c-p9vr is fixed in 13.0.1, so anything ≥13.0.1 clears the scanner — 13.0.2 is the right choice for the offline-cache and no-production-delta reasons the plan gives, not because 13.0.1 is insufficient.

(a) Root cause verified: AccountController.cs:497-499 is `[HttpPost][IgnoreAntiforgeryToken]` with no `[Authorize]`; a grep for `AutoValidateAntiforgery`/`AddAntiforgery` across src returns nothing, so the plan's key insight — that deleting the attribute is a no-op and the positive attribute is required — is right; the three-way oracle at :504 / :510 / :534-537 is real; appsettings.json:77-105 has no ValidatePromoCode rule and confirms the canonical + /portal-twin convention (12 pairs plus `POST:/pub/register.aspx`). No `GeneralRules`/`IpRateLimiting` override exists anywhere in ops/, infra/, scripts/, e2e/ or .github/, so the array-index-shift risk is clean in-repo (still verify live App Service config).

Problems:

P3-1. **Layer 1 is much weaker than described.** Antiforgery tokens are neither single-use nor per-request: one GET of `/Account/Register` yields a cookie+token pair reusable for unlimited POSTs until the data-protection key rotates. `[ValidateAntiForgeryToken]` stops CSRF and naive scripts; a harvesting script adds three lines and continues. The write-up's "kills scripted enumeration" must be corrected, or someone ships layer 1 alone and closes WEB-H-2. Layers 2 and 3 are the actual controls.

P3-2. **5/5m is too tight for this endpoint.** ForgotPassword tolerates 5/5m because it is rare; Apply-promo is clicked during an active signup and the counter is per-IP and in-memory (Program.cs:107). A shared NAT (agency office, conference wifi) locks out a genuine second signup. Use 10/5m — still a ~60× reduction from the 7,200/hr the `*` rule allows, with no realistic false positive.

P3-3. **The proposed helper signature can't carry the package branch.** `BuildPromoValidationResponse(PromotionCode? promo)` cannot express "package not found", and the action must still short-circuit at :501-505 — `ValidatePromotionCodeAsync` (PayPalBillingService.cs:2694) only checks `RestrictedBillingRuleId` when it is set, so an unrestricted code would validate against a nonexistent package id. Keep both branches, and make the shared rejection string a `const` so T1.3's reference-identity assertion is meaningful.

P3-4. **T1.2 is over-tight and will fail on a legitimate message.** `Assert.DoesNotContain(message, c => char.IsDigit(c))` bans digits outright; "Code accepted — applied at step 2 of checkout" would fail. Keep the `%` / `$` / "setup" / "life of your subscription" assertions, drop the blanket digit ban, or just assert equality with the const.

P3-5. **Missed sibling, and the more valuable one: the unmetered *redemption* surface.** `AccountController.Profile` (:602-653) writes `agent.PromotionCode` with no validation, and `PayPalBillingService.cs:125` reads it at Subscribe time. There is no rate-limit rule for `POST:/Account/Profile` or `POST:/Billing/Subscribe` in appsettings.json:77-105, so harvested codes are spent at the `*` rule's 120/min. Add `POST:/Billing/Subscribe` (and its `/portal` twin) at 5m/10 in the same commit. The plan is right that Profile is not an *oracle* and should not gain validation feedback — but it is the spend path.

P3-6. **Handle 400 as well as 429 in the JS.** The plan covers the 429 text/plain vs `r.json()` trap at Register.cshtml:459 — correct and important. Also branch on 400 with a "refresh the page and try again" message; otherwise a stale antiforgery token (data protection is not explicitly configured — a grep for `DataProtection` in src/IPRO.Web/Program.cs returns nothing, so the app relies on the Azure App Service default key ring) reads as a service outage on the signup page.

P3-7. Minor: Register.cshtml:443 already early-returns if any element is missing. Read the token as `document.querySelector('form [name="__RequestVerificationToken"]')` and fail visibly if absent, rather than posting an empty token and giving every visitor a 400.

### Corrections to apply

PLAN 1
1. Replace test D.16 with a guard that actually fires: fail on any `"/Billing/` literal outside src/IPRO.Web/Infrastructure/PortalUrlHelper.cs AND on any `GetAgentPortalBaseUrl` call under src/IPRO.Web/Controllers/. After the refactor the literal `/Billing/PayPalReturn` exists in zero files (BillingController.cs:105 already uses `nameof`), so the plan's grep is vacuous.
2. Fix line refs: the AccountController duplicate is lines 425-431 (`var portalBase` :425, the two strings :429-430).
3. Drop local-dev test 18's PayPal round-trip: Program.cs:105 `CookieSecurePolicy.Always` means no auth cookie over http://localhost:5100, and PayPal rejects non-https return_url. Assert the composed string in the unit tests; do the round-trip on https only.
4. Add PollDispatcher.cs:153-162 `BuildVoteUrl` to the sibling list — it hand-rolls App:BaseUrl + its own placeholder check + its own hardcoded fallback, bypassing WebAppUrlHelper. Keep it canonical, but collapse it onto WebAppUrlHelper in the same sweep.
5. `IsAppHost` must mirror Program.cs:678-680's admin exclusion (App:AdminDomain, `admin.` prefix) or document why not; in Development the two predicates disagree (appsettings.Development.json:6 AdminDomain=localhost:5200 vs :10 PlatformDomains containing bare localhost).
6. Do not describe the custom-domain lookup as "the same predicate as PublicWebsiteController.cs:94-98" — that one is website-scoped and handles the legacy "SslBound" value (:95-99). Verify no equivalent legacy value exists for AzureBindingStatus before gating on `== AgentDomainStatus.Bound` (AgentDomain.cs:8).

PLAN 2
7. Atomicity is real, rationale is inverted: MySqlConnector's UseAffectedRows defaults to FALSE (found-rows), not true — proof in-repo is DidYouKnowEmailDispatchJob.cs:81-84, which claims with a single SetProperty and works. Keep `ClaimAttempts + 1` for the retry budget and for forward-safety, but delete the "A_reclaim_… still_reports_one_affected_row" test (it passes either way) and restate the lock argument as "UPDATE does a current read", not "semi-consistent read".
8. Do NOT add Unsubscribed to IsTerminal (EmailDeliveryTracker.cs:53) — it feeds :95-99 and would mark delivered recipients Failed. Map unsubscribe/group_unsubscribe to a non-terminal outcome that writes LastEvent only, is a no-op in ApplyTimestamps (:191-206), and triggers suppression from RecordAsync.
9. Do NOT route PublicWebsiteController.cs:227/:245 through ResubscribeAsync — it is an unauthenticated public form and ResubscribeAsync clears EmailOptOutAt globally. Keep the global opt-out; record the lead and say so. ResubscribeAsync stays reachable only from token-authenticated EmailPreferencesController.cs:99-129.
10. Do NOT simply delete `&& c.IsNewsletterSubscribed` from PollDispatcher.cs:201. Move the rule into IsSuppressed (make IsNewsletterSubscribed the marketing gate for Poll, and decide explicitly for ECard/ELetter/DidYouKnow), THEN delete the SQL copy. Otherwise lead-form contacts created at PublicWebsiteController.cs:227 with IsNewsletterSubscribed=false start receiving polls.
11. Add AsNoTracking() + `.Select(x => x.Id)` to all four jobs (ECardDispatchJob.cs:24-26 and twins) and Reload/ChangeTracker.Clear after a successful claim — the jobs pre-load the row tracked into the same scoped context (Program.cs:37, UnitOfWork.cs:13), so claim-before-load in the dispatcher does not defeat the identity map.
12. Derive terminal status and counters from the recipient table after the loop, not from the in-pass counter: ECardDispatcher.cs:131-133, ELetterDispatcher.cs:112-116, PollDispatcher.cs:136-139 all use `= sentCount`, which on a resume marks a partially-sent item Failed and overwrites TotalSent.
13. Make PollSurvey counter updates SQL-side increments (PollDispatcher.cs:141-145) — the per-PollSend claim does not serialise the shared survey row, and incremental saves widen the lost-update window. Also unwind survey.Status on the audience-failure path (:45-52).
14. Make IUnsubscribeNotifier an optional ctor parameter or update EmailConsentChannelTests.cs:18-19 in the same commit — `new EmailConsentService(null!, config)` is a compile break otherwise.
15. Add `<PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />` to src/IPRO.DataAccess/IPRO.DataAccess.csproj before writing SendClaims.RetireExhaustedAsync(…, ILogger).
16. Only consume a ClaimAttempt on a stale reclaim, not on the first Scheduled→Sending transition, so MaxAttempts=3 cannot retire a healthy slow send after ~45 minutes.
17. In SuppressAllAsync, always re-apply the three field writes and short-circuit only the sweeps and the notification.

PLAN 3
18. Restate layer 1's value honestly: antiforgery tokens are reusable, so [ValidateAntiForgeryToken] stops CSRF and naive scripts but not enumeration. Layers 2 and 3 are the controls; do not close WEB-H-2 on layer 1.
19. Use 10/5m rather than 5/5m for POST:/Account/ValidatePromoCode (both the canonical rule and its /portal twin) — per-IP in-memory counters (Program.cs:107) plus shared NAT make 5/5m a real signup-lockout risk.
20. Keep the two branches in the action (AccountController.cs:501-505 must still short-circuit on a bad packageId, because PayPalBillingService.cs:2694 only enforces RestrictedBillingRuleId when set) and return a shared `const` string from both so T1.3's identity assertion means something.
21. Drop the blanket `char.IsDigit` assertion from T1.2; keep the %/$/"setup"/"life of your subscription" assertions or assert equality with the const.
22. Add rate-limit rules for POST:/Billing/Subscribe and POST:/portal/Billing/Subscribe (5m/10) — Profile (AccountController.cs:602-653) writes agent.PromotionCode unvalidated and PayPalBillingService.cs:125 spends it, and neither path has a rule today.
23. Branch on HTTP 400 as well as 429 in the Register.cshtml fetch, and read the token as `document.querySelector('form [name="__RequestVerificationToken"]')`; a stale token (no explicit DataProtection config in src/IPRO.Web/Program.cs) otherwise reads as a service outage.

## MUST NOT BREAK

Behaviours that must still work after all three changes:

CROSS-HOST / URLS
1. Out-of-band links must stay CANONICAL, unchanged: AccountController.cs:105 (password reset), ClientsController.cs:148 (portal activation), TestimonialsController.cs:170, ClientInvoicesController.cs:36, PublicWebsiteController.cs:969/:1004 (lead notification), EmailConsentService.cs:108 (unsubscribe — the comment there says explicitly why), NewsLetterDispatcher.cs:164, ECardDispatcher/ECardDispatchers' WebAppUrlHelper call at ECardDispatcher.cs:52 (card artwork absolute URLs), OverdueInvoiceReminderJob.cs:81, PayPalBillingService.cs:1871. A blanket "make it host-aware" sweep across all 19 GetWebAppBaseUrl/GetAgentPortalBaseUrl call sites is the single most likely way to break this.
2. Google Calendar Connect must keep bouncing to the canonical host (GoogleCalendarController.cs:35-52 → :56) so Url.ActionLink at :62 produces a redirect_uri Google has registered.
3. `/Billing`, `/Billing/PayPalReturn`, `/Billing/Cancel` and `/Account/*` must keep resolving to the portal on every host (Program.cs:600) and stay exempt from the subscription gate (Program.cs:271-272).
4. `/portal/...` must remain unconditionally portal (Program.cs:641-644) and the unprefixed legacy routes must keep working (Program.cs:337-341, :367-369) — inbox links depend on them.
5. Local dev at http://localhost:5100 and http://localhost:5200 must keep building and starting offline (ops\Start-LocalEnv.ps1) — hence Newtonsoft 13.0.2, and hence Host.Value (with port), not Host.Host, in GetSessionBaseUrl.

EMAIL / CONSENT
6. Transactional mail must always send regardless of EmailOptOutAt: password reset (AccountController.cs:105), overdue-invoice reminders (OverdueInvoiceReminderJob.cs:62), invoice and document links (ClientInvoicesController.cs:331), client-portal invitations (ClientsController.cs:159), and every billing/subscription email. Today this holds because EmailOptOutAt is read only at EmailConsentService.cs:64 and NewsLetterDispatcher.cs:210 — do not add a consent check to any of those senders, and do not extend EmailDeliveryTracker.RecordAsync (:62-79) beyond ecard|eletter|poll|didyouknow.
7. The greetings exemption must survive: EmailConsentService.cs:66-77 requires BOTH ECardDesign.SendAfterUnsubscribe AND client.GreetingsOptInAt. SuppressAllAsync clears GreetingsOptInAt, which correctly kills birthday cards — but the exemption logic itself must stay intact for clients who opt back in.
8. Delivery statistics must stay honest: an unsubscribe must NOT set recipient.Status = Failed or write BouncedAt (EmailDeliveryTracker.cs:95-99, :191-206). The "Delivered" column on the Card & Letter Activity screen is the thing this class exists to populate.
9. Newsletter recipients must not be re-mailed: a resumed NewsLetterSend must reuse existing NewsLetterRecipients rows (NewsLetterDispatcher.cs:84-101) rather than rebuilding, and TotalRecipients must not double. Same for PollRecipients by PollSendId (PollDispatcher.cs:68-83).
10. A partially-sent ECard/ELetter/PollSend must not be reported as Failed with TotalSent reset (ECardDispatcher.cs:131-133, ELetterDispatcher.cs:112-116, PollDispatcher.cs:136-139).
11. Fail-closed audience resolution must survive: a send whose target client or category was deleted must still Fail loudly rather than mail everyone (NewsLetterDispatcher.cs:67-82, PollDispatcher.cs:40-53). ScheduledSendAudienceTests.cs already pins this.
12. Per-item error isolation in all four jobs must remain (NewsLetterDispatchJob.cs:27-35 and twins) — one bad item must not abort the pass.

TESTS THAT MUST STILL COMPILE AND PASS
13. EmailConsentChannelTests.cs:18-19 constructs `new EmailConsentService(null!, config)` — any ctor change must keep this compiling.
14. ScheduledSendAudienceTests.cs:77 independently mirrors the newsletter audience query (`IsNewsletterSubscribed && EmailOptOutAt == null`) — if that rule moves into IsSuppressed, this test's mirror must be updated in the same commit, not left to drift.
15. BillingPeriodGuardTests, BillingProrationMatrixTests, BillingWebhookDedupeTests, InvoiceNumberRaceTests, ClientPortalTokenSecurityTests, both DataEraser coverage suites, AgentDeletionRetentionTests — none of these should be touched; if the BillingController ctor gains IPRODbContext, check nothing constructs it directly.
16. TestDatabase.cs:40-47 uses EnsureCreatedAsync off the entity model, so new columns appear automatically in tests and the EmailDeliverySchema ALTER path (EmailDeliverySchema.cs:56-112) is never exercised by the suite — it must be verified by hand against a pre-change local MySQL snapshot, twice (add, then no-op).

SIGNUP / BILLING
17. Signup must still complete for an anonymous visitor with no account: [Authorize] must never be added to ValidatePromoCode, and a valid promo code must still reach PayPalBillingService.cs:136-160 and produce the correct discounted amount.
18. The fully-comped promo path (PayPalBillingService.cs:148-153 → AccountController.cs:437-442) must still activate without a PayPal step.
19. Both PayPal APIs must keep receiving the same two URL strings — subscriptions (PayPalBillingService.cs:2480-2481) and orders (:3141-3142).
20. No hardcoded price strings may be reintroduced; BillingRule.EffectiveSetupFee / IsSetupFeeWaivedOn remain the only source (PayPalBillingService.cs:133).

DEPLOY
21. Confirm the live build via /health/version (Program.cs:355-368), never /health, before believing any of this is running in production.
