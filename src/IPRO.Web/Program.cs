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
builder.Services.AddScoped<IAgentService, AgentService>();
builder.Services.AddScoped<IClientService, ClientService>();
builder.Services.AddScoped<INewsLetterService, NewsLetterService>();
builder.Services.AddScoped<IWebsiteService, WebsiteService>();
builder.Services.Configure<EmailSettings>(builder.Configuration.GetSection("Email"));
builder.Services.AddScoped<IEmailService, SendGridEmailService>();
builder.Services.AddScoped<NewsLetterDispatcher>();
builder.Services.Configure<PayPalSettings>(builder.Configuration.GetSection("PayPal"));
builder.Services.AddScoped<IBillingService, PayPalBillingService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IBlobStorageService, AzureBlobStorageService>();
builder.Services.AddScoped<IContactImporter, ContactImporter>();
builder.Services.AddSingleton<ITenantResolver>(_ => 
    new DomainTenantResolver(builder.Configuration["App:AdminDomain"]?? "admin.iprosystem.com"));
builder.Services.AddHttpClient("plesk");
builder.Services.AddScoped<IPleskHostingService, PleskHostingService>();
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
        o.Cookie.SameSite = SameSiteMode.Strict;
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

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IPRODbContext>();
    await db.Database.MigrateAsync();
}

app.Run();
