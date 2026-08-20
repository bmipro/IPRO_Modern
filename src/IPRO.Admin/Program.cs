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
        // ADMIN-7: role/active status are re-checked against the database on every request, so a
        // demotion or deactivation takes effect immediately instead of at cookie expiry.
        o.EventsType = typeof(IPRO.Admin.Infrastructure.AdminCookieRevalidator);
    });
builder.Services.AddScoped<IPRO.Admin.Infrastructure.AdminCookieRevalidator>();

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
    //
    // Must stay identical to IPRO.Web/Program.cs (INVARIANTS.md rule 4).
    await db.Database.MigrateAsync();
    // Silence here is not success (TODO 425) -- see the identical check in IPRO.Web/Program.cs.
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
    // Must run BEFORE the starter-content seeders below, which read the tables it creates --
    // same first-boot ordering fix as IPRO.Web (found in the local environment, 2026-08-07).
    try
    {
        await StartupSchemaRepair.EnsureECardDesignSchemaAsync(db);
        // Same second call as IPRO.Web -- see the note there. EmailDeliverySchema adds
        // ECardDesigns.SendAfterUnsubscribe, which ECardDesignSeeder writes, so it has to run before
        // the seeders as well as after the recipient tables are created. On a fresh database this
        // omission crashed Admin outright: the seeder failed with 1054, left its entities tracked,
        // and EnsureAdminUserSchemaAsync's SaveChangesAsync inherited them and threw (2026-08-15).
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
    await StartupSchemaRepair.EnsureNewsLetterClickTrackingSchemaAsync(db);
    await EnsureAdminUserSchemaAsync(db, app.Configuration);
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
    // Same shared call as IPRO.Web/Program.cs -- see the note there. Admin needs it too because
    // CardLetterActivity reads DeliveredAt, and because whichever app starts first must find the
    // schema complete (INVARIANTS.md rule 4).
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

    // LAST, after every schema repair and seeder: report any model relationship whose foreign key
    // the database does not enforce and that is not in the known baseline (auditor 5, F14). Never
    // fatal -- see SchemaIntegrityReporter.
    await IPRO.DataAccess.SchemaIntegrityReporter.ReportAsync(db, "IPRO.Admin");
}

app.Run();

static string EnsureMySqlMigrationOptions(string connectionString)
{
    return connectionString.Contains("Allow User Variables", StringComparison.OrdinalIgnoreCase)
        ? connectionString
        : connectionString.TrimEnd(';') + ";Allow User Variables=True";
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
        await StartupSchemaRepair.EnsureTableColumnAsync(db, "AdminUsers", "PortalAccentColor", "ALTER TABLE `AdminUsers` ADD COLUMN `PortalAccentColor` varchar(20) CHARACTER SET utf8mb4 NULL");
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

    // Auditor 5, F11: Username is the login identifier -- every auth path does
    // FirstOrDefaultAsync(u => u.Username == ...) -- but the table had only a primary key. With a
    // duplicate, the second account can never log in and audit-log attribution is ambiguous.
    // AdminUsersController's ExistsAsync check is check-then-insert; this makes the database the
    // final arbiter. EnsureUniqueIndexAsync screams (but does not crash) if duplicates already exist.
    await StartupSchemaRepair.EnsureUniqueIndexAsync(db, "AdminUsers", "UX_AdminUsers_Username",
        "ALTER TABLE `AdminUsers` ADD UNIQUE INDEX `UX_AdminUsers_Username` (`Username`)");

    // SeedGuard, not bare check-then-insert (INVARIANTS rule on seeders; this one was missed): both
    // this app's instances can boot at once, and two racing inserts would create two bootstrap
    // admins -- with the unique index above, one of them would now crash on startup instead.
    await IPRO.DataAccess.SeedGuard.RunAsync(db, "seed-bootstrap-admin", logger: null, async () =>
    {
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
    });

    await RunAdminRecoveryResetAsync(db, configuration);
}

// BREAK-GLASS RECOVERY (2026-08-15). The Admin login has no "forgot password" link on purpose:
// admin accounts carry no email address, and an email-based reset on the SuperAdmin door would be an
// account-takeover vector. But that left one unrecoverable state -- the LAST SuperAdmin forgetting
// their password. AdminUsersController's last-SuperAdmin guard keeps an account existing; it cannot
// make a human remember. The bootstrap seeder above only fires when AdminUsers is EMPTY, so the old
// answer was "hand-edit MySQL in production", which is a terrible thing to attempt under stress.
//
// Root of trust is the Azure portal -- already the root of trust for the connection string, the
// PayPal credentials and the deploy itself, so this grants no new authority to anyone.
//
// RUNBOOK (also in DOCS/09_TROUBLESHOOTING.md):
//   1. Azure Portal -> ipro-prod-admin -> Environment variables: set Admin__RecoveryReset = true
//      (confirm Admin__Username / Admin__Password are the credentials you want restored).
//   2. Restart the app. On boot this resets that ONE account: password re-hashed, IsActive = true,
//      Role = SuperAdmin. No other account is touched, nothing is deleted.
//   3. Log in, change the password in-app (Admin Users -> your account -> Reset Password).
//   4. REMOVE Admin__RecoveryReset (or set it false) and restart again.
//
// Leaving the flag on is not a silent backdoor: it re-applies the configured password on every
// restart and logs a warning each time, and step 4 is enforced by a startup nag below.
static async Task RunAdminRecoveryResetAsync(IPRODbContext db, IConfiguration configuration)
{
    if (!configuration.GetValue<bool>("Admin:RecoveryReset")) return;

    var username = configuration["Admin:Username"];
    var password = configuration["Admin:Password"];
    if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
    {
        Console.Error.WriteLine(
            "[AdminRecovery] Admin:RecoveryReset is set but Admin:Username / Admin:Password are not configured. " +
            "Nothing was changed -- set both and restart.");
        return;
    }

    // Serialized like every other seeder: two instances booting at once must not race on the same row.
    await IPRO.DataAccess.SeedGuard.RunAsync(db, "admin-recovery-reset", logger: null, async () =>
    {
        var hasher = new PasswordHasher<AdminUser>();
        var user = await db.AdminUsers.FirstOrDefaultAsync(a => a.Username == username);
        if (user == null)
        {
            // The account was deleted outright rather than forgotten -- recreate it, since an empty
            // AdminUsers table is the only case the bootstrap seeder above would have covered.
            user = new AdminUser
            {
                Username = username,
                FullName = "System Administrator",
                Role = AdminRoles.SuperAdmin,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };
            user.PasswordHash = hasher.HashPassword(user, password);
            db.AdminUsers.Add(user);
            await db.SaveChangesAsync();
            Console.Error.WriteLine(
                $"[AdminRecovery] RECOVERY RESET: admin account '{username}' did not exist and was RECREATED as an " +
                "active Super Admin from configuration. Log in, change the password, then remove Admin__RecoveryReset.");
            return;
        }

        user.PasswordHash = hasher.HashPassword(user, password);
        user.IsActive = true;
        user.Role = AdminRoles.SuperAdmin;
        await db.SaveChangesAsync();

        db.AdminAuditLogEntries.Add(new AdminAuditLogEntry
        {
            AdminUserId = user.Id,
            AdminUsername = username,
            Action = "AdminRecoveryReset",
            Details = "Break-glass recovery: password reset from configuration, account forced active + Super Admin.",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        Console.Error.WriteLine(
            $"[AdminRecovery] RECOVERY RESET APPLIED to admin '{username}': password reset from configuration, " +
            "account forced active and Super Admin. Log in, change the password in-app, then REMOVE the " +
            "Admin__RecoveryReset setting and restart -- while it stays set, every restart re-applies this password.");
    });
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
