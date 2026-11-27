using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IPRO.Billing;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Scheduler;
using IPRO.Utility;
using IPRO.Web.Controllers;
using IPRO.Web.Infrastructure;
using IPRO.Web.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IPRO.IntegrationTests;

// 678: the 2026-11-24 audit's Batch 1 (DOCS/AUDIT_2026-11-24.md in the notes), with the owner's three decisions of
// 2026-11-25: double opt-in, the setup fee charged at the switch to an iPro website, team members kept out of the
// owner's settings.
public class AuditBatch1678Tests
{
    // ---- A-1: team members and the owner's settings ------------------------------------------------------

    [Theory]
    [InlineData("/Payments", true)]
    [InlineData("/Payments/Save", true)]
    [InlineData("/Payments/StripeCallback", true)]
    [InlineData("/GoogleCalendar/Connect", true)]
    [InlineData("/GoogleCalendar/Callback", true)]
    [InlineData("/Website/KeepOwnWebsite", true)]
    [InlineData("/Website/UseIproWebsite", true)]
    [InlineData("/Website/WebsiteSetupFeeReturn", true)]
    [InlineData("/Website/Domains", true)]
    [InlineData("/Website/AddDomain", true)]
    [InlineData("/Website/RemoveDomain/7", true)]
    [InlineData("/Billing", true)]
    [InlineData("/Team/Add", true)]
    [InlineData("/Website", false)]
    [InlineData("/Website/Save", false)]
    [InlineData("/PaymentsReport", false)]
    [InlineData("/Clients", false)]
    [InlineData("/ClientInvoices", false)]
    public void The_owner_only_areas_are_payments_calendar_own_website_domains_billing_and_team(string path, bool ownerOnly)
    {
        Assert.Equal(ownerOnly, OwnerOnlyAreas.IsOwnerOnly(path));
    }

    [Fact]
    public void The_middleware_and_the_website_actions_refuse_team_members_by_the_one_list()
    {
        var program = Read(@"src\IPRO.Web\Program.cs");
        Assert.Contains("isTeamMemberSession && IPRO.Web.Infrastructure.OwnerOnlyAreas.IsOwnerOnly(path)", program);
        var website = Read(@"src\IPRO.Web\Controllers\WebsiteController.cs").Replace("\r\n", "\n");
        foreach (var action in new[] { "KeepOwnWebsite", "SaveOwnWebsite", "UseIproWebsite", "Domains", "DomainDisplay", "AddDomain", "RemoveDomain", "RetryDomain", "DomainGuide", "DomainHelp", "SetPrimaryDomain", "WebsiteSetupFeeReturn", "WebsiteSetupFeeCancel" })
        {
            Assert.Matches(new System.Text.RegularExpressions.Regex(
                @"public async Task<IActionResult> " + action + @"\([^)]*\)\n    \{\n        var ownerOnly = OwnerOnlyGate\(\);"), website);
        }
        var calendar = Read(@"src\IPRO.Web\Controllers\GoogleCalendarController.cs");
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(calendar, @"OwnerOnlyAreas\.IsTeamMember\(User\)").Count);
    }

    [Fact]
    public async Task A_team_member_is_turned_away_from_the_Payments_page_and_its_save()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await AgentAsync(db);

        var controller = Payments(db, agentId, new TestEmailRecorder(), teamMember: true);
        Assert.Equal("/portal/Dashboard", Assert.IsType<RedirectResult>(await controller.Index()).Url);
        var save = Payments(db, agentId, new TestEmailRecorder(), teamMember: true);
        Assert.Equal("/portal/Dashboard", Assert.IsType<RedirectResult>(await save.Save(new PaymentsForm { ETransferEmail = "assistant@example.test" })).Url);
        Assert.False(await db.AgentPaymentMethods.AnyAsync(m => m.AgentUserId == agentId));
    }

    [Fact]
    public async Task The_owner_is_emailed_each_time_where_clients_pay_changes_and_not_otherwise()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await AgentAsync(db);
        var email = new TestEmailRecorder();

        Assert.IsType<RedirectToActionResult>(await Payments(db, agentId, email).Save(new PaymentsForm { ETransferEmail = "owner@example.test" }));
        var notice = Assert.Single(email.Sent);
        Assert.Equal(PaymentChangeNotice.Subject, notice.Subject);
        Assert.Contains("owner@example.test", notice.Html);
        Assert.Contains("Interac e-Transfer email", notice.Html);

        // Saved again unchanged: nothing to tell.
        await Payments(db, agentId, email).Save(new PaymentsForm { ETransferEmail = "owner@example.test" });
        Assert.Single(email.Sent);

        // Changed: told again, with the old and the new.
        await Payments(db, agentId, email).Save(new PaymentsForm { ETransferEmail = "other@example.test" });
        Assert.Equal(2, email.Sent.Count);
        Assert.Contains("other@example.test", email.Sent[1].Html);
    }

    // ---- D-1: an opt-out kept from a deleted contact ------------------------------------------------------

    [Fact]
    public async Task Editing_a_contact_to_a_deleted_opted_out_address_keeps_that_opt_out()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await AgentAsync(db);
        var consent = Consent(db);
        var alice = await ClientAsync(db, agentId, "alice-678@example.test");
        await consent.SuppressAllAsync(await db.Clients.SingleAsync(c => c.Id == alice), "link");
        await ClientRecycleBin.DeleteToBinAsync(db, alice);
        var bob = await ClientAsync(db, agentId, "bob-678@example.test");
        db.ChangeTracker.Clear();

        var controller = Clients(db, consent, agentId);
        var current = await db.Clients.AsNoTracking().SingleAsync(c => c.Id == bob);
        await controller.Edit(new Client { Id = bob, FirstName = current.FirstName, LastName = current.LastName, Email = "alice-678@example.test", IsNewsletterSubscribed = true, Country = "Canada" }, Array.Empty<int>());

        var edited = await db.Clients.AsNoTracking().SingleAsync(c => c.Id == bob);
        Assert.Equal("alice-678@example.test", edited.Email);
        Assert.NotNull(edited.EmailOptOutAt);
        Assert.Contains("opted out", controller.TempData["Warning"] as string);
    }

    [Fact]
    public void The_three_website_forms_put_a_kept_opt_out_on_a_new_contact_and_a_newsletter_contact_starts_unsubscribed()
    {
        var source = Read(@"src\IPRO.Web\Controllers\PublicWebsiteController.cs");
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(source, @"await KeepEarlierOptOutAsync\(client\);").Count);
        Assert.DoesNotContain("IsNewsletterSubscribed = submissionType == WebsiteLeadTypes.Newsletter", source);
        Assert.Contains("IsNewsletterSubscribed = false,", source);
    }

    // ---- D-4: double opt-in -----------------------------------------------------------------------------

    [Fact]
    public async Task A_newsletter_sign_up_waits_for_the_click_and_one_email_a_day_is_sent()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await AgentAsync(db);
        var clientId = await ClientAsync(db, agentId, "signup-678@example.test");
        var client = await db.Clients.SingleAsync(c => c.Id == clientId);
        var now = DateTime.UtcNow;

        var token = await SignupConfirmations.RequestAsync(db, client, EmailSignupKinds.Newsletter, "site.example.test", now);
        Assert.NotNull(token);
        Assert.Null(await SignupConfirmations.RequestAsync(db, client, EmailSignupKinds.Newsletter, "site.example.test", now.AddHours(2)));
        Assert.False(client.IsNewsletterSubscribed);
        Assert.False(await SignupConfirmations.AlreadyConfirmedAsync(db, client));
        // The token is never stored, only its hash.
        Assert.False(await db.EmailSignupConfirmations.AnyAsync(c => c.TokenHash == token));

        var row = await SignupConfirmations.FindAsync(db, token, now);
        Assert.NotNull(row);
        Assert.Equal(SignupConfirmations.Outcome.Subscribed, await SignupConfirmations.ConfirmAsync(db, Consent(db), row!, now));
        db.ChangeTracker.Clear();
        var confirmed = await db.Clients.AsNoTracking().SingleAsync(c => c.Id == clientId);
        Assert.True(confirmed.IsNewsletterSubscribed);
        Assert.True(await SignupConfirmations.AlreadyConfirmedAsync(db, confirmed));
        Assert.Equal(SignupConfirmations.Outcome.AlreadyConfirmed,
            await SignupConfirmations.ConfirmAsync(db, Consent(db), (await SignupConfirmations.FindAsync(db, token, now))!, now));

        // A week on, an unconfirmed link no longer works.
        var late = await SignupConfirmations.RequestAsync(db, await db.Clients.SingleAsync(c => c.Id == clientId), EmailSignupKinds.DidYouKnow, "site.example.test", now);
        Assert.Null(await SignupConfirmations.FindAsync(db, late, now.AddDays(SignupConfirmations.LinkDays + 1)));
    }

    [Fact]
    public async Task The_click_lifts_an_earlier_opt_out_because_it_is_the_persons_own_later_word()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await AgentAsync(db);
        var clientId = await ClientAsync(db, agentId, "back-678@example.test");
        var consent = Consent(db);
        await consent.SuppressAllAsync(await db.Clients.SingleAsync(c => c.Id == clientId), "link");
        var client = await db.Clients.SingleAsync(c => c.Id == clientId);
        Assert.False(await SignupConfirmations.AlreadyConfirmedAsync(db, client));

        var token = await SignupConfirmations.RequestAsync(db, client, EmailSignupKinds.Newsletter, "site.example.test", DateTime.UtcNow);
        await SignupConfirmations.ConfirmAsync(db, consent, (await SignupConfirmations.FindAsync(db, token, DateTime.UtcNow))!, DateTime.UtcNow);
        db.ChangeTracker.Clear();
        var back = await db.Clients.AsNoTracking().SingleAsync(c => c.Id == clientId);
        Assert.Null(back.EmailOptOutAt);
        Assert.True(back.IsNewsletterSubscribed);
    }

    [Fact]
    public async Task Did_You_Know_articles_are_held_until_the_click_and_then_go_a_few_minutes_apart()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await AgentAsync(db);
        var clientId = await ClientAsync(db, agentId, "dyk-678@example.test");
        var articles = Enumerable.Range(0, 3).Select(i => new Article { AgentUserId = agentId, Title = $"A{i}", Content = "x", IsPublished = true }).ToList();
        db.AddRange(articles);
        await db.SaveChangesAsync();
        var now = DateTime.UtcNow;
        foreach (var a in articles)
            db.DidYouKnowEmailQueueItems.Add(new DidYouKnowEmailQueueItem { ArticleId = a.Id, ClientId = clientId, ScheduledForUtc = now.AddMinutes(-5), Status = DidYouKnowQueueStatuses.AwaitingConfirmation, CreatedAt = now });
        await db.SaveChangesAsync();

        // The dispatch job never picks a held one.
        Assert.Contains("&& q.Status != DidYouKnowQueueStatuses.AwaitingConfirmation", Read(@"src\IPRO.Scheduler\DidYouKnowEmailDispatchJob.cs"));

        var token = await SignupConfirmations.RequestAsync(db, await db.Clients.SingleAsync(c => c.Id == clientId), EmailSignupKinds.DidYouKnow, "site.example.test", now);
        Assert.Equal(SignupConfirmations.Outcome.ArticlesOnTheirWay,
            await SignupConfirmations.ConfirmAsync(db, Consent(db), (await SignupConfirmations.FindAsync(db, token, now))!, now));
        db.ChangeTracker.Clear();
        var items = await db.DidYouKnowEmailQueueItems.AsNoTracking().Where(q => q.ClientId == clientId).OrderBy(q => q.ScheduledForUtc).ToListAsync();
        Assert.All(items, q => Assert.Equal(DidYouKnowQueueStatuses.Queued, q.Status));
        Assert.True(items[0].ScheduledForUtc > now);
        Assert.True(items[2].ScheduledForUtc >= items[0].ScheduledForUtc.AddMinutes(8));
    }

    [Fact]
    public async Task The_confirm_page_asks_and_only_its_button_confirms()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await AgentAsync(db);
        var clientId = await ClientAsync(db, agentId, "page-678@example.test");
        var token = await SignupConfirmations.RequestAsync(db, await db.Clients.SingleAsync(c => c.Id == clientId), EmailSignupKinds.Newsletter, "site.example.test", DateTime.UtcNow);
        db.ChangeTracker.Clear();

        var view = Assert.IsType<ViewResult>(await Preferences(db).ConfirmPage(token));
        Assert.Equal("Confirm", view.ViewName);
        Assert.False(await db.Clients.AsNoTracking().Where(c => c.Id == clientId).Select(c => c.IsNewsletterSubscribed).SingleAsync());

        var pressed = Preferences(db);
        await pressed.Confirm(token);
        Assert.Equal("Subscribed", pressed.ViewBag.Outcome as string);
        Assert.True(await db.Clients.AsNoTracking().Where(c => c.Id == clientId).Select(c => c.IsNewsletterSubscribed).SingleAsync());

        var nonsense = Preferences(db);
        await nonsense.ConfirmPage("not-a-real-token-at-all-000000000");
        Assert.True(nonsense.ViewBag.Invalid as bool?);
    }

    [Fact]
    public async Task Opening_the_unsubscribe_link_asks_and_the_button_unsubscribes()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await AgentAsync(db);
        var clientId = await ClientAsync(db, agentId, "unsub-678@example.test");
        var token = "pref-" + Guid.NewGuid().ToString("N");
        await db.Clients.Where(c => c.Id == clientId).ExecuteUpdateAsync(u => u.SetProperty(c => c.EmailPreferencesToken, token));

        var opened = Preferences(db);
        await opened.Index(token);
        Assert.True(opened.ViewBag.AskToUnsubscribe as bool?);
        Assert.Null(await db.Clients.AsNoTracking().Where(c => c.Id == clientId).Select(c => c.EmailOptOutAt).SingleAsync());

        await Preferences(db).Unsubscribe(token);
        Assert.NotNull(await db.Clients.AsNoTracking().Where(c => c.Id == clientId).Select(c => c.EmailOptOutAt).SingleAsync());
    }

    [Fact]
    public void The_visitor_is_told_to_confirm_from_their_inbox()
    {
        Assert.Contains("SignupThanks.Newsletter", Read(@"src\IPRO.Web\Views\PublicWebsite\_WebsiteLeadForm.cshtml"));
        Assert.Contains("SignupThanks.DidYouKnow", Read(@"src\IPRO.Web\Views\PublicWebsite\_WebsiteLeadForm.cshtml"));
        Assert.Contains("SignupThanks.NewsletterFrench", Read(@"src\IPRO.Web\Views\PublicWebsite\_PublicPopup.cshtml"));
        Assert.Contains("Press the button", SignupThanks.Newsletter);
    }

    // ---- C-2: the setup fee at the switch to an iPro website ---------------------------------------------

    [Fact]
    public async Task Switching_to_an_iPro_website_after_a_sign_up_without_the_fee_charges_it_then_nothing_is_owed()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, package) = await SubscribedAsync(db, setupFee: 200m);
        await OwnWebsitePreference.ChooseAsync(db, agentId, "www.own-678.example.test");
        var paypal = new OrderPayPal();
        var billing = SetupFeeBilling(db, paypal);

        await billing.RecordWaivedSetupFeeAsync(agentId, package, null);
        var quote = await billing.QuoteAsync(agentId);
        Assert.NotNull(quote);
        Assert.Equal(200m, quote!.Fee);
        Assert.Equal(26m, quote.Tax);

        var started = await billing.BeginAsync(agentId, "https://app.example.test/Website/WebsiteSetupFeeReturn", "https://app.example.test/Website/WebsiteSetupFeeCancel");
        Assert.True(started.Success, started.Message);
        Assert.Equal("https://paypal.example.test/approve/ORDER-678", started.ApprovalUrl);
        Assert.Equal(226m, paypal.OrderAmount);

        Assert.Equal(WebsiteSetupFeeOutcome.Paid, await billing.CompleteAsync(agentId, "ORDER-678"));
        db.ChangeTracker.Clear();
        var invoice = await db.Invoices.AsNoTracking().Include(i => i.LineItems).SingleAsync(i => i.AgentUserId == agentId && i.PayPalTransactionId == "ORDER-678");
        Assert.True(invoice.IsPaid);
        Assert.Equal(226m, invoice.Total);
        Assert.Contains(invoice.LineItems, l => l.Description == PayPalBillingService.WebsiteSetupFeeLine && l.Amount == 200m);
        Assert.Null(await billing.QuoteAsync(agentId));

        // A second return for the same order changes nothing and charges nothing.
        Assert.Equal(WebsiteSetupFeeOutcome.NothingOwed, await billing.CompleteAsync(agentId, "ORDER-678"));
        Assert.Equal(1, paypal.Captures);
    }

    [Fact]
    public async Task Without_a_running_subscription_or_with_the_fee_paid_at_sign_up_the_switch_costs_nothing()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, package) = await SubscribedAsync(db, setupFee: 200m, status: BillingStatus.Cancelled);
        await OwnWebsitePreference.ChooseAsync(db, agentId, null);
        var billing = SetupFeeBilling(db, new OrderPayPal());
        await billing.RecordWaivedSetupFeeAsync(agentId, package, null);
        Assert.Null(await billing.QuoteAsync(agentId));   // their next checkout prices the fee itself

        var (paidAgent, _) = await SubscribedAsync(db, setupFee: 200m);
        await OwnWebsitePreference.ChooseAsync(db, paidAgent, null);   // chose it after paying: no checkout left anything out
        Assert.Null(await billing.QuoteAsync(paidAgent));

        Assert.Contains("if (keepsOwnWebsite && !completesScheduledChange) await RecordWaivedSetupFeeAsync(", Read(@"src\IPRO.Billing\PayPalBillingService.cs"));
    }

    // ---- C-3: recurring client invoices -----------------------------------------------------------------

    [Fact]
    public async Task One_advisers_bad_schedules_are_paused_and_every_other_advisers_invoice_is_still_made()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var flooder = await AgentAsync(db);
        var flooderClient = await ClientAsync(db, flooder, "flood-678@example.test");
        var other = await AgentAsync(db);
        var otherClient = await ClientAsync(db, other, "other-678@example.test");
        var today = DateTime.UtcNow.Date;
        var poison = new[]
        {
            Schedule(flooder, flooderClient, new DateTime(1000, 1, 1), 15),
            Schedule(flooder, flooderClient, today.AddDays(-1), 3_000_000),
        };
        var many = Enumerable.Range(0, RecurringScheduleRules.PerAdviserPerRun + 5).Select(_ => Schedule(flooder, flooderClient, today.AddDays(-1), 15)).ToList();
        var theirs = Schedule(other, otherClient, today.AddDays(-1), 15);
        db.AddRange(poison);
        db.AddRange(many);
        db.Add(theirs);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await using (var jobDb = testDb.CreateContext())
        {
            await new RecurringClientInvoiceJob(jobDb, new ClientInvoiceService(new UnitOfWork(jobDb)), NullLogger<RecurringClientInvoiceJob>.Instance).RunAsync();
        }

        Assert.Equal(1, await db.ClientInvoices.CountAsync(i => i.AgentUserId == other));
        Assert.Equal(RecurringScheduleRules.PerAdviserPerRun, await db.ClientInvoices.CountAsync(i => i.AgentUserId == flooder));
        Assert.All(await db.RecurringInvoiceSchedules.AsNoTracking().Where(s => poison.Select(p => p.Id).Contains(s.Id)).ToListAsync(), s => Assert.False(s.IsActive));
    }

    [Theory]
    [InlineData(-400, 15, true, false)]
    [InlineData(-30, 15, false, false)]
    [InlineData(1000, 15, false, false)]
    [InlineData(2500, 15, true, false)]
    [InlineData(0, 366, false, true)]
    [InlineData(0, -1, false, true)]
    public void A_schedules_date_and_days_to_pay_stay_within_bounds(int daysFromToday, int dueInDays, bool dateRefused, bool dueRefused)
    {
        var today = new DateTime(2026, 11, 25);
        Assert.Equal(dateRefused, RecurringScheduleRules.DateProblem(today.AddDays(daysFromToday), today) != null);
        Assert.Equal(dueRefused, RecurringScheduleRules.DueProblem(dueInDays) != null);
    }

    // ---- E-2: gallery pixel cap -------------------------------------------------------------------------

    [Fact]
    public void A_gallery_picture_past_the_pixel_cap_is_refused_from_its_header_alone()
    {
        var huge = Png(12_001, 1);
        Assert.Equal((12_001, 1), GalleryImages.TooLarge(huge));
        Assert.Null(GalleryImages.Process(huge));
        Assert.Contains("12,001 x 1 pixels", GalleryImages.TooLargeText((12_001, 1)));

        var fine = Png(40, 30);
        Assert.Null(GalleryImages.TooLarge(fine));
        Assert.NotNull(GalleryImages.Process(fine));
        Assert.Contains("GalleryImages.TooLarge(original)", Read(@"src\IPRO.Web\Controllers\WebsitePagesController.cs"));
    }

    // ---- E-3: spreadsheet formulas in exports -----------------------------------------------------------

    [Theory]
    [InlineData("=HYPERLINK(\"https://x.example\",\"Open\")", "'=HYPERLINK(\"https://x.example\",\"Open\")")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("+cmd|' /C calc'!A0", "'+cmd|' /C calc'!A0")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("+1 416-555-0100", "+1 416-555-0100")]
    [InlineData("-50.00", "-50.00")]
    [InlineData("Alice", "Alice")]
    [InlineData("", "")]
    public void A_cell_a_spreadsheet_would_run_is_kept_as_text(string value, string expected)
    {
        Assert.Equal(expected, CsvCell.Neutralize(value));
    }

    [Fact]
    public void The_three_exports_use_the_one_neutralizer()
    {
        Assert.Contains("IPRO.Utility.CsvCell.Escape(value)", Read(@"src\IPRO.Web\Controllers\WebsiteLeadsController.cs"));
        Assert.Contains("IPRO.Utility.CsvCell.Escape(value)", Read(@"src\IPRO.Web\Controllers\ClientInvoicesController.cs"));
        Assert.Contains("FirstName    = CsvCell.Neutralize(c.FirstName),", Read(@"src\IPRO.Utility\ContactImporter.cs"));
        Assert.Equal("\"'=1+1,2\"", CsvCell.Escape("=1+1,2"));
    }

    // ---- F-2: a deleted contact forgotten in the history rows ---------------------------------------------

    [Fact]
    public async Task Erasing_a_contact_without_the_bin_forgets_them_in_newsletter_rows_and_deletes_their_enquiries()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, websiteId) = await SiteAsync(db);
        var clientId = await ClientAsync(db, agentId, "forget-678@example.test");
        var keptId = await ClientAsync(db, agentId, "kept-678@example.test");
        var newsletter = new NewsLetter { AgentUserId = agentId, Subject = "News" };
        db.Add(newsletter);
        await db.SaveChangesAsync();
        db.AddRange(
            new NewsLetterRecipient { NewsLetterId = newsletter.Id, ClientId = clientId, Email = "forget-678@example.test", RecipientName = "Forget Me" },
            new NewsLetterRecipient { NewsLetterId = newsletter.Id, ClientId = keptId, Email = "kept-678@example.test", RecipientName = "Kept" },
            new WebsiteLead { AgentUserId = agentId, AgentWebsiteId = websiteId, ClientId = clientId, FirstName = "Forget", LastName = "Me", Email = "forget-678@example.test", Message = "Call me" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await ClientDataEraser.EraseAsync(db, clientId, forgetHistory: true);

        var rows = await db.NewsLetterRecipients.AsNoTracking().Where(r => r.NewsLetterId == newsletter.Id).ToListAsync();
        Assert.DoesNotContain(rows, r => r.Email.Contains("forget-678"));
        Assert.Contains(rows, r => r.RecipientName == ClientDataEraser.ForgottenName && r.Email.EndsWith("@deleted.invalid"));
        Assert.Contains(rows, r => r.Email == "kept-678@example.test" && r.RecipientName == "Kept");
        Assert.False(await db.WebsiteLeads.AnyAsync(l => l.Email == "forget-678@example.test"));
        Assert.Contains("ClientService", Read(@"src\IPRO.Business\Services\ClientService.cs"));
        Assert.Contains("EraseAsync(_db, id, forgetHistory: true)", Read(@"src\IPRO.Business\Services\ClientService.cs"));
    }

    [Fact]
    public async Task A_contact_in_the_bin_keeps_their_history_until_the_30_days_are_over()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await AgentAsync(db);
        var clientId = await ClientAsync(db, agentId, "bin-678@example.test");
        var newsletter = new NewsLetter { AgentUserId = agentId, Subject = "News" };
        db.Add(newsletter);
        await db.SaveChangesAsync();
        db.Add(new NewsLetterRecipient { NewsLetterId = newsletter.Id, ClientId = clientId, Email = "bin-678@example.test", RecipientName = "In Bin" });
        await db.SaveChangesAsync();

        var item = await ClientRecycleBin.DeleteToBinAsync(db, clientId);
        Assert.True(await db.NewsLetterRecipients.AnyAsync(r => r.Email == "bin-678@example.test"));   // a restore can bring it back

        await db.ClientRecycleBinItems.Where(i => i.Id == item.Id).ExecuteUpdateAsync(u => u.SetProperty(i => i.PurgeAfter, DateTime.UtcNow.AddMinutes(-1)));
        db.ChangeTracker.Clear();
        await new ClientRecycleBinPurgeJob(db, new NoBlobs(), NullLogger<ClientRecycleBinPurgeJob>.Instance).RunAsync();
        Assert.False(await db.NewsLetterRecipients.AnyAsync(r => r.Email == "bin-678@example.test"));
        Assert.True(await db.NewsLetterRecipients.AnyAsync(r => r.NewsLetterId == newsletter.Id && r.RecipientName == ClientDataEraser.ForgottenName));
    }

    // ---- F-3: the nightly dump --------------------------------------------------------------------------

    [Fact]
    public void The_dump_escapes_text_the_way_mysqldump_does()
    {
        Assert.Equal("a\\0b\\Zc\\nd\\re\\'f\\\"g\\\\h", DatabaseDump.Escape("a\0b\u001Ac\nd\re'f\"g\\h"));
        Assert.Contains("-e \"source ipro_crm-20260911-061500.sql\"", Read(@"DOCS\14_BACKUP_AND_RELEASE_CHECKLIST.md"));
    }

    // A restore rehearsal with the real mysql client (678, F-3), run by hand: with IPRO_DUMP_REHEARSAL_DIR set, a dump of
    // a test database holding a NUL and a Ctrl-Z is written there, for `mysql ... -e "source <file>"`. Without it,
    // nothing happens (the gate machine is not required to have the client).
    [Fact]
    public async Task A_dump_for_the_restore_rehearsal()
    {
        var dir = Environment.GetEnvironmentVariable("IPRO_DUMP_REHEARSAL_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await AgentAsync(db);
        db.Clients.Add(new Client { AgentUserId = agentId, FirstName = "Nul", LastName = "a\0b\u001Ac'd\"e\\f\ng\rh", Email = "rehearsal-678@example.test", Notes = "utm=a\0b" });
        await db.SaveChangesAsync();
        await using var writer = new StreamWriter(Path.Combine(dir, "rehearsal-678.sql"), false, new UTF8Encoding(false));
        await DatabaseDump.WriteAsync(db, writer);
    }

    // ---- F-4: AI drafting --------------------------------------------------------------------------------

    [Fact]
    public async Task AI_drafting_has_a_topic_limit_and_a_daily_allowance_per_adviser()
    {
        Assert.NotNull(AiDraftAllowance.TopicProblem(""));
        Assert.NotNull(AiDraftAllowance.TopicProblem(new string('x', AiDraftAllowance.MaxTopicLength + 1)));
        Assert.Null(AiDraftAllowance.TopicProblem("Year-end tax tips for small businesses"));

        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await AgentAsync(db);
        var other = await AgentAsync(db);
        var now = DateTime.UtcNow;
        var limit = await AiDraftAllowance.LimitAsync(db);
        Assert.Equal(AiDraftAllowance.DefaultPerDay, limit);
        for (var i = 0; i < limit; i++) Assert.True((await AiDraftAllowance.TryTakeAsync(db, agentId, now)).Allowed);
        var refused = await AiDraftAllowance.TryTakeAsync(db, agentId, now);
        Assert.False(refused.Allowed);
        Assert.Contains($"today's {limit} AI drafts", AiDraftAllowance.UsedUpMessage(refused.Limit));
        Assert.True((await AiDraftAllowance.TryTakeAsync(db, other, now)).Allowed);          // another adviser's day is their own
        Assert.True((await AiDraftAllowance.TryTakeAsync(db, agentId, now.AddDays(1))).Allowed);   // and tomorrow is a new day

        await AiDraftAllowance.FinishAsync(db, other, 1200, 800, now);
        var day = await AiDraftAllowance.DayAsync(db, other, now);
        var usage = await db.AiAdviserDailyUsages.AsNoTracking().SingleAsync(u => u.AgentUserId == other && u.Date == day);
        Assert.Equal(1200, usage.InputTokens);
        Assert.Equal(800, usage.OutputTokens);
        foreach (var file in new[] { "NewsletterController", "ArticlesController", "SocialPostsController" })
        {
            var source = Read($@"src\IPRO.Web\Controllers\{file}.cs");
            Assert.Contains("AiDraftAllowance.TopicProblem(topic)", source);
            Assert.Contains("AiDraftAllowance.TryTakeAsync(_db, AgentId, DateTime.UtcNow)", source);
            Assert.Contains("AiDraftAllowance.FinishAsync(_db, AgentId, result.InputTokens, result.OutputTokens, DateTime.UtcNow)", source);
        }
    }

    // ---- D-2: tracking links on iPro's own address only ---------------------------------------------------

    [Theory]
    [InlineData("app.iproadvisers.com", true)]
    [InlineData("ipro-prod-web.azurewebsites.net", true)]
    [InlineData("localhost", true)]
    [InlineData("bob.247advisers.com", false)]
    [InlineData("www.adviser-own.ca", false)]
    public void An_emails_tracking_links_answer_only_on_the_platforms_own_address(string host, bool answers)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = "https://app.iproadvisers.com" }).Build();
        Assert.Equal(answers, PortalUrlHelper.IsTrackingHost(host, config));
    }

    [Fact]
    public async Task A_signed_click_link_on_another_advisers_domain_is_not_found()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Email:AzureEventWebhookSecret"] = "k-678" }).Build();
        var consent = new EmailConsentService(db, config, NullLogger<EmailConsentService>.Instance, Array.Empty<IUnsubscribeNotifier>());
        var controller = new EmailTrackingController(db, new NewsLetterService(new UnitOfWork(db), consent, db),
            new EmailDeliveryTracker(db, NullLogger<EmailDeliveryTracker>.Instance, consent), config, NullLogger<EmailTrackingController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = TestEmailRecorder.PlatformRequest("www.adviser-own.ca") }
        };
        Assert.IsType<NotFoundResult>(await controller.Click("newsletter", "tok", "https://evil.example/login", "sig"));
        Assert.IsType<NotFoundResult>(await controller.Open("newsletter", "tok"));
    }

    // ---- the Lows ----------------------------------------------------------------------------------------

    [Fact]
    public void A_business_name_never_brings_an_address_into_the_From_name()   // D-7
    {
        Assert.Equal("Interac e-Transfer notifypayments.interac.ca via iPro", AdviserSender.SenderName("Interac e-Transfer <notify@payments.interac.ca>"));
        Assert.Equal(AdviserSender.MaxNameLength + " via iPro".Length, AdviserSender.SenderName(new string('A', 200)).Length);
        Assert.Equal("Global Business Solution via iPro", AdviserSender.SenderName("Global Business Solution"));
    }

    [Theory]   // C-6
    [InlineData("Canada", "Ontario", false, false)]
    [InlineData("Canada", "ON", false, false)]
    [InlineData("Canada", "XX", true, false)]
    [InlineData("Canada", "", false, true)]
    [InlineData("Other", "Ontario", false, true)]
    [InlineData("United States", "Texas", false, false)]
    [InlineData("", "Ontario", false, true)]
    public void A_Canadian_address_needs_a_real_province_and_an_odd_pair_is_flagged(string country, string province, bool refused, bool flagged)
    {
        Assert.Equal(refused, TaxAddress.Problem(country, province) != null);
        Assert.Equal(flagged, TaxAddress.Suspicious(country, province));
    }

    [Fact]
    public void Checkout_the_profile_and_sign_up_check_the_tax_address()   // C-6
    {
        Assert.Contains("TaxAddress.Problem(taxAgent?.Country, taxAgent?.Province)", Read(@"src\IPRO.Billing\PayPalBillingService.cs"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(Read(@"src\IPRO.Web\Controllers\AccountController.cs"), @"IPRO\.Billing\.TaxAddress\.Problem\(model\.Country, model\.Province\)").Count);
    }

    [Theory]   // E-4
    [InlineData("app.iproadvisers.com", "max-age=31536000; includeSubDomains")]
    [InlineData("bob.247advisers.com", "max-age=31536000; includeSubDomains")]
    [InlineData("iproadvisers.com", "max-age=31536000")]
    [InlineData("www.adviser-own.ca", "max-age=31536000")]
    [InlineData("firm.on.ca", "max-age=31536000")]
    public void Subdomains_are_pinned_only_on_names_that_are_all_iPros(string host, string expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = "https://app.iproadvisers.com" }).Build();
        Assert.Equal(expected, IPRO.Web.Middleware.SecurityHeadersMiddlewareExtensions.HstsFor(host, config));
    }

    [Fact]
    public void Signing_out_is_a_button_with_its_token_in_both_apps()   // A-4
    {
        static void Check(Type controller)
        {
            var post = controller.GetMethods().Single(m => m.Name == "Logout");
            Assert.NotNull(post.GetCustomAttributes(typeof(HttpPostAttribute), false).SingleOrDefault());
            Assert.NotNull(post.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), false).SingleOrDefault());
        }
        Check(typeof(AccountController));
        Check(typeof(IPRO.Admin.Controllers.AdminController));
        Assert.Contains("<form method=\"post\" action=\"/portal/Account/Logout\"", Read(@"src\IPRO.Web\Views\Shared\_Layout.cshtml"));
        Assert.Contains("<form method=\"post\" action=\"/Admin/Logout\"", Read(@"src\IPRO.Admin\Views\Shared\_Layout.cshtml"));
    }

    [Fact]
    public void Whether_an_email_has_an_account_is_said_only_after_the_password_proof()   // A-5
    {
        var source = Read(@"src\IPRO.Web\Controllers\AccountController.cs");
        var proof = source.IndexOf("var proofError = await EmailChangeProofErrorAsync(agent, model.CurrentPassword, model.TwoStepCode);", StringComparison.Ordinal);
        var taken = source.IndexOf("ModelState.AddModelError(\"\", \"Another account already uses that email address.\");", StringComparison.Ordinal);
        Assert.True(proof > 0 && taken > proof);
    }

    [Fact]
    public async Task A_client_session_ends_when_their_email_changes_and_changing_it_takes_the_password()   // A-2
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await AgentAsync(db);
        var clientId = await ClientAsync(db, agentId, "portal-678@example.test");
        var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<Client>();
        var hash = hasher.HashPassword(new Client(), "Portal-Pass-678");
        await db.Clients.Where(c => c.Id == clientId).ExecuteUpdateAsync(u => u.SetProperty(c => c.PortalPasswordHash, hash));
        db.ChangeTracker.Clear();

        Assert.Equal(ClientPortalCookieRevalidator.Verdict.Ok,
            await ClientPortalCookieRevalidator.EvaluateAsync(db, clientId.ToString(), agentId.ToString(), "Portal-678@Example.test"));

        var email = new TestEmailRecorder();
        var refused = PortalProfile(db, clientId, email);
        Assert.IsType<ViewResult>(await refused.Index(new PortalProfileViewModel { FirstName = "Re", LastName = "Cipient", Email = "taken-678@example.test" }));
        Assert.Equal("portal-678@example.test", await db.Clients.AsNoTracking().Where(c => c.Id == clientId).Select(c => c.Email).SingleAsync());

        await PortalProfile(db, clientId, email).Index(new PortalProfileViewModel { FirstName = "Re", LastName = "Cipient", Email = "new-678@example.test", CurrentPassword = "Portal-Pass-678" });
        Assert.Equal("new-678@example.test", await db.Clients.AsNoTracking().Where(c => c.Id == clientId).Select(c => c.Email).SingleAsync());
        Assert.Equal("portal-678@example.test", Assert.Single(email.Sent).To);   // the old address is told
        Assert.Equal(ClientPortalCookieRevalidator.Verdict.Reject,
            await ClientPortalCookieRevalidator.EvaluateAsync(db, clientId.ToString(), agentId.ToString(), "portal-678@example.test"));
    }

    [Fact]
    public void Support_cannot_set_a_promotion_code_and_the_scrub_covers_the_new_names()   // B-2, F-8, C-4, D-5
    {
        var agents = Read(@"src\IPRO.Admin\Controllers\AgentsController.cs");
        Assert.Contains("blockedFields.Add(\"promotion code\");", agents);
        Assert.Contains("model.PromotionCode = agent.PromotionCode;", agents);

        var scrubbed = SensitiveTelemetry.ScrubText("/GoogleCalendar/Callback?code=abc123&state=xyz&t=conf-token&search=Alice&trialCode=TRIAL1");
        foreach (var secret in new[] { "abc123", "xyz", "conf-token", "Alice", "TRIAL1" }) Assert.DoesNotContain(secret, scrubbed);

        Assert.Contains("renewals[^1].IssuedAt >= renewals[0].IssuedAt.AddDays(ReferralProgram.RenewalMinDays)", Read(@"src\IPRO.Billing\ReferralProgram.cs"));
        Assert.Contains("_correlation.SuppressUntrackedAsync(clientId, agentId, hardBounce, \"ses\")", Read(@"src\IPRO.Web\Controllers\SesEmailEventsController.cs"));
        Assert.Equal("123", AdviserSender.ForClient(123)[AdviserSender.ClientTag]);
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative));
    }

    private static AgentUser NewAgent() => new()
    {
        UserName = $"b678-{Guid.NewGuid():N}"[..20],
        Email = $"b678-{Guid.NewGuid():N}"[..14] + "@example.test",
        FirstName = "Batch", LastName = "One", CompanyName = "Batch One Ltd",
        DomainName = $"b678-{Guid.NewGuid():N}"[..24],
        Country = "Canada", Province = "Ontario",
        TrialEndsAt = DateTime.UtcNow.AddDays(10)
    };

    private static async Task<int> AgentAsync(IPRODbContext db)
    {
        var agent = NewAgent();
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static async Task<int> ClientAsync(IPRODbContext db, int agentId, string email)
    {
        var client = new Client { AgentUserId = agentId, FirstName = "Re", LastName = "Cipient", Email = email, Country = "Canada" };
        db.Add(client);
        await db.SaveChangesAsync();
        return client.Id;
    }

    private static async Task<(int AgentId, int WebsiteId)> SiteAsync(IPRODbContext db)
    {
        var agentId = await AgentAsync(db);
        var template = new WebsiteTemplate { TemplateKey = $"tk-{Guid.NewGuid():N}"[..16], Name = "Test", BusinessType = "All" };
        db.Add(template);
        await db.SaveChangesAsync();
        var website = new AgentWebsite { AgentUserId = agentId, TemplateId = template.Id, SiteTitle = "Site", IsPublished = true, CustomDomain = $"s678-{Guid.NewGuid():N}"[..14] + ".example.test" };
        db.Add(website);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (agentId, website.Id);
    }

    private static async Task<(int AgentId, BillingRule Package)> SubscribedAsync(IPRODbContext db, decimal setupFee, BillingStatus status = BillingStatus.Active)
    {
        if (!await db.ProvinceTaxRates.AnyAsync(t => t.ProvinceCode == "ON"))
            db.Add(new ProvinceTaxRate { ProvinceCode = "ON", ProvinceName = "Ontario", Rate = 0.13m, TaxLabel = "ON 13% HST", IsActive = true });
        var package = new BillingRule { PackageName = $"P678-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m, AnnualPrice = 600m, SetupFee = setupFee };
        db.Add(package);
        var agent = NewAgent();
        db.Add(agent);
        await db.SaveChangesAsync();
        db.Add(new IPRO.Entities.Billing { AgentUserId = agent.Id, BillingRuleId = package.Id, Amount = 60m, Status = status, StartDate = DateTime.UtcNow.AddDays(-5), PayPalSubscriptionId = $"I-{Guid.NewGuid():N}"[..14] });
        await db.SaveChangesAsync();
        return (agent.Id, package);
    }

    private static RecurringInvoiceSchedule Schedule(int agentId, int clientId, DateTime next, int dueInDays) => new()
    {
        AgentUserId = agentId, ClientId = clientId, NextRunDate = next, DueInDays = dueInDays, IsActive = true,
        LineItems = new List<RecurringInvoiceLineItem> { new() { Description = "Bookkeeping", Quantity = 1, UnitPrice = 100m, SortOrder = 10 } }
    };

    private static EmailConsentService Consent(IPRODbContext db) =>
        new(db, new ConfigurationBuilder().Build(), NullLogger<EmailConsentService>.Instance, Array.Empty<IUnsubscribeNotifier>());

    private static ClaimsPrincipal Principal(int agentId, bool teamMember)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, agentId.ToString()) };
        if (teamMember) claims.Add(new Claim("TeamMemberId", "99"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static PaymentsController Payments(IPRODbContext db, int agentId, TestEmailRecorder email, bool teamMember = false)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = "https://app.example.test" }).Build();
        var controller = new PaymentsController(db, new NoStripe(), new GrantAll(), config, new EphemeralDataProtectionProvider(), email, NullLogger<PaymentsController>.Instance);
        var ctx = new DefaultHttpContext { User = Principal(agentId, teamMember) };
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        controller.TempData = new TempDataDictionary(ctx, new NoTempData());
        return controller;
    }

    private static ClientsController Clients(IPRODbContext db, IEmailConsentService consent, int agentId)
    {
        var controller = new ClientsController(null!, null!, new UnitOfWork(db), db, null!, null!, null!, null!,
            new EphemeralDataProtectionProvider(), new ConfigurationBuilder().Build(), consent);
        var ctx = new DefaultHttpContext { User = Principal(agentId, false) };
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        controller.TempData = new TempDataDictionary(ctx, new NoTempData());
        return controller;
    }

    private static EmailPreferencesController Preferences(IPRODbContext db)
    {
        var controller = new EmailPreferencesController(db, new TestEmailRecorder(), new ConfigurationBuilder().Build(),
            NullLogger<EmailPreferencesController>.Instance, Consent(db));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static ClientPortalProfileController PortalProfile(IPRODbContext db, int clientId, TestEmailRecorder email)
    {
        var controller = new ClientPortalProfileController(db, Consent(db), new Microsoft.AspNetCore.Identity.PasswordHasher<Client>(), email);
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, clientId.ToString()) }, "ClientPortal")) };
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        controller.TempData = new TempDataDictionary(ctx, new NoTempData());
        return controller;
    }

    private static PayPalBillingService SetupFeeBilling(IPRODbContext db, OrderPayPal paypal) =>
        new(new UnitOfWork(db), db, paypal, new TestEmailRecorder(),
            Options.Create(new PayPalSettings { ClientId = "client-678", ClientSecret = "secret-678" }),
            new ConfigurationBuilder().Build(), NullLogger<PayPalBillingService>.Instance);

    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        bitmap.Erase(SkiaSharp.SKColors.SteelBlue);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // PayPal's order API as the setup fee uses it: a token, an order with its approval link, and a capture.
    private sealed class OrderPayPal : IHttpClientFactory
    {
        public decimal OrderAmount;
        public int Captures;
        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler : HttpMessageHandler
        {
            private readonly OrderPayPal _owner;
            public Handler(OrderPayPal owner) => _owner = owner;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var url = request.RequestUri!.AbsoluteUri;
                if (url.Contains("/v1/oauth2/token")) return Json("{\"access_token\":\"token-678\",\"expires_in\":32400}");
                if (request.Method == HttpMethod.Post && url.EndsWith("/v2/checkout/orders"))
                {
                    var body = await request.Content!.ReadAsStringAsync(ct);
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    _owner.OrderAmount = decimal.Parse(doc.RootElement.GetProperty("purchase_units")[0].GetProperty("amount").GetProperty("value").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                    return Json("{\"id\":\"ORDER-678\",\"links\":[{\"rel\":\"approve\",\"href\":\"https://paypal.example.test/approve/ORDER-678\"}]}");
                }
                if (request.Method == HttpMethod.Post && url.EndsWith("/v2/checkout/orders/ORDER-678/capture"))
                {
                    Interlocked.Increment(ref _owner.Captures);
                    return Json("{\"status\":\"COMPLETED\",\"purchase_units\":[{\"payments\":{\"captures\":[{\"status\":\"COMPLETED\"}]}}]}");
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            private static HttpResponseMessage Json(string body) =>
                new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class NoStripe : IPRO.Utility.IStripeConnectService
    {
        public bool IsConfigured => false;
        public string BuildAuthorizationUrl(string redirectUri, string state) => throw new NotSupportedException();
        public Task<IPRO.Utility.StripeConnectedAccount> ExchangeCodeAsync(string code) => throw new NotSupportedException();
        public Task DeauthorizeAsync(string accountId) => throw new NotSupportedException();
    }

    private sealed class GrantAll : IPRO.Business.Interfaces.IPackageEntitlementService
    {
        public Task<IPRO.Business.Interfaces.PackageFeatureAccess> GetAccessAsync(int agentId, string featureCode) =>
            Task.FromResult(new IPRO.Business.Interfaces.PackageFeatureAccess { FeatureCode = featureCode, IsIncluded = true });
        public Task<bool> HasAccessAsync(int agentId, string featureCode) => Task.FromResult(true);
        public Task<Dictionary<int, bool>> HasAccessBulkAsync(IEnumerable<int> agentIds, string featureCode) =>
            Task.FromResult(agentIds.Distinct().ToDictionary(a => a, _ => true));
        public Task<bool> IsAccessGatedAsync(int agentId) => Task.FromResult(false);
    }

    private sealed class NoTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class NoBlobs : IBlobStorageService
    {
        public Task<string> UploadAsync(Stream fileStream, string fileName, string containerName, string contentType, bool isPrivate) => Task.FromResult(string.Empty);
        public Task<bool> DeleteAsync(string blobUrl) => Task.FromResult(true);
        public Task<Stream?> DownloadAsync(string blobUrl) => Task.FromResult<Stream?>(null);
        public Task<List<string>> ListAsync(string containerName) => Task.FromResult(new List<string>());
        public string GetPublicUrl(string containerName, string fileName) => string.Empty;
        public Task EnsureContainerAccessAsync(string containerName, bool isPrivate) => Task.CompletedTask;
    }
}
