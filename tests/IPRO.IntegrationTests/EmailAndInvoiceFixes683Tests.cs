using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Scheduler;
using IPRO.Web.Controllers;
using IPRO.Web.Infrastructure;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// 683, the whole-code audit of 2026-11-26: seven Medium findings, each an email or an invoice that goes wrong for
// a customer's client.
//   W05-01  a newsletter (or poll) picked for a group or one client went to every subscriber
//   W05-02  "Newsletter sent." (and "E-card sent to N") whatever had happened
//   W05-06  deleting a campaign step moved everyone part-way through onto the wrong step
//   W02-06  an unsubscribe was forgotten for the old address when a contact's address was changed
//   W04-02  "Resend email" turned an approved estimate, or a void invoice, back into Sent
//   W04-03  resuming a paused recurring invoice billed every missed period at once
//   W04-04  the QuickBooks file left the tax out
// and the owner's own find of 2026-11-27: the tick boxes of the e-card and letter client lists were cut off.
public class EmailAndInvoiceFixes683Tests
{
    // ---------------------------------------------------------------- W05-01, W05-02

    [Theory]
    [InlineData(NewsLetterAudienceType.AllSubscribers, null, null, null)]
    [InlineData(NewsLetterAudienceType.AllSubscribers, 7, null, "AudienceType")]      // a group picked, "everyone" selected
    [InlineData(NewsLetterAudienceType.AllSubscribers, null, 9, "AudienceType")]      // one client picked, "everyone" selected
    [InlineData(NewsLetterAudienceType.AccountType, 7, null, null)]
    [InlineData(NewsLetterAudienceType.AccountType, null, null, "ClientCategoryId")]
    [InlineData(NewsLetterAudienceType.AccountType, 7, 9, "AudienceType")]
    [InlineData(NewsLetterAudienceType.IndividualClient, null, 9, null)]
    [InlineData(NewsLetterAudienceType.IndividualClient, null, null, "ClientId")]
    [InlineData(NewsLetterAudienceType.IndividualClient, 7, 9, "AudienceType")]
    [InlineData(NewsLetterAudienceType.SelectedClients, null, null, "AudienceType")]  // no screen offers it; it meant everyone
    public void What_is_posted_must_name_one_audience(NewsLetterAudienceType audience, int? group, int? client, string? refusedOn)
    {
        var problems = NewsletterSendRules.AudienceProblems(audience, group, client).ToList();
        if (refusedOn == null) Assert.Empty(problems);
        else Assert.Equal(refusedOn, Assert.Single(problems).Field);
    }

    [Fact]
    public void What_the_page_says_after_a_send_is_what_happened()
    {
        static NewsLetterSend Send(NewsLetterSendStatus status, int sent, int total) => new() { Status = status, TotalSent = sent, TotalRecipients = total };

        Assert.Equal((true, "Newsletter sent to 1 subscriber."), NewsletterSendRules.Outcome(Send(NewsLetterSendStatus.Sent, 1, 1)));
        Assert.Equal((true, "Newsletter sent to 40 subscribers."), NewsletterSendRules.Outcome(Send(NewsLetterSendStatus.Sent, 40, 40)));
        var partly = NewsletterSendRules.Outcome(Send(NewsLetterSendStatus.Sent, 38, 40));
        Assert.True(partly.Ok);
        Assert.StartsWith("Newsletter sent to 38 of 40 subscribers;", partly.Text);

        var nobody = NewsletterSendRules.Outcome(Send(NewsLetterSendStatus.Cancelled, 0, 0));
        Assert.False(nobody.Ok);
        Assert.StartsWith("Nobody was emailed: no one in that audience is on the newsletter list.", nobody.Text);
        Assert.False(NewsletterSendRules.Outcome(Send(NewsLetterSendStatus.Cancelled, 0, 3)).Ok);
        Assert.False(NewsletterSendRules.Outcome(Send(NewsLetterSendStatus.Failed, 0, 0)).Ok);
        Assert.False(NewsletterSendRules.Outcome(null).Ok);

        // Paused part-way (it goes back to Scheduled and resumes by itself): said as started, never as sent.
        var started = NewsletterSendRules.Outcome(Send(NewsLetterSendStatus.Scheduled, 100, 1000));
        Assert.True(started.Ok);
        Assert.StartsWith("Sending has started: 100 of 1000 sent so far.", started.Text);

        // E-cards, letters and polls: the same from their own records.
        Assert.Equal((true, "E-card sent to 3 client(s)."), NewsletterSendRules.Outcome("E-card", "Sent", 3, 3));
        Assert.StartsWith("E-card sent to 2 of 3 client(s);", NewsletterSendRules.Outcome("E-card", "Sent", 2, 3).Text);
        Assert.False(NewsletterSendRules.Outcome("Letter", "Sent", 0, 3).Ok);
        Assert.False(NewsletterSendRules.Outcome("Letter", "Failed", 0, 3).Ok);
        Assert.Equal((false, "Nobody was emailed: the poll had no one it could go to in that audience."), NewsletterSendRules.Outcome("Poll", "Cancelled", 0, 0));
        Assert.StartsWith("Sending has started:", NewsletterSendRules.Outcome("Letter", "Sending", 1, 3).Text);
    }

    [Fact]
    public async Task A_newsletter_picked_for_one_client_no_longer_goes_to_everyone()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var listed = Enumerable.Range(0, 4).Select(i => Person(agentId, $"Listed{i}", onNewsletterList: true)).ToList();
        var notListed = Person(agentId, "NotListed", onNewsletterList: false);
        db.Clients.AddRange(listed);
        db.Clients.Add(notListed);
        var newsletter = new NewsLetter { AgentUserId = agentId, Subject = "A note for you", HtmlBody = "<p>Hello</p>" };
        db.NewsLetters.Add(newsletter);
        await db.SaveChangesAsync();
        var email = new RecordingEmail();

        // The page as it was used: one client picked in the list, "All newsletter subscribers" still selected.
        var first = Newsletters(db, agentId, email);
        var refused = await first.Send(new NewsLetterSendViewModel
        {
            NewsLetterId = newsletter.Id, AudienceType = NewsLetterAudienceType.AllSubscribers, ClientId = listed[0].Id, SendNow = true
        });
        Assert.IsType<ViewResult>(refused);
        Assert.Contains("\"All newsletter subscribers\" is selected", first.ModelState[NewsletterSendRules.AudienceField]!.Errors.Single().ErrorMessage);
        Assert.Empty(email.To);
        Assert.False(await db.NewsLetterSends.AnyAsync());

        // A client who is not on the newsletter list: said before anything is made. It used to say "Newsletter sent."
        var second = Newsletters(db, agentId, email);
        Assert.IsType<ViewResult>(await second.Send(new NewsLetterSendViewModel
        {
            NewsLetterId = newsletter.Id, AudienceType = NewsLetterAudienceType.IndividualClient, ClientId = notListed.Id, SendNow = true
        }));
        Assert.Equal(NewsletterSendRules.Nobody, second.ModelState[NewsletterSendRules.AudienceField]!.Errors.Single().ErrorMessage);
        Assert.Empty(email.To);
        Assert.False(await db.NewsLetterSends.AnyAsync());

        // The one client, chosen with its button: that client only, and the page says so.
        var third = Newsletters(db, agentId, email);
        Assert.IsType<RedirectToActionResult>(await third.Send(new NewsLetterSendViewModel
        {
            NewsLetterId = newsletter.Id, AudienceType = NewsLetterAudienceType.IndividualClient, ClientId = listed[0].Id, SendNow = true
        }));
        Assert.Equal(new[] { listed[0].Email }, email.To);
        Assert.Equal("Newsletter sent to 1 subscriber.", third.TempData["Success"]);

        // The page offers only who can be reached.
        var page = Newsletters(db, agentId, email);
        Assert.IsType<ViewResult>(await page.Send(newsletter.Id));
        var offered = Assert.IsType<HashSet<int>>((object)page.ViewData["NewsletterClientIds"]!);
        Assert.Equal(listed.Select(c => c.Id).OrderBy(i => i).ToArray(), offered.OrderBy(i => i).ToArray());
    }

    [Fact]
    public async Task A_send_whose_audience_the_dispatcher_cannot_resolve_reaches_nobody()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        db.Clients.AddRange(Enumerable.Range(0, 3).Select(i => Person(agentId, $"Listed{i}", onNewsletterList: true)));
        var newsletter = new NewsLetter { AgentUserId = agentId, Subject = "S", HtmlBody = "<p>x</p>" };
        db.NewsLetters.Add(newsletter);
        await db.SaveChangesAsync();
        // "Selected clients" has no screen and no list of clients: the dispatcher read it as everyone.
        var send = new NewsLetterSend
        {
            NewsLetterId = newsletter.Id, AgentUserId = agentId, AudienceType = NewsLetterAudienceType.SelectedClients,
            Status = NewsLetterSendStatus.Scheduled, ScheduledAt = DateTime.UtcNow.AddMinutes(-1)
        };
        db.NewsLetterSends.Add(send);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var email = new RecordingEmail();
        await new NewsLetterDispatcher(new UnitOfWork(db), db, email, new ConfigurationBuilder().Build(), NullLogger<NewsLetterDispatcher>.Instance).DispatchSendAsync(send.Id);

        Assert.Empty(email.To);
        var after = await db.NewsLetterSends.AsNoTracking().SingleAsync(s => s.Id == send.Id);
        Assert.Equal(NewsLetterSendStatus.Failed, after.Status);
        Assert.Equal(0, after.TotalSent);

        foreach (var dispatcher in new[] { @"src\IPRO.Email\NewsLetterDispatcher.cs", @"src\IPRO.Email\PollDispatcher.cs" })
        {
            var source = Read(dispatcher);
            Assert.Contains("NewsLetterAudienceType.AllSubscribers => query,", source);
            Assert.DoesNotContain("            _ => query", source);
        }
    }

    [Fact]
    public void The_send_pages_tie_each_list_to_its_button_and_say_who_it_goes_to()
    {
        foreach (var view in new[] { Read(@"src\IPRO.Web\Views\Newsletter\Send.cshtml"), Read(@"src\IPRO.Web\Views\Polls\Send.cshtml") })
        {
            Assert.Contains("group.addEventListener('change', () => { if (group.value) audType.checked = true; syncAudience(); });", view);
            Assert.Contains("client.addEventListener('change', () => { if (client.value) audClient.checked = true; syncAudience(); });", view);
            Assert.Contains("if (!audType.checked) group.value = '';", view);
            Assert.Contains("<strong id=\"audienceSummary\">All newsletter subscribers", view);
            Assert.Contains("<span asp-validation-for=\"AudienceType\"", view);
        }
        var newsletter = Read(@"src\IPRO.Web\Views\Newsletter\Send.cshtml");
        Assert.Contains("<option value=\"@client.Id\" disabled=\"@(!listed)\">", newsletter);
        Assert.Contains("<option value=\"@type.Id\" disabled=\"@(inGroup == 0)\">", newsletter);

        Assert.Contains("NewsletterSendRules.AudienceProblems(model.AudienceType, model.ClientCategoryId, model.ClientId)", Read(@"src\IPRO.Web\Controllers\PollsController.cs"));
        Assert.Contains("NewsletterSendRules.Outcome(\"Poll\",", Read(@"src\IPRO.Web\Controllers\PollsController.cs"));
        Assert.Contains("NewsletterSendRules.Outcome(\"E-card\",", Read(@"src\IPRO.Web\Controllers\ECardsController.cs"));
        Assert.Contains("NewsletterSendRules.Outcome(\"Letter\",", Read(@"src\IPRO.Web\Controllers\ELettersController.cs"));
        Assert.DoesNotContain("\"Newsletter sent.\"", Read(@"src\IPRO.Web\Controllers\NewsletterController.cs"));
        Assert.DoesNotContain("\"Poll sent.\"", Read(@"src\IPRO.Web\Controllers\PollsController.cs"));
    }

    // The owner, 2026-11-27, on the e-card page and then the letter page: "The check box is not fully visible".
    [Fact]
    public void The_client_lists_leave_room_for_their_tick_boxes()
    {
        foreach (var view in new[] { Read(@"src\IPRO.Web\Views\ECards\Create.cshtml"), Read(@"src\IPRO.Web\Views\ELetters\Create.cshtml") })
        {
            Assert.Contains(".client-row { padding: 6px 8px 6px calc(1.5em + 8px); border-radius: 6px; }", view);
            Assert.DoesNotContain(".client-row { padding: 6px 8px; border-radius: 6px; }", view);
            Assert.Contains("<div class=\"form-check client-row\"", view);
        }
    }

    // ---------------------------------------------------------------- W05-06

    [Fact]
    public async Task Deleting_a_campaign_step_keeps_everyone_on_the_step_they_were_waiting_for()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var people = Enumerable.Range(0, 5).Select(i => Person(agentId, $"P{i}", onNewsletterList: true)).ToList();
        db.Clients.AddRange(people);
        var campaign = new DripCampaign { AgentUserId = agentId, Name = "Welcome series" };
        db.Add(campaign);
        await db.SaveChangesAsync();
        var a = new DripCampaignStep { DripCampaignId = campaign.Id, Subject = "A", HtmlBody = "<p>a</p>", SortOrder = 10 };
        var b = new DripCampaignStep { DripCampaignId = campaign.Id, Subject = "B", HtmlBody = "<p>b</p>", SortOrder = 20 };
        var c = new DripCampaignStep { DripCampaignId = campaign.Id, Subject = "C", HtmlBody = "<p>c</p>", SortOrder = 30 };
        db.AddRange(a, b, c);
        DripCampaignEnrollment Enrolled(int person, int next, DripCampaignEnrollmentStatus status = DripCampaignEnrollmentStatus.Active) =>
            new() { AgentUserId = agentId, DripCampaignId = campaign.Id, ClientId = people[person].Id, NextStepIndex = next, Status = status, NextSendAt = DateTime.UtcNow.AddDays(3) };
        var waitsForA = Enrolled(0, 0);
        var waitsForB = Enrolled(1, 1);
        var waitsForC = Enrolled(2, 2);
        var finished = Enrolled(3, 3, DripCampaignEnrollmentStatus.Completed);
        var stopped = Enrolled(4, 2, DripCampaignEnrollmentStatus.Cancelled);
        db.AddRange(waitsForA, waitsForB, waitsForC, finished, stopped);
        await db.SaveChangesAsync();

        // Delete A (to rewrite it). Before: the client waiting for B was now pointing at C and never got B.
        var controller = Campaigns(db, agentId);
        Assert.IsType<RedirectToActionResult>(await controller.DeleteStep(campaign.Id, a.Id));
        Assert.StartsWith("Campaign step removed. 2 clients part-way through this series keep their place", (string)controller.TempData["Success"]!);

        db.ChangeTracker.Clear();
        async Task<int> NextOf(DripCampaignEnrollment e) => (await db.DripCampaignEnrollments.AsNoTracking().SingleAsync(x => x.Id == e.Id)).NextStepIndex;
        var remaining = await db.DripCampaignSteps.AsNoTracking().Where(s => s.DripCampaignId == campaign.Id).OrderBy(s => s.SortOrder).Select(s => s.Subject).ToListAsync();
        Assert.Equal(new[] { "B", "C" }, remaining);
        Assert.Equal("B", remaining[await NextOf(waitsForA)]);    // the deleted step's own waiter gets the one after it
        Assert.Equal("B", remaining[await NextOf(waitsForB)]);
        Assert.Equal("C", remaining[await NextOf(waitsForC)]);
        Assert.Equal(3, await NextOf(finished));                    // a finished or cancelled series is left as it was
        Assert.Equal(2, await NextOf(stopped));

        // Delete the last step: the one waiting for it is now past the end, which the job reads as complete.
        var again = Campaigns(db, agentId);
        Assert.IsType<RedirectToActionResult>(await again.DeleteStep(campaign.Id, c.Id));
        Assert.Equal("Campaign step removed.", again.TempData["Success"]);
        db.ChangeTracker.Clear();
        Assert.Equal(1, await NextOf(waitsForC));
        Assert.Equal(0, await NextOf(waitsForB));
    }

    // ---------------------------------------------------------------- W02-06

    [Fact]
    public async Task An_unsubscribe_is_remembered_for_the_address_it_was_given_at_when_the_address_is_replaced()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var optedOutAt = new DateTime(2026, 10, 2, 14, 0, 0, DateTimeKind.Utc);

        Assert.Empty(await ContactOptOuts.ForAgentAsync(db, agentId));
        await ContactOptOuts.RememberAddressAsync(db, agentId, "A.Chen@Old.example", optedOutAt, "preferences-page");
        await ContactOptOuts.RememberAddressAsync(db, agentId, "  ", optedOutAt, "preferences-page");   // no address: nothing to remember
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // The old list is imported again months later: the same address comes in opted out, not clean.
        var kept = await ContactOptOuts.ForAgentAsync(db, agentId);
        Assert.Single(kept);
        var reimported = new Client { AgentUserId = agentId, FirstName = "Ava", LastName = "Chen", Email = "a.chen@old.example", IsNewsletterSubscribed = true };
        Assert.True(ContactOptOuts.Apply(reimported, kept));
        Assert.Equal(optedOutAt, reimported.EmailOptOutAt);
        // Another adviser's list is not touched by it.
        var otherAgent = await SeedAgentAsync(db);
        Assert.False(ContactOptOuts.Apply(new Client { AgentUserId = otherAgent, Email = "a.chen@old.example" }, await ContactOptOuts.ForAgentAsync(db, otherAgent)));

        // Both places an address is replaced remember it, and only a person's own "stop" (a bounce is the address's).
        var edit = Read(@"src\IPRO.Web\Controllers\ClientsController.cs");
        Assert.Contains("if (addressReplaced && client.EmailOptOutAt is { } optedOutAt && EmailOptOut.ReasonOf(client) != EmailOptOutReason.Bounced)", edit);
        Assert.Contains("await IPRO.Business.Services.ContactOptOuts.RememberAddressAsync(_db, AgentId, client.Email, optedOutAt, client.EmailOptOutSource);", edit);
        Assert.True(edit.IndexOf("ContactOptOuts.RememberAddressAsync(_db, AgentId", StringComparison.Ordinal) < edit.IndexOf("        ApplyClientFields(client, model);\r\n        // 678", StringComparison.Ordinal)
                    || edit.IndexOf("ContactOptOuts.RememberAddressAsync(_db, AgentId", StringComparison.Ordinal) < edit.IndexOf("        ApplyClientFields(client, model);\n        // 678", StringComparison.Ordinal),
            "the old address is remembered before the new one is written");
        Assert.Contains("ContactOptOuts.RememberAddressAsync(_db, client.AgentUserId, client.Email, optedOutAt, client.EmailOptOutSource);", Read(@"src\IPRO.Web\Controllers\ClientPortalProfileController.cs"));
    }

    // ---------------------------------------------------------------- W04-02

    [Fact]
    public async Task Resend_email_is_a_copy_and_a_void_document_is_not_emailed()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var client = Person(agentId, "Ava", onNewsletterList: false);
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        ClientInvoice Document(string number, ClientInvoiceDocumentType type, ClientInvoiceStatus status) => new()
        {
            AgentUserId = agentId, ClientId = client.Id, DocumentType = type, Status = status, DocumentNumber = number, ViewToken = Guid.NewGuid().ToString("N"),
            IssueDate = new DateTime(2026, 11, 20), DueDate = new DateTime(2026, 12, 5), SubTotal = 100m, Total = 100m, Currency = "CAD"
        };
        var approved = Document("EST-1", ClientInvoiceDocumentType.Estimate, ClientInvoiceStatus.Approved);
        var declined = Document("EST-2", ClientInvoiceDocumentType.Estimate, ClientInvoiceStatus.Declined);
        var voided = Document("INV-3", ClientInvoiceDocumentType.Invoice, ClientInvoiceStatus.Void);
        var draft = Document("INV-4", ClientInvoiceDocumentType.Invoice, ClientInvoiceStatus.Draft);
        var paid = Document("INV-5", ClientInvoiceDocumentType.Invoice, ClientInvoiceStatus.Paid);
        db.ClientInvoices.AddRange(approved, declined, voided, draft, paid);
        await db.SaveChangesAsync();
        var email = new RecordingEmail();

        async Task<(ClientInvoiceStatus Status, ClientInvoicesController Controller)> SendAsync(ClientInvoice document)
        {
            var controller = Invoices(db, agentId, email);
            Assert.IsType<RedirectToActionResult>(await controller.Send(document.Id));
            return ((await db.ClientInvoices.AsNoTracking().SingleAsync(i => i.Id == document.Id)).Status, controller);
        }

        Assert.Equal(ClientInvoiceStatus.Approved, (await SendAsync(approved)).Status);   // was turned back into Sent
        Assert.Equal(ClientInvoiceStatus.Declined, (await SendAsync(declined)).Status);
        Assert.Equal(ClientInvoiceStatus.Paid, (await SendAsync(paid)).Status);
        Assert.Equal(ClientInvoiceStatus.Sent, (await SendAsync(draft)).Status);          // the first send still makes it Sent
        Assert.Equal(4, email.To.Count);

        var refused = await SendAsync(voided);
        Assert.Equal(ClientInvoiceStatus.Void, refused.Status);                           // was revived into aging and reminders
        Assert.Equal("Invoice INV-3 is void, so it was not emailed.", refused.Controller.TempData["Error"]);
        Assert.Equal(4, email.To.Count);
        Assert.Null((await db.ClientInvoices.AsNoTracking().SingleAsync(i => i.Id == voided.Id)).SentAt);
    }

    // ---------------------------------------------------------------- W04-03

    [Fact]
    public void A_resumed_schedule_starts_from_today_on_its_own_rhythm()
    {
        var today = new DateTime(2026, 11, 27);
        // Paused in May on the 15th of each month: the next is 15 December, not seven invoices for May to November.
        Assert.Equal(new DateTime(2026, 12, 15), RecurringClientInvoiceJob.NextRunOnOrAfter(new DateTime(2026, 5, 15), RecurringInvoiceFrequency.Monthly, 15, today));
        // Due today stays today; a date ahead is left alone.
        Assert.Equal(today, RecurringClientInvoiceJob.NextRunOnOrAfter(new DateTime(2026, 8, 27), RecurringInvoiceFrequency.Monthly, 27, today));
        Assert.Equal(new DateTime(2027, 1, 5), RecurringClientInvoiceJob.NextRunOnOrAfter(new DateTime(2027, 1, 5), RecurringInvoiceFrequency.Monthly, 5, today));
        // The schedule's own day survives short months (670), and quarters and years keep their step.
        Assert.Equal(new DateTime(2026, 11, 30), RecurringClientInvoiceJob.NextRunOnOrAfter(new DateTime(2026, 1, 31), RecurringInvoiceFrequency.Monthly, 31, today));
        Assert.Equal(new DateTime(2027, 2, 10), RecurringClientInvoiceJob.NextRunOnOrAfter(new DateTime(2026, 2, 10), RecurringInvoiceFrequency.Quarterly, 10, today));
        Assert.Equal(new DateTime(2027, 3, 1), RecurringClientInvoiceJob.NextRunOnOrAfter(new DateTime(2024, 3, 1), RecurringInvoiceFrequency.Annually, 1, today));
    }

    [Fact]
    public async Task Resuming_a_paused_schedule_bills_nothing_for_the_time_it_was_paused()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var client = Person(agentId, "Ava", onNewsletterList: false);
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var today = AgentLocalTime.FromUtc(DateTime.UtcNow, "America/Toronto").Date;
        var pausedSince = today.AddMonths(-6).AddDays(-3);
        var old = new RecurringInvoiceSchedule
        {
            AgentUserId = agentId, ClientId = client.Id, Frequency = RecurringInvoiceFrequency.Monthly, NextRunDate = pausedSince, AnchorDay = pausedSince.Day,
            DueInDays = 15, IsActive = false
        };
        var ahead = new RecurringInvoiceSchedule
        {
            AgentUserId = agentId, ClientId = client.Id, Frequency = RecurringInvoiceFrequency.Monthly, NextRunDate = today.AddDays(9), AnchorDay = today.AddDays(9).Day,
            DueInDays = 15, IsActive = false
        };
        db.AddRange(old, ahead);
        await db.SaveChangesAsync();

        var controller = Recurring(db, agentId);
        Assert.IsType<RedirectToActionResult>(await controller.Resume(old.Id));
        db.ChangeTracker.Clear();
        var resumed = await db.RecurringInvoiceSchedules.AsNoTracking().SingleAsync(s => s.Id == old.Id);
        Assert.True(resumed.IsActive);
        Assert.True(resumed.NextRunDate.Date >= today, "the next invoice is not in the past");
        Assert.True(resumed.NextRunDate.Date < today.AddMonths(1).AddDays(1), "and it is the very next one");
        Assert.Equal(RecurringClientInvoiceJob.NextRunOnOrAfter(pausedSince, RecurringInvoiceFrequency.Monthly, pausedSince.Day, today), resumed.NextRunDate);
        Assert.StartsWith("Schedule resumed. Nothing is billed for the time it was paused: the next invoice is made on ", (string)controller.TempData["Success"]!);

        var second = Recurring(db, agentId);
        Assert.IsType<RedirectToActionResult>(await second.Resume(ahead.Id));
        Assert.Equal("Schedule resumed.", second.TempData["Success"]);
        Assert.Equal(today.AddDays(9), (await db.RecurringInvoiceSchedules.AsNoTracking().SingleAsync(s => s.Id == ahead.Id)).NextRunDate);
    }

    // ---------------------------------------------------------------- W04-04

    [Fact]
    public async Task The_QuickBooks_file_carries_the_tax_and_its_rows_add_up_to_the_invoice()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var client = Person(agentId, "Ava", onNewsletterList: false);
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        // $1,000 of taxed work plus $130 HST: the file used to say $1,000 and nothing else.
        var plain = Invoice(agentId, client.Id, "INV-100", subTotal: 1000m, tax: 130m, discount: 0m);
        plain.LineItems.Add(Line("Consulting", 10, 100m, 1000m, taxable: true, 0));
        // Mixed: a taxed and an untaxed item with 10% off the whole invoice (the 566 bakery order).
        var mixed = Invoice(agentId, client.Id, "INV-101", subTotal: 130.50m, tax: 9.36m, discount: 14.50m);
        mixed.DiscountType = InvoiceDiscountType.Percent;
        mixed.DiscountValue = 10m;
        mixed.LineItems.Add(Line("Sourdough loaves", 10, 6.50m, 65m, taxable: false, 0));
        mixed.LineItems.Add(Line("Birthday cake", 1, 80m, 80m, taxable: true, 1));
        // Three taxed items whose tax rounds down three times: the cent goes on the last taxed row.
        var cents = Invoice(agentId, client.Id, "INV-102", subTotal: 0.30m, tax: 0.04m, discount: 0m);
        for (var i = 0; i < 3; i++) cents.LineItems.Add(Line($"Sticker {i}", 1, 0.10m, 0.10m, taxable: true, i));
        // No tax at all.
        var exempt = Invoice(agentId, client.Id, "INV-103", subTotal: 50m, tax: 0m, discount: 0m);
        exempt.TaxRate = 0m;
        exempt.LineItems.Add(Line("Book", 1, 50m, 50m, taxable: true, 0));
        db.ClientInvoices.AddRange(plain, mixed, cents, exempt);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var file = Assert.IsType<FileContentResult>(await Invoices(db, agentId, new RecordingEmail()).ExportQuickBooks());
        var lines = Encoding.UTF8.GetString(file.FileContents).Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("InvoiceNo,Customer,InvoiceDate,DueDate,Terms,Product/Service,Description,Qty,Rate,Amount,ItemTaxCode,ItemTaxAmount,Currency,Status,PaidDate", lines[0]);

        string[][] RowsOf(string number) => lines.Where(l => l.StartsWith(number + ",", StringComparison.Ordinal)).Select(l => l.Split(',')).ToArray();
        static decimal Money(string cell) => decimal.Parse(cell, CultureInfo.InvariantCulture);

        var one = Assert.Single(RowsOf("INV-100"));
        Assert.Equal(("1000.00", "ON 13%", "130.00"), (one[9], one[10], one[11]));

        var two = RowsOf("INV-101");
        Assert.Equal(4, two.Length);                                                       // two items, and the discount in its two shares
        Assert.Equal(130.50m, two.Sum(r => Money(r[9])));                                  // the amounts are the subtotal
        Assert.Equal(9.36m, two.Sum(r => Money(r[11])));                                   // the tax is the invoice's tax
        Assert.Equal(("65.00", "Exempt", "0.00"), (two[0][9], two[0][10], two[0][11]));
        Assert.Equal(("80.00", "ON 13%", "10.40"), (two[1][9], two[1][10], two[1][11]));
        Assert.Equal(("Discount 10%", "-8.00", "ON 13%", "-1.04"), (two[2][6], two[2][9], two[2][10], two[2][11]));
        Assert.Equal(("Discount 10%", "-6.50", "Exempt", "0.00"), (two[3][6], two[3][9], two[3][10], two[3][11]));

        var three = RowsOf("INV-102");
        Assert.Equal(new[] { "0.01", "0.01", "0.02" }, three.Select(r => r[11]).ToArray());
        Assert.Equal(0.04m, three.Sum(r => Money(r[11])));

        var four = Assert.Single(RowsOf("INV-103"));
        Assert.Equal(("50.00", "Exempt", "0.00"), (four[9], four[10], four[11]));

        Assert.Contains("**ItemTaxCode** and **ItemTaxAmount**", Read(@"DOCS\10_CLIENT_INVOICING.md"));
    }

    // ---------------------------------------------------------------- harness

    private static ClientInvoice Invoice(int agentId, int clientId, string number, decimal subTotal, decimal tax, decimal discount) => new()
    {
        AgentUserId = agentId, ClientId = clientId, DocumentType = ClientInvoiceDocumentType.Invoice, Status = ClientInvoiceStatus.Sent, DocumentNumber = number,
        ViewToken = Guid.NewGuid().ToString("N"), IssueDate = new DateTime(2026, 11, 20), DueDate = new DateTime(2026, 12, 5), Currency = "CAD",
        TaxRegion = "ON", TaxRate = 0.13m, TaxAmount = tax, DiscountAmount = discount, SubTotal = subTotal, Total = subTotal + tax
    };

    private static ClientInvoiceLineItem Line(string description, decimal quantity, decimal unitPrice, decimal amount, bool taxable, int order) => new()
    {
        Description = description, Quantity = quantity, UnitPrice = unitPrice, Amount = amount, IsTaxable = taxable, SortOrder = order
    };

    private static Client Person(int agentId, string firstName, bool onNewsletterList) => new()
    {
        AgentUserId = agentId, FirstName = firstName, LastName = "Reid", Email = $"{firstName.ToLowerInvariant()}-{Guid.NewGuid():N}@example.test",
        IsNewsletterSubscribed = onNewsletterList
    };

    private static async Task<int> SeedAgentAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"T683m-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t683m-{Guid.NewGuid():N}")[..20], Email = $"{Guid.NewGuid():N}@example.test", FirstName = "Morgan", LastName = "Reid",
            CompanyName = "Reid Advisory", DomainName = ($"t683m-{Guid.NewGuid():N}")[..24], CompanyAddress = "123 Main Street", City = "Toronto",
            Province = "Ontario", PostalCode = "M5V 1A1", Country = "Canada", TimeZone = "America/Toronto", PackageId = rule.Id, IsActive = true
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static T Prepared<T>(T controller, int agentId) where T : Controller
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString(CultureInfo.InvariantCulture)) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, new NoTempData());
        return controller;
    }

    private static EmailConsentService Consent(IPRODbContext db) =>
        new(db, new ConfigurationBuilder().Build(), NullLogger<EmailConsentService>.Instance, Array.Empty<IUnsubscribeNotifier>());

    private static NewsletterController Newsletters(IPRODbContext db, int agentId, IEmailService email)
    {
        var uow = new UnitOfWork(db);
        var configuration = new ConfigurationBuilder().Build();
        return Prepared(new NewsletterController(new NewsLetterService(uow, Consent(db), db), new ClientService(uow, db), new GrantAll(), uow, db,
            new NewsLetterDispatcher(uow, db, email, configuration, NullLogger<NewsLetterDispatcher>.Instance), email, null!, null!, Consent(db), configuration,
            NullLogger<NewsletterController>.Instance), agentId);
    }

    private static CampaignsController Campaigns(IPRODbContext db, int agentId) =>
        Prepared(new CampaignsController(db, new GrantAll(), Consent(db), null!), agentId);

    private static ClientInvoicesController Invoices(IPRODbContext db, int agentId, IEmailService email) =>
        Prepared(new ClientInvoicesController(db, new GrantAll(), new ClientInvoiceService(new UnitOfWork(db)), email, new ConfigurationBuilder().Build()), agentId);

    private static RecurringInvoicesController Recurring(IPRODbContext db, int agentId) =>
        Prepared(new RecurringInvoicesController(db, new GrantAll()), agentId);

    private sealed class GrantAll : IPackageEntitlementService
    {
        public Task<PackageFeatureAccess> GetAccessAsync(int agentId, string featureCode) =>
            Task.FromResult(new PackageFeatureAccess { FeatureCode = featureCode, IsIncluded = true });
        public Task<bool> HasAccessAsync(int agentId, string featureCode) => Task.FromResult(true);
        public Task<Dictionary<int, bool>> HasAccessBulkAsync(IEnumerable<int> agentIds, string featureCode) =>
            Task.FromResult(agentIds.Distinct().ToDictionary(a => a, _ => true));
        public Task<bool> IsAccessGatedAsync(int agentId) => Task.FromResult(false);
    }

    private sealed class RecordingEmail : IEmailService
    {
        public List<string> To { get; } = new();
        public async Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null,
            IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
            (await SendDetailedAsync(toEmail, toName, subject, htmlBody, textBody, customArgs, replyToEmail, replyToName, listUnsubscribeUrl)).Success;
        public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null,
            IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        {
            To.Add(toEmail);
            return Task.FromResult(EmailSendResult.Sent($"test-{To.Count}"));
        }
        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData) => Task.FromResult(true);
    }

    private sealed class NoTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative));
    }
}
