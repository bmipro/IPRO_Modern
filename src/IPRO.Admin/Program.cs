using AspNetCoreRateLimit;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.Storage.MySql;
using IPRO.Billing;
using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
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

builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<IAgentService, AgentService>();
builder.Services.AddScoped<IPackageEntitlementService, PackageEntitlementService>();
builder.Services.AddScoped<IClientService, ClientService>();
builder.Services.AddScoped<INewsLetterService, NewsLetterService>();
builder.Services.AddScoped<IWebsiteService, WebsiteService>();
builder.Services.Configure<EmailSettings>(builder.Configuration.GetSection("Email"));
builder.Services.AddScoped<IEmailService, SendGridEmailService>();
builder.Services.Configure<PayPalSettings>(builder.Configuration.GetSection("PayPal"));
builder.Services.Configure<AzureDomainAutomationOptions>(builder.Configuration.GetSection("AzureDomainAutomation"));
builder.Services.AddScoped<IBillingService, PayPalBillingService>();
builder.Services.AddScoped<IPasswordHasher<AgentUser>, PasswordHasher<AgentUser>>();
builder.Services.AddScoped<IPasswordHasher<AdminUser>, PasswordHasher<AdminUser>>();
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("plesk");
builder.Services.AddScoped<IPleskHostingService, PleskHostingService>();
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
builder.Services.AddSingleton<IIpPolicyStore, MemoryCacheIpPolicyStore>();
builder.Services.AddSingleton<IRateLimitCounterStore, MemoryCacheRateLimitCounterStore>();
builder.Services.AddSingleton<IRateLimitConfiguration, RateLimitConfiguration>();
builder.Services.AddSingleton<IProcessingStrategy, AsyncKeyLockProcessingStrategy>();
builder.Services.AddInMemoryRateLimiting();

builder.Services.AddControllersWithViews();
builder.Services.AddSession(o => { o.IdleTimeout = TimeSpan.FromMinutes(20); o.Cookie.HttpOnly = true; });

var app = builder.Build();

if (!app.Environment.IsDevelopment()) { app.UseExceptionHandler("/Admin/Error"); app.UseHsts(); }

app.UseIpRateLimiting();
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();
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
    await EnsureDripCampaignStepSendSchemaAsync(db);
    await EnsureNewsLetterClickTrackingSchemaAsync(db);
    await EnsureAdminUserSchemaAsync(db, app.Configuration);
    await EnsureSupportTicketSchemaAsync(db);
    await EnsurePromotionCodeSchemaAsync(db);
    await EnsureClientInvoiceSchemaAsync(db);
    await EnsureClientPortalSchemaAsync(db);
    await EnsureClientLifeEventSchemaAsync(db);
    await db.Database.MigrateAsync();
    await PackageEntitlementSeeder.SeedAsync(db);
    await TaxRateSeeder.SeedAsync(db);
    await WebsiteTemplateSeeder.SeedAsync(db);
    await WebsiteStarterContentSeeder.SeedAsync(db);
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
        await EnsureWebsiteTemplateColumnAsync(db, "BusinessType", "ALTER TABLE `WebsiteTemplates` ADD COLUMN `BusinessType` longtext CHARACTER SET utf8mb4 NULL");
        await EnsureWebsiteTemplateColumnAsync(db, "IsDefault", "ALTER TABLE `WebsiteTemplates` ADD COLUMN `IsDefault` tinyint(1) NOT NULL DEFAULT FALSE");
        await EnsureWebsiteTemplateColumnAsync(db, "TemplateKey", "ALTER TABLE `WebsiteTemplates` ADD COLUMN `TemplateKey` varchar(80) CHARACTER SET utf8mb4 NULL");
        await EnsureTableColumnAsync(db, "BillingRules", "DefaultWebsiteTemplateId", "ALTER TABLE `BillingRules` ADD COLUMN `DefaultWebsiteTemplateId` int NULL");
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
    PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;");

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

class SuperAdminDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        return httpContext.User.Identity?.IsAuthenticated == true
            && httpContext.User.HasClaim("Role", AdminRoles.SuperAdmin);
    }
}
