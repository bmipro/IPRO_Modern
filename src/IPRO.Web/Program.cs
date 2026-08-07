#nullable enable

using AspNetCoreRateLimit;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.Storage.MySql;
using Microsoft.AspNetCore.HttpOverrides;
using IPRO.Billing;
using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Scheduler;
using IPRO.Utility;
using IPRO.Web.Infrastructure;
using IPRO.Web.Middleware;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Application Insights is enabled via Azure's codeless auto-instrumentation agent
// (APPLICATIONINSIGHTS_CONNECTION_STRING app setting); adding the SDK here lets
// XDT_MicrosoftApplicationInsights_PreemptSdk (already set on the App Service) hand off to it, which
// is what makes the initializer below actually run instead of being bypassed by the bare agent.
builder.Services.AddApplicationInsightsTelemetry();
builder.Services.AddSingleton<ITelemetryInitializer, SensitiveDataTelemetryInitializer>();

var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
             ?? throw new InvalidOperationException("ConnectionString 'DefaultConnection' not found.");
connStr = EnsureMySqlMigrationOptions(connStr);

builder.Services.AddDbContext<IPRODbContext>(o =>
    o.UseMySql(connStr, ServerVersion.AutoDetect(connStr)));

builder.Services.AddHangfire(config => config
   .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
   .UseSimpleAssemblyNameTypeSerializer()
   .UseRecommendedSerializerSettings()
   .UseStorage(new MySqlStorage(connStr, new MySqlStorageOptions
    {
        TablesPrefix = "Hangfire_"
    })));

builder.Services.AddHangfireServer(o =>
{
    o.WorkerCount = 5;
    o.Queues = new[] { "newsletters", "drip", "reminders", "default" };
});

// Liveness only -- no database or storage checks, deliberately. Azure's health check restarts an
// instance that reports unhealthy; a restart fixes a wedged worker process (the 2026-08-07 outage
// signature: instant 503s while "Running") but fixes nothing about a database outage, so external
// dependencies must not fail this probe.
builder.Services.AddHealthChecks();

//... rest unchanged...
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<IPasswordHasher<AgentUser>, PasswordHasher<AgentUser>>();
builder.Services.AddScoped<IPasswordHasher<Client>, PasswordHasher<Client>>();
builder.Services.AddScoped<IAgentService, AgentService>();
builder.Services.AddScoped<IPackageEntitlementService, PackageEntitlementService>();
builder.Services.AddScoped<IClientService, ClientService>();
builder.Services.AddScoped<INewsLetterService, NewsLetterService>();
builder.Services.AddScoped<IWebsiteService, WebsiteService>();
builder.Services.AddScoped<IClientInvoiceService, ClientInvoiceService>();
builder.Services.Configure<EmailSettings>(builder.Configuration.GetSection("Email"));
builder.Services.AddScoped<IEmailService, SendGridEmailService>();
builder.Services.AddScoped<NewsLetterDispatcher>();
builder.Services.AddScoped<ECardDispatcher>();
builder.Services.AddScoped<ELetterDispatcher>();
builder.Services.AddScoped<PollDispatcher>();
builder.Services.Configure<PayPalSettings>(builder.Configuration.GetSection("PayPal"));
builder.Services.Configure<AzureDomainAutomationOptions>(builder.Configuration.GetSection("AzureDomainAutomation"));
builder.Services.AddScoped<IBillingService, PayPalBillingService>();
builder.Services.Configure<GoogleCalendarSettings>(builder.Configuration.GetSection("GoogleCalendar"));
builder.Services.AddScoped<IGoogleCalendarService, GoogleCalendarService>();
builder.Services.Configure<AiSettings>(builder.Configuration.GetSection("Ai"));
builder.Services.AddScoped<IAiSuggestionService, AnthropicAiSuggestionService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IBlobStorageService, AzureBlobStorageService>();
builder.Services.AddScoped<IContactImporter, ContactImporter>();
builder.Services.AddSingleton<ITenantResolver>(_ => 
    new DomainTenantResolver(builder.Configuration["App:AdminDomain"]?? "admin.iprosystem.com"));
builder.Services.AddScoped<IAzureDomainAutomationService, AzureDomainAutomationService>();
builder.Services.AddScoped<IDomainCheckService, DomainCheckService>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
   .AddCookie(o =>
    {
        o.LoginPath = "/Account/Login";
        o.LogoutPath = "/Account/Logout";
        o.AccessDeniedPath = "/Account/AccessDenied";
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.Cookie.SameSite = SameSiteMode.Lax;
    })
   .AddCookie("ClientPortal", o =>
    {
        o.LoginPath = "/ClientPortalAccount/Login";
        o.LogoutPath = "/ClientPortalAccount/Logout";
        o.AccessDeniedPath = "/ClientPortalAccount/AccessDenied";
        o.Cookie.Name = "IPRO.ClientPortal";
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.Cookie.SameSite = SameSiteMode.Lax;
    });
builder.Services.AddAuthorization();
builder.Services.AddMemoryCache();
builder.Services.Configure<IpRateLimitOptions>(builder.Configuration.GetSection("IpRateLimiting"));
// AspNetCoreRateLimit's IpRateLimitOptions.RealIpHeader defaults to "X-Real-IP" INSIDE THE LIBRARY
// ITSELF - removing the appsettings.json key that used to override it does NOT clear this, since
// config binding only overwrites keys present in the JSON. With it still set, RegisterResolvers()
// adds a header-based IP resolver ahead of the connection-based one, and the client-supplied
// X-Real-IP header (not X-Forwarded-For, and never touched by UseForwardedHeaders) wins every time
// with zero trust/origin validation - a complete rate-limit bypass (H-1's original bug, security
// audit 2026-07-24). Forcing it null here makes RegisterResolvers() skip the header resolver
// entirely and fall through to the connection-based one, which the ForwardedHeaders configuration
// below has already made trustworthy.
builder.Services.PostConfigure<IpRateLimitOptions>(o => o.RealIpHeader = null);
builder.Services.AddSingleton<IIpPolicyStore, MemoryCacheIpPolicyStore>();
builder.Services.AddSingleton<IRateLimitCounterStore, MemoryCacheRateLimitCounterStore>();
builder.Services.AddSingleton<IRateLimitConfiguration, RateLimitConfiguration>();
builder.Services.AddSingleton<IProcessingStrategy, AsyncKeyLockProcessingStrategy>();
builder.Services.AddInMemoryRateLimiting();
builder.Services.AddControllersWithViews()
    .AddMvcOptions(o => o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true);
builder.Services.Configure<Microsoft.AspNetCore.Mvc.Razor.RazorViewEngineOptions>(o =>
    o.ViewLocationExpanders.Add(new IPRO.Web.Infrastructure.PublicWebsiteViewLocationExpander()));
builder.Services.AddSession(o =>
{
    o.IdleTimeout = TimeSpan.FromMinutes(30);
    o.Cookie.HttpOnly = true;
    o.Cookie.IsEssential = true;
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Azure App Service's front-end always sits directly in front of this container -- the
    // platform does not allow a public request to reach Kestrel without passing through it --
    // so trusting X-Forwarded-For/-Proto from whatever connects directly to us is equivalent to
    // trusting Azure's own edge here, not "trust anyone." Azure's internal network presents as
    // IPv4-mapped IPv6 addresses on these private ranges, including the 169.254.0.0/16 link-local
    // range the front-end actually connects from - omitting it left every request untrusted and
    // silently falling back to that internal hop address instead of the real client IP (caught
    // 2026-07-28 via a RegistrationIpAddress showing 169.254.x.x instead of a real client IP).
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
    foreach (var cidr in new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16" })
    {
        var (prefix, length) = ParseCidr(cidr);
        options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, length));
        options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix.MapToIPv6(), length + 96));
    }
});

var app = builder.Build();

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseSecurityHeaders();
app.UseIpRateLimiting();
app.UseHttpsRedirection();
app.UseStaticFiles();
var portalRoutePrefixes = BuildPortalRoutePrefixes();
app.Use(async (context, next) =>
{
    // PUBLIC PAGE SLUGS THAT COLLIDE WITH PORTAL CONTROLLER NAMES (fixed 2026-08-06)
    //
    // BuildPortalRoutePrefixes reserves every controller name in this assembly, and the portal and the
    // public websites share one application and one URL space. So an agent page slugged "testimonials"
    // was swallowed by TestimonialsController and the VISITOR was shown a login form. Confirmed live on
    // a real agent site: /testimonials, /articles, /forms, /documents and /newsletter all 302'd to
    // /Account/Login. The Testimonials page ships in the default starter navigation, so this affected
    // every agent provisioned since Nav v2 -- the nav link renders perfectly and only fails when clicked.
    //
    // The rule now: on a public agent host, a reserved prefix still wins UNLESS that agent's site
    // actually has a published page with that slug. Deliberately narrow -- it can only ever un-break a
    // page that exists and is currently unreachable, and it cannot take a portal route away from
    // anything else.
    await MarkPublicSlugOverrideAsync(context, app.Configuration, portalRoutePrefixes);

    if (ShouldRouteToPublicWebsite(context, app.Configuration, portalRoutePrefixes))
    {
        context.Items["IproPublicPath"] = context.Request.Path.Value is { Length: > 0 } rawPath ? rawPath : "/";
        var requestedPath = context.Request.Path.Value?.Trim('/') ?? string.Empty;
        var existingQuery = context.Request.QueryString;
        if (requestedPath.Equals("PublicWebsite", StringComparison.OrdinalIgnoreCase) ||
            requestedPath.Equals("PublicWebsite/Page", StringComparison.OrdinalIgnoreCase))
        {
            context.Request.Path = "/PublicWebsite";
        }
        else if (requestedPath.Equals("PublicWebsite/DownloadLeadMagnet", StringComparison.OrdinalIgnoreCase))
        {
            context.Request.Path = "/PublicWebsite/DownloadLeadMagnet";
        }
        else if (requestedPath.StartsWith("PublicWebsite/Page/", StringComparison.OrdinalIgnoreCase))
        {
            var publicSlug = requestedPath["PublicWebsite/Page/".Length..];
            context.Request.Path = "/PublicWebsite/Page";
            context.Request.QueryString = existingQuery.Add("slug", publicSlug);
        }
        else if (string.IsNullOrWhiteSpace(requestedPath))
        {
            context.Request.Path = "/PublicWebsite";
        }
        else
        {
            context.Request.Path = "/PublicWebsite/Page";
            context.Request.QueryString = existingQuery.Add("slug", requestedPath);
        }
    }

    await next();
});
app.UseRouting();
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";

    // The /portal alias must be exempt exactly like the unprefixed routes (review L-3). This is
    // not cosmetic: since the portal route registered ahead of default, generated form actions
    // prefer /portal/..., so a billing-locked agent's Subscribe POST goes to
    // /portal/Billing/Subscribe -- and without this normalization the gate 302'd that POST
    // straight back to /Billing, silently blocking the one action the lock exists to funnel
    // them into: paying. Caught live in the local environment during the step-5 sandbox test.
    if (path.StartsWith("/portal/", StringComparison.OrdinalIgnoreCase))
    {
        path = path["/portal".Length..];
    }

    var canChangePassword = path.StartsWith("/Account/ChangePassword", StringComparison.OrdinalIgnoreCase);
    var canLogout = path.StartsWith("/Account/Logout", StringComparison.OrdinalIgnoreCase);
    var isAuthenticated = context.User.Identity?.IsAuthenticated == true;
    var mustChangePassword = isAuthenticated
        && string.Equals(context.User.FindFirst("MustChangePassword")?.Value, "true", StringComparison.OrdinalIgnoreCase);

    if (mustChangePassword && !canChangePassword && !canLogout)
    {
        context.Response.Redirect("/Account/ChangePassword");
        return;
    }

    // No active subscription and outside the trial + grace window (or never on a trial at all):
    // every tab except Billing (and Account, so they can still log out) is blocked until they
    // subscribe. Checked here rather than baked into the auth cookie because access needs to stop
    // working the moment the grace period lapses, not just at the next login.
    var canUseBilling = string.Equals(path, "/Billing", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/Billing/", StringComparison.OrdinalIgnoreCase);
    var canLoginOrLogout = path.StartsWith("/Account/Login", StringComparison.OrdinalIgnoreCase) || canLogout;
    var isAgentSession = context.User.Identity?.AuthenticationType == CookieAuthenticationDefaults.AuthenticationScheme;
    if (isAuthenticated && isAgentSession && !mustChangePassword && !canUseBilling && !canLoginOrLogout)
    {
        var idClaim = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (int.TryParse(idClaim, out var gateAgentId))
        {
            var entitlements = context.RequestServices.GetRequiredService<IPackageEntitlementService>();
            if (await entitlements.IsAccessGatedAsync(gateAgentId))
            {
                context.Response.Redirect("/Billing");
                return;
            }
        }
    }

    await next();
});

app.MapControllerRoute(
    "legacy-register",
    "pub/register.aspx",
    new { controller = "Account", action = "Register" });

// PORTAL ROUTES LIVE UNDER /portal (added 2026-08-07)
//
// The portal and every agent's public website share one application and one URL space, so a portal
// controller name and an agent's page slug are the same string. That collision has now produced two
// production bugs in two days, in opposite directions: an agent's /testimonials page showed visitors
// a login form (fixed 9fa27c8), and then the fix for that served a signed-in agent their own public
// page instead of the portal screen (fixed cb12b1d).
//
// Both are symptoms of the namespace, not of either fix. Under /portal the rule needs no heuristics:
// anything beginning /portal is the portal, everything else belongs to the agent's site. No database
// lookup, no cookie sniffing, no reserved-prefix list.
//
// ADDITIVE ON PURPOSE. The unprefixed routes below still work, because password-reset links, invoice
// links and client-portal invitations already sitting in people's inboxes point at them. Portal
// navigation moves to the prefixed form now; retiring the unprefixed routes and deleting the whole
// override mechanism is a separate, later change once nothing in the wild depends on them.
app.MapHealthChecks("/health");

app.MapControllerRoute("portal", "portal/{controller=Dashboard}/{action=Index}/{id?}");

app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
app.MapHangfireDashboard("/hangfire", new DashboardOptions
{
    IsReadOnlyFunc = _ => false,
    Authorization = new[] { new WebDashboardAuthorizationFilter(app.Environment.IsDevelopment()) }
});

RecurringJob.AddOrUpdate<NewsLetterDispatchJob>("dispatch-newsletters", job => job.RunAsync(), Cron.Minutely);
RecurringJob.AddOrUpdate<PollDispatchJob>("dispatch-polls", job => job.RunAsync(), Cron.Minutely);
RecurringJob.AddOrUpdate<DidYouKnowEmailDispatchJob>("dispatch-did-you-know-emails", job => job.RunAsync(), Cron.Minutely);
RecurringJob.AddOrUpdate<DripCampaignJob>("drip-campaigns", job => job.RunAsync(), Cron.Hourly);
RecurringJob.AddOrUpdate<CalendarReminderJob>("calendar-reminders", job => job.RunAsync(), Cron.Hourly);
RecurringJob.AddOrUpdate<SubscriptionBillingJob>("subscription-billing", job => job.RunAsync(), Cron.Hourly);
RecurringJob.AddOrUpdate<DomainAutomationJob>("domain-automation", job => job.RunAsync(), "*/5 * * * *");
RecurringJob.AddOrUpdate<RecurringClientInvoiceJob>("recurring-client-invoices", job => job.RunAsync(), Cron.Daily);
RecurringJob.AddOrUpdate<GoogleCalendarSyncJob>("google-calendar-sync", job => job.RunAsync(), "*/15 * * * *");
RecurringJob.AddOrUpdate<ClientLifeEventReminderJob>("client-life-event-reminders", job => job.RunAsync(), Cron.Daily);
RecurringJob.AddOrUpdate<OverdueInvoiceReminderJob>("overdue-invoice-reminders", job => job.RunAsync(), Cron.Daily);
RecurringJob.AddOrUpdate<AiDailyDigestJob>("ai-daily-digest", job => job.RunAsync(), Cron.Daily);
RecurringJob.AddOrUpdate<TrialReminderJob>("trial-reminders", job => job.RunAsync(), Cron.Daily);
RecurringJob.AddOrUpdate<ECardDispatchJob>("dispatch-ecards", job => job.RunAsync(), Cron.Minutely);
RecurringJob.AddOrUpdate<ELetterDispatchJob>("dispatch-eletters", job => job.RunAsync(), Cron.Minutely);
// 07:00 UTC so a due certificate is a red row on the Job Scheduler dashboard at the start of the
// day rather than overnight. Deliberately fails when renewal is due -- see CertificateExpiryJob.
RecurringJob.AddOrUpdate<CertificateExpiryJob>("certificate-expiry", job => job.RunAsync(), "0 7 * * *");

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IPRODbContext>();
    await EnsureWebsiteTemplateSchemaAsync(db);
    await WebsiteContentSchema.EnsureAsync(db);
    await EnsureWebsiteLeadSchemaAsync(db);
    await EnsureWebsiteContentBlockSchemaAsync(db);
    await EnsureDripCampaignEnrollmentSchemaAsync(db);
    await EnsureNewsLetterTemplateSchemaAsync(db);

    // Starter content is optional: an agent can work without a card design or a letter template,
    // so a failure here must never stop the app from booting. This is the same isolation the
    // background jobs already use, applied to startup -- learned the hard way, when a seeding
    // failure took both sites down with an unhandled exception during startup.
    //
    // Structural seeders above (entitlements, tax rates, website templates) are deliberately NOT
    // wrapped: an agent genuinely cannot function without those, so failing loudly is correct.
    var seedLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
        .CreateLogger("StarterContentSeeding");
    try
    {
        await NewsLetterTemplateSeeder.SeedAsync(db, seedLogger);
        await ECardDesignSeeder.SeedAsync(db, seedLogger);
        await ELetterTemplateSeeder.SeedAsync(db, seedLogger);
    }
    catch (Exception ex)
    {
        seedLogger.LogError(ex, "Starter content seeding failed. The app is starting anyway; " +
                                "newsletter templates, e-card designs or e-letter templates may be missing.");
        // Also to stderr: the container log is readable even when telemetry never flushes.
        Console.Error.WriteLine("[StarterContentSeeding] FAILED: " + ex);
    }

    try
    {
        await EnsureWebsiteStarterArticleSchemaAsync(db);
        await WebsiteStarterArticleSeeder.SeedAsync(db, seedLogger);
    }
    catch (Exception ex)
    {
        seedLogger.LogError(ex, "Website starter article seeding failed. The app is starting anyway; " +
                                "the Resources starter article library may be missing.");
        Console.Error.WriteLine("[WebsiteStarterArticleSeeding] FAILED: " + ex);
    }

    try
    {
        await EnsureWebsiteStarterFormSchemaAsync(db);
        await WebsiteStarterFormSeeder.SeedAsync(db, seedLogger);
    }
    catch (Exception ex)
    {
        seedLogger.LogError(ex, "Website starter form seeding failed. The app is starting anyway; " +
                                "the Starter Forms template library may be missing.");
        Console.Error.WriteLine("[WebsiteStarterFormSeeding] FAILED: " + ex);
    }

    await EnsureDripCampaignStepSendSchemaAsync(db);
    await EnsureDidYouKnowEmailQueueSchemaAsync(db);
    await EnsureNewsLetterClickTrackingSchemaAsync(db);
    await EnsureSupportTicketSchemaAsync(db);
    await EnsurePromotionCodeSchemaAsync(db);
    await EnsureClientInvoiceSchemaAsync(db);
    await EnsureClientPortalSchemaAsync(db);
    await EnsureClientLifeEventSchemaAsync(db);
    await EnsureAgentDocumentSchemaAsync(db);
    await EnsureSocialPostSchemaAsync(db);
    await EnsureTestimonialSubmissionSchemaAsync(db);
    await EnsurePollSchemaAsync(db);
    await EnsureWebsiteFormSchemaAsync(db);
    await EnsureAgentDailyInsightSchemaAsync(db);
    await EnsureAiUsageSchemaAsync(db);
    await EnsureTrialFeatureSchemaAsync(db);
    await EnsureECardSchemaAsync(db);
    await EnsureELetterSchemaAsync(db);
    // Wrapped for the same reason as the seeders below: these two tables back an admin-only
    // content library, and a DDL failure here must not stop the whole app from serving.
    try
    {
        await EnsureECardDesignSchemaAsync(db);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("[ECardDesignSchema] FAILED: " + ex);
    }
    await db.Database.MigrateAsync();
    await PackageEntitlementSeeder.SeedAsync(db);
    await TaxRateSeeder.SeedAsync(db);
    await WebsiteTemplateSeeder.SeedAsync(db);
    await WebsiteStarterContentSeeder.SeedAsync(db, seedLogger);
    await WebsiteStarterContentSeeder.SeedNavV2AdditionsAsync(db, seedLogger);

    var blob = scope.ServiceProvider.GetRequiredService<IBlobStorageService>();
    await blob.EnsureContainerAccessAsync("portal-documents", isPrivate: true);
    await blob.EnsureContainerAccessAsync("agent-documents", isPrivate: true);
}

app.Run();

// Every real app route (agent portal, admin bits, client portal, etc.) must work from an
// agent's own domain (temporary *.247advisers.com or a custom domain) too - agents manage
// their whole portal from that one URL, not from an internal Azure hostname. Reflects over
// every MVC controller once at startup to build the set of first-path-segment prefixes that
// are real app routes (respecting a class-level [Route] override where one exists, e.g.
// TestimonialRequestController -> "testimonial") so any request whose first segment matches
// falls through to normal MVC routing instead of being swallowed by the page-slug lookup
// below. This is deliberately NOT a hand-maintained list: a single missed entry here once
// took down an agent's entire portal (everything but the login page) in production, because
// nothing failed loudly - it just silently looked like "page not found."
static HashSet<string> BuildPortalRoutePrefixes()
{
    var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var type in typeof(Program).Assembly.GetTypes())
    {
        if (!typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(type) || type.IsAbstract) continue;
        // PublicWebsiteController IS the page-slug lookup target - its own specific paths
        // (/PublicWebsite, /PublicWebsite/Page/{slug}, ...) are already handled by the more
        // specific branches below; it must not reserve "PublicWebsite" as an off-limits slug.
        if (string.Equals(type.Name, "PublicWebsiteController", StringComparison.OrdinalIgnoreCase)) continue;

        var routeTemplate = type.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RouteAttribute), false)
            .Cast<Microsoft.AspNetCore.Mvc.RouteAttribute>()
            .FirstOrDefault()?.Template;

        string prefix;
        if (!string.IsNullOrWhiteSpace(routeTemplate))
        {
            prefix = routeTemplate.Split('/')[0];
            if (prefix.Contains('{') || prefix.Contains('[')) continue; // route param/token first, not a literal prefix
        }
        else
        {
            var name = type.Name;
            prefix = name.EndsWith("Controller", StringComparison.Ordinal) ? name[..^"Controller".Length] : name;
        }

        if (!string.IsNullOrWhiteSpace(prefix)) prefixes.Add(prefix);
    }
    return prefixes;
}

// Marker left on HttpContext.Items when a reserved portal prefix should yield to a real public page.
const string PublicSlugOverrideKey = "IproPublicSlugOverride";

// Routes a colliding slug to the public site ONLY when all of the following hold:
//   - it is a GET for an extensionless path (same preconditions the router already applies)
//   - the first segment collides with a portal controller name
//   - the segment is not one the portal can never surrender (see NeverShadowedPrefixes)
//   - the host resolves to a real agent website
//   - that website has a PUBLISHED page with exactly that slug
//
// The database is only touched when a collision actually occurs, which is rare -- ordinary public
// slugs and every genuine portal request return before the query.
static async Task MarkPublicSlugOverrideAsync(HttpContext context, IConfiguration configuration, HashSet<string> portalRoutePrefixes)
{
    if (!HttpMethods.IsGet(context.Request.Method)) return;
    if (context.Request.Path.HasValue && Path.HasExtension(context.Request.Path.Value)) return;

    // Anything under /portal is unambiguously the portal and never a public slug.
    if (context.Request.Path.StartsWithSegments("/portal", StringComparison.OrdinalIgnoreCase)) return;

    var requestPath = context.Request.Path.Value?.Trim('/') ?? string.Empty;
    if (requestPath.Length == 0) return;

    var firstSegment = requestPath.Split('/', 2)[0];
    if (!portalRoutePrefixes.Contains(firstSegment)) return;

    // An agent must never be able to make their own login unreachable by naming a page "account".
    // Everything an agent needs to administer or recover the site stays reserved unconditionally.
    if (IsNeverShadowedPrefix(firstSegment)) return;

    // Only agent-facing public hosts are eligible. The portal host, the admin host, azurewebsites.net
    // and localhost all keep the existing behaviour untouched.
    if (!IsPublicAgentHost(context, configuration)) return;

    // An agent signed into the PORTAL on their own domain must keep the portal routes.
    //
    // The portal is reachable on an agent's custom domain (the public site footer carries a login
    // link), and the auth cookie is host-only. So an agent who signs in at www.theirfirm.com is
    // working in the portal on that host -- and their sidebar links are relative. Without this,
    // clicking "Testimonials" in the portal served them their own PUBLIC testimonials page, which is
    // exactly what the 2026-08-06 fix did to the reverse case. Reported 2026-08-07.
    //
    // Read as a cookie rather than context.User because this middleware necessarily runs before
    // UseAuthentication: the path rewrite has to happen before UseRouting selects an endpoint, and
    // authentication sits after routing. A stale or invalid cookie merely yields the portal route and
    // its login redirect, which is the pre-fix behaviour and safe.
    if (HasPortalSessionCookie(context)) return;

    try
    {
        var db = context.RequestServices.GetService<IPRO.DataAccess.IPRODbContext>();
        if (db == null) return;

        var host = NormalizeHostForLookup(context.Request.Host.Host);
        var hostCandidates = new[] { host, host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : "www." + host };

        var slug = firstSegment.ToLowerInvariant();
        var exists = await db.WebsitePages
            .AsNoTracking()
            .AnyAsync(p => p.IsPublished
                        && p.Slug.ToLower() == slug
                        && (hostCandidates.Contains(p.AgentWebsite.CustomDomain.ToLower())
                            || hostCandidates.Contains(p.AgentWebsite.AgentUser.DomainName.ToLower())
                            || db.AgentDomains.Any(d => d.AgentWebsiteId == p.AgentWebsiteId
                                && (hostCandidates.Contains(d.DomainName.ToLower())
                                 || hostCandidates.Contains(d.RootDomain.ToLower())
                                 || hostCandidates.Contains(d.WwwDomain.ToLower())))));

        if (exists)
        {
            context.Items[PublicSlugOverrideKey] = true;
        }
    }
    catch
    {
        // A lookup failure must never take the site down: fall through to the previous behaviour,
        // which is the portal route. Worst case the page stays unreachable, exactly as it is today.
    }
}

// Reserved no matter what an agent names a page. Losing any of these on an agent's own domain would
// lock them (or their clients) out rather than merely hiding content.
static bool IsNeverShadowedPrefix(string segment) => segment.ToLowerInvariant() switch
{
    "account" or "clientportal" or "clientportalaccount" or "billing"
        or "publicwebsite" or "media" or "hangfire" or "health" => true,
    _ => false
};

// True when the request carries an agent-portal auth cookie for THIS host.
//
// The default cookie scheme sets no explicit Cookie.Name, so ASP.NET Core uses
// ".AspNetCore." + scheme name. Matched by prefix because a large identity is split into chunked
// cookies (".AspNetCore.CookiesC1", "C2", ...) and an exact-name check would miss those.
//
// Deliberately not renaming the cookie to something fixed: that would invalidate every signed-in
// agent's session on deploy, which is a worse trade than depending on a stable framework default.
static bool HasPortalSessionCookie(HttpContext context)
{
    const string prefix = ".AspNetCore." + CookieAuthenticationDefaults.AuthenticationScheme;
    foreach (var key in context.Request.Cookies.Keys)
    {
        if (key.StartsWith(prefix, StringComparison.Ordinal)) return true;
    }
    return false;
}

static string NormalizeHostForLookup(string host) => host.Trim().Trim('.').ToLowerInvariant();

static bool IsPublicAgentHost(HttpContext context, IConfiguration configuration)
{
    var host = NormalizeHostForLookup(context.Request.Host.Host);
    if (string.IsNullOrWhiteSpace(host)) return false;
    if (host is "localhost" or "127.0.0.1" or "::1") return false;
    if (host.EndsWith(".azurewebsites.net", StringComparison.OrdinalIgnoreCase)) return false;
    if (host.StartsWith("admin.", StringComparison.OrdinalIgnoreCase)) return false;

    var adminDomain = configuration["App:AdminDomain"]?.Trim().Trim('.').ToLowerInvariant();
    if (!string.IsNullOrWhiteSpace(adminDomain) && host == adminDomain) return false;

    var platformDomains = (configuration["App:PlatformDomains"] ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(d => d.Trim().Trim('.').ToLowerInvariant())
        .Where(d => d.Length > 0);
    return !platformDomains.Contains(host);
}

static bool ShouldRouteToPublicWebsite(HttpContext context, IConfiguration configuration, HashSet<string> portalRoutePrefixes)
{
    if (!HttpMethods.IsGet(context.Request.Method)) return false;
    if (context.Request.Path.HasValue && Path.HasExtension(context.Request.Path.Value)) return false;

    // /portal belongs to the portal on every host, unconditionally. This is the whole point of the
    // prefix: no slug lookup, no cookie check, no reserved list -- one segment decides it. An agent
    // cannot reach it by naming a page "portal" either, because this returns before any of that.
    if (context.Request.Path.StartsWithSegments("/portal", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var requestPath = context.Request.Path.Value?.Trim('/') ?? string.Empty;
    var firstSegment = requestPath.Split('/', 2)[0];
    if (firstSegment.Length > 0 && portalRoutePrefixes.Contains(firstSegment))
    {
        // A portal controller name wins... unless the agent whose site this is has genuinely built a
        // public page with that slug. See ResolvesToRealPublicPage below for why this exception has
        // to exist at all.
        if (!context.Items.ContainsKey(PublicSlugOverrideKey))
        {
            return false;
        }
    }

    var host = context.Request.Host.Host.Trim().Trim('.').ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(host)) return false;
    if (host is "localhost" or "127.0.0.1" or "::1") return false;
    if (host.EndsWith(".azurewebsites.net", StringComparison.OrdinalIgnoreCase)) return false;

    var adminDomain = configuration["App:AdminDomain"]?.Trim().Trim('.').ToLowerInvariant();
    if (!string.IsNullOrWhiteSpace(adminDomain) && host == adminDomain) return false;
    if (host.StartsWith("admin.", StringComparison.OrdinalIgnoreCase)) return false;

    var platformDomains = (configuration["App:PlatformDomains"] ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(d => d.Trim().Trim('.').ToLowerInvariant())
        .Where(d => !string.IsNullOrWhiteSpace(d))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (platformDomains.Contains(host)) return false;

    var baseUrl = configuration["App:BaseUrl"];
    if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
        string.Equals(host, uri.Host, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var temporaryRoot = (configuration["App:TemporarySiteRootDomain"] ?? "247advisers.com")
        .Trim()
        .Trim('.')
        .ToLowerInvariant();

    return host.EndsWith("." + temporaryRoot, StringComparison.OrdinalIgnoreCase) || !platformDomains.Contains(host);
}

static string EnsureMySqlMigrationOptions(string connectionString)
{
    return connectionString.Contains("Allow User Variables", StringComparison.OrdinalIgnoreCase)
        ? connectionString
        : connectionString.TrimEnd(';') + ";Allow User Variables=True";
}

static (System.Net.IPAddress prefix, int length) ParseCidr(string cidr)
{
    var parts = cidr.Split('/');
    return (System.Net.IPAddress.Parse(parts[0]), int.Parse(parts[1]));
}

static async Task EnsureWebsiteTemplateSchemaAsync(IPRODbContext db)
{
    await db.Database.OpenConnectionAsync();
    try
    {
        await EnsureAgentDomainSchemaAsync(db);
        await EnsureBillingRuleSchemaAsync(db);
        await EnsureWebsiteTemplateColumnAsync(db, "BusinessType", "ALTER TABLE `WebsiteTemplates` ADD COLUMN `BusinessType` longtext CHARACTER SET utf8mb4 NULL");
        await EnsureWebsiteTemplateColumnAsync(db, "IsDefault", "ALTER TABLE `WebsiteTemplates` ADD COLUMN `IsDefault` tinyint(1) NOT NULL DEFAULT FALSE");
        await EnsureWebsiteTemplateColumnAsync(db, "TemplateKey", "ALTER TABLE `WebsiteTemplates` ADD COLUMN `TemplateKey` varchar(80) CHARACTER SET utf8mb4 NULL");
        await EnsureTableColumnAsync(db, "AgentWebsites", "HeaderSettingsJson", "ALTER TABLE `AgentWebsites` ADD COLUMN `HeaderSettingsJson` longtext CHARACTER SET utf8mb4 NULL");
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE `AgentWebsites` SET `HeaderSettingsJson` = {0} WHERE `HeaderSettingsJson` IS NULL OR `HeaderSettingsJson` = ''",
            "{}");
        await EnsureTableColumnAsync(db, "AgentWebsites", "FooterSettingsJson", "ALTER TABLE `AgentWebsites` ADD COLUMN `FooterSettingsJson` longtext CHARACTER SET utf8mb4 NULL");
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE `AgentWebsites` SET `FooterSettingsJson` = {0} WHERE `FooterSettingsJson` IS NULL OR `FooterSettingsJson` = ''",
            "{}");
        await EnsureTableColumnAsync(db, "AgentWebsites", "FontFamilyOverride", "ALTER TABLE `AgentWebsites` ADD COLUMN `FontFamilyOverride` longtext CHARACTER SET utf8mb4 NULL");
        await EnsureTableColumnAsync(db, "AgentWebsites", "HeadingFontSizeOverride", "ALTER TABLE `AgentWebsites` ADD COLUMN `HeadingFontSizeOverride` int NOT NULL DEFAULT 0");
        await EnsureTableColumnAsync(db, "AgentWebsites", "BodyFontSizeOverride", "ALTER TABLE `AgentWebsites` ADD COLUMN `BodyFontSizeOverride` int NOT NULL DEFAULT 0");
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE `AgentWebsites` SET `FontFamilyOverride` = {0} WHERE `FontFamilyOverride` IS NULL",
            "");
        await EnsureTableColumnAsync(db, "AgentWebsites", "BackgroundColorOverride", "ALTER TABLE `AgentWebsites` ADD COLUMN `BackgroundColorOverride` longtext CHARACTER SET utf8mb4 NULL");
        await EnsureTableColumnAsync(db, "AgentWebsites", "ButtonStyleOverride", "ALTER TABLE `AgentWebsites` ADD COLUMN `ButtonStyleOverride` longtext CHARACTER SET utf8mb4 NULL");
        await EnsureTableColumnAsync(db, "AgentWebsites", "SectionSpacingOverride", "ALTER TABLE `AgentWebsites` ADD COLUMN `SectionSpacingOverride` longtext CHARACTER SET utf8mb4 NULL");
        await EnsureTableColumnAsync(db, "AgentWebsites", "HeroStyleOverride", "ALTER TABLE `AgentWebsites` ADD COLUMN `HeroStyleOverride` longtext CHARACTER SET utf8mb4 NULL");
        await EnsureTableColumnAsync(db, "AgentWebsites", "SidebarPositionOverride", "ALTER TABLE `AgentWebsites` ADD COLUMN `SidebarPositionOverride` longtext CHARACTER SET utf8mb4 NULL");
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE `AgentWebsites` SET `BackgroundColorOverride` = {0} WHERE `BackgroundColorOverride` IS NULL",
            "");
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE `AgentWebsites` SET `ButtonStyleOverride` = {0} WHERE `ButtonStyleOverride` IS NULL",
            "");
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE `AgentWebsites` SET `SectionSpacingOverride` = {0} WHERE `SectionSpacingOverride` IS NULL",
            "");
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE `AgentWebsites` SET `HeroStyleOverride` = {0} WHERE `HeroStyleOverride` IS NULL",
            "");
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE `AgentWebsites` SET `SidebarPositionOverride` = {0} WHERE `SidebarPositionOverride` IS NULL",
            "");
        await db.Database.ExecuteSqlRawAsync("UPDATE `WebsiteTemplates` SET `BusinessType` = '' WHERE `BusinessType` IS NULL");
        await db.Database.ExecuteSqlRawAsync("UPDATE `WebsiteTemplates` SET `TemplateKey` = CONCAT('template-', `Id`) WHERE `TemplateKey` IS NULL OR `TemplateKey` = ''");
    }
    finally
    {
        await db.Database.CloseConnectionAsync();
    }
}

static async Task EnsureBillingRuleSchemaAsync(IPRODbContext db)
{
    await EnsureTableColumnAsync(db, "BillingRules", "MonthlyPrice", "ALTER TABLE `BillingRules` ADD COLUMN `MonthlyPrice` decimal(10,2) NOT NULL DEFAULT 0");
    await EnsureTableColumnAsync(db, "BillingRules", "QuarterlyPrice", "ALTER TABLE `BillingRules` ADD COLUMN `QuarterlyPrice` decimal(10,2) NOT NULL DEFAULT 0");
    await EnsureTableColumnAsync(db, "BillingRules", "AnnualPrice", "ALTER TABLE `BillingRules` ADD COLUMN `AnnualPrice` decimal(10,2) NOT NULL DEFAULT 0");
    await EnsureTableColumnAsync(db, "BillingRules", "SetupFee", "ALTER TABLE `BillingRules` ADD COLUMN `SetupFee` decimal(10,2) NOT NULL DEFAULT 0");
    await EnsureTableColumnAsync(db, "BillingRules", "PayPalMonthlyPlanId", "ALTER TABLE `BillingRules` ADD COLUMN `PayPalMonthlyPlanId` longtext CHARACTER SET utf8mb4 NULL");
    await EnsureTableColumnAsync(db, "BillingRules", "PayPalAnnualPlanId", "ALTER TABLE `BillingRules` ADD COLUMN `PayPalAnnualPlanId` longtext CHARACTER SET utf8mb4 NULL");
    await EnsureTableColumnAsync(db, "BillingRules", "MaxClients", "ALTER TABLE `BillingRules` ADD COLUMN `MaxClients` int NOT NULL DEFAULT 500");
    await EnsureTableColumnAsync(db, "BillingRules", "MaxNewsletters", "ALTER TABLE `BillingRules` ADD COLUMN `MaxNewsletters` int NOT NULL DEFAULT 12");
    await EnsureTableColumnAsync(db, "BillingRules", "DefaultWebsiteTemplateId", "ALTER TABLE `BillingRules` ADD COLUMN `DefaultWebsiteTemplateId` int NULL");
    await EnsureTableColumnAsync(db, "BillingRules", "IsActive", "ALTER TABLE `BillingRules` ADD COLUMN `IsActive` tinyint(1) NOT NULL DEFAULT TRUE");
    await EnsureTableColumnAsync(db, "BillingRules", "CreatedAt", "ALTER TABLE `BillingRules` ADD COLUMN `CreatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)");
    await EnsureTableColumnAsync(db, "BillingRules", "IsTrialPackage", "ALTER TABLE `BillingRules` ADD COLUMN `IsTrialPackage` tinyint(1) NOT NULL DEFAULT FALSE");
    await EnsureTableColumnAsync(db, "BillingRules", "TrialDurationDays", "ALTER TABLE `BillingRules` ADD COLUMN `TrialDurationDays` int NULL");
    await EnsureTableColumnAsync(db, "BillingRules", "TrialReminderDayOffsets", "ALTER TABLE `BillingRules` ADD COLUMN `TrialReminderDayOffsets` varchar(120) CHARACTER SET utf8mb4 NULL");
}

static async Task EnsureAgentDomainSchemaAsync(IPRODbContext db)
{
    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `AgentDomains` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `AgentUserId` int NOT NULL,
    `AgentWebsiteId` int NOT NULL,
    `DomainName` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
    `RootDomain` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
    `WwwDomain` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
    `DnsTarget` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
    `DnsStatus` varchar(40) CHARACTER SET utf8mb4 NOT NULL,
    `AzureBindingStatus` varchar(40) CHARACTER SET utf8mb4 NOT NULL,
    `SslStatus` varchar(40) CHARACTER SET utf8mb4 NOT NULL,
    `IsPrimary` tinyint(1) NOT NULL,
    `LastCheckedAt` datetime(6) NULL,
    `LastError` varchar(1000) CHARACTER SET utf8mb4 NOT NULL,
    `RetryCount` int NOT NULL DEFAULT 0,
    `LastFailedAt` datetime(6) NULL,
    `NextRetryAt` datetime(6) NULL,
    `AutoRetryExhausted` tinyint(1) NOT NULL DEFAULT FALSE,
    `RootDnsStatus` varchar(40) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'PendingDns',
    `RootRedirectsToWww` tinyint(1) NOT NULL DEFAULT FALSE,
    `RootLastCheckedAt` datetime(6) NULL,
    `RootLastError` varchar(1000) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;");

    await EnsureTableColumnAsync(db, "AgentDomains", "AgentUserId", "ALTER TABLE `AgentDomains` ADD COLUMN `AgentUserId` int NOT NULL DEFAULT 0");
    await EnsureTableColumnAsync(db, "AgentDomains", "AgentWebsiteId", "ALTER TABLE `AgentDomains` ADD COLUMN `AgentWebsiteId` int NOT NULL DEFAULT 0");
    await EnsureTableColumnAsync(db, "AgentDomains", "DomainName", "ALTER TABLE `AgentDomains` ADD COLUMN `DomainName` varchar(255) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''");
    await EnsureTableColumnAsync(db, "AgentDomains", "RootDomain", "ALTER TABLE `AgentDomains` ADD COLUMN `RootDomain` varchar(255) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''");
    await EnsureTableColumnAsync(db, "AgentDomains", "WwwDomain", "ALTER TABLE `AgentDomains` ADD COLUMN `WwwDomain` varchar(255) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''");
    await EnsureTableColumnAsync(db, "AgentDomains", "DnsTarget", "ALTER TABLE `AgentDomains` ADD COLUMN `DnsTarget` varchar(255) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''");
    await EnsureTableColumnAsync(db, "AgentDomains", "DnsStatus", "ALTER TABLE `AgentDomains` ADD COLUMN `DnsStatus` varchar(40) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'PendingDns'");
    await EnsureTableColumnAsync(db, "AgentDomains", "AzureBindingStatus", "ALTER TABLE `AgentDomains` ADD COLUMN `AzureBindingStatus` varchar(40) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'BindingPending'");
    await EnsureTableColumnAsync(db, "AgentDomains", "SslStatus", "ALTER TABLE `AgentDomains` ADD COLUMN `SslStatus` varchar(40) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'BindingPending'");
    await EnsureTableColumnAsync(db, "AgentDomains", "IsPrimary", "ALTER TABLE `AgentDomains` ADD COLUMN `IsPrimary` tinyint(1) NOT NULL DEFAULT TRUE");
    await EnsureTableColumnAsync(db, "AgentDomains", "LastCheckedAt", "ALTER TABLE `AgentDomains` ADD COLUMN `LastCheckedAt` datetime(6) NULL");
    await EnsureTableColumnAsync(db, "AgentDomains", "LastError", "ALTER TABLE `AgentDomains` ADD COLUMN `LastError` varchar(1000) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''");
    await EnsureTableColumnAsync(db, "AgentDomains", "CreatedAt", "ALTER TABLE `AgentDomains` ADD COLUMN `CreatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)");
    await EnsureTableColumnAsync(db, "AgentDomains", "UpdatedAt", "ALTER TABLE `AgentDomains` ADD COLUMN `UpdatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)");
    await EnsureTableColumnAsync(db, "AgentDomains", "RetryCount", "ALTER TABLE `AgentDomains` ADD COLUMN `RetryCount` int NOT NULL DEFAULT 0");
    await EnsureTableColumnAsync(db, "AgentDomains", "LastFailedAt", "ALTER TABLE `AgentDomains` ADD COLUMN `LastFailedAt` datetime(6) NULL");
    await EnsureTableColumnAsync(db, "AgentDomains", "NextRetryAt", "ALTER TABLE `AgentDomains` ADD COLUMN `NextRetryAt` datetime(6) NULL");
    await EnsureTableColumnAsync(db, "AgentDomains", "AutoRetryExhausted", "ALTER TABLE `AgentDomains` ADD COLUMN `AutoRetryExhausted` tinyint(1) NOT NULL DEFAULT FALSE");
    await EnsureTableColumnAsync(db, "AgentDomains", "RootDnsStatus", "ALTER TABLE `AgentDomains` ADD COLUMN `RootDnsStatus` varchar(40) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'PendingDns'");
    await EnsureTableColumnAsync(db, "AgentDomains", "RootRedirectsToWww", "ALTER TABLE `AgentDomains` ADD COLUMN `RootRedirectsToWww` tinyint(1) NOT NULL DEFAULT FALSE");
    await EnsureTableColumnAsync(db, "AgentDomains", "RootLastCheckedAt", "ALTER TABLE `AgentDomains` ADD COLUMN `RootLastCheckedAt` datetime(6) NULL");
    await EnsureTableColumnAsync(db, "AgentDomains", "RootLastError", "ALTER TABLE `AgentDomains` ADD COLUMN `RootLastError` varchar(1000) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''");
    await EnsureTableColumnAsync(db, "AgentDomains", "CertificateAlertSentAt", "ALTER TABLE `AgentDomains` ADD COLUMN `CertificateAlertSentAt` datetime(6) NULL");
}

static async Task EnsureWebsiteTemplateColumnAsync(IPRODbContext db, string columnName, string alterSql)
{
    await EnsureTableColumnAsync(db, "WebsiteTemplates", columnName, alterSql);
}

static async Task EnsureWebsiteLeadSchemaAsync(IPRODbContext db)
{
    await db.Database.OpenConnectionAsync();
    try
    {
        await EnsureTableColumnAsync(db, "WebsiteLeads", "NotificationSent", "ALTER TABLE `WebsiteLeads` ADD COLUMN `NotificationSent` tinyint(1) NOT NULL DEFAULT TRUE");
        await EnsureTableColumnAsync(db, "WebsiteLeads", "NotificationError", "ALTER TABLE `WebsiteLeads` ADD COLUMN `NotificationError` varchar(500) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''");
    }
    finally
    {
        await db.Database.CloseConnectionAsync();
    }
}

static async Task EnsureWebsiteContentBlockSchemaAsync(IPRODbContext db)
{
    await db.Database.OpenConnectionAsync();
    try
    {
        await EnsureTableColumnAsync(db, "WebsiteContentBlocks", "LayoutVariant", "ALTER TABLE `WebsiteContentBlocks` ADD COLUMN `LayoutVariant` varchar(30) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''");
    }
    finally
    {
        await db.Database.CloseConnectionAsync();
    }
}

static async Task EnsureDripCampaignEnrollmentSchemaAsync(IPRODbContext db)
{
    await db.Database.OpenConnectionAsync();
    try
    {
        await EnsureTableColumnAsync(db, "DripCampaignEnrollments", "UnsubscribeToken", "ALTER TABLE `DripCampaignEnrollments` ADD COLUMN `UnsubscribeToken` varchar(80) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''");
    }
    finally
    {
        await db.Database.CloseConnectionAsync();
    }
}

static async Task EnsureNewsLetterTemplateSchemaAsync(IPRODbContext db)
{
    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `NewsLetterTemplates` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `Name` varchar(160) CHARACTER SET utf8mb4 NOT NULL,
    `Description` varchar(500) CHARACTER SET utf8mb4 NOT NULL,
    `Subject` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
    `HtmlBody` longtext CHARACTER SET utf8mb4 NOT NULL,
    `TextBody` longtext CHARACTER SET utf8mb4 NOT NULL,
    `IsActive` tinyint(1) NOT NULL DEFAULT TRUE,
    `SortOrder` int NOT NULL DEFAULT 0,
    `CreatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;");

}

static async Task EnsureDripCampaignStepSendSchemaAsync(IPRODbContext db)
{
    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `DripCampaignStepSends` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `DripCampaignEnrollmentId` int NOT NULL,
    `DripCampaignStepId` int NOT NULL,
    `StepIndex` int NOT NULL,
    `Email` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
    `RecipientName` varchar(160) CHARACTER SET utf8mb4 NOT NULL,
    `Status` int NOT NULL,
    `SendGridMessageId` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
    `FailureReason` varchar(1000) CHARACTER SET utf8mb4 NOT NULL,
    `SentAt` datetime(6) NULL,
    `DeliveredAt` datetime(6) NULL,
    `OpenedAt` datetime(6) NULL,
    `ClickedAt` datetime(6) NULL,
    `BouncedAt` datetime(6) NULL,
    `FailedAt` datetime(6) NULL,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;");
}

static async Task EnsureDidYouKnowEmailQueueSchemaAsync(IPRODbContext db)
{
    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `DidYouKnowEmailQueueItems` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `ArticleId` int NOT NULL,
    `ClientId` int NOT NULL,
    `ScheduledForUtc` datetime(6) NOT NULL,
    `ClaimedAtUtc` datetime(6) NULL,
    `SentAtUtc` datetime(6) NULL,
    `CreatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`),
    KEY `IX_DidYouKnowEmailQueueItems_Dispatch` (`SentAtUtc`, `ScheduledForUtc`)
) CHARACTER SET=utf8mb4;");

    // Existing installs already have the table, so CREATE TABLE IF NOT EXISTS is a no-op for them --
    // the column has to be added separately. ClaimedAtUtc separates "a run owns this" from "this was
    // delivered", so an item claimed by a process that then died is recoverable instead of silently
    // lost. See DidYouKnowEmailDispatchJob.
    await EnsureTableColumnAsync(db, "DidYouKnowEmailQueueItems", "ClaimedAtUtc",
        "ALTER TABLE `DidYouKnowEmailQueueItems` ADD COLUMN `ClaimedAtUtc` datetime(6) NULL");
}

static async Task EnsureNewsLetterClickTrackingSchemaAsync(IPRODbContext db)
{
    await db.Database.OpenConnectionAsync();
    try
    {
        await EnsureTableColumnAsync(db, "NewsLetterSends", "TotalClicked", "ALTER TABLE `NewsLetterSends` ADD COLUMN `TotalClicked` int NOT NULL DEFAULT 0");
        await EnsureTableColumnAsync(db, "NewsLetters", "TotalClicked", "ALTER TABLE `NewsLetters` ADD COLUMN `TotalClicked` int NOT NULL DEFAULT 0");
        await EnsureTableColumnAsync(db, "NewsLetters", "BannerUrl", "ALTER TABLE `NewsLetters` ADD COLUMN `BannerUrl` varchar(500) CHARACTER SET utf8mb4 NULL");
        await EnsureTableColumnAsync(db, "NewsLetters", "Edition", "ALTER TABLE `NewsLetters` ADD COLUMN `Edition` varchar(200) CHARACTER SET utf8mb4 NULL");
        await EnsureTableColumnAsync(db, "NewsLetters", "SidebarCtasJson", "ALTER TABLE `NewsLetters` ADD COLUMN `SidebarCtasJson` longtext CHARACTER SET utf8mb4 NULL");
    }
    finally
    {
        await db.Database.CloseConnectionAsync();
    }
}

static async Task EnsureSupportTicketSchemaAsync(IPRODbContext db)
{
    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `SupportTickets` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `AgentUserId` int NOT NULL,
    `Subject` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
    `Status` int NOT NULL,
    `HasUnreadForAgent` tinyint(1) NOT NULL DEFAULT FALSE,
    `HasUnreadForAdmin` tinyint(1) NOT NULL DEFAULT TRUE,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NOT NULL,
    `LastMessageAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;");

    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `SupportTicketMessages` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `SupportTicketId` int NOT NULL,
    `IsFromAdmin` tinyint(1) NOT NULL DEFAULT FALSE,
    `AuthorName` varchar(160) CHARACTER SET utf8mb4 NOT NULL,
    `Body` longtext CHARACTER SET utf8mb4 NOT NULL,
    `CreatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;");
}

static async Task EnsurePromotionCodeSchemaAsync(IPRODbContext db)
{
    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `PromotionCodes` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `Code` varchar(60) CHARACTER SET utf8mb4 NOT NULL,
    `Description` varchar(300) CHARACTER SET utf8mb4 NULL,
    `IsActive` tinyint(1) NOT NULL DEFAULT TRUE,
    `ExpiresAt` datetime(6) NULL,
    `MaxRedemptions` int NULL,
    `RedemptionCount` int NOT NULL DEFAULT 0,
    `RestrictedBillingRuleId` int NULL,
    `RecurringDiscountType` int NOT NULL DEFAULT 0,
    `RecurringDiscountValue` decimal(10,2) NOT NULL DEFAULT 0,
    `RecurringDurationCycles` int NULL,
    `SetupFeeDiscountType` int NOT NULL DEFAULT 0,
    `SetupFeeDiscountValue` decimal(10,2) NOT NULL DEFAULT 0,
    `PayPalPromoPlanIdMonthly` varchar(80) CHARACTER SET utf8mb4 NULL,
    `PayPalPromoPlanIdAnnual` varchar(80) CHARACTER SET utf8mb4 NULL,
    `CreatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`),
    UNIQUE KEY `IX_PromotionCodes_Code` (`Code`)
) CHARACTER SET=utf8mb4;");

    await db.Database.ExecuteSqlRawAsync(@"
ALTER TABLE `PromotionCodes`
    MODIFY COLUMN `Description` varchar(300) CHARACTER SET utf8mb4 NULL,
    MODIFY COLUMN `PayPalPromoPlanIdMonthly` varchar(80) CHARACTER SET utf8mb4 NULL,
    MODIFY COLUMN `PayPalPromoPlanIdAnnual` varchar(80) CHARACTER SET utf8mb4 NULL;");

    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `PromotionCodeRedemptions` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `PromotionCodeId` int NOT NULL,
    `AgentUserId` int NOT NULL,
    `BillingRuleId` int NOT NULL,
    `Period` int NOT NULL,
    `OriginalRecurringAmount` decimal(10,2) NOT NULL DEFAULT 0,
    `DiscountedRecurringAmount` decimal(10,2) NOT NULL DEFAULT 0,
    `OriginalSetupFee` decimal(10,2) NOT NULL DEFAULT 0,
    `DiscountedSetupFee` decimal(10,2) NOT NULL DEFAULT 0,
    `RedeemedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;");

    await db.Database.OpenConnectionAsync();
    try
    {
        await EnsureTableColumnAsync(db, "SubscriptionChanges", "PromotionCodeId", "ALTER TABLE `SubscriptionChanges` ADD COLUMN `PromotionCodeId` int NULL");
    }
    finally
    {
        await db.Database.CloseConnectionAsync();
    }
}

static async Task EnsureClientInvoiceSchemaAsync(IPRODbContext db)
{
    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `ClientInvoices` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `AgentUserId` int NOT NULL,
    `ClientId` int NOT NULL,
    `DocumentType` int NOT NULL,
    `Status` int NOT NULL,
    `DocumentNumber` varchar(40) CHARACTER SET utf8mb4 NOT NULL,
    `IssueDate` datetime(6) NOT NULL,
    `DueDate` datetime(6) NULL,
    `Currency` varchar(10) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'CAD',
    `SubTotal` decimal(10,2) NOT NULL DEFAULT 0,
    `TaxRegion` varchar(100) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `TaxRate` decimal(6,4) NOT NULL DEFAULT 0,
    `TaxAmount` decimal(10,2) NOT NULL DEFAULT 0,
    `Total` decimal(10,2) NOT NULL DEFAULT 0,
    `Notes` varchar(2000) CHARACTER SET utf8mb4 NULL,
    `PaidAt` datetime(6) NULL,
    `PaidMethod` int NULL,
    `ViewToken` varchar(80) CHARACTER SET utf8mb4 NOT NULL,
    `SentAt` datetime(6) NULL,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`),
    UNIQUE KEY `IX_ClientInvoices_ViewToken` (`ViewToken`)
) CHARACTER SET=utf8mb4;");

    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `ClientInvoiceLineItems` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `ClientInvoiceId` int NOT NULL,
    `Description` varchar(500) CHARACTER SET utf8mb4 NOT NULL,
    `Quantity` decimal(10,2) NOT NULL DEFAULT 1,
    `UnitPrice` decimal(10,2) NOT NULL DEFAULT 0,
    `Amount` decimal(10,2) NOT NULL DEFAULT 0,
    `SortOrder` int NOT NULL DEFAULT 0,
    PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;");

    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `RecurringInvoiceSchedules` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `AgentUserId` int NOT NULL,
    `ClientId` int NOT NULL,
    `Frequency` int NOT NULL,
    `NextRunDate` datetime(6) NOT NULL,
    `DueInDays` int NOT NULL DEFAULT 15,
    `IsActive` tinyint(1) NOT NULL DEFAULT TRUE,
    `Notes` varchar(2000) CHARACTER SET utf8mb4 NULL,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;");

    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `RecurringInvoiceLineItems` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `RecurringInvoiceScheduleId` int NOT NULL,
    `Description` varchar(500) CHARACTER SET utf8mb4 NOT NULL,
    `Quantity` decimal(10,2) NOT NULL DEFAULT 1,
    `UnitPrice` decimal(10,2) NOT NULL DEFAULT 0,
    `SortOrder` int NOT NULL DEFAULT 0,
    PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;");

    await db.Database.OpenConnectionAsync();
    try
    {
        await EnsureTableColumnAsync(db, "AgentUsers", "DefaultPaymentLink", "ALTER TABLE `AgentUsers` ADD COLUMN `DefaultPaymentLink` varchar(500) CHARACTER SET utf8mb4 NULL");
        await EnsureTableColumnAsync(db, "AgentUsers", "PortalAccentColor", "ALTER TABLE `AgentUsers` ADD COLUMN `PortalAccentColor` varchar(20) CHARACTER SET utf8mb4 NULL");
        await EnsureTableColumnAsync(db, "AgentUsers", "PhotoUrl", "ALTER TABLE `AgentUsers` ADD COLUMN `PhotoUrl` varchar(500) CHARACTER SET utf8mb4 NULL");
        await EnsureTableColumnAsync(db, "AgentUsers", "PasswordResetToken", "ALTER TABLE `AgentUsers` ADD COLUMN `PasswordResetToken` varchar(80) CHARACTER SET utf8mb4 NULL");
        await EnsureTableColumnAsync(db, "AgentUsers", "PasswordResetTokenExpiresAt", "ALTER TABLE `AgentUsers` ADD COLUMN `PasswordResetTokenExpiresAt` datetime(6) NULL");
        await EnsureTableColumnAsync(db, "AgentUsers", "TrialEndsAt", "ALTER TABLE `AgentUsers` ADD COLUMN `TrialEndsAt` datetime(6) NULL");
        await EnsureTableColumnAsync(db, "AgentUsers", "TrialRemindersSentCount", "ALTER TABLE `AgentUsers` ADD COLUMN `TrialRemindersSentCount` int NOT NULL DEFAULT 0");
        await EnsureTableColumnAsync(db, "ClientInvoices", "LastReminderSentAt", "ALTER TABLE `ClientInvoices` ADD COLUMN `LastReminderSentAt` datetime(6) NULL");
        await EnsureUniqueIndexAsync(db, "ClientInvoices", "UX_ClientInvoices_Agent_DocumentNumber",
            "ALTER TABLE `ClientInvoices` ADD UNIQUE INDEX `UX_ClientInvoices_Agent_DocumentNumber` (`AgentUserId`, `DocumentNumber`)");
        // Backstop for the 2026-08-05 domain-takeover fix. DescribeDomainClaimAsync is a read followed
        // by a write, so two simultaneous requests can both pass it; this makes the database the final
        // arbiter of who owns a hostname. EnsureUniqueIndexAsync tolerates pre-existing duplicate data
        // (it catches 1062 and leaves the index uncreated), so a dirty table cannot block startup.
        await EnsureUniqueIndexAsync(db, "AgentDomains", "UX_AgentDomains_DomainName",
            "ALTER TABLE `AgentDomains` ADD UNIQUE INDEX `UX_AgentDomains_DomainName` (`DomainName`)");
    }
    finally
    {
        await db.Database.CloseConnectionAsync();
    }
}

static async Task EnsureClientPortalSchemaAsync(IPRODbContext db)
{
    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `PortalMessages` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `ClientId` int NOT NULL,
    `IsFromClient` tinyint(1) NOT NULL DEFAULT FALSE,
    `AuthorName` varchar(160) CHARACTER SET utf8mb4 NOT NULL,
    `Body` longtext CHARACTER SET utf8mb4 NOT NULL,
    `IsReadByAgent` tinyint(1) NOT NULL DEFAULT FALSE,
    `IsReadByClient` tinyint(1) NOT NULL DEFAULT TRUE,
    `CreatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;");

    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `PortalDocuments` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `ClientId` int NOT NULL,
    `UploadedByClient` tinyint(1) NOT NULL DEFAULT FALSE,
    `FileName` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
    `BlobUrl` varchar(1000) CHARACTER SET utf8mb4 NOT NULL,
    `ContentType` varchar(150) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `FileSizeBytes` bigint NOT NULL DEFAULT 0,
    `UploadedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;");

    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `PortalAppointmentRequests` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `ClientId` int NOT NULL,
    `Notes` varchar(2000) CHARACTER SET utf8mb4 NULL,
    `PreferredDate` datetime(6) NULL,
    `Status` int NOT NULL DEFAULT 0,
    `CreatedAt` datetime(6) NOT NULL,
    `RespondedAt` datetime(6) NULL,
    `ScheduledAt` datetime(6) NULL,
    `ClientFollowUpId` int NULL,
    PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;");

    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `GoogleCalendarConnections` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `AgentUserId` int NOT NULL,
    `GoogleAccountEmail` varchar(255) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `EncryptedAccessToken` longtext CHARACTER SET utf8mb4 NOT NULL,
    `EncryptedRefreshToken` longtext CHARACTER SET utf8mb4 NOT NULL,
    `AccessTokenExpiresAt` datetime(6) NOT NULL,
    `GoogleCalendarId` varchar(255) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'primary',
    `SyncToken` longtext CHARACTER SET utf8mb4 NULL,
    `IsActive` tinyint(1) NOT NULL DEFAULT TRUE,
    `ConnectedAt` datetime(6) NOT NULL,
    `LastSyncedAt` datetime(6) NULL,
    PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;");

    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `ExternalCalendarEvents` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `AgentUserId` int NOT NULL,
    `GoogleEventId` varchar(255) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `Title` varchar(500) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `StartAt` datetime(6) NOT NULL,
    `EndAt` datetime(6) NULL,
    `LastSyncedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;");

    await db.Database.OpenConnectionAsync();
    try
    {
        await EnsureTableColumnAsync(db, "Clients", "PortalPasswordHash", "ALTER TABLE `Clients` ADD COLUMN `PortalPasswordHash` varchar(500) CHARACTER SET utf8mb4 NULL");
        await EnsureTableColumnAsync(db, "Clients", "PortalInviteToken", "ALTER TABLE `Clients` ADD COLUMN `PortalInviteToken` varchar(80) CHARACTER SET utf8mb4 NULL");
        await EnsureTableColumnAsync(db, "Clients", "PortalInviteTokenExpiresAt", "ALTER TABLE `Clients` ADD COLUMN `PortalInviteTokenExpiresAt` datetime(6) NULL");
        await EnsureTableColumnAsync(db, "Clients", "PortalActivatedAt", "ALTER TABLE `Clients` ADD COLUMN `PortalActivatedAt` datetime(6) NULL");
        await EnsureTableColumnAsync(db, "PortalAppointmentRequests", "ScheduledAt", "ALTER TABLE `PortalAppointmentRequests` ADD COLUMN `ScheduledAt` datetime(6) NULL");
        await EnsureTableColumnAsync(db, "PortalAppointmentRequests", "ClientFollowUpId", "ALTER TABLE `PortalAppointmentRequests` ADD COLUMN `ClientFollowUpId` int NULL");
        await EnsureTableColumnAsync(db, "ClientFollowUps", "GoogleEventId", "ALTER TABLE `ClientFollowUps` ADD COLUMN `GoogleEventId` varchar(255) CHARACTER SET utf8mb4 NULL");
    }
    finally
    {
        await db.Database.CloseConnectionAsync();
    }
}

static async Task EnsureClientLifeEventSchemaAsync(IPRODbContext db)
{
    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `ClientLifeEvents` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `ClientId` int NOT NULL,
    `EventType` int NOT NULL,
    `Label` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `EventDate` datetime(6) NOT NULL,
    `ReminderDaysBefore` int NOT NULL DEFAULT 7,
    `IsActive` tinyint(1) NOT NULL DEFAULT TRUE,
    `LastReminderYear` int NULL,
    `CreatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;");

    await db.Database.OpenConnectionAsync();
    try
    {
        await EnsureTableColumnAsync(db, "Clients", "LastBirthdayReminderYear", "ALTER TABLE `Clients` ADD COLUMN `LastBirthdayReminderYear` int NULL");
        await EnsureTableColumnAsync(db, "ClientLifeEvents", "LastCheckedAt", "ALTER TABLE `ClientLifeEvents` ADD COLUMN `LastCheckedAt` datetime(6) NULL");
        await EnsureTableColumnAsync(db, "Clients", "BirthdayReminderLastCheckedAt", "ALTER TABLE `Clients` ADD COLUMN `BirthdayReminderLastCheckedAt` datetime(6) NULL");
    }
    finally
    {
        await db.Database.CloseConnectionAsync();
    }
}

static async Task EnsureAgentDocumentSchemaAsync(IPRODbContext db)
{
    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `AgentDocuments` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `AgentUserId` int NOT NULL,
    `FileName` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
    `BlobUrl` varchar(1000) CHARACTER SET utf8mb4 NOT NULL,
    `ContentType` varchar(150) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `FileSizeBytes` bigint NOT NULL DEFAULT 0,
    `Category` varchar(100) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `UploadedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;");
}

static async Task EnsureSocialPostSchemaAsync(IPRODbContext db)
{
    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `SocialPostDrafts` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `AgentUserId` int NOT NULL,
    `Topic` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `Body` longtext CHARACTER SET utf8mb4 NOT NULL,
    `Status` int NOT NULL DEFAULT 0,
    `PostedAt` datetime(6) NULL,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;");

    await db.Database.OpenConnectionAsync();
    try
    {
        await EnsureTableColumnAsync(db, "SocialPostDrafts", "ScheduledAt", "ALTER TABLE `SocialPostDrafts` ADD COLUMN `ScheduledAt` datetime(6) NULL");
    }
    finally
    {
        await db.Database.CloseConnectionAsync();
    }
}

static async Task EnsureECardDesignSchemaAsync(IPRODbContext db)
{
    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `ECardDesigns` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `Key` varchar(80) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `Occasion` varchar(80) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `Name` varchar(120) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `Kind` varchar(20) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'image',
    `DefaultHeaderText` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `DefaultMessage` varchar(600) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `ImageUrl` varchar(500) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `Width` int NOT NULL DEFAULT 0,
    `Height` int NOT NULL DEFAULT 0,
    `Accent` varchar(20) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `Emoji` varchar(20) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `IsDark` tinyint(1) NOT NULL DEFAULT 1,
    `IsActive` tinyint(1) NOT NULL DEFAULT 1,
    `SortOrder` int NOT NULL DEFAULT 0,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`),
    UNIQUE INDEX `IX_ECardDesigns_Key` (`Key`)
) CHARACTER SET=utf8mb4;

CREATE TABLE IF NOT EXISTS `ELetterTemplates` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `Key` varchar(80) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `Name` varchar(120) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `Description` varchar(400) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `Subject` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `Body` longtext CHARACTER SET utf8mb4 NOT NULL,
    `IsActive` tinyint(1) NOT NULL DEFAULT 1,
    `SortOrder` int NOT NULL DEFAULT 0,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`),
    UNIQUE INDEX `IX_ELetterTemplates_Key` (`Key`)
) CHARACTER SET=utf8mb4;");
}

static async Task EnsureECardSchemaAsync(IPRODbContext db)
{
    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `ECards` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `AgentUserId` int NOT NULL,
    `Occasion` varchar(40) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Birthday',
    `Subject` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `Message` varchar(2000) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `Status` varchar(20) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Draft',
    `ScheduledAt` datetime(6) NOT NULL,
    `SentAt` datetime(6) NULL,
    `TotalRecipients` int NOT NULL DEFAULT 0,
    `TotalSent` int NOT NULL DEFAULT 0,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;

CREATE TABLE IF NOT EXISTS `ECardRecipients` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `ECardId` int NOT NULL,
    `ClientId` int NOT NULL,
    `Email` varchar(255) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `RecipientName` varchar(160) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `Status` varchar(20) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Queued',
    `SendGridMessageId` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `FailureReason` varchar(1000) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `SentAt` datetime(6) NULL,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`),
    INDEX `IX_ECardRecipients_ECardId` (`ECardId`)
) CHARACTER SET=utf8mb4;");
}

static async Task EnsureELetterSchemaAsync(IPRODbContext db)
{
    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `ELetters` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `AgentUserId` int NOT NULL,
    `TemplateKey` varchar(60) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `Subject` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `Body` longtext CHARACTER SET utf8mb4 NOT NULL,
    `Status` varchar(20) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Draft',
    `ScheduledAt` datetime(6) NOT NULL,
    `SentAt` datetime(6) NULL,
    `TotalRecipients` int NOT NULL DEFAULT 0,
    `TotalSent` int NOT NULL DEFAULT 0,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;

CREATE TABLE IF NOT EXISTS `ELetterRecipients` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `ELetterId` int NOT NULL,
    `ClientId` int NOT NULL,
    `Email` varchar(255) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `RecipientName` varchar(160) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `Status` varchar(20) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Queued',
    `SendGridMessageId` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `FailureReason` varchar(1000) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `SentAt` datetime(6) NULL,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`),
    INDEX `IX_ELetterRecipients_ELetterId` (`ELetterId`)
) CHARACTER SET=utf8mb4;");
}

static async Task EnsureTestimonialSubmissionSchemaAsync(IPRODbContext db)
{
    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `TestimonialSubmissions` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `AgentUserId` int NOT NULL,
    `FirstName` varchar(100) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `LastName` varchar(100) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `Email` varchar(255) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `Body` longtext CHARACTER SET utf8mb4 NOT NULL,
    `Status` int NOT NULL DEFAULT 0,
    `SubmittedAt` datetime(6) NOT NULL,
    `ReviewedAt` datetime(6) NULL,
    PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;");

    await db.Database.OpenConnectionAsync();
    try
    {
        await EnsureTableColumnAsync(db, "TestimonialSubmissions", "ClientId", "ALTER TABLE `TestimonialSubmissions` ADD COLUMN `ClientId` int NULL");
        await EnsureTableColumnAsync(db, "TestimonialSubmissions", "RequestToken", "ALTER TABLE `TestimonialSubmissions` ADD COLUMN `RequestToken` varchar(80) CHARACTER SET utf8mb4 NULL");
    }
    finally
    {
        await db.Database.CloseConnectionAsync();
    }
}

static async Task EnsureAgentDailyInsightSchemaAsync(IPRODbContext db)
{
    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `AgentDailyInsights` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `AgentUserId` int NOT NULL,
    `NewLeadCount` int NOT NULL DEFAULT 0,
    `StaleLeadCount` int NOT NULL DEFAULT 0,
    `NoFollowUpClientCount` int NOT NULL DEFAULT 0,
    `SuggestedActionType` varchar(30) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'None',
    `SuggestedActionText` varchar(300) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `SuggestedActionUrl` varchar(300) CHARACTER SET utf8mb4 NULL,
    `SuggestedActionReason` varchar(1000) CHARACTER SET utf8mb4 NULL,
    `GeneratedAt` datetime(6) NOT NULL,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`),
    UNIQUE KEY `IX_AgentDailyInsights_AgentUserId` (`AgentUserId`)
) CHARACTER SET=utf8mb4;");

    await db.Database.OpenConnectionAsync();
    try
    {
        await EnsureTableColumnAsync(db, "AgentDailyInsights", "RelatedEntityId", "ALTER TABLE `AgentDailyInsights` ADD COLUMN `RelatedEntityId` int NULL");
    }
    finally
    {
        await db.Database.CloseConnectionAsync();
    }
}

static async Task EnsureAiUsageSchemaAsync(IPRODbContext db)
{
    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `AiUsageDailyLogs` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `Date` date NOT NULL,
    `CallCount` int NOT NULL DEFAULT 0,
    `InputTokens` bigint NOT NULL DEFAULT 0,
    `OutputTokens` bigint NOT NULL DEFAULT 0,
    `EstimatedCostUsd` decimal(10,4) NOT NULL DEFAULT 0,
    `UpdatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`),
    UNIQUE KEY `IX_AiUsageDailyLogs_Date` (`Date`)
) CHARACTER SET=utf8mb4;");

    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `AiBillingSettings` (
    `Id` int NOT NULL,
    `TotalFundedUsd` decimal(10,4) NOT NULL DEFAULT 0,
    `LowBalanceThresholdPercent` int NOT NULL DEFAULT 20,
    `UpdatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;");

    await db.Database.ExecuteSqlRawAsync(@"
INSERT INTO `AiBillingSettings` (`Id`, `TotalFundedUsd`, `LowBalanceThresholdPercent`, `UpdatedAt`)
SELECT 1, 0, 20, UTC_TIMESTAMP()
WHERE NOT EXISTS (SELECT 1 FROM `AiBillingSettings` WHERE `Id` = 1);");
}

static async Task EnsureTrialFeatureSchemaAsync(IPRODbContext db)
{
    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `TrialInviteCodes` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `Code` varchar(60) CHARACTER SET utf8mb4 NOT NULL,
    `Description` varchar(300) CHARACTER SET utf8mb4 NULL,
    `BillingRuleId` int NOT NULL,
    `IsActive` tinyint(1) NOT NULL DEFAULT TRUE,
    `ExpiresAt` datetime(6) NULL,
    `MaxRedemptions` int NULL,
    `RedemptionCount` int NOT NULL DEFAULT 0,
    `CreatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`),
    UNIQUE KEY `IX_TrialInviteCodes_Code` (`Code`)
) CHARACTER SET=utf8mb4;");

    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `TrialInviteCodeRedemptions` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `TrialInviteCodeId` int NOT NULL,
    `AgentUserId` int NOT NULL,
    `RedeemedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;");

    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `TrialSettings` (
    `Id` int NOT NULL,
    `GracePeriodDays` int NOT NULL DEFAULT 1,
    `UpdatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;");

    // Same "INSERT ... SELECT ... WHERE NOT EXISTS" singleton pattern as AiBillingSettings above.
    // The row count this returns doubles as a one-time-only marker: it's only > 0 the very first
    // time this method ever runs, which is exactly when the backfill below should run too - on
    // every later restart the row already exists, the insert is a no-op, and the backfill (which
    // must never re-run against agents who registered normally after this feature shipped) stays
    // skipped.
    var trialSettingsRowsInserted = await db.Database.ExecuteSqlRawAsync(@"
INSERT INTO `TrialSettings` (`Id`, `GracePeriodDays`, `UpdatedAt`)
SELECT 1, 1, UTC_TIMESTAMP()
WHERE NOT EXISTS (SELECT 1 FROM `TrialSettings` WHERE `Id` = 1);");

    if (trialSettingsRowsInserted > 0)
    {
        // One-time grandfather grace period: agents already using the system for free (no active
        // Billing row, e.g. via the entitlement fallback bug this feature closes) get a real week
        // before enforcement applies to them, instead of being cut off the instant this deploys.
        await db.Database.ExecuteSqlRawAsync(@"
UPDATE `AgentUsers`
SET `TrialEndsAt` = DATE_ADD(UTC_TIMESTAMP(), INTERVAL 7 DAY)
WHERE `TrialEndsAt` IS NULL
  AND NOT EXISTS (SELECT 1 FROM `Billings` WHERE `Billings`.`AgentUserId` = `AgentUsers`.`Id` AND `Billings`.`Status` = 1);");
    }
}

static async Task EnsureWebsiteStarterArticleSchemaAsync(IPRODbContext db)
{
    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `WebsiteStarterArticles` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `BusinessType` varchar(80) CHARACTER SET utf8mb4 NOT NULL,
    `Title` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
    `Summary` varchar(500) CHARACTER SET utf8mb4 NULL,
    `Content` longtext CHARACTER SET utf8mb4 NULL,
    `ImageUrl` varchar(1000) CHARACTER SET utf8mb4 NULL,
    `IsActive` tinyint(1) NOT NULL DEFAULT TRUE,
    `SortOrder` int NOT NULL DEFAULT 0,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;");

    await EnsureTableColumnAsync(db, "WebsiteStarterArticles", "Category", "ALTER TABLE `WebsiteStarterArticles` ADD COLUMN `Category` varchar(120) CHARACTER SET utf8mb4 NULL");
}

static async Task EnsureWebsiteStarterFormSchemaAsync(IPRODbContext db)
{
    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `WebsiteStarterForms` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `BusinessType` varchar(80) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'All',
    `Title` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `Description` varchar(2000) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `SubmitButtonText` varchar(100) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `SuccessMessage` varchar(500) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `IsActive` tinyint(1) NOT NULL DEFAULT 1,
    `SortOrder` int NOT NULL DEFAULT 0,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`),
    KEY `idx_website_starter_forms_business_type` (`BusinessType`)
) CHARACTER SET=utf8mb4;");

    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `WebsiteStarterFormFields` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `WebsiteStarterFormId` int NOT NULL,
    `FieldType` varchar(50) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `Label` varchar(300) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `Placeholder` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `HelpText` varchar(500) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `IsRequired` tinyint(1) NOT NULL DEFAULT 0,
    `SortOrder` int NOT NULL DEFAULT 0,
    `CreatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`),
    KEY `idx_website_starter_form_fields_form` (`WebsiteStarterFormId`)
) CHARACTER SET=utf8mb4;");

    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `WebsiteStarterFormFieldOptions` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `WebsiteStarterFormFieldId` int NOT NULL,
    `Text` varchar(300) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `SortOrder` int NOT NULL DEFAULT 0,
    PRIMARY KEY (`Id`),
    KEY `idx_website_starter_form_field_options_field` (`WebsiteStarterFormFieldId`)
) CHARACTER SET=utf8mb4;");
}

static async Task EnsurePollSchemaAsync(IPRODbContext db)
{
    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `PollSurveys` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `AgentUserId` int NOT NULL,
    `Title` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `Subject` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `IntroText` varchar(2000) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `Status` int NOT NULL DEFAULT 0,
    `ScheduledAt` datetime(6) NULL,
    `SentAt` datetime(6) NULL,
    `TotalRecipients` int NOT NULL DEFAULT 0,
    `TotalSent` int NOT NULL DEFAULT 0,
    `TotalFailed` int NOT NULL DEFAULT 0,
    `TotalResponded` int NOT NULL DEFAULT 0,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`),
    KEY `idx_poll_surveys_agent` (`AgentUserId`)
) CHARACTER SET=utf8mb4;");

    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `PollQuestions` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `PollSurveyId` int NOT NULL,
    `Text` varchar(500) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `SortOrder` int NOT NULL DEFAULT 0,
    `CreatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`),
    KEY `idx_poll_questions_survey` (`PollSurveyId`)
) CHARACTER SET=utf8mb4;");

    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `PollOptions` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `PollQuestionId` int NOT NULL,
    `Text` varchar(300) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `SortOrder` int NOT NULL DEFAULT 0,
    PRIMARY KEY (`Id`),
    KEY `idx_poll_options_question` (`PollQuestionId`)
) CHARACTER SET=utf8mb4;");

    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `PollSends` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `PollSurveyId` int NOT NULL,
    `AgentUserId` int NOT NULL,
    `AudienceType` int NOT NULL DEFAULT 0,
    `AudienceLabel` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `ClientCategoryId` int NULL,
    `ClientId` int NULL,
    `Status` int NOT NULL DEFAULT 0,
    `ScheduledAt` datetime(6) NOT NULL,
    `SentAt` datetime(6) NULL,
    `TotalRecipients` int NOT NULL DEFAULT 0,
    `TotalSent` int NOT NULL DEFAULT 0,
    `TotalFailed` int NOT NULL DEFAULT 0,
    `TotalResponded` int NOT NULL DEFAULT 0,
    `CreatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`),
    KEY `idx_poll_sends_survey` (`PollSurveyId`),
    KEY `idx_poll_sends_agent_scheduled` (`AgentUserId`, `ScheduledAt`),
    KEY `idx_poll_sends_status_scheduled` (`Status`, `ScheduledAt`)
) CHARACTER SET=utf8mb4;");

    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `PollRecipients` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `PollSurveyId` int NOT NULL,
    `PollSendId` int NULL,
    `ClientId` int NULL,
    `Email` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `RecipientName` varchar(160) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `Status` int NOT NULL DEFAULT 0,
    `SendGridMessageId` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `VoteToken` varchar(80) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `FailureReason` varchar(1000) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `SentAt` datetime(6) NULL,
    `FailedAt` datetime(6) NULL,
    `RespondedAt` datetime(6) NULL,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`),
    KEY `idx_poll_recipients_survey` (`PollSurveyId`),
    KEY `idx_poll_recipients_send` (`PollSendId`),
    KEY `idx_poll_recipients_vote_token` (`VoteToken`)
) CHARACTER SET=utf8mb4;");

    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `PollAnswers` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `PollRecipientId` int NOT NULL,
    `PollQuestionId` int NOT NULL,
    `PollOptionId` int NOT NULL,
    `CreatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`),
    UNIQUE KEY `ux_poll_answers_recipient_question` (`PollRecipientId`, `PollQuestionId`),
    KEY `idx_poll_answers_option` (`PollOptionId`)
) CHARACTER SET=utf8mb4;");
}

static async Task EnsureWebsiteFormSchemaAsync(IPRODbContext db)
{
    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `WebsiteForms` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `AgentUserId` int NOT NULL,
    `Title` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `Description` varchar(2000) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `SubmitButtonText` varchar(100) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `SuccessMessage` varchar(500) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `IsActive` tinyint(1) NOT NULL DEFAULT 1,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`),
    KEY `idx_website_forms_agent` (`AgentUserId`)
) CHARACTER SET=utf8mb4;");

    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `WebsiteFormFields` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `WebsiteFormId` int NOT NULL,
    `FieldType` varchar(50) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `Label` varchar(300) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `Placeholder` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `HelpText` varchar(500) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `IsRequired` tinyint(1) NOT NULL DEFAULT 0,
    `SortOrder` int NOT NULL DEFAULT 0,
    `CreatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`),
    KEY `idx_website_form_fields_form` (`WebsiteFormId`)
) CHARACTER SET=utf8mb4;");

    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `WebsiteFormFieldOptions` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `WebsiteFormFieldId` int NOT NULL,
    `Text` varchar(300) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `SortOrder` int NOT NULL DEFAULT 0,
    PRIMARY KEY (`Id`),
    KEY `idx_website_form_field_options_field` (`WebsiteFormFieldId`)
) CHARACTER SET=utf8mb4;");

    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `WebsiteFormSubmissionAnswers` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `WebsiteLeadId` int NOT NULL,
    `WebsiteFormId` int NOT NULL,
    `WebsiteFormFieldId` int NOT NULL,
    `FieldLabel` varchar(300) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `FieldType` varchar(50) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `Value` varchar(4000) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `SortOrder` int NOT NULL DEFAULT 0,
    `CreatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`),
    KEY `idx_website_form_submission_answers_lead` (`WebsiteLeadId`),
    KEY `idx_website_form_submission_answers_form` (`WebsiteFormId`)
) CHARACTER SET=utf8mb4;");
}

// Self-managing: opens its own connection unless the caller already has one open (detected via
// connection.State), so a bare call from anywhere - a brand-new Ensure*SchemaAsync method, a nested
// helper, whatever - is always safe. This is deliberate: this exact "caller forgot to wrap the call
// in OpenConnectionAsync/CloseConnectionAsync" mistake has taken production down three times
// (2026-07-16, 2026-07-24, 2026-07-26 - see 09_TROUBLESHOOTING.md). A documented convention wasn't
// enough; making the helper foolproof is.
static async Task EnsureTableColumnAsync(IPRODbContext db, string tableName, string columnName, string alterSql)
{
    var ownsConnection = db.Database.GetDbConnection().State != System.Data.ConnectionState.Open;
    if (ownsConnection) await db.Database.OpenConnectionAsync();
    try
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = @"
SELECT COUNT(1)
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = DATABASE()
  AND TABLE_NAME = @tableName
  AND COLUMN_NAME = @columnName";

        var tableParameter = command.CreateParameter();
        tableParameter.ParameterName = "@tableName";
        tableParameter.Value = tableName;
        command.Parameters.Add(tableParameter);

        var parameter = command.CreateParameter();
        parameter.ParameterName = "@columnName";
        parameter.Value = columnName;
        command.Parameters.Add(parameter);

        var exists = Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
        if (!exists)
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(alterSql);
            }
            catch (MySqlConnector.MySqlException ex)
                when (ex.ErrorCode == MySqlConnector.MySqlErrorCode.DuplicateFieldName)
            {
                // The column appeared between our INFORMATION_SCHEMA check and this ALTER.
                //
                // This is check-then-act across two processes: ipro-prod-web and ipro-prod-admin
                // deploy from the SAME push, start within seconds of each other, and run identical
                // schema repair against the SAME database. Both can see the column missing; the one
                // that ALTERs second gets MySQL 1060.
                //
                // Unhandled, that exception escapes Main and Linux exits the process via SIGABRT --
                // the same signature that took both apps down on 2026-07-29, and the reason
                // SeedGuard exists. SeedGuard covered the DML seeders; this is the DDL half that
                // was left uncovered.
                //
                // Swallowing is correct rather than merely convenient: the desired end state is
                // "column exists", and it does. Catching only 1060 keeps a typo'd ALTER or a
                // missing privilege loud.
            }
        }
    }
    finally
    {
        if (ownsConnection) await db.Database.CloseConnectionAsync();
    }
}

// Self-managing for the same reason as EnsureTableColumnAsync above.
static async Task EnsureUniqueIndexAsync(IPRODbContext db, string tableName, string indexName, string alterSql)
{
    var ownsConnection = db.Database.GetDbConnection().State != System.Data.ConnectionState.Open;
    if (ownsConnection) await db.Database.OpenConnectionAsync();
    try
    {
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = @"
SELECT COUNT(1)
FROM INFORMATION_SCHEMA.STATISTICS
WHERE TABLE_SCHEMA = DATABASE()
  AND TABLE_NAME = @tableName
  AND INDEX_NAME = @indexName";

            var tableParameter = command.CreateParameter();
            tableParameter.ParameterName = "@tableName";
            tableParameter.Value = tableName;
            command.Parameters.Add(tableParameter);

            var indexParameter = command.CreateParameter();
            indexParameter.ParameterName = "@indexName";
            indexParameter.Value = indexName;
            command.Parameters.Add(indexParameter);

            var exists = Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
            if (exists) return;
        }

        try
        {
            await db.Database.ExecuteSqlRawAsync(alterSql);
        }
        catch (MySqlConnector.MySqlException ex)
            when (ex.ErrorCode == MySqlConnector.MySqlErrorCode.DuplicateKeyEntry ||
                  ex.ErrorCode == MySqlConnector.MySqlErrorCode.DuplicateKeyName)
        {
            // DuplicateKeyEntry (1062): pre-existing duplicate DATA, from the exact race this index
            // is meant to prevent, makes the ALTER fail. Skip rather than crash startup; it retries
            // on a later restart once the duplicate rows are cleaned up.
            //
            // DuplicateKeyName (1061): the other app created this same index between our
            // INFORMATION_SCHEMA check and this ALTER. Previously uncaught, so it crashed startup --
            // the index-shaped half of the same two-process DDL race as EnsureTableColumnAsync.
            //
            // Any other error (typo'd SQL, missing privilege) still surfaces loudly.
        }
    }
    finally
    {
        if (ownsConnection) await db.Database.CloseConnectionAsync();
    }
}

class WebDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    private readonly bool _isDevelopment;

    public WebDashboardAuthorizationFilter(bool isDevelopment) => _isDevelopment = isDevelopment;

    // IPRO.Web has no operator/staff role of its own -- IPRO.Admin already exposes
    // this same underlying Hangfire storage to authenticated SuperAdmins. Only allow
    // the dashboard here during local development.
    public bool Authorize(DashboardContext context) => _isDevelopment;
}
