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
builder.Services.AddScoped<IPasswordHasher<TeamMember>, PasswordHasher<TeamMember>>();
builder.Services.AddScoped<IAgentService, AgentService>();
builder.Services.AddScoped<IPackageEntitlementService, PackageEntitlementService>();
builder.Services.AddScoped<IClientService, ClientService>();
builder.Services.AddScoped<INewsLetterService, NewsLetterService>();
// Records SendGrid delivery events for every sender other than newsletters/drip campaigns.
builder.Services.AddScoped<IEmailDeliveryTracker, EmailDeliveryTracker>();
// The single "may we email this client?" decision point. Every dispatcher asks this and nothing
// re-implements it -- see EmailConsentService.
builder.Services.AddScoped<IEmailConsentService, EmailConsentService>();
// Lets EmailConsentService tell the agent when a client unsubscribes, without IPRO.Business having
// to reference IPRO.Email (the dependency runs the other way). Registered here only: IPRO.Admin has
// no reason to send this, and the service takes IEnumerable so its absence there is not an error.
builder.Services.AddScoped<IPRO.Business.Services.IUnsubscribeNotifier, IPRO.Email.UnsubscribeNotifier>();
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
app.Use(async (context, next) =>
{
    // THE URL-SPACE RULE. See DOCS/INVARIANTS.md -- this middleware is its only enforcement point.
    //
    //   On an agent's public host, a bare path is ALWAYS the public website.
    //   The agent portal lives ONLY under /portal.
    //   No exceptions -- not for signed-in agents, not for colliding slugs.
    //
    // The portal and every agent's public website are one application sharing one URL space, so
    // "which of the two owns /testimonials?" had to be answered somewhere. Answering it by reserving
    // controller names produced bugs in both directions: visitors on an agent's own firm website were
    // served the portal LOGIN FORM for /testimonials, and later a signed-in agent clicking their own
    // site's Testimonials link was thrown into the portal instead.
    //
    // /portal (7052444, 2026-08-07 09:25) made the question unnecessary. But the cookie- and
    // reserved-prefix machinery written 18 minutes earlier was left in place beside it and kept
    // winning, which is why the original bug survived three separate "fixes". Removed 2026-08-08:
    // one rule, one place, no second mechanism to fall out of sync with it.
    if (ShouldRouteToPublicWebsite(context, app.Configuration))
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

    // A gated agent keeps access to their OWN account: their profile, their password, and their
    // portal colour. Locking someone out of their own password change is hostile -- it is the one
    // thing you must always be able to do, especially if the reason they cannot pay is that they
    // cannot get in properly -- and editing their own contact details or accent colour grants no
    // product functionality. Requested 2026-08-08 after a real signup showed these bouncing silently
    // back to /Billing. SetPortalAccentColor is safe to expose here: it accepts only a colour from a
    // fixed allow-list and only redirects to a local URL.
    // UploadPhoto/RemovePhoto belong to the same own-account rule as Profile itself: the Photo card
    // renders on the (exempt) Profile page, but its forms post to these separate paths, so a gated
    // agent's photo change 302'd silently to /Billing (found by the #375 sweep, 2026-08-12).
    var canUseOwnAccount = path.StartsWith("/Account/Profile", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/Account/SetPortalAccentColor", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/Account/UploadPhoto", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/Account/RemovePhoto", StringComparison.OrdinalIgnoreCase)
        || canChangePassword;
    var isAgentSession = context.User.Identity?.AuthenticationType == CookieAuthenticationDefaults.AuthenticationScheme;

    // Team members (#379) act as their agent everywhere EXCEPT the owner-only controls: Billing
    // (money) and Team (staffing -- otherwise an assistant could mint more logins). Everything
    // else, including their own ChangePassword, stays open. Owner decision 2026-08-12:
    // "everything except Billing".
    var isTeamMemberSession = context.User.FindFirst("TeamMemberId") != null;
    if (isAuthenticated && isAgentSession && isTeamMemberSession &&
        (canUseBilling || path.StartsWith("/Team", StringComparison.OrdinalIgnoreCase)))
    {
        context.Response.Redirect("/portal/Dashboard");
        return;
    }

    if (isAuthenticated && isAgentSession && !mustChangePassword && !canUseBilling && !canLoginOrLogout && !canUseOwnAccount)
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

// Which BUILD is serving -- not whether the app is up, which is what /health answers.
//
// The two are not the same, and assuming they were cost real time: with WEBSITE_RUN_FROM_PACKAGE=1
// the worker serves an already-mounted package, so a deploy can finish, report success, and leave
// production on the previous build until something restarts the site. /health said Healthy
// throughout. The deploy workflow now polls this and fails if the commit it just pushed is not the
// commit answering here.
//
// The value comes from AssemblyInformationalVersion, which the workflow stamps via
// -p:SourceRevisionId=<git sha> at publish time; locally it is just the base version.
// Under /health, which is already a never-shadowed prefix, so no routing change is needed.
app.MapGet("/health/version", () =>
{
    var informational = (System.Reflection.Assembly.GetEntryAssembly()
        ?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)
        ?.InformationalVersion ?? "unknown";

    // "1.0.0+abc123..." -> "abc123..."
    var plus = informational.IndexOf('+');
    return Results.Text(plus >= 0 ? informational[(plus + 1)..] : informational);
});

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

    // Migrations FIRST, then the repair functions. This ordering was inverted until 2026-08-15, and
    // on an empty database that made the whole startup unrunnable: the repairs ALTER tables that only
    // migrations create, so the very first one raised 1146 and killed the process before a single
    // table existed. Nobody noticed because no database has been created from scratch since.
    //
    // On an established database this move changes nothing at all -- every migration EF can see is
    // already recorded in __EFMigrationsHistory, so MigrateAsync is a no-op wherever it sits. On a
    // fresh one it is the difference between booting and crash-looping.
    //
    // NOTE: MigrateAsync only applies the 15 migrations that carry [DbContext]; the 28 added since
    // 2026-07-11 are invisible to EF and never run (TODO item 425). The repair functions below are
    // what actually build most of this schema -- do not "simplify" them away.
    await db.Database.MigrateAsync();
    // Silence here is not success (TODO 425): the 28 migrations written between 2026-07-11 and
    // 2026-08-14 lacked [DbContext], EF never discovered them, and MigrateAsync reported success
    // while applying nothing -- for a month. The attribute is fixed now, and this check makes that
    // failure mode impossible to reintroduce quietly: any discoverable-but-unapplied migration
    // screams on every boot. Not fatal -- the schema repairs still hold the schema together -- but
    // it must never be silent again.
    var pendingMigrations = (await db.Database.GetPendingMigrationsAsync()).ToList();
    if (pendingMigrations.Count > 0)
    {
        Console.Error.WriteLine(
            $"[Migrations] {pendingMigrations.Count} migration(s) are discoverable but NOT applied -- " +
            "MigrateAsync did not run them. This is the silent-no-op failure of TODO 425: " +
            string.Join(", ", pendingMigrations));
    }
    // Immediately after MigrateAsync, never before: the migrations create the ON DELETE CASCADE
    // constraints on the financial ledger, so the guard must run once they exist to strip them.
    // Running it earlier (as it did until 2026-08-14) is a no-op on a fresh/restored database and
    // leaves the first boot serving with the cascade live.
    await IPRO.DataAccess.FinancialLedgerSchemaGuard.EnsureAsync(db);

    await StartupSchemaRepair.EnsureWebsiteTemplateSchemaAsync(db);
    await WebsiteContentSchema.EnsureAsync(db);
    await StartupSchemaRepair.EnsureWebsiteLeadSchemaAsync(db);
    await StartupSchemaRepair.EnsureWebsiteContentBlockSchemaAsync(db);
    await StartupSchemaRepair.EnsureDripCampaignEnrollmentSchemaAsync(db);
    await StartupSchemaRepair.EnsurePrepaidValueSchemaAsync(db);
    await StartupSchemaRepair.EnsureNewsLetterTemplateSchemaAsync(db);

    // Starter content is optional: an agent can work without a card design or a letter template,
    // so a failure here must never stop the app from booting. This is the same isolation the
    // background jobs already use, applied to startup -- learned the hard way, when a seeding
    // failure took both sites down with an unhandled exception during startup.
    //
    // Structural seeders above (entitlements, tax rates, website templates) are deliberately NOT
    // wrapped: an agent genuinely cannot function without those, so failing loudly is correct.
    var seedLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
        .CreateLogger("StarterContentSeeding");
    // Must run BEFORE the starter-content seeders below: ECardDesignSeeder and
    // ELetterTemplateSeeder read the tables this creates. On an existing database the order never
    // mattered, which is how it went unnoticed; on a fresh database (the local environment,
    // 2026-08-07) the first boot failed both seeders and only healed on the second.
    try
    {
        await StartupSchemaRepair.EnsureECardDesignSchemaAsync(db);
        // EmailDeliverySchema also runs later, after the E-Card/E-Letter/Poll CREATE TABLEs, because
        // it adds columns to the recipient tables those create. But it ALSO adds
        // ECardDesigns.SendAfterUnsubscribe, which ECardDesignSeeder writes -- so on a fresh database
        // the seeder was inserting a column that did not exist yet and failing with 1054. It is a
        // plain idempotent (table, column, definition) list, so calling it twice costs two cheap
        // INFORMATION_SCHEMA passes and removes the ordering trap entirely. The later call handles
        // the recipient tables; this one handles the design table before anything seeds into it.
        await EmailDeliverySchema.EnsureAsync(db);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("[ECardDesignSchema] FAILED: " + ex);
        db.ChangeTracker.Clear();
    }

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
        // A swallowed seeding failure leaves the failed entities TRACKED on this shared DbContext.
        // The next SaveChangesAsync anywhere in this startup scope re-attempts them and dies on
        // someone else's behalf -- that is how a missing ECardDesigns column crashed the ADMIN USER
        // seeder on a fresh database (2026-08-15). Dropping the tracked state keeps the failure
        // local to the seeder that caused it.
        db.ChangeTracker.Clear();
    }

    try
    {
        await StartupSchemaRepair.EnsureWebsiteStarterArticleSchemaAsync(db);
        await WebsiteStarterArticleSeeder.SeedAsync(db, seedLogger);
    }
    catch (Exception ex)
    {
        seedLogger.LogError(ex, "Website starter article seeding failed. The app is starting anyway; " +
                                "the Resources starter article library may be missing.");
        Console.Error.WriteLine("[WebsiteStarterArticleSeeding] FAILED: " + ex);
        // A swallowed seeding failure leaves the failed entities TRACKED on this shared DbContext.
        // The next SaveChangesAsync anywhere in this startup scope re-attempts them and dies on
        // someone else's behalf -- that is how a missing ECardDesigns column crashed the ADMIN USER
        // seeder on a fresh database (2026-08-15). Dropping the tracked state keeps the failure
        // local to the seeder that caused it.
        db.ChangeTracker.Clear();
    }

    try
    {
        await StartupSchemaRepair.EnsureWebsiteStarterFormSchemaAsync(db);
        await WebsiteStarterFormSeeder.SeedAsync(db, seedLogger);
    }
    catch (Exception ex)
    {
        seedLogger.LogError(ex, "Website starter form seeding failed. The app is starting anyway; " +
                                "the Starter Forms template library may be missing.");
        Console.Error.WriteLine("[WebsiteStarterFormSeeding] FAILED: " + ex);
        // A swallowed seeding failure leaves the failed entities TRACKED on this shared DbContext.
        // The next SaveChangesAsync anywhere in this startup scope re-attempts them and dies on
        // someone else's behalf -- that is how a missing ECardDesigns column crashed the ADMIN USER
        // seeder on a fresh database (2026-08-15). Dropping the tracked state keeps the failure
        // local to the seeder that caused it.
        db.ChangeTracker.Clear();
    }

    await StartupSchemaRepair.EnsureDripCampaignStepSendSchemaAsync(db);
    await StartupSchemaRepair.EnsureDidYouKnowEmailQueueSchemaAsync(db);
    await StartupSchemaRepair.EnsureBillingCancellationClaimSchemaAsync(db);
    await StartupSchemaRepair.EnsureNewsLetterClickTrackingSchemaAsync(db);
    await StartupSchemaRepair.EnsureSupportTicketSchemaAsync(db);
    await StartupSchemaRepair.EnsurePromotionCodeSchemaAsync(db);
    await StartupSchemaRepair.EnsureClientInvoiceSchemaAsync(db);
    await StartupSchemaRepair.EnsureClientPortalSchemaAsync(db);
    await StartupSchemaRepair.EnsureClientLifeEventSchemaAsync(db);
    await StartupSchemaRepair.EnsureAgentDocumentSchemaAsync(db);
    await StartupSchemaRepair.EnsureSocialPostSchemaAsync(db);
    await StartupSchemaRepair.EnsureTestimonialSubmissionSchemaAsync(db);
    await StartupSchemaRepair.EnsurePollSchemaAsync(db);
    await StartupSchemaRepair.EnsureWebsiteFormSchemaAsync(db);
    await StartupSchemaRepair.EnsureAgentDailyInsightSchemaAsync(db);
    await StartupSchemaRepair.EnsureAiUsageSchemaAsync(db);
    await StartupSchemaRepair.EnsureTrialFeatureSchemaAsync(db);
    await StartupSchemaRepair.EnsureECardSchemaAsync(db);
    await StartupSchemaRepair.EnsureELetterSchemaAsync(db);
    // Must run AFTER the three CREATE TABLE passes above (E-Card, E-Letter, Poll) -- it adds the
    // delivery-tracking columns to tables those create. Shared with IPRO.Admin/Program.cs so the
    // column list cannot drift between the two apps; see INVARIANTS.md rule 4.
    await EmailDeliverySchema.EnsureAsync(db);
    // MigrateAsync + FinancialLedgerSchemaGuard used to sit here. They moved to the TOP of this block
    // on 2026-08-15 so that migration-created tables exist before the repairs try to ALTER them --
    // see the comment there. The guard runs a second time below, after the repair CREATE TABLEs, in
    // case any of them recreated a cascade path into the ledger.
    await IPRO.DataAccess.FinancialLedgerSchemaGuard.EnsureAsync(db);
    await PackageEntitlementSeeder.SeedAsync(db);
    await TaxRateSeeder.SeedAsync(db);
    await WebsiteTemplateSeeder.SeedAsync(db);
    await WebsiteStarterContentSeeder.SeedAsync(db, seedLogger);
    await WebsiteStarterContentSeeder.SeedNavV2AdditionsAsync(db, seedLogger);

    // QA-only, sandbox-gated (see QaDailyBillingPackageSeeder). Involves live PayPal API calls, so
    // isolated the same way as starter-content seeding above -- a PayPal outage at boot must never
    // take the app down.
    try
    {
        var billingForSeed = scope.ServiceProvider.GetRequiredService<IBillingService>();
        var payPalSettings = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<PayPalSettings>>().Value;
        await QaDailyBillingPackageSeeder.SeedAsync(db, billingForSeed, payPalSettings.IsSandbox, seedLogger);
    }
    catch (Exception ex)
    {
        seedLogger.LogError(ex, "QA daily-billing test package seeding failed. The app is starting anyway.");
        Console.Error.WriteLine("[QaDailyBillingPackageSeeding] FAILED: " + ex);
    }

    var blob = scope.ServiceProvider.GetRequiredService<IBlobStorageService>();
    await blob.EnsureContainerAccessAsync("portal-documents", isPrivate: true);
    await blob.EnsureContainerAccessAsync("agent-documents", isPrivate: true);

    // LAST, after every schema repair and seeder: report any model relationship whose foreign key
    // the database does not enforce and that is not in the known baseline (auditor 5, F14). Never
    // fatal -- see SchemaIntegrityReporter.
    await IPRO.DataAccess.SchemaIntegrityReporter.ReportAsync(db, "IPRO.Web");
}

app.Run();

// REMOVED 2026-08-08 along with MarkPublicSlugOverrideAsync and HasPortalSessionCookie.
//
// BuildPortalRoutePrefixes() reflected over every controller and reserved its name as a portal
// prefix on all hosts, so an agent page slugged "testimonials" was swallowed by
// TestimonialsController and the visitor got a login form. Patching that produced a published-page
// lookup, then a cookie exemption, then an exemption to the exemption -- four mechanisms deciding
// one question, none of them agreeing.
//
// /portal answers the question by itself. Nothing needs to know which controller names exist.

// Reserved no matter what an agent names a page. Losing any of these on an agent's own domain would
// lock them (or their clients) out rather than merely hiding content.
static bool IsNeverShadowedPrefix(string segment) => segment.ToLowerInvariant() switch
{
    "account" or "billing" or "publicwebsite" or "media" or "hangfire" or "health" => true,

    // The CLIENT-facing surfaces. These are not agent-portal pages and must NOT move under /portal:
    // an agent's client reaches them on the AGENT'S domain, by following a link in an email we sent
    // them (portal invitation, invoice, poll, testimonial request). Those links are already in
    // people's inboxes and cannot be rewritten.
    //
    // Only "clientportal" and "clientportalaccount" were listed until 2026-08-08, so
    // /ClientPortalMessages, /ClientPortalDocuments and /ClientPortalAppointments 404'd on every
    // agent domain -- a client signed into the portal hit the agent's public 404 on Messages,
    // Documents or Appointments. Found while fixing the agent-facing half of the same problem.
    "clientportal" or "clientportalaccount" or "clientportalappointments"
        or "clientportaldocuments" or "clientportalmessages" or "clientportalpreferences"
        or "clientportalprofile" => true,

    // Attribute-routed client-facing endpoints: ClientDocumentController [Route("invoice")],
    // PollVoteController [Route("Poll/[action]")], TestimonialRequestController [Route("testimonial")].
    "invoice" or "poll" or "testimonial" => true,

    // EmailPreferencesController [Route("email-preferences")] -- the unsubscribe link carried by
    // every email we send. This one matters more than the rest of the list: an unsubscribe that
    // 404s is not a broken page, it is a spam complaint, and these links stay in inboxes for years.
    "email-preferences" => true,

    _ => false
};

static string NormalizeHostForLookup(string host) => host.Trim().Trim('.').ToLowerInvariant();

// IsPublicAgentHost was deleted here on 2026-08-08. It was a near-copy of the host checks inside
// ShouldRouteToPublicWebsite -- same question, two answers, and they had already drifted (only the
// one below knows about App:BaseUrl and App:TemporarySiteRootDomain). Two predicates for "is this an
// agent's public host" is the same failure that let the slug collision survive three fixes.
static bool ShouldRouteToPublicWebsite(HttpContext context, IConfiguration configuration)
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

    // The only paths an agent host does NOT surrender to the public site. These are not "portal
    // features" -- they are the ways in and the ways to recover. Losing them on an agent's own domain
    // would lock the agent or their clients out rather than merely hiding a page, and /health has to
    // answer for the load-balancer probe on every hostname it might arrive under.
    if (firstSegment.Length > 0 && IsNeverShadowedPrefix(firstSegment)) return false;

    // NOTE: there is deliberately no portal-controller-name check here, and no auth-cookie check.
    // Adding either one back re-creates the collision this file spent three days fixing. If a portal
    // route needs to be reachable on an agent's domain, it is reachable at /portal/<that route>.

    var host = NormalizeHostForLookup(context.Request.Host.Host);
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

class WebDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    private readonly bool _isDevelopment;

    public WebDashboardAuthorizationFilter(bool isDevelopment) => _isDevelopment = isDevelopment;

    // IPRO.Web has no operator/staff role of its own -- IPRO.Admin already exposes
    // this same underlying Hangfire storage to authenticated SuperAdmins. Only allow
    // the dashboard here during local development.
    public bool Authorize(DashboardContext context) => _isDevelopment;
}
