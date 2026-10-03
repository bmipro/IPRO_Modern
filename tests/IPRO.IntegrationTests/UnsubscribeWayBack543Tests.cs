using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

// 543 (2026-10-03). The owner, testing his own newsletter on the Amazon pilot, clicked its
// "Unsubscribe from future newsletters" link and then could not get back: "when i click back on
// unsbscibe it wont allow me to set it up like i did once before". That link's page confirmed the
// unsubscribe and offered nothing else, while every other client email links to the preferences page,
// which offers birthday and anniversary greetings only, or "Resubscribe to everything". He got back
// only through an older testimonial email's footer. His word on the fix: "go, fix it with the next push".
// A person's click on a newsletter's or a series' unsubscribe link now lands on that same page, after
// the unsubscribe has taken full effect; a mail provider's one-click POST keeps its plain 200.
public class UnsubscribeWayBack543Tests
{
    [Fact]
    public async Task A_person_who_unsubscribes_from_a_newsletter_lands_on_the_page_with_the_way_back()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (token, clientId) = await SeedNewsletterRecipientAsync(db);

        var result = await NewController(db, "GET").Unsubscribe(token);

        var redirect = Assert.IsType<RedirectResult>(result);
        db.ChangeTracker.Clear();
        var client = await db.Clients.SingleAsync(c => c.Id == clientId);
        Assert.NotNull(client.EmailOptOutAt);                                   // the unsubscribe took full effect first
        Assert.Equal(EmailOptOutReason.Unsubscribed, EmailOptOut.ReasonOf(client));
        Assert.False(string.IsNullOrEmpty(client.EmailPreferencesToken));       // a token was made for the page
        Assert.EndsWith($"/email-preferences?token={client.EmailPreferencesToken}", redirect.Url);
        Assert.Equal(NewsLetterRecipientStatus.Unsubscribed,
            (await db.NewsLetterRecipients.SingleAsync(r => r.UnsubscribeToken == token)).Status);
    }

    [Fact]
    public async Task A_mail_providers_one_click_post_still_gets_a_plain_page()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (token, clientId) = await SeedNewsletterRecipientAsync(db);

        var result = await NewController(db, "POST").Unsubscribe(token);

        Assert.IsType<ViewResult>(result);
        db.ChangeTracker.Clear();
        Assert.NotNull((await db.Clients.SingleAsync(c => c.Id == clientId)).EmailOptOutAt);
    }

    [Fact]
    public void Both_unsubscribe_links_go_to_the_preferences_page_and_it_says_iPro()
    {
        var controller = Read(@"src\IPRO.Web\Controllers\NewsletterController.cs");
        Assert.Equal(3, Count(controller, "PreferencesPageAsync("));             // the newsletter, the series, and the helper itself
        Assert.Contains("_consent.BuildPreferencesUrl(", controller);

        var page = Read(@"src\IPRO.Web\Views\EmailPreferences\Index.cshtml");
        Assert.Contains("Changed your mind? Resubscribe to everything", page);   // the way back the links now reach
        Assert.Contains("sent through iPro.", page);
        Assert.DoesNotContain("sent through IPRO", page);
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private static IPRO.Web.Controllers.NewsletterController NewController(IPRODbContext db, string method)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:BaseUrl"] = "https://app.example.test"
        }).Build();
        var uow = new UnitOfWork(db);
        var consent = new EmailConsentService(db, config, NullLogger<EmailConsentService>.Instance, Array.Empty<IUnsubscribeNotifier>());
        var controller = new IPRO.Web.Controllers.NewsletterController(
            new NewsLetterService(uow, consent, db), null!, null!, uow, db, null!, null!, null!,
            new EmailDeliveryTracker(db, NullLogger<EmailDeliveryTracker>.Instance, consent),
            consent, config, NullLogger<IPRO.Web.Controllers.NewsletterController>.Instance);
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        return controller;
    }

    private static async Task<(string Token, int ClientId)> SeedNewsletterRecipientAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"T543-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t543-{Guid.NewGuid():N}")[..20], Email = "agent543@example.test",
            FirstName = "Way", LastName = "Back", CompanyName = "Global Business Solution",
            DomainName = ($"t543-{Guid.NewGuid():N}")[..24], PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        var client = new Client
        {
            AgentUserId = agent.Id, FirstName = "Bob", LastName = "Moore",
            Email = $"bob-{Guid.NewGuid():N}"[..16] + "@example.test", IsNewsletterSubscribed = true
        };
        db.Add(client);
        var newsletter = new NewsLetter { AgentUserId = agent.Id, Subject = "October", HtmlBody = "<p>x</p>" };
        db.Add(newsletter);
        await db.SaveChangesAsync();
        var token = Guid.NewGuid().ToString("N");
        db.Add(new NewsLetterRecipient
        {
            NewsLetterId = newsletter.Id, ClientId = client.Id, Email = client.Email,
            RecipientName = "Bob Moore", UnsubscribeToken = token
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (token, client.Id);
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
