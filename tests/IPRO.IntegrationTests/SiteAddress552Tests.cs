using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// 552 (2026-10-05). The owner sent an e-card and its "web site:" line read bahmanmotamed.247advisers.com while his
// site lives at www.4iPro.com: "I want it to show www.4iPro.com since that is the domain I m using". Cards, letters
// and newsletters printed the free address made at registration whatever domain was connected since. They now show
// the website's custom domain once it is live (bound, SSL bound -- ClientPortalUrls' rule), in the adviser's own
// capitals when they wrote it so on My Website; otherwise the free address.
public class SiteAddress552Tests
{
    private const string Temporary = "bahmanmotamed.247advisers.com";

    [Fact]
    public async Task The_live_custom_domain_is_the_address_in_the_advisers_capitals_and_the_free_one_until_then()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agent, website) = await AdviserWithSiteAsync(db, customDomain: "", display: "");
        Assert.Equal(Temporary, await AgentSiteAddress.HostAsync(db, agent.Id, agent.DomainName));   // no custom domain

        await db.AgentWebsites.Where(w => w.Id == website.Id).ExecuteUpdateAsync(s => s.SetProperty(w => w.CustomDomain, "www.4ipro.com"));
        var domain = new AgentDomain
        {
            AgentUserId = agent.Id, AgentWebsiteId = website.Id, DomainName = "www.4ipro.com", RootDomain = "4ipro.com", WwwDomain = "www.4ipro.com",
            DnsStatus = AgentDomainStatus.DnsReady, AzureBindingStatus = AgentDomainStatus.BindingPending, SslStatus = AgentDomainStatus.BindingPending,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.Add(domain);
        await db.SaveChangesAsync();
        Assert.Equal(Temporary, await AgentSiteAddress.HostAsync(db, agent.Id, agent.DomainName));   // still being set up

        await db.AgentDomains.Where(d => d.Id == domain.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.AzureBindingStatus, AgentDomainStatus.Bound).SetProperty(d => d.SslStatus, "SslBound"));
        Assert.Equal("www.4ipro.com", await AgentSiteAddress.HostAsync(db, agent.Id, agent.DomainName));   // live (legacy SslBound counts)

        await db.AgentWebsites.Where(w => w.Id == website.Id).ExecuteUpdateAsync(s => s.SetProperty(w => w.CustomDomainDisplay, "www.4iPro.com"));
        Assert.Equal("www.4iPro.com", await AgentSiteAddress.HostAsync(db, agent.Id, agent.DomainName));

        // The portal links agree on which domain is live (ClientPortalUrls, the rule this mirrors).
        Assert.Equal("https://www.4ipro.com", await IPRO.Web.Infrastructure.ClientPortalUrls.GetBaseUrlAsync(db, agent.Id, new ConfigurationBuilder().Build()));
    }

    [Theory]
    [InlineData("www.4ipro.com", "www.4iPro.com", "www.4iPro.com")]
    [InlineData("www.4ipro.com", "  WWW.4IPRO.COM ", "WWW.4IPRO.COM")]
    [InlineData("www.4ipro.com", "www.other.com", "www.4ipro.com")]   // a different address is never shown
    [InlineData("www.4ipro.com", "4iPro.com", "www.4ipro.com")]       // nor one with letters left out
    [InlineData("www.4ipro.com", "", "www.4ipro.com")]
    public void Only_the_capitals_may_differ(string host, string display, string shown) =>
        Assert.Equal(shown, AgentSiteAddress.Written(host, display));

    [Fact]
    public void Cards_letters_and_newsletters_print_the_address_they_are_given()
    {
        var agent = Agent();
        var card = new ECard { Subject = "Happy birthday", Message = "Many happy returns." };
        var design = ECardDesignSeeder.BuildDefaults().First();

        var html = ECardHtmlComposer.Wrap(card, agent, design, "https://app.test", "www.4iPro.com");
        Assert.Contains(">web site:</td>", html);
        Assert.Contains("href=\"https://www.4iPro.com\"", html);
        Assert.Contains(">www.4iPro.com</a>", html);
        Assert.DoesNotContain(Temporary, html);
        Assert.Contains("www.4iPro.com", ECardHtmlComposer.WrapText(card, agent, design, "https://app.test/p", "www.4iPro.com"));
        Assert.DoesNotContain(Temporary, ECardHtmlComposer.WrapText(card, agent, design, "https://app.test/p", "www.4iPro.com"));
        Assert.Contains(Temporary, ECardHtmlComposer.Wrap(card, agent, design, "https://app.test"));   // not given: the free address

        var letter = new ELetter { Body = "Dear client" };
        var letterHtml = ELetterHtmlComposer.Wrap(letter, agent, null, "www.4iPro.com");
        Assert.Contains("href=\"https://www.4iPro.com\"", letterHtml);
        Assert.DoesNotContain(Temporary, letterHtml);
        Assert.Contains("https://www.4iPro.com", ELetterHtmlComposer.WrapText(letter, agent, null, "https://app.test/p", "www.4iPro.com"));

        var newsletterHtml = NewsletterHtmlComposer.Wrap(new NewsLetter { Edition = "October", HtmlBody = "<p>News</p>" }, agent, "https://app.test", siteHost: "www.4iPro.com");
        Assert.Equal(2, newsletterHtml.Split(">www.4iPro.com</a>").Length - 1);   // the header link and the contact line
        Assert.DoesNotContain(Temporary, newsletterHtml);
    }

    [Fact]
    public async Task A_card_sent_by_an_adviser_with_a_live_domain_shows_that_domain()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agent, website) = await AdviserWithSiteAsync(db, customDomain: "www.4ipro.com", display: "www.4iPro.com");
        db.Add(new AgentDomain
        {
            AgentUserId = agent.Id, AgentWebsiteId = website.Id, DomainName = "www.4ipro.com", RootDomain = "4ipro.com", WwwDomain = "www.4ipro.com",
            DnsStatus = AgentDomainStatus.DnsReady, AzureBindingStatus = AgentDomainStatus.Bound, SslStatus = AgentDomainStatus.Bound,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        var client = new Client { AgentUserId = agent.Id, FirstName = "Bob", LastName = "Moore", Email = "bob552@example.test" };
        db.Add(client);
        var design = new ECardDesign
        {
            Key = ($"t552-{Guid.NewGuid():N}")[..20], Occasion = "Birthday", Name = "Simple birthday",
            ImageUrl = "/images/ecard-art/simple-birthday.jpg", Width = 540, Height = 396, IsActive = true
        };
        db.Add(design);
        await db.SaveChangesAsync();
        var card = new ECard
        {
            AgentUserId = agent.Id, Occasion = design.Key, Subject = "Happy BirthDay", Message = "Many happy returns.",
            Status = ECardStatuses.Scheduled, ScheduledAt = DateTime.UtcNow.AddMinutes(-1), TotalRecipients = 1
        };
        db.Add(card);
        await db.SaveChangesAsync();
        db.Add(new ECardRecipient { ECardId = card.Id, ClientId = client.Id, Email = client.Email, RecipientName = "Bob Moore" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var transport = new RecordingEmail();
        await new ECardDispatcher(db, transport, new EmailConsentService(db, new ConfigurationBuilder().Build(), NullLogger<EmailConsentService>.Instance, Array.Empty<IUnsubscribeNotifier>()),
            new ConfigurationBuilder().Build(), NullLogger<ECardDispatcher>.Instance).DispatchAsync(card.Id);

        var sent = Assert.Single(transport.Sent);
        Assert.Contains("href=\"https://www.4iPro.com\"", sent.Html);
        Assert.DoesNotContain(Temporary, sent.Html);
        Assert.Contains("www.4iPro.com", sent.Text);
    }

    [Fact]
    public void Every_send_preview_and_test_send_passes_the_address_and_My_Website_takes_the_capitals()
    {
        var card = Read(@"src\IPRO.Email\ECardDispatcher.cs");
        Assert.Contains("var siteHost = await AgentSiteAddress.HostAsync(_db, agent.Id, agent.DomainName);", card);
        Assert.Contains("ECardHtmlComposer.Wrap(card, agent, design, baseUrl, siteHost);", card);
        Assert.Contains("ECardHtmlComposer.WrapText(card, agent, design, preferencesUrl, siteHost),", card);

        var letter = Read(@"src\IPRO.Email\ELetterDispatcher.cs");
        Assert.Contains("ELetterHtmlComposer.Wrap(letter, agent, client, siteHost);", letter);
        Assert.Contains("ELetterHtmlComposer.WrapText(letter, agent, client, preferencesUrl, siteHost),", letter);
        Assert.Contains("await AgentSiteAddress.HostAsync(_db, sendingAgent.Id, sendingAgent.DomainName)", Read(@"src\IPRO.Email\NewsLetterDispatcher.cs"));

        Assert.Contains("await AgentSiteAddress.HostAsync(_db, agent.Id, agent.DomainName)", Read(@"src\IPRO.Web\Controllers\ECardsController.cs"));
        Assert.Contains("await AgentSiteAddress.HostAsync(_db, agent.Id, agent.DomainName)", Read(@"src\IPRO.Web\Controllers\ELettersController.cs"));
        var newsletters = Read(@"src\IPRO.Web\Controllers\NewsletterController.cs");
        Assert.Contains("await AgentSiteAddress.HostAsync(_db, previewAgent.Id, previewAgent.DomainName)", newsletters);
        Assert.Contains("testSendArticles, testSendCtas, testSendSiteHost)", newsletters);

        var website = Read(@"src\IPRO.Web\Controllers\WebsiteController.cs");
        Assert.Contains("if (model.CustomDomainDisplay.Length > 0 && !string.Equals(model.CustomDomainDisplay, model.CustomDomain, StringComparison.OrdinalIgnoreCase))", website);
        Assert.Contains("existing.CustomDomainDisplay = model.CustomDomainDisplay;", website);
        Assert.Contains("name=\"CustomDomainDisplay\"", Read(@"src\IPRO.Web\Views\Website\Index.cshtml"));
        Assert.Contains("\"CustomDomainDisplay\", \"ALTER TABLE `AgentWebsites` ADD COLUMN `CustomDomainDisplay` varchar(255)", Read(@"src\IPRO.DataAccess\StartupSchemaRepair.cs"));
        Assert.Contains("### The address on your emails", Read(@"DOCS\05_DOMAINS_AND_LEADS.md"));
    }

    private static AgentUser Agent() => new()
    {
        FirstName = "Bahman", LastName = "Motamed", CompanyName = "Global Business Solution", Email = "agent552@example.test",
        Phone = "416-555-0101", DomainName = Temporary,
    };

    private static async Task<(AgentUser Agent, AgentWebsite Website)> AdviserWithSiteAsync(IPRODbContext db, string customDomain, string display)
    {
        var rule = new BillingRule { PackageName = ($"T552-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        var template = new WebsiteTemplate { Name = "Modern", TemplateKey = ($"t552-{Guid.NewGuid():N}")[..20] };
        db.AddRange(rule, template);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t552-{Guid.NewGuid():N}")[..20], Email = "agent552@example.test", FirstName = "Bahman", LastName = "Motamed",
            CompanyName = "Global Business Solution", DomainName = Temporary, PackageId = rule.Id,
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        var website = new AgentWebsite { AgentUserId = agent.Id, TemplateId = template.Id, CustomDomain = customDomain, CustomDomainDisplay = display, IsPublished = true };
        db.Add(website);
        await db.SaveChangesAsync();
        return (agent, website);
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative)).Replace("\r\n", "\n");
    }

    private sealed class RecordingEmail : IEmailService
    {
        public List<(string To, string Html, string Text)> Sent { get; } = new();

        public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null,
            IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        {
            Sent.Add((toEmail, htmlBody, textBody ?? string.Empty));
            return Task.FromResult(EmailSendResult.Sent($"msg-{Sent.Count}"));
        }

        public async Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null,
            IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
            (await SendDetailedAsync(toEmail, toName, subject, htmlBody, textBody)).Success;

        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData) => Task.FromResult(true);
    }
}
