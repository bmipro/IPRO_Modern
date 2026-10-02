using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Scheduler;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// 533 (2026-09-29): Canada's anti-spam law asks every commercial email to say who sent it, give a
// mailing address, say on whose behalf it was sent when someone else sends it, and offer a way out.
// iPro's marketing emails named the adviser and a phone or email but carried no mailing address, and
// the newsletter footer called the sender "your IPRO adviser" -- the wording the owner had removed
// from client emails that morning (530). One footer, SenderFooter, now closes every marketing email.
public class SenderFooter533Tests
{
    private static AgentUser Adviser() => new()
    {
        FirstName = "Bahman", LastName = "Motamed", CompanyName = "Global Business Solution",
        CompanyAddress = "123 Main Street", City = "Toronto", Province = "ON", PostalCode = "M5V 1A1",
        Country = "Canada", BusinessType = "Accountants", Email = "bm@example.test"
    };

    [Fact]
    public void The_mailing_address_is_the_profile_address_or_nothing()
    {
        Assert.Equal("123 Main Street, Toronto ON M5V 1A1, Canada", AdviserSender.MailingAddress(Adviser()));
        Assert.True(AdviserSender.HasStreetAddress(Adviser()));

        var noStreet = Adviser();
        noStreet.CompanyAddress = "  ";
        Assert.Equal("Toronto ON M5V 1A1, Canada", AdviserSender.MailingAddress(noStreet));
        Assert.False(AdviserSender.HasStreetAddress(noStreet));

        // The country alone (it defaults to Canada) is not an address.
        Assert.Equal(string.Empty, AdviserSender.MailingAddress(new AgentUser()));
        Assert.Equal(string.Empty, AdviserSender.MailingAddress(null));
        Assert.False(AdviserSender.HasStreetAddress(null));
    }

    [Fact]
    public void The_footer_names_the_business_its_address_the_way_out_and_iPro_on_its_behalf()
    {
        var html = SenderFooter.Html(Adviser(), "https://x.test/p?token=a&b", SenderFooterKind.Client);
        Assert.Contains("You received this because you are a client of Global Business Solution.", html);
        Assert.Contains("Global Business Solution, 123 Main Street, Toronto ON M5V 1A1, Canada", html);
        Assert.Contains("href=\"https://x.test/p?token=a&amp;b\"", html);
        Assert.Contains("Unsubscribe or change what you receive", html);
        Assert.Contains("Sent with <a href=\"https://www.iproaccountants.com/\"", html);
        Assert.Contains(">iPro</a> on behalf of Global Business Solution", html);
        Assert.DoesNotContain("IPRO adviser", html);

        var text = SenderFooter.Text(Adviser(), "https://x.test/p", SenderFooterKind.Client).Replace("\r\n", "\n");
        Assert.Contains("You received this because you are a client of Global Business Solution.", text);
        Assert.Contains("Global Business Solution, 123 Main Street, Toronto ON M5V 1A1, Canada", text);
        Assert.Contains("Unsubscribe or change what you receive:\nhttps://x.test/p", text);
        Assert.Contains("Sent with iPro on behalf of Global Business Solution: https://www.iproaccountants.com/", text);

        var news = SenderFooter.Html(Adviser(), "https://x.test/u", SenderFooterKind.Newsletter);
        Assert.Contains("You are receiving this email because you are subscribed to updates from Global Business Solution.", news);
        Assert.Contains("Unsubscribe from future newsletters", news);
    }

    [Fact]
    public void Business_details_are_encoded_and_missing_ones_are_left_out()
    {
        var odd = Adviser();
        odd.CompanyName = "Smith & <Sons>";
        var html = SenderFooter.Html(odd, "https://x.test/u", SenderFooterKind.Client);
        Assert.Contains("Smith &amp; &lt;Sons&gt;", html);
        Assert.DoesNotContain("<Sons>", html);

        // No business, no address, no link: nothing invented, and iPro still says it sent it.
        var none = SenderFooter.Html(new AgentUser(), null, SenderFooterKind.Client);
        Assert.Contains("client of the sender", none);
        Assert.DoesNotContain("on behalf of", none);
        Assert.DoesNotContain("Unsubscribe", none);
        Assert.Contains(">iPro</a>", none);
    }

    [Fact]
    public void The_shared_footers_and_plain_text_parts_carry_the_identification()
    {
        var newsletter = NewsLetterDispatcher.AppendUnsubscribeHtml("<p>Body</p>", "https://x.test/u", Adviser());
        Assert.StartsWith("<p>Body</p>", newsletter);
        Assert.Contains("123 Main Street, Toronto ON M5V 1A1, Canada", newsletter);
        Assert.Contains("on behalf of Global Business Solution", newsletter);
        Assert.Contains("123 Main Street", NewsLetterDispatcher.AppendUnsubscribeText("Hello", "https://x.test/u", Adviser()));

        var card = EmailUnsubscribeFooter.AppendHtml("<p>Card</p>", "https://x.test/p", Adviser());
        Assert.StartsWith("<p>Card</p>", card);
        Assert.Contains("client of Global Business Solution", card);
        Assert.Contains("123 Main Street", card);

        var cardText = ECardHtmlComposer.WrapText(new ECard { Subject = "Hi", Message = "Hello" }, Adviser(), new ECardDesign(), "https://x.test/p");
        Assert.Contains("Global Business Solution, 123 Main Street, Toronto ON M5V 1A1, Canada", cardText);
        Assert.Contains("https://x.test/p", cardText);

        var letterText = ELetterHtmlComposer.WrapText(new ELetter { Body = "Dear client" }, Adviser(), null, "https://x.test/p");
        Assert.Contains("Global Business Solution, 123 Main Street, Toronto ON M5V 1A1, Canada", letterText);
        Assert.Contains("https://x.test/p", letterText);
    }

    [Fact]
    public void Every_marketing_sender_uses_the_footer_and_the_send_pages_ask_for_a_street_address()
    {
        Assert.Contains("public static class SenderFooter", Read(@"src\IPRO.Entities\SenderFooter.cs"));

        // The two shared footers and the plain-text parts delegate to SenderFooter...
        Assert.Contains("SenderFooter.", Read(@"src\IPRO.Business\Services\EmailUnsubscribeFooter.cs"));
        Assert.Contains("SenderFooter.", Read(@"src\IPRO.Email\NewsLetterDispatcher.cs"));
        Assert.Contains("SenderFooter.Text(", Read(@"src\IPRO.Business\Services\ECardHtmlComposer.cs"));
        Assert.Contains("SenderFooter.Text(", Read(@"src\IPRO.Business\Services\ELetterHtmlComposer.cs"));
        // ...the cards and letters hand it the adviser...
        Assert.Contains("EmailUnsubscribeFooter.AppendHtml(html, preferencesUrl, agent)", Read(@"src\IPRO.Email\ECardDispatcher.cs"));
        Assert.Contains("EmailUnsubscribeFooter.AppendHtml(html, preferencesUrl, agent)", Read(@"src\IPRO.Email\ELetterDispatcher.cs"));
        // ...and the three senders that had no visible footer at all now close with it.
        foreach (var file in new[] { @"src\IPRO.Email\PollDispatcher.cs", @"src\IPRO.Scheduler\DidYouKnowEmailDispatchJob.cs", @"src\IPRO.Web\Controllers\TestimonialsController.cs" })
            Assert.Contains("SenderFooter.AppendHtml(", Read(file));
        Assert.Contains("SenderFooter.Text(", Read(@"src\IPRO.Email\PollDispatcher.cs"));

        // "IPRO adviser" is gone from what a client reads.
        foreach (var file in new[] { @"src\IPRO.Email\NewsLetterDispatcher.cs", @"src\IPRO.Web\Views\Newsletter\Unsubscribe.cshtml" })
            Assert.DoesNotContain("IPRO adviser", Read(file));

        // Every marketing send page asks for the street address while it is missing.
        foreach (var view in new[] { @"src\IPRO.Web\Views\Newsletter\Send.cshtml", @"src\IPRO.Web\Views\Polls\Send.cshtml", @"src\IPRO.Web\Views\Campaigns\Index.cshtml", @"src\IPRO.Web\Views\ECards\Create.cshtml", @"src\IPRO.Web\Views\ELetters\Create.cshtml" })
            Assert.Contains("Component.InvokeAsync(\"MailingAddressNotice\")", Read(view));
        Assert.Contains("AdviserSender.HasStreetAddress(", Read(@"src\IPRO.Web\ViewComponents\MailingAddressNoticeViewComponent.cs"));
    }

    // -- against MySQL: an email that had no footer, and one that shared the newsletter's -------------

    [Fact]
    public async Task A_did_you_know_email_and_a_drip_step_arrive_with_the_footer()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agent = Adviser();
        agent.UserName = $"t533-{Guid.NewGuid():N}"[..20];
        agent.DomainName = $"t533-{Guid.NewGuid():N}"[..24];
        db.Add(agent);
        await db.SaveChangesAsync();

        var article = new Article { AgentUserId = agent.Id, Title = "Tax tips", Content = "<p>content</p>", IsPublished = true };
        var client = new Client { AgentUserId = agent.Id, FirstName = "Ann", LastName = "Client", Email = $"{Guid.NewGuid():N}"[..12] + "@example.test" };
        var campaign = new DripCampaign { AgentUserId = agent.Id, Name = "Welcome", IsActive = true };
        db.AddRange(article, client, campaign);
        await db.SaveChangesAsync();
        db.Add(new DidYouKnowEmailQueueItem { ArticleId = article.Id, ClientId = client.Id, ScheduledForUtc = DateTime.UtcNow.AddMinutes(-5) });
        db.Add(new DripCampaignStep { DripCampaignId = campaign.Id, Subject = "Welcome", HtmlBody = "<p>one</p>", DelayDays = 0, SortOrder = 0 });
        db.Add(new DripCampaignEnrollment { AgentUserId = agent.Id, DripCampaignId = campaign.Id, ClientId = client.Id, NextStepIndex = 0, NextSendAt = DateTime.UtcNow.AddMinutes(-5) });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var email = new RecordingEmail();
        await new DidYouKnowEmailDispatchJob(db, email, new StubConsent(), new ConfigurationBuilder().Build(),
            NullLogger<DidYouKnowEmailDispatchJob>.Instance).RunAsync();
        var dyk = Assert.Single(email.Html);
        Assert.Contains("client of Global Business Solution", dyk);
        Assert.Contains("123 Main Street, Toronto ON M5V 1A1, Canada", dyk);
        Assert.Contains("href=\"https://example.test/prefs/tok\"", dyk);
        Assert.Contains("on behalf of Global Business Solution", dyk);

        email = new RecordingEmail();
        await new DripCampaignJob(new UnitOfWork(db), db,
            new NewsLetterDispatcher(new UnitOfWork(db), db, email, new ConfigurationBuilder().Build(), NullLogger<NewsLetterDispatcher>.Instance),
            new StubConsent(), NullLogger<DripCampaignJob>.Instance).RunAsync();
        var drip = Assert.Single(email.Html);
        Assert.Contains("subscribed to updates from Global Business Solution", drip);
        Assert.Contains("123 Main Street, Toronto ON M5V 1A1, Canada", drip);
        Assert.Contains("on behalf of Global Business Solution", drip);
        Assert.DoesNotContain("IPRO adviser", drip);
    }

    // -- helpers -------------------------------------------------------------------------------------

    private sealed class RecordingEmail : IEmailService
    {
        public List<string> Html { get; } = new();
        public async Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null,
            IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
            (await SendDetailedAsync(toEmail, toName, subject, htmlBody, textBody, customArgs, replyToEmail, replyToName, listUnsubscribeUrl)).Success;
        public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null,
            IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        {
            Html.Add(htmlBody);
            return Task.FromResult(EmailSendResult.Sent($"acs-{Html.Count}"));
        }
        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData) => Task.FromResult(true);
    }

    private sealed class StubConsent : IEmailConsentService
    {
        public bool IsSuppressed(Client client, EmailChannel channel, bool designSurvivesOptOut = false) => false;
        public Task<SuppressionResult> SuppressAllAsync(Client client, string source) => throw new NotSupportedException();
        public Task ResubscribeAsync(Client client) => throw new NotSupportedException();
        public bool LiftBounceSuppression(Client client) => throw new NotSupportedException();
        public Task<int> CancelSuppressedDripEnrollmentsAsync(int batchLimit = 500) => Task.FromResult(0);
        public Task<string> GetOrCreateTokenAsync(Client client) => Task.FromResult("tok");
        public string BuildPreferencesUrl(string token) => $"https://example.test/prefs/{token}";
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
