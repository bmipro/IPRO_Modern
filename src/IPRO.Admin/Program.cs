using AspNetCoreRateLimit;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.Storage.MySql;
using IPRO.Admin.Infrastructure;
using IPRO.Admin.Middleware;
using Microsoft.AspNetCore.HttpOverrides;
using IPRO.Billing;
using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Utility;
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

var connStr = builder.Configuration.GetConnectionString("DefaultConnection")!;
connStr = EnsureMySqlMigrationOptions(connStr);

builder.Services.AddDbContext<IPRODbContext>(o =>
    o.UseMySql(connStr, ServerVersion.AutoDetect(connStr)));

// Dashboard-only view of the same Hangfire storage IPRO.Web writes to - no AddHangfireServer here,
// since Admin should never run background jobs, only monitor/manage the shared queue.
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseStorage(new MySqlStorage(connStr, new MySqlStorageOptions
    {
        TablesPrefix = "Hangfire_"
    })));

// Liveness only, no dependency checks -- same reasoning as IPRO.Web: the Azure health check's
// remedy is an instance restart, which only helps when the process itself is wedged.
builder.Services.AddHealthChecks();

builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<IAgentService, AgentService>();
builder.Services.AddScoped<IPackageEntitlementService, PackageEntitlementService>();
builder.Services.AddScoped<IAdminAuditLogService, AdminAuditLogService>();
// Admin uploads e-card artwork, so it needs blob storage too -- same registration as IPRO.Web.
builder.Services.AddSingleton<IBlobStorageService, AzureBlobStorageService>();
builder.Services.AddScoped<IClientService, ClientService>();
builder.Services.AddScoped<INewsLetterService, NewsLetterService>();
// Registered in Admin too: the scheduler jobs that live in this process dispatch email and must
// consult the same consent rule as the Web app.
builder.Services.AddScoped<IEmailConsentService, EmailConsentService>();
builder.Services.AddScoped<IWebsiteService, WebsiteService>();
builder.Services.Configure<EmailSettings>(builder.Configuration.GetSection("Email"));
builder.Services.AddScoped<IEmailService, SendGridEmailService>();
builder.Services.Configure<PayPalSettings>(builder.Configuration.GetSection("PayPal"));
builder.Services.Configure<AzureDomainAutomationOptions>(builder.Configuration.GetSection("AzureDomainAutomation"));
builder.Services.AddScoped<IBillingService, PayPalBillingService>();
builder.Services.AddScoped<IPasswordHasher<AgentUser>, PasswordHasher<AgentUser>>();
builder.Services.AddScoped<IPasswordHasher<AdminUser>, PasswordHasher<AdminUser>>();
builder.Services.AddHttpClient();
builder.Services.AddScoped<IAzureDomainAutomationService, AzureDomainAutomationService>();
builder.Services.AddScoped<IDomainCheckService, DomainCheckService>();

// ── Auth ──────────────────────────────────────────────────
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath        = "/Admin/Login";
        o.LogoutPath       = "/Admin/Logout";
        o.AccessDeniedPath = "/Admin/AccessDenied";
        o.ExpireTimeSpan   = TimeSpan.FromHours(4);
        o.Cookie.Name      = "IPRO.Admin.Auth";
        o.Cookie.HttpOnly  = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    });

builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("SuperAdmin", p => p.RequireClaim("Role", "SuperAdmin"));
    o.AddPolicy("AdminAccess", p => p.RequireAuthenticatedUser());
});

// ── Rate Limiting ─────────────────────────────────────────
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
builder.Services.AddSession(o => { o.IdleTimeout = TimeSpan.FromMinutes(20); o.Cookie.HttpOnly = true; });

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
        var parts = cidr.Split('/');
        var prefix = System.Net.IPAddress.Parse(parts[0]);
        var length = int.Parse(parts[1]);
        options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, length));
        options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix.MapToIPv6(), length + 96));
    }
});

var app = builder.Build();

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment()) { app.UseExceptionHandler("/Admin/Error"); app.UseHsts(); }

app.UseSecurityHeaders();
app.UseIpRateLimiting();
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health");

// Which BUILD is serving. See the matching endpoint in IPRO.Web/Program.cs for why this exists:
// with WEBSITE_RUN_FROM_PACKAGE=1 a deploy can succeed while the worker keeps serving the previous
// package, and /health cannot tell the difference. The deploy workflow polls this and fails if the
// commit it just pushed is not the commit answering here.
app.MapGet("/health/version", () =>
{
    var informational = (System.Reflection.Assembly.GetEntryAssembly()
        ?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)
        ?.InformationalVersion ?? "unknown";

    var plus = informational.IndexOf('+');
    return Results.Text(plus >= 0 ? informational[(plus + 1)..] : informational);
});

app.MapControllerRoute("admin", "{controller=AdminDashboard}/{action=Index}/{id?}");
app.MapHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new SuperAdminDashboardAuthorizationFilter() },
    IsReadOnlyFunc = _ => false
});

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
    // Must run BEFORE the starter-content seeders below, which read the tables it creates --
    // same first-boot ordering fix as IPRO.Web (found in the local environment, 2026-08-07).
    try
    {
        await EnsureECardDesignSchemaAsync(db);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("[ECardDesignSchema] FAILED: " + ex);
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
    await EnsureAdminUserSchemaAsync(db, app.Configuration);
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
    // Same shared call as IPRO.Web/Program.cs -- see the note there. Admin needs it too because
    // CardLetterActivity reads DeliveredAt, and because whichever app starts first must find the
    // schema complete (INVARIANTS.md rule 4).
    await EmailDeliverySchema.EnsureAsync(db);
    await db.Database.MigrateAsync();
    // AFTER MigrateAsync, never before: the migrations create the ON DELETE CASCADE constraints on
    // the financial ledger, so the guard must run once they exist to strip them. Running it earlier
    // (as it did until 2026-08-14) is a no-op on a fresh/restored database -- the tables don't exist
    // yet -- and leaves the first boot serving with the cascade live.
    await IPRO.DataAccess.FinancialLedgerSchemaGuard.EnsureAsync(db);
    await PackageEntitlementSeeder.SeedAsync(db);
    await TaxRateSeeder.SeedAsync(db);
    await WebsiteTemplateSeeder.SeedAsync(db);
    await WebsiteStarterContentSeeder.SeedAsync(db, seedLogger);
    await WebsiteStarterContentSeeder.SeedNavV2AdditionsAsync(db, seedLogger);
}

app.Run();

static string EnsureMySqlMigrationOptions(string connectionString)
{
    return connectionString.Contains("Allow User Variables", StringComparison.OrdinalIgnoreCase)
        ? connectionString
        : connectionString.TrimEnd(';') + ";Allow User Variables=True";
}

static async Task EnsureWebsiteTemplateSchemaAsync(IPRODbContext db)
{
    await db.Database.OpenConnectionAsync();
    try
    {
        await EnsureAgentDomainSchemaAsync(db);
        await EnsureTeamMemberSchemaAsync(db);
        await EnsureWebsiteTemplateColumnAsync(db, "BusinessType", "ALTER TABLE `WebsiteTemplates` ADD COLUMN `BusinessType` longtext CHARACTER SET utf8mb4 NULL");
        await EnsureWebsiteTemplateColumnAsync(db, "IsDefault", "ALTER TABLE `WebsiteTemplates` ADD COLUMN `IsDefault` tinyint(1) NOT NULL DEFAULT FALSE");
        await EnsureWebsiteTemplateColumnAsync(db, "TemplateKey", "ALTER TABLE `WebsiteTemplates` ADD COLUMN `TemplateKey` varchar(80) CHARACTER SET utf8mb4 NULL");
        await EnsureTableColumnAsync(db, "BillingRules", "DefaultWebsiteTemplateId", "ALTER TABLE `BillingRules` ADD COLUMN `DefaultWebsiteTemplateId` int NULL");
        await EnsureTableColumnAsync(db, "BillingRules", "IsTrialPackage", "ALTER TABLE `BillingRules` ADD COLUMN `IsTrialPackage` tinyint(1) NOT NULL DEFAULT FALSE");
        await EnsureTableColumnAsync(db, "BillingRules", "TrialDurationDays", "ALTER TABLE `BillingRules` ADD COLUMN `TrialDurationDays` int NULL");
        await EnsureTableColumnAsync(db, "BillingRules", "TrialReminderDayOffsets", "ALTER TABLE `BillingRules` ADD COLUMN `TrialReminderDayOffsets` varchar(120) CHARACTER SET utf8mb4 NULL");
        await EnsureTableColumnAsync(db, "BillingRules", "IsHiddenTestPackage", "ALTER TABLE `BillingRules` ADD COLUMN `IsHiddenTestPackage` tinyint(1) NOT NULL DEFAULT FALSE");
        await EnsureTableColumnAsync(db, "BillingRules", "SetupFeeWaived", "ALTER TABLE `BillingRules` ADD COLUMN `SetupFeeWaived` tinyint(1) NOT NULL DEFAULT FALSE");
        await EnsureTableColumnAsync(db, "BillingRules", "SetupFeeWaivedUntil", "ALTER TABLE `BillingRules` ADD COLUMN `SetupFeeWaivedUntil` datetime(6) NULL");

        // Quebec's 14.975% needs 5 decimals as a fraction (0.14975); the original decimal(7,4) column
        // rounded it to 0.1498, so invoices displayed "14.980 %" beside a region label saying 14.975%.
        await EnsureDecimalColumnScaleAsync(db, "Invoices", "TaxRate", 5, "ALTER TABLE `Invoices` MODIFY COLUMN `TaxRate` decimal(7,5) NOT NULL");

        // Bill-to snapshot: invoices are financial records retained after their agent is deleted, so
        // the bill-to must live ON the invoice. Backfill fills blanks from AgentUsers while the row
        // still exists; it runs every startup and touches only invoices whose snapshot is empty.
        await EnsureTableColumnAsync(db, "Invoices", "BillToName", "ALTER TABLE `Invoices` ADD COLUMN `BillToName` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''");
        await EnsureTableColumnAsync(db, "Invoices", "BillToCompany", "ALTER TABLE `Invoices` ADD COLUMN `BillToCompany` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''");
        await EnsureTableColumnAsync(db, "Invoices", "BillToEmail", "ALTER TABLE `Invoices` ADD COLUMN `BillToEmail` varchar(255) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''");
        await EnsureTableColumnAsync(db, "Invoices", "BillToAddress", "ALTER TABLE `Invoices` ADD COLUMN `BillToAddress` varchar(500) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''");
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE `Invoices` i JOIN `AgentUsers` a ON a.Id = i.AgentUserId SET " +
            "i.BillToName = CASE WHEN TRIM(CONCAT(COALESCE(a.FirstName,''),' ',COALESCE(a.LastName,''))) = '' THEN COALESCE(a.UserName,'') ELSE TRIM(CONCAT(COALESCE(a.FirstName,''),' ',COALESCE(a.LastName,''))) END, " +
            "i.BillToCompany = COALESCE(a.CompanyName,''), " +
            "i.BillToEmail = COALESCE(a.Email,''), " +
            "i.BillToAddress = CONCAT_WS('\\n', NULLIF(a.CompanyAddress,''), NULLIF(a.City,''), NULLIF(TRIM(CONCAT(COALESCE(a.Province,''),' ',COALESCE(a.PostalCode,''))),''), NULLIF(a.Country,'')) " +
            "WHERE i.BillToName = ''");
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

static async Task EnsureTeamMemberSchemaAsync(IPRODbContext db)
{
    // Keep in step with the IPRO.Web copy (INVARIANTS rule 4: both apps run identical schema repair).
    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `TeamMembers` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `AgentUserId` int NOT NULL,
    `FullName` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
    `Email` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
    `PasswordHash` varchar(500) CHARACTER SET utf8mb4 NOT NULL,
    `IsActive` tinyint(1) NOT NULL DEFAULT TRUE,
    `MustChangePassword` tinyint(1) NOT NULL DEFAULT TRUE,
    `CreatedAt` datetime(6) NOT NULL,
    `LastLoginAt` datetime(6) NULL,
    PRIMARY KEY (`Id`),
    UNIQUE KEY `IX_TeamMembers_Email` (`Email`),
    KEY `IX_TeamMembers_AgentUserId` (`AgentUserId`)
) CHARACTER SET utf8mb4;");
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

static async Task EnsureAdminUserSchemaAsync(IPRODbContext db, IConfiguration configuration)
{
    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `AdminUsers` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `Username` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
    `PasswordHash` varchar(500) CHARACTER SET utf8mb4 NOT NULL,
    `FullName` varchar(160) CHARACTER SET utf8mb4 NOT NULL,
    `Role` varchar(40) CHARACTER SET utf8mb4 NOT NULL,
    `IsActive` tinyint(1) NOT NULL DEFAULT TRUE,
    `CreatedAt` datetime(6) NOT NULL,
    `LastLoginAt` datetime(6) NULL,
    `PortalAccentColor` varchar(20) CHARACTER SET utf8mb4 NULL,
    PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;");

    await db.Database.OpenConnectionAsync();
    try
    {
        await EnsureTableColumnAsync(db, "AdminUsers", "PortalAccentColor", "ALTER TABLE `AdminUsers` ADD COLUMN `PortalAccentColor` varchar(20) CHARACTER SET utf8mb4 NULL");
    }
    finally
    {
        await db.Database.CloseConnectionAsync();
    }

    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `AdminAuditLogEntries` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `AdminUserId` int NOT NULL,
    `AdminUsername` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
    `Action` varchar(80) CHARACTER SET utf8mb4 NOT NULL,
    `Details` varchar(500) CHARACTER SET utf8mb4 NOT NULL,
    `CreatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;");

    if (await db.AdminUsers.AnyAsync())
    {
        return;
    }

    var bootstrapUsername = configuration["Admin:Username"];
    var bootstrapPassword = configuration["Admin:Password"];
    if (string.IsNullOrWhiteSpace(bootstrapUsername) || string.IsNullOrWhiteSpace(bootstrapPassword))
    {
        return;
    }

    var bootstrapUser = new AdminUser
    {
        Username = bootstrapUsername,
        FullName = "System Administrator",
        Role = AdminRoles.SuperAdmin,
        IsActive = true,
        CreatedAt = DateTime.UtcNow
    };
    bootstrapUser.PasswordHash = new PasswordHasher<AdminUser>().HashPassword(bootstrapUser, bootstrapPassword);
    db.AdminUsers.Add(bootstrapUser);
    await db.SaveChangesAsync();
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
        // Mirrors IPRO.Web -- both apps repair the same schema, so the index must be declared in both
        // or whichever app happens to start first is the only one that creates it.
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

    // Mirrors IPRO.Web's copy of this method exactly - both apps share the same database, so
    // whichever app starts first does the actual work and the other finds it all already done.
    var trialSettingsRowsInserted = await db.Database.ExecuteSqlRawAsync(@"
INSERT INTO `TrialSettings` (`Id`, `GracePeriodDays`, `UpdatedAt`)
SELECT 1, 1, UTC_TIMESTAMP()
WHERE NOT EXISTS (SELECT 1 FROM `TrialSettings` WHERE `Id` = 1);");

    if (trialSettingsRowsInserted > 0)
    {
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
                // ipro-prod-web added this column between our INFORMATION_SCHEMA check and this
                // ALTER. Both apps deploy from the same push and run identical schema repair against
                // the same database, so the one that ALTERs second gets MySQL 1060. Unhandled, that
                // escapes Main and Linux aborts the process (SIGABRT) -- the outage signature from
                // 2026-07-29. Keep this in step with the IPRO.Web copy.
            }
        }
    }
    finally
    {
        if (ownsConnection) await db.Database.CloseConnectionAsync();
    }
}

// Widens a decimal column whose scale proved too small. Only ever widens: if the live column's
// NUMERIC_SCALE is already at or above the requested scale, nothing runs. No 1060-style race catch
// on purpose: unlike ADD COLUMN, a MODIFY that loses the web/admin startup race simply re-applies
// the same definition and succeeds. Keep this in step with the IPRO.Web copy.
static async Task EnsureDecimalColumnScaleAsync(IPRODbContext db, string tableName, string columnName, int minScale, string alterSql)
{
    var ownsConnection = db.Database.GetDbConnection().State != System.Data.ConnectionState.Open;
    if (ownsConnection) await db.Database.OpenConnectionAsync();
    try
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = @"
SELECT COALESCE(MAX(NUMERIC_SCALE), -1)
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = DATABASE()
  AND TABLE_NAME = @tableName
  AND COLUMN_NAME = @columnName";

        var tableParameter = command.CreateParameter();
        tableParameter.ParameterName = "@tableName";
        tableParameter.Value = tableName;
        command.Parameters.Add(tableParameter);

        var columnParameter = command.CreateParameter();
        columnParameter.ParameterName = "@columnName";
        columnParameter.Value = columnName;
        command.Parameters.Add(columnParameter);

        var scale = Convert.ToInt32(await command.ExecuteScalarAsync());
        if (scale >= 0 && scale < minScale)
        {
            await db.Database.ExecuteSqlRawAsync(alterSql);
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
            // 1061 (DuplicateKeyName): the other app won the race and the index exists -- benign.
            // 1062 (DuplicateKeyEntry) is NOT: duplicate rows blocked index creation, so the app is
            // running WITHOUT the uniqueness guarantee. It still boots, but never again silently
            // (independent review H-8) -- stderr screams on every boot until the duplicates are
            // cleaned up. Any other error still surfaces loudly.
            if (ex.ErrorCode == MySqlConnector.MySqlErrorCode.DuplicateKeyEntry)
            {
                Console.Error.WriteLine(
                    $"[SCHEMA] UNIQUE INDEX {indexName} ON {tableName} NOT CREATED: existing rows already " +
                    $"violate it. The app is running WITHOUT this uniqueness guarantee. Clean up the " +
                    $"duplicate rows and restart. MySQL said: {ex.Message}");
            }
        }
    }
    finally
    {
        if (ownsConnection) await db.Database.CloseConnectionAsync();
    }
}

class SuperAdminDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        return httpContext.User.Identity?.IsAuthenticated == true
            && httpContext.User.HasClaim("Role", AdminRoles.SuperAdmin);
    }
}
