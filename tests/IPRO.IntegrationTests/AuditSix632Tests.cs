using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.Business.SiteImport;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Scheduler;
using IPRO.Web.Controllers;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// 632 (2026-11-24): the narrow audit's six after-launch items, re-checked against the code on the owner's
// word and built together -- L-7 reserved page addresses, D-2 the alias redirects' headers, H-2 lead
// notifications across all sites and sent later, B-1 an alert for a send that has stopped, C-1 tracking
// tokens saved before the send, M-1 a click link that matches no email on record no longer opens.
public class AuditSix632Tests
{
    private const string Key = "a-tracking-signing-key-632";

    // ---- L-7: the addresses iPro keeps for itself ------------------------------------------------

    [Fact]
    public void Every_door_that_makes_a_page_uses_one_list_of_reserved_addresses()
    {
        Assert.Same(ReservedPageAddresses.All, BuilderContract.ReservedSlugs);
        foreach (var name in new[] { "health", "Media", "t", "/billing/", "email-preferences", "portal", "clientportalmessages" })
            Assert.True(ReservedPageAddresses.IsReserved(name), name);
        foreach (var name in new[] { "home", "about", "health-page", "testimonials", "media-kit", "", null })
            Assert.False(ReservedPageAddresses.IsReserved(name), name ?? "null");

        Assert.Contains("ReservedPageAddresses.IsReserved(slug)", Read(@"src\IPRO.Admin\Controllers\StarterContentController.cs"));
        Assert.Contains("ReservedPageAddresses.IsReserved(slug)", Read(@"src\IPRO.Web\Controllers\WebsitePagesController.cs"));
        Assert.Contains("ReservedPageAddresses.IsReserved(baseSlug)) baseSlug += \"-page\";", Read(@"src\IPRO.DataAccess\WebsiteStarterResourcesHelper.cs"));
        Assert.Contains("ReservedPageAddresses.IsReserved(baseSlug)) baseSlug += \"-page\";", Read(@"src\IPRO.Web\Infrastructure\ProspectWebsitePreviewBuilder.cs"));
    }

    [Fact]
    public void A_starter_article_called_Health_gets_an_address_a_visitor_can_reach()
    {
        var unique = typeof(WebsiteStarterResourcesHelper).GetMethod("UniqueSlug",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.Equal("health-page", unique.Invoke(null, new object[] { "health", new HashSet<string>(StringComparer.OrdinalIgnoreCase) }));
        Assert.Equal("health-page-2", unique.Invoke(null, new object[] { "health", new HashSet<string>(new[] { "health-page" }, StringComparer.OrdinalIgnoreCase) }));
        Assert.Equal("retirement", unique.Invoke(null, new object[] { "retirement", new HashSet<string>(StringComparer.OrdinalIgnoreCase) }));
    }

    [Fact]
    public async Task The_page_editor_refuses_an_address_iPro_keeps_and_tells_a_page_already_there()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, websiteId) = await SiteAsync(db);
        db.AddRange(
            new WebsitePage { AgentWebsiteId = websiteId, Title = "About", Slug = "about", NavigationLabel = "About" },
            new WebsitePage { AgentWebsiteId = websiteId, Title = "Media", Slug = "media", NavigationLabel = "Media" });   // from before the rule
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var about = await db.WebsitePages.AsNoTracking().SingleAsync(p => p.Slug == "about");
        var media = await db.WebsitePages.AsNoTracking().SingleAsync(p => p.Slug == "media");

        // A new page titled "Health" (the address follows the title) is refused, and nothing is made.
        var create = PagesController(db, agentId);
        await create.SavePage(0, "Health", "", "Health", "", "", null, true, true, false);
        Assert.Equal(ReservedPageAddresses.Refusal("health"), create.TempData["Error"]);
        Assert.False(await db.WebsitePages.AnyAsync(p => p.AgentWebsiteId == websiteId && p.Slug == "health"));

        // Renaming a page to one is refused; the page keeps its address.
        var rename = PagesController(db, agentId);
        await rename.SavePage(about.Id, "About", "T", "About", "", "", null, true, true, false);
        Assert.Equal(ReservedPageAddresses.Refusal("t"), rename.TempData["Error"]);
        db.ChangeTracker.Clear();
        Assert.Equal("about", (await db.WebsitePages.AsNoTracking().SingleAsync(p => p.Id == about.Id)).Slug);

        // A page already there is saved (nothing on the form lost) and the adviser is told why no one sees it.
        var keep = PagesController(db, agentId);
        await keep.SavePage(media.Id, "Media Room", "media", "Media Room", "", "", null, true, true, false);
        db.ChangeTracker.Clear();
        Assert.Equal("Media Room", (await db.WebsitePages.AsNoTracking().SingleAsync(p => p.Id == media.Id)).Title);
        Assert.Contains(ReservedPageAddresses.StillThere("media"), keep.TempData["Warning"] as string);

        // An old address iPro answers itself could never forward, so it is left out and named.
        var old = PagesController(db, agentId);
        await old.SavePage(about.Id, "About", "about", "About", "", "", null, true, true, false, oldAddresses: "billing\nabout-us");
        db.ChangeTracker.Clear();
        Assert.Equal("about-us", (await db.WebsitePages.AsNoTracking().SingleAsync(p => p.Id == about.Id)).OldAddresses);
        Assert.Contains("/billing is an address iPro keeps for itself", old.TempData["Warning"] as string);
    }

    // ---- D-2: the alias redirects carry the security headers --------------------------------------

    [Fact]
    public async Task A_brand_name_redirect_carries_HSTS_and_the_other_security_headers()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:AliasHosts"] = "www.iproadvisers.com,iproadvisers.com",
            ["App:BaseUrl"] = "https://app.iproadvisers.com"
        });
        await using var app = builder.Build();
        IPRO.Web.Middleware.SecurityHeadersMiddlewareExtensions.UseSecurityHeaders(app);
        app.Use(async (context, next) =>
        {
            if (PlatformAliasHosts.TryHandle(app.Configuration, context))
            {
                IPRO.Web.Middleware.SecurityHeadersMiddlewareExtensions.PinThisNameOnly(context);
                return;
            }
            await next();
        });
        var pipeline = ((IApplicationBuilder)app).Build();

        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("www.iproadvisers.com");
        context.Request.Path = "/Account/Login";
        await pipeline(context);

        Assert.Equal(StatusCodes.Status301MovedPermanently, context.Response.StatusCode);
        // HTTPS pinned on this name only: the names under an old brand name or an adviser's own domain are not iPro's (audit E-4).
        Assert.Equal("max-age=31536000", context.Response.Headers["Strict-Transport-Security"].ToString());
        Assert.False(string.IsNullOrEmpty(context.Response.Headers["Content-Security-Policy"].ToString()));
        Assert.Equal("nosniff", context.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.False(string.IsNullOrEmpty(context.Response.Headers["Referrer-Policy"].ToString()));
    }

    [Fact]
    public void The_web_app_adds_the_security_headers_before_the_alias_and_short_address_redirects()
    {
        var program = Read(@"src\IPRO.Web\Program.cs");
        var hsts = program.IndexOf("app.UseHsts();", StringComparison.Ordinal);
        var headers = program.IndexOf("app.UseSecurityHeaders();", StringComparison.Ordinal);
        var alias = program.IndexOf("PlatformAliasHosts.TryHandle(app.Configuration, context)", StringComparison.Ordinal);
        var shortAddress = program.IndexOf("ShortAddressRedirect.TryHandleAsync(context, app.Configuration)", StringComparison.Ordinal);
        var limits = program.IndexOf("app.UseIpRateLimiting();", StringComparison.Ordinal);
        Assert.True(hsts > 0 && headers > hsts && alias > headers && shortAddress > alias && limits > shortAddress,
            "HSTS, then the security headers, then the alias names and the short addresses, then the rate limits");
        var redirects = program[alias..limits];
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(redirects, @"SecurityHeadersMiddlewareExtensions\.PinThisNameOnly\(context\);").Count);
    }

    // ---- H-2: lead notifications, all websites together, and later rather than never --------------

    [Fact]
    public async Task Lead_notifications_share_a_limit_across_websites_but_every_site_keeps_its_first_three()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, flooded, quiet) = await TwoWebsitesAsync(db);
        var (_, third, _) = await TwoWebsitesAsync(db);
        var now = DateTime.UtcNow;

        // One site flooded to its own limit does not fill the shared hour (the audit's D-3 / E-1).
        for (var i = 0; i < WebsiteLeadNotifications.PerWebsitePerHour; i++) db.Add(Lead(agentId, flooded, now.AddMinutes(-i - 1), sentAt: null, notified: true));
        await db.SaveChangesAsync();
        Assert.True(await WebsiteLeadNotifications.IsHeldAsync(db, flooded, now));
        Assert.False(await WebsiteLeadNotifications.PlatformFullAsync(db, now));
        Assert.True(await WebsiteLeadNotifications.WithinSharedLimitAsync(db, quiet, now));

        // A second busy site fills it: a third site still sends its first three an hour, not a fourth.
        for (var i = 0; i < 5; i++) db.Add(Lead(agentId, third, now.AddMinutes(-i - 1), sentAt: null, notified: true));
        db.Add(Lead(agentId, quiet, now.AddHours(-3), sentAt: null, notified: true));            // three hours ago: does not count
        await db.SaveChangesAsync();
        Assert.True(await WebsiteLeadNotifications.PlatformFullAsync(db, now));
        Assert.True(await WebsiteLeadNotifications.WithinSharedLimitAsync(db, quiet, now));       // none this hour
        db.Add(Lead(agentId, quiet, now.AddMinutes(-5), sentAt: null, notified: true));
        db.Add(Lead(agentId, quiet, now.AddMinutes(-6), sentAt: null, notified: true));
        await db.SaveChangesAsync();
        Assert.True(await WebsiteLeadNotifications.WithinSharedLimitAsync(db, quiet, now));       // two: the third still goes
        // Created three hours ago, emailed ten minutes ago by the later job: it counts in this hour.
        db.Add(Lead(agentId, quiet, now.AddHours(-3), sentAt: now.AddMinutes(-10), notified: true));
        await db.SaveChangesAsync();
        Assert.False(await WebsiteLeadNotifications.WithinSharedLimitAsync(db, quiet, now));      // three, and the shared hour is full

        var src = Read(@"src\IPRO.Web\Controllers\PublicWebsiteController.cs");
        var notify = src[src.IndexOf("private async Task NotifyAgentAsync(", StringComparison.Ordinal)..];
        notify = notify[..notify.IndexOf("private async Task RecordSpamAttemptAsync(", StringComparison.Ordinal)];
        Assert.True(notify.IndexOf("WebsiteLeadNotifications.WithinSharedLimitAsync(", StringComparison.Ordinal) is > 0 and var shared
                    && shared < notify.IndexOf("_email.SendDetailedAsync(", StringComparison.Ordinal), "both limits are asked before the send");
        Assert.Contains("lead.NotificationSentAt = result.Success ? DateTime.UtcNow : null;", notify);
        Assert.Contains("RecurringJob.AddOrUpdate<HeldLeadNotificationJob>(\"held-lead-notifications\"", Read(@"src\IPRO.Web\Program.cs"));
    }

    [Fact]
    public async Task One_flooded_website_does_not_hold_another_websites_lead_email()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, flooded, quiet) = await TwoWebsitesAsync(db);
        var now = DateTime.UtcNow;
        for (var i = 0; i < WebsiteLeadNotifications.PerWebsitePerHour; i++) db.Add(Lead(agentId, flooded, now.AddMinutes(-i - 1), sentAt: null, notified: true));
        var floodHeld = Lead(agentId, flooded, now.AddMinutes(-20), sentAt: null, notified: false, error: WebsiteLeadNotifications.HeldMessage);
        var quietHeld = Lead(agentId, quiet, now.AddMinutes(-10), sentAt: null, notified: false, error: WebsiteLeadNotifications.HeldMessage);
        db.AddRange(floodHeld, quietHeld);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var email = new RecordingEmail();
        Assert.Equal(1, await Job(db, email).RunAsync());                                         // the older, flooded one is passed over
        db.ChangeTracker.Clear();
        Assert.True((await db.WebsiteLeads.AsNoTracking().SingleAsync(l => l.Id == quietHeld.Id)).NotificationSent);
        Assert.Equal(WebsiteLeadNotifications.HeldMessage, (await db.WebsiteLeads.AsNoTracking().SingleAsync(l => l.Id == floodHeld.Id)).NotificationError);
    }

    [Fact]
    public async Task A_held_notification_goes_out_later_and_nothing_else_is_resent()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, one, _) = await TwoWebsitesAsync(db);
        var now = DateTime.UtcNow;

        var held = Lead(agentId, one, now.AddMinutes(-30), sentAt: null, notified: false, error: WebsiteLeadNotifications.HeldMessage);
        var heldBefore632 = Lead(agentId, one, now.AddMinutes(-20), sentAt: null, notified: false, error: WebsiteLeadNotifications.HeldMessageBefore632);
        var tooOld = Lead(agentId, one, now.AddDays(-2), sentAt: null, notified: false, error: WebsiteLeadNotifications.HeldMessage);
        var refused = Lead(agentId, one, now.AddMinutes(-40), sentAt: null, notified: false, error: "SendGrid rejected the email. Status: 401 Unauthorized.");
        held.FirstName = "Held";
        db.AddRange(held, heldBefore632, tooOld, refused);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var email = new RecordingEmail();
        Assert.Equal(2, await Job(db, email).RunAsync());

        db.ChangeTracker.Clear();
        var rows = await db.WebsiteLeads.AsNoTracking().ToDictionaryAsync(l => l.Id);
        Assert.True(rows[held.Id].NotificationSent);
        Assert.NotNull(rows[held.Id].NotificationSentAt);
        Assert.Equal(string.Empty, rows[held.Id].NotificationError);
        Assert.True(rows[heldBefore632.Id].NotificationSent);
        Assert.Equal(WebsiteLeadNotifications.ExpiredMessage, rows[tooOld.Id].NotificationError);
        Assert.False(rows[refused.Id].NotificationSent);                                          // a provider's old refusal is never resent
        Assert.StartsWith("SendGrid rejected", rows[refused.Id].NotificationError);
        Assert.Equal(2, email.Sent.Count);
        Assert.Equal("New website lead: Held Form", email.Sent[0].Subject);                       // oldest first
        Assert.Contains("/WebsiteLeads", email.Sent[0].Html);

        // A second pass sends nothing more.
        Assert.Equal(0, await Job(db, email).RunAsync());
        Assert.Equal(2, email.Sent.Count);
    }

    [Fact]
    public async Task A_held_notification_waits_while_the_limits_are_used_and_after_a_provider_says_not_now()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, one, two) = await TwoWebsitesAsync(db);
        var now = DateTime.UtcNow;
        var held = Lead(agentId, one, now.AddMinutes(-5), sentAt: null, notified: false, error: WebsiteLeadNotifications.HeldMessage);
        db.Add(held);
        for (var i = 0; i < WebsiteLeadNotifications.AlwaysPerWebsitePerHour; i++) db.Add(Lead(agentId, one, now.AddMinutes(-i - 1), sentAt: null, notified: true));
        for (var i = 0; i < WebsiteLeadNotifications.PlatformPerHour - WebsiteLeadNotifications.AlwaysPerWebsitePerHour; i++)
            db.Add(Lead(agentId, two, now.AddMinutes(-i - 1), sentAt: null, notified: true));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var email = new RecordingEmail();
        Assert.Equal(0, await Job(db, email).RunAsync());
        Assert.Empty(email.Sent);
        Assert.Equal(WebsiteLeadNotifications.HeldMessage, (await db.WebsiteLeads.AsNoTracking().SingleAsync(l => l.Id == held.Id)).NotificationError);

        // Room again (the notifications already sent are over an hour old), but the provider says "not right now".
        await db.WebsiteLeads.Where(l => l.NotificationSent).ExecuteUpdateAsync(u => u.SetProperty(l => l.CreatedAt, now.AddHours(-2)));
        var busy = new RecordingEmail { Answer = EmailSendResult.FailedTransient("429 Too Many Requests") };
        Assert.Equal(0, await Job(db, busy).RunAsync());
        Assert.Single(busy.Sent);
        db.ChangeTracker.Clear();
        var row = await db.WebsiteLeads.AsNoTracking().SingleAsync(l => l.Id == held.Id);
        Assert.False(row.NotificationSent);
        Assert.Equal(WebsiteLeadNotifications.HeldMessage, row.NotificationError);              // held again, for the next pass
    }

    // ---- B-1: a send that has stopped is reported ----------------------------------------------------

    [Fact]
    public async Task A_send_that_has_sent_nothing_for_an_hour_is_found_and_held_or_moving_ones_are_not()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var now = DateTime.UtcNow;
        var sender = await AgentAsync(db, maySend: true);
        var suspended = await AgentAsync(db, maySend: false);

        var stuckCard = await CardAsync(db, sender, now.AddHours(-3), (null, true));
        var moving = await CardAsync(db, sender, now.AddHours(-3), (now.AddMinutes(-10), false), (null, true));   // the hourly limit spreading it out
        var justDue = await CardAsync(db, sender, now.AddMinutes(-30), (null, true));
        var heldOnPurpose = await CardAsync(db, suspended, now.AddHours(-3), (null, true));
        var stoppedLetter = await LetterAsync(db, sender, now.AddHours(-2), (now.AddMinutes(-100), false), (null, true));
        var newsletter = new NewsLetter { AgentUserId = sender, Subject = "November", HtmlBody = "<p>x</p>" };
        db.Add(newsletter);
        await db.SaveChangesAsync();
        var neverBuilt = new NewsLetterSend { NewsLetterId = newsletter.Id, AgentUserId = sender, Status = NewsLetterSendStatus.Scheduled, ScheduledAt = now.AddHours(-2) };
        db.Add(neverBuilt);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var found = await StuckSendWatchJob.FindAsync(db, now);
        Assert.Equal(
            new[] { ("E-card", stuckCard), ("Newsletter", neverBuilt.Id), ("E-letter", stoppedLetter) }.OrderBy(x => x.Item1).ToArray(),
            found.Select(s => (s.Kind, s.Id)).OrderBy(x => x.Kind).ToArray());
        Assert.DoesNotContain(found, s => s.Id == moving || s.Id == justDue || s.Id == heldOnPurpose);
        var letter = found.Single(s => s.Kind == "E-letter");
        Assert.Equal((1, 1), (letter.Sent, letter.Waiting));
        Assert.Equal(now.AddMinutes(-100), letter.StuckSince, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task The_alert_goes_by_the_other_provider_when_the_usual_one_refuses_and_the_job_turns_red()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var now = DateTime.UtcNow;
        var sender = await AgentAsync(db, maySend: true);
        var card = await CardAsync(db, sender, now.AddMinutes(-90), (null, true));   // stopped this hour: a new one
        db.ChangeTracker.Clear();

        var email = new RecordingEmail { FailFirst = true };
        var config = Config(("Support:NotificationEmail", "support@example.test"));
        var job = new StuckSendWatchJob(db, email, config, NullLogger<StuckSendWatchJob>.Instance);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => job.RunAsync());
        Assert.Contains($"E-card {card}", error.Message);
        Assert.Contains("by the other provider", error.Message);

        Assert.Equal(2, email.Sent.Count);
        Assert.Equal("support@example.test", email.Sent[1].To);
        Assert.Null(email.Sent[0].Stream);                                       // the usual provider first
        Assert.Equal(EmailStreams.Notify, email.Sent[1].Stream);                 // then Amazon SES's notify stream
        Assert.Contains($"E-card {card}</strong>", email.Sent[1].Html);
        Assert.Contains("nothing sent for 1 hour", email.Sent[1].Html);
        Assert.Contains("RecurringJob.AddOrUpdate<StuckSendWatchJob>(\"stuck-send-watch\"", Read(@"src\IPRO.Web\Program.cs"));
    }

    [Fact]
    public async Task A_send_stuck_for_hours_is_not_emailed_again_until_the_daily_reminder()
    {
        if (DateTime.UtcNow.Hour == StuckSendWatchJob.DailyReminderHourUtc) return;   // the reminder hour itself emails, by design
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var sender = await AgentAsync(db, maySend: true);
        await CardAsync(db, sender, DateTime.UtcNow.AddHours(-6), (null, true));
        db.ChangeTracker.Clear();

        var email = new RecordingEmail();
        await new StuckSendWatchJob(db, email, Config(("Support:NotificationEmail", "support@example.test")), NullLogger<StuckSendWatchJob>.Instance).RunAsync();
        Assert.Empty(email.Sent);
    }

    // ---- C-1: the tracking token is saved before the email goes ----------------------------------

    [Fact]
    public async Task An_ecard_recipients_tracking_token_is_in_the_database_before_the_email_is_sent()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await AgentAsync(db, maySend: true);
        var client = new Client { AgentUserId = agentId, FirstName = "Ada", LastName = "Reader", Email = "ada632@example.test" };
        var design = new ECardDesign
        {
            Key = ($"t632-{Guid.NewGuid():N}")[..20], Occasion = "Birthday", Name = "Simple birthday",
            ImageUrl = "/images/ecard-art/simple-birthday.jpg", Width = 540, Height = 396, IsActive = true
        };
        db.AddRange(client, design);
        await db.SaveChangesAsync();
        var card = new ECard
        {
            AgentUserId = agentId, Occasion = design.Key, Subject = "Happy birthday", Message = "Many happy returns.",
            Status = ECardStatuses.Scheduled, ScheduledAt = DateTime.UtcNow.AddMinutes(-1), TotalRecipients = 1
        };
        db.Add(card);
        await db.SaveChangesAsync();
        var recipient = new ECardRecipient { ECardId = card.Id, ClientId = client.Id, Email = client.Email, RecipientName = "Ada Reader" };
        db.Add(recipient);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        string? savedAtSend = null;
        var email = new RecordingEmail
        {
            OnSend = () =>
            {
                using var look = testDb.CreateContext();
                savedAtSend = look.ECardRecipients.AsNoTracking().Single(r => r.Id == recipient.Id).TrackingToken;
            }
        };
        var config = Config(("Email:TrackingSigningKey", Key), ("App:BaseUrl", "https://app.example.test"));
        await new ECardDispatcher(db, email, new EmailConsentService(db, config, NullLogger<EmailConsentService>.Instance, Array.Empty<IUnsubscribeNotifier>()),
            config, NullLogger<ECardDispatcher>.Instance).DispatchAsync(card.Id);

        Assert.Single(email.Sent);
        Assert.False(string.IsNullOrEmpty(savedAtSend));                                   // on record before the send
        Assert.Contains($"/t/o/ecard/{savedAtSend}.gif", email.Sent[0].Html);              // and it is the one in the email
    }

    [Theory]
    [InlineData(@"src\IPRO.Email\NewsLetterDispatcher.cs")]
    [InlineData(@"src\IPRO.Email\ECardDispatcher.cs")]
    [InlineData(@"src\IPRO.Email\ELetterDispatcher.cs")]
    [InlineData(@"src\IPRO.Email\PollDispatcher.cs")]
    public void Each_blast_saves_its_tracking_tokens_before_its_send_loop(string file)
    {
        var src = Read(file);
        var mint = src.IndexOf("var untracked = recipients.Where(r => string.IsNullOrEmpty(r.TrackingToken)).ToList();", StringComparison.Ordinal);
        var save = src.IndexOf("if (untracked.Count > 0) await ", StringComparison.Ordinal);
        var loop = src.IndexOf("foreach (var recipient in recipients)", StringComparison.Ordinal);
        Assert.True(mint > 0 && save > mint && loop > save, $"{file}: the tokens are saved before the loop that sends");
    }

    // ---- M-1: a click link opens only while its email is on record -------------------------------

    [Fact]
    public async Task A_signed_click_whose_email_is_no_longer_on_record_does_not_open()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (recipientId, token) = await SentCardAsync(db);
        const string target = "https://www.example-adviser.test/news";
        var config = Config(("Email:TrackingSigningKey", Key));

        Assert.IsType<RedirectResult>(await TrackingController(db, config).Click("ecard", token, target, EmailTrackingLinks.Sign("ecard", token, target, Key)));

        // Signed correctly, but no email carries that token: someone minted it, or the data is gone.
        var invented = Guid.NewGuid().ToString("N");
        var refused = Assert.IsType<ObjectResult>(await TrackingController(db, config).Click("ecard", invented, target, EmailTrackingLinks.Sign("ecard", invented, target, Key)));
        Assert.Equal(StatusCodes.Status410Gone, refused.StatusCode);
        Assert.Equal("This link is no longer available.", refused.Value);
        var wrongKind = Assert.IsType<ObjectResult>(await TrackingController(db, config).Click("poll", token, target, EmailTrackingLinks.Sign("poll", token, target, Key)));
        Assert.Equal(StatusCodes.Status410Gone, wrongKind.StatusCode);

        // The adviser's data erased: the row goes, and so does the link.
        await db.ECardRecipients.Where(r => r.Id == recipientId).ExecuteDeleteAsync();
        var gone = Assert.IsType<ObjectResult>(await TrackingController(db, config).Click("ecard", token, target, EmailTrackingLinks.Sign("ecard", token, target, Key)));
        Assert.Equal(StatusCodes.Status410Gone, gone.StatusCode);
    }

    [Fact]
    public async Task A_click_still_opens_when_the_database_cannot_be_read()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var db = testDb.CreateContext();
        var (_, token) = await SentCardAsync(db);
        const string target = "https://www.example-adviser.test/news";
        var config = Config(("Email:TrackingSigningKey", Key));
        var controller = TrackingController(db, config);
        await db.DisposeAsync();                                                           // "the database is having a moment"

        Assert.IsType<RedirectResult>(await controller.Click("ecard", token, target, EmailTrackingLinks.Sign("ecard", token, target, Key)));
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static HeldLeadNotificationJob Job(IPRODbContext db, IEmailService email) =>
        new(db, email, Config(("App:BaseUrl", "https://app.example.test")), NullLogger<HeldLeadNotificationJob>.Instance);

    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value)).Build();

    private static AgentUser NewAgent(string prefix) => new()
    {
        UserName = $"{prefix}-{Guid.NewGuid():N}"[..20],
        Email = $"{prefix}-{Guid.NewGuid():N}"[..12] + "@example.test",
        FirstName = "Audit", LastName = "Six", CompanyName = "Six & Sons",
        DomainName = $"{prefix}-{Guid.NewGuid():N}"[..24],
        Country = "Canada", Province = "Ontario"
    };

    // An adviser whose mail may go (a trial still open) or one suspended (held on purpose).
    private static async Task<int> AgentAsync(IPRODbContext db, bool maySend)
    {
        var agent = NewAgent("a632");
        agent.TrialEndsAt = DateTime.UtcNow.AddDays(10);
        agent.IsActive = maySend;
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static async Task<int> ClientAsync(IPRODbContext db, int agentId)
    {
        var client = new Client { AgentUserId = agentId, FirstName = "Re", LastName = "Cipient", Email = $"c-{Guid.NewGuid():N}"[..12] + "@example.test" };
        db.Add(client);
        await db.SaveChangesAsync();
        return client.Id;
    }

    private static async Task<int> CardAsync(IPRODbContext db, int agentId, DateTime scheduledAt, params (DateTime? SentAt, bool Waiting)[] recipients)
    {
        var card = new ECard { AgentUserId = agentId, Occasion = "Birthday", Subject = "Hello", Status = ECardStatuses.Scheduled, ScheduledAt = scheduledAt };
        db.Add(card);
        await db.SaveChangesAsync();
        foreach (var (sentAt, waiting) in recipients)
            db.Add(new ECardRecipient
            {
                ECardId = card.Id, ClientId = await ClientAsync(db, agentId), Email = $"r-{Guid.NewGuid():N}"[..12] + "@example.test", RecipientName = "R",
                Status = waiting ? ECardRecipientStatuses.Queued : ECardRecipientStatuses.Sent, SentAt = sentAt
            });
        await db.SaveChangesAsync();
        return card.Id;
    }

    private static async Task<int> LetterAsync(IPRODbContext db, int agentId, DateTime scheduledAt, params (DateTime? SentAt, bool Waiting)[] recipients)
    {
        var letter = new ELetter { AgentUserId = agentId, Subject = "A note", Body = "<p>Hello</p>", Status = ELetterStatuses.Sending, ScheduledAt = scheduledAt };
        db.Add(letter);
        await db.SaveChangesAsync();
        foreach (var (sentAt, waiting) in recipients)
            db.Add(new ELetterRecipient
            {
                ELetterId = letter.Id, ClientId = await ClientAsync(db, agentId), Email = $"r-{Guid.NewGuid():N}"[..12] + "@example.test", RecipientName = "R",
                Status = waiting ? ELetterRecipientStatuses.Queued : ELetterRecipientStatuses.Sent, SentAt = sentAt
            });
        await db.SaveChangesAsync();
        return letter.Id;
    }

    private static async Task<(int RecipientId, string Token)> SentCardAsync(IPRODbContext db)
    {
        var agentId = await AgentAsync(db, maySend: true);
        var card = new ECard { AgentUserId = agentId, Occasion = "Birthday", Subject = "Happy birthday", Status = ECardStatuses.Sent };
        db.Add(card);
        await db.SaveChangesAsync();
        var recipient = new ECardRecipient
        {
            ECardId = card.Id, ClientId = await ClientAsync(db, agentId), Email = "reader632@example.test", RecipientName = "Reader",
            Status = ECardRecipientStatuses.Sent, SentAt = DateTime.UtcNow.AddMinutes(-10), TrackingToken = Guid.NewGuid().ToString("N")
        };
        db.Add(recipient);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (recipient.Id, recipient.TrackingToken);
    }

    private static EmailTrackingController TrackingController(IPRODbContext db, IConfiguration config)
    {
        var consent = new EmailConsentService(db, config, NullLogger<EmailConsentService>.Instance, Array.Empty<IUnsubscribeNotifier>());
        return new EmailTrackingController(db, new NewsLetterService(new UnitOfWork(db), consent, db),
            new EmailDeliveryTracker(db, NullLogger<EmailDeliveryTracker>.Instance, consent), config,
            NullLogger<EmailTrackingController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = TestEmailRecorder.PlatformRequest() }   // 678 (audit D-2): /t answers on iPro's own address
        };
    }

    // One adviser, one website: the page editor finds the website by its adviser.
    private static async Task<(int AgentId, int WebsiteId)> SiteAsync(IPRODbContext db)
    {
        var agentId = await AgentAsync(db, maySend: true);
        var template = new WebsiteTemplate { TemplateKey = $"tk-{Guid.NewGuid():N}"[..16], Name = "Test", BusinessType = "All" };
        db.Add(template);
        await db.SaveChangesAsync();
        var website = new AgentWebsite { AgentUserId = agentId, TemplateId = template.Id, SiteTitle = "Site", IsPublished = true, CustomDomain = $"site-{Guid.NewGuid():N}"[..14] + ".example.test" };
        db.Add(website);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (agentId, website.Id);
    }

    private static async Task<(int AgentId, int One, int Two)> TwoWebsitesAsync(IPRODbContext db)
    {
        var agentId = await AgentAsync(db, maySend: true);
        var template = new WebsiteTemplate { TemplateKey = $"tk-{Guid.NewGuid():N}"[..16], Name = "Test", BusinessType = "All" };
        db.Add(template);
        await db.SaveChangesAsync();
        var one = new AgentWebsite { AgentUserId = agentId, TemplateId = template.Id, SiteTitle = "One", IsPublished = true, CustomDomain = $"one-{Guid.NewGuid():N}"[..14] + ".example.test" };
        var two = new AgentWebsite { AgentUserId = agentId, TemplateId = template.Id, SiteTitle = "Two", IsPublished = true, CustomDomain = $"two-{Guid.NewGuid():N}"[..14] + ".example.test" };
        db.AddRange(one, two);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (agentId, one.Id, two.Id);
    }

    private static WebsiteLead Lead(int agentId, int websiteId, DateTime createdAt, DateTime? sentAt, bool notified, string error = "") => new()
    {
        AgentUserId = agentId, AgentWebsiteId = websiteId,
        FirstName = "Lead", LastName = "Form", Email = $"lead-{Guid.NewGuid():N}"[..12] + "@example.test",
        NotificationSent = notified, NotificationSentAt = sentAt, NotificationError = error, CreatedAt = createdAt, UpdatedAt = createdAt
    };

    private static WebsitePagesController PagesController(IPRODbContext db, int agentId)
    {
        var controller = new WebsitePagesController(db, new PackageEntitlementService(new UnitOfWork(db), db), new NullBlob());
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString(CultureInfo.InvariantCulture)) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new TempDataDictionary(context, new NullTempData());
        return controller;
    }

    private sealed class RecordingEmail : IEmailService
    {
        public List<(string To, string Subject, string Html, string? Stream)> Sent { get; } = new();
        public EmailSendResult? Answer { get; init; }
        public bool FailFirst { get; init; }
        public Action? OnSend { get; init; }

        public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null,
            IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        {
            OnSend?.Invoke();
            Sent.Add((toEmail, subject, htmlBody, customArgs != null && customArgs.TryGetValue(AdviserSender.StreamTag, out var stream) ? stream : null));
            if (FailFirst && Sent.Count == 1) return Task.FromResult(EmailSendResult.Failed("401 Unauthorized: the key was rejected"));
            return Task.FromResult(Answer ?? EmailSendResult.Sent($"msg-{Sent.Count}"));
        }

        public async Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null,
            IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
            (await SendDetailedAsync(toEmail, toName, subject, htmlBody, textBody, customArgs, replyToEmail, replyToName, listUnsubscribeUrl)).Success;

        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData) => Task.FromResult(true);
    }

    private sealed class NullBlob : IPRO.Utility.IBlobStorageService
    {
        public Task<string> UploadAsync(Stream fileStream, string fileName, string containerName, string contentType, bool isPrivate) => Task.FromResult("https://blob.example.test/x");
        public Task<bool> DeleteAsync(string blobUrl) => Task.FromResult(true);
        public Task<Stream?> DownloadAsync(string blobUrl) => Task.FromResult<Stream?>(null);
        public Task<List<string>> ListAsync(string containerName) => Task.FromResult(new List<string>());
        public string GetPublicUrl(string containerName, string fileName) => $"https://blob.example.test/{containerName}/{fileName}";
        public Task EnsureContainerAccessAsync(string containerName, bool isPrivate) => Task.CompletedTask;
    }

    private sealed class NullTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private static string Read(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "IPRO.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative)).Replace("\r\n", "\n");
    }
}
