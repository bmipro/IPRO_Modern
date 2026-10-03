using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// 545 (2026-10-03). On the Amazon pilot the owner sent Bob Moore two birthday cards, at 4:15:43 and
// 4:19:07 p.m. Each card's open pixel was loaded two seconds later from a server in San Francisco --
// the receiving mail system checking a picture-heavy email as it arrived -- and Email Activity said
// "Opened" before anyone had looked. Opens are write-once, so his own opens at 4:20:30 and 4:20:36
// (through Gmail's image servers) never showed. The owner: "I could delete the email without reading
// it ... but the agent thinks it was read." A pixel load in the first minute after the send is no
// longer an open; the reader's later load is. Clicks are not held back.
public class ArrivalCheck545Tests
{
    private const string Key = "unit-test-signing-key-545";

    [Fact]
    public async Task A_load_two_seconds_after_the_send_is_not_an_open_and_the_readers_later_load_is()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (recipientId, token) = await SeedCardAsync(db, sentSecondsAgo: 2);

        Assert.IsType<FileContentResult>(await NewController(db).Open("ecard", token));   // the image is still served
        db.ChangeTracker.Clear();
        Assert.Null((await db.ECardRecipients.SingleAsync(r => r.Id == recipientId)).OpenedAt);

        // A minute and a half later the reader opens it.
        var row = await db.ECardRecipients.SingleAsync(r => r.Id == recipientId);
        row.SentAt = DateTime.UtcNow.AddSeconds(-90);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await NewController(db).Open("ecard", token);
        db.ChangeTracker.Clear();
        var opened = await db.ECardRecipients.SingleAsync(r => r.Id == recipientId);
        Assert.NotNull(opened.OpenedAt);
        Assert.Equal("open", opened.LastEvent);
    }

    [Fact]
    public async Task A_click_is_recorded_whenever_it_comes()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (recipientId, token) = await SeedCardAsync(db, sentSecondsAgo: 2);
        const string target = "https://www.example-adviser.test/";

        Assert.IsType<RedirectResult>(await NewController(db).Click("ecard", token, target, EmailTrackingLinks.Sign("ecard", token, target, Key)));
        db.ChangeTracker.Clear();
        Assert.NotNull((await db.ECardRecipients.SingleAsync(r => r.Id == recipientId)).ClickedAt);
    }

    [Fact]
    public async Task A_newsletter_load_on_arrival_is_not_an_open_either()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agent = await SeedAgentAsync(db);
        var newsletter = new NewsLetter { AgentUserId = agent.Id, Subject = "October", HtmlBody = "<p>x</p>" };
        db.Add(newsletter);
        await db.SaveChangesAsync();
        var send = new NewsLetterSend { NewsLetterId = newsletter.Id, AgentUserId = agent.Id, Status = NewsLetterSendStatus.Sent, TotalRecipients = 1 };
        db.Add(send);
        await db.SaveChangesAsync();
        var token = Guid.NewGuid().ToString("N");
        var recipient = new NewsLetterRecipient
        {
            NewsLetterId = newsletter.Id, NewsLetterSendId = send.Id, Email = "reader545@example.test", RecipientName = "Reader",
            Status = NewsLetterRecipientStatus.Sent, SentAt = DateTime.UtcNow.AddSeconds(-2),
            UnsubscribeToken = Guid.NewGuid().ToString("N"), TrackingToken = token
        };
        db.Add(recipient);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await NewController(db).Open("newsletter", token);
        db.ChangeTracker.Clear();

        var row = await db.NewsLetterRecipients.SingleAsync(r => r.Id == recipient.Id);
        Assert.Null(row.OpenedAt);
        Assert.Equal(NewsLetterRecipientStatus.Sent, row.Status);
        Assert.Equal(0, (await db.NewsLetterSends.SingleAsync(s => s.Id == send.Id)).TotalOpened);
    }

    [Fact]
    public void Every_kind_the_pixel_records_holds_back_the_first_minute_and_the_screen_says_so()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), IPRO.Web.Controllers.EmailTrackingController.ArrivalCheckWindow);
        var controller = Read(@"src\IPRO.Web\Controllers\EmailTrackingController.cs");
        Assert.Equal(6, Count(controller, "&& !IsArrivalCheck(eventName, row.Sent, now)"));   // newsletter, drip, card, letter, poll, Did You Know
        Assert.Contains("first minute after sending are not counted", Read(@"src\IPRO.Web\Views\EmailActivity\Details.cshtml"));
        Assert.Contains("first minute after sending are not counted", Read(@"DOCS\23_EMAIL_ACTIVITY.md"));
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private static IPRO.Web.Controllers.EmailTrackingController NewController(IPRODbContext db)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Email:AzureEventWebhookSecret"] = Key })
            .Build();
        var consent = new EmailConsentService(db, config, NullLogger<EmailConsentService>.Instance, Array.Empty<IUnsubscribeNotifier>());
        return new IPRO.Web.Controllers.EmailTrackingController(
            db,
            new NewsLetterService(new UnitOfWork(db), consent, db),
            new EmailDeliveryTracker(db, NullLogger<EmailDeliveryTracker>.Instance, consent),
            config,
            NullLogger<IPRO.Web.Controllers.EmailTrackingController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static async Task<AgentUser> SeedAgentAsync(IPRODbContext db)
    {
        var agent = new AgentUser
        {
            UserName = $"t545-{Guid.NewGuid():N}"[..20], Email = "agent545@example.test",
            FirstName = "Arrival", LastName = "Check", DomainName = $"t545-{Guid.NewGuid():N}"[..24]
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent;
    }

    private static async Task<(int RecipientId, string Token)> SeedCardAsync(IPRODbContext db, int sentSecondsAgo)
    {
        var agent = await SeedAgentAsync(db);
        var client = new Client { AgentUserId = agent.Id, FirstName = "Bob", LastName = "Moore", Email = "bob545@example.test" };
        db.Add(client);
        var card = new ECard { AgentUserId = agent.Id, Occasion = "simple-birthday", Subject = "Happy Birthday" };
        db.Add(card);
        await db.SaveChangesAsync();
        var token = Guid.NewGuid().ToString("N");
        var recipient = new ECardRecipient
        {
            ECardId = card.Id, ClientId = client.Id, Email = client.Email, RecipientName = "Bob Moore",
            Status = ECardRecipientStatuses.Sent, SentAt = DateTime.UtcNow.AddSeconds(-sentSecondsAgo), TrackingToken = token
        };
        db.Add(recipient);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (recipient.Id, token);
    }

    private static int Count(string source, string needle)
    {
        var count = 0;
        for (var at = source.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = source.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative));
    }
}
