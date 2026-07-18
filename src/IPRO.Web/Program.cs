#nullable enable

using AspNetCoreRateLimit;
using Hangfire;
using Hangfire.Storage.MySql;
using IPRO.Billing;
using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Scheduler;
using IPRO.Utility;
using IPRO.Web.Middleware;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.Configure<PayPalSettings>(builder.Configuration.GetSection("PayPal"));
builder.Services.Configure<AzureDomainAutomationOptions>(builder.Configuration.GetSection("AzureDomainAutomation"));
builder.Services.AddScoped<IBillingService, PayPalBillingService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IBlobStorageService, AzureBlobStorageService>();
builder.Services.AddScoped<IContactImporter, ContactImporter>();
builder.Services.AddSingleton<ITenantResolver>(_ => 
    new DomainTenantResolver(builder.Configuration["App:AdminDomain"]?? "admin.iprosystem.com"));
builder.Services.AddHttpClient("plesk");
builder.Services.AddScoped<IPleskHostingService, PleskHostingService>();
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
builder.Services.AddSingleton<IIpPolicyStore, MemoryCacheIpPolicyStore>();
builder.Services.AddSingleton<IRateLimitCounterStore, MemoryCacheRateLimitCounterStore>();
builder.Services.AddSingleton<IRateLimitConfiguration, RateLimitConfiguration>();
builder.Services.AddSingleton<IProcessingStrategy, AsyncKeyLockProcessingStrategy>();
builder.Services.AddInMemoryRateLimiting();
builder.Services.AddControllersWithViews();
builder.Services.Configure<Microsoft.AspNetCore.Mvc.Razor.RazorViewEngineOptions>(o =>
    o.ViewLocationExpanders.Add(new IPRO.Web.Infrastructure.PublicWebsiteViewLocationExpander()));
builder.Services.AddSession(o => 
{ 
    o.IdleTimeout = TimeSpan.FromMinutes(30); 
    o.Cookie.HttpOnly = true; 
    o.Cookie.IsEssential = true; 
});

var app = builder.Build();

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
    var canChangePassword = path.StartsWith("/Account/ChangePassword", StringComparison.OrdinalIgnoreCase);
    var canLogout = path.StartsWith("/Account/Logout", StringComparison.OrdinalIgnoreCase);
    var mustChangePassword = context.User.Identity?.IsAuthenticated == true
        && string.Equals(context.User.FindFirst("MustChangePassword")?.Value, "true", StringComparison.OrdinalIgnoreCase);

    if (mustChangePassword && !canChangePassword && !canLogout)
    {
        context.Response.Redirect("/Account/ChangePassword");
        return;
    }

    await next();
});

app.MapControllerRoute(
    "legacy-register",
    "pub/register.aspx",
    new { controller = "Account", action = "Register" });
app.MapControllerRoute("default", "{controller=Dashboard}/{action=Index}/{id?}");
app.MapHangfireDashboard("/hangfire", new DashboardOptions { IsReadOnlyFunc = _ => false });

RecurringJob.AddOrUpdate<NewsLetterDispatchJob>("dispatch-newsletters", job => job.RunAsync(), Cron.Minutely);
RecurringJob.AddOrUpdate<DripCampaignJob>("drip-campaigns", job => job.RunAsync(), Cron.Hourly);
RecurringJob.AddOrUpdate<CalendarReminderJob>("calendar-reminders", job => job.RunAsync(), Cron.Hourly);
RecurringJob.AddOrUpdate<SubscriptionBillingJob>("subscription-billing", job => job.RunAsync(), Cron.Hourly);
RecurringJob.AddOrUpdate<DomainAutomationJob>("domain-automation", job => job.RunAsync(), "*/5 * * * *");
RecurringJob.AddOrUpdate<RecurringClientInvoiceJob>("recurring-client-invoices", job => job.RunAsync(), Cron.Daily);

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IPRODbContext>();
    await EnsureWebsiteTemplateSchemaAsync(db);
    await WebsiteContentSchema.EnsureAsync(db);
    await EnsureWebsiteLeadSchemaAsync(db);
    await EnsureWebsiteContentBlockSchemaAsync(db);
    await EnsureDripCampaignEnrollmentSchemaAsync(db);
    await EnsureNewsLetterTemplateSchemaAsync(db);
    await EnsureDripCampaignStepSendSchemaAsync(db);
    await EnsureNewsLetterClickTrackingSchemaAsync(db);
    await EnsureSupportTicketSchemaAsync(db);
    await EnsurePromotionCodeSchemaAsync(db);
    await EnsureClientInvoiceSchemaAsync(db);
    await EnsureClientPortalSchemaAsync(db);
    await db.Database.MigrateAsync();
    await PackageEntitlementSeeder.SeedAsync(db);
    await TaxRateSeeder.SeedAsync(db);
    await WebsiteTemplateSeeder.SeedAsync(db);
    await WebsiteStarterContentSeeder.SeedAsync(db);

    var blob = scope.ServiceProvider.GetRequiredService<IBlobStorageService>();
    await blob.EnsureContainerAccessAsync("portal-documents", isPrivate: true);
}

app.Run();

static bool ShouldRouteToPublicWebsite(HttpContext context, IConfiguration configuration)
{
    if (!HttpMethods.IsGet(context.Request.Method)) return false;
    if (context.Request.Path.HasValue && Path.HasExtension(context.Request.Path.Value)) return false;

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

    await NewsLetterTemplateSeeder.SeedAsync(db);
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
        await EnsureTableColumnAsync(db, "AgentUsers", "PasswordResetToken", "ALTER TABLE `AgentUsers` ADD COLUMN `PasswordResetToken` varchar(80) CHARACTER SET utf8mb4 NULL");
        await EnsureTableColumnAsync(db, "AgentUsers", "PasswordResetTokenExpiresAt", "ALTER TABLE `AgentUsers` ADD COLUMN `PasswordResetTokenExpiresAt` datetime(6) NULL");
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
    PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;");

    await db.Database.OpenConnectionAsync();
    try
    {
        await EnsureTableColumnAsync(db, "Clients", "PortalPasswordHash", "ALTER TABLE `Clients` ADD COLUMN `PortalPasswordHash` varchar(500) CHARACTER SET utf8mb4 NULL");
        await EnsureTableColumnAsync(db, "Clients", "PortalInviteToken", "ALTER TABLE `Clients` ADD COLUMN `PortalInviteToken` varchar(80) CHARACTER SET utf8mb4 NULL");
        await EnsureTableColumnAsync(db, "Clients", "PortalActivatedAt", "ALTER TABLE `Clients` ADD COLUMN `PortalActivatedAt` datetime(6) NULL");
    }
    finally
    {
        await db.Database.CloseConnectionAsync();
    }
}

static async Task EnsureTableColumnAsync(IPRODbContext db, string tableName, string columnName, string alterSql)
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
        await db.Database.ExecuteSqlRawAsync(alterSql);
    }
}
