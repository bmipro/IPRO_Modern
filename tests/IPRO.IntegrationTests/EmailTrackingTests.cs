using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// 488 (2026-09-14). Microsoft will not enable engagement tracking on a custom domain with default
// sending limits (442); the quota request has been open since 31 August with no decision, and the
// launch is on the 21st. So the platform records opens and clicks itself: a one-pixel image and a
// signed redirect on the platform host, one random token per recipient row, landing in the SAME
// OpenedAt / ClickedAt columns the provider's engagement events would fill, through the same two
// recorders. The Email Activity screen did not have to change shape.
public class EmailTrackingTests
{
    private const string Base = "https://app.example.test";
    private const string Key = "unit-test-signing-key";

    // ---- the token ----------------------------------------------------------------------------

    [Fact]
    public void A_token_is_random_url_safe_and_long_enough()
    {
        var a = EmailTrackingLinks.NewToken();
        var b = EmailTrackingLinks.NewToken();
        Assert.NotEqual(a, b);
        Assert.Equal(32, a.Length);
        Assert.Matches(new Regex("^[A-Za-z0-9_-]+$"), a);
    }

    // ---- the pixel ----------------------------------------------------------------------------

    [Fact]
    public void Instrument_appends_one_pixel_carrying_the_kind_and_token()
    {
        const string html = "<p>Hello</p>";
        var result = EmailTrackingLinks.Instrument(html, "ecard", "tok123", Base, Key);

        Assert.StartsWith(html, result);
        Assert.Single(Regex.Matches(result, "<img", RegexOptions.IgnoreCase));
        Assert.Contains($"{Base}/t/o/ecard/tok123.gif", result);
        Assert.Contains("width=\"1\"", result);
        Assert.Contains("height=\"1\"", result);
    }

    // ---- links --------------------------------------------------------------------------------

    [Fact]
    public void Instrument_rewrites_absolute_links_through_the_signed_redirect()
    {
        const string target = "https://www.example-adviser.test/page?x=1&y=2";
        var html = $"<p><a href=\"{WebUtility.HtmlEncode(target)}\" style=\"color:red\">Read</a></p>";

        var result = EmailTrackingLinks.Instrument(html, "newsletter", "tok123", Base, Key);

        var href = WebUtility.HtmlDecode(Regex.Match(result, "href=\"([^\"]+)\"").Groups[1].Value);
        Assert.StartsWith($"{Base}/t/c/newsletter/tok123?", href);
        var query = ParseQuery(href);
        Assert.Equal(target, query["u"]);
        Assert.True(EmailTrackingLinks.VerifySignature("newsletter", "tok123", target, query["s"], Key));
        // The destination no longer appears as a bare href, and the link's text and styling survive.
        Assert.DoesNotContain($"href=\"{WebUtility.HtmlEncode(target)}\"", result);
        Assert.Contains("style=\"color:red\">Read</a>", result);
    }

    [Theory]
    [InlineData("https://app.example.test/email-preferences?token=abc")]
    [InlineData("https://app.example.test/Newsletter/Unsubscribe?token=abc")]
    [InlineData("https://app.example.test/Poll/Vote?token=abc")]
    [InlineData("mailto:someone@example.test")]
    [InlineData("tel:+14165551234")]
    [InlineData("#top")]
    [InlineData("/relative/path")]
    public void Instrument_leaves_unsubscribe_vote_mailto_tel_and_anchors_alone(string href)
    {
        // The unsubscribe link must stay the exact URL the List-Unsubscribe header carries (RFC 8058),
        // a vote link already carries its own single-use token, and the rest are not web pages.
        var html = $"<a href=\"{WebUtility.HtmlEncode(href)}\">x</a>";
        var result = EmailTrackingLinks.Instrument(html, "ecard", "tok123", Base, Key);

        Assert.Contains($"href=\"{WebUtility.HtmlEncode(href)}\"", result);
        Assert.DoesNotContain("/t/c/", result);
    }

    [Fact]
    public void Image_sources_are_never_rewritten()
    {
        const string html = "<img src=\"https://cdn.example.test/card.png\" alt=\"\">";
        var result = EmailTrackingLinks.Instrument(html, "ecard", "tok123", Base, Key);
        Assert.Contains("src=\"https://cdn.example.test/card.png\"", result);
    }

    [Fact]
    public void Without_a_signing_key_the_pixel_is_added_but_no_link_is_rewritten()
    {
        // An unsigned redirect would be an open redirector on the platform host. No key, no rewrite;
        // opens are still counted.
        const string html = "<a href=\"https://www.example-adviser.test/\">x</a>";
        var result = EmailTrackingLinks.Instrument(html, "ecard", "tok123", Base, signingKey: null);

        Assert.Contains("href=\"https://www.example-adviser.test/\"", result);
        Assert.Contains("/t/o/ecard/tok123.gif", result);
    }

    [Fact]
    public void A_tampered_destination_or_token_fails_the_signature()
    {
        var s = EmailTrackingLinks.Sign("ecard", "tok123", "https://good.example.test/", Key);
        Assert.NotEmpty(s);
        Assert.True(EmailTrackingLinks.VerifySignature("ecard", "tok123", "https://good.example.test/", s, Key));
        Assert.False(EmailTrackingLinks.VerifySignature("ecard", "tok123", "https://evil.example.test/", s, Key));
        Assert.False(EmailTrackingLinks.VerifySignature("ecard", "tok999", "https://good.example.test/", s, Key));
        Assert.False(EmailTrackingLinks.VerifySignature("eletter", "tok123", "https://good.example.test/", s, Key));
        Assert.False(EmailTrackingLinks.VerifySignature("ecard", "tok123", "https://good.example.test/", s + "x", Key));
        Assert.False(EmailTrackingLinks.VerifySignature("ecard", "tok123", "https://good.example.test/", s, "another-key"));
        Assert.False(EmailTrackingLinks.VerifySignature("ecard", "tok123", "https://good.example.test/", null, Key));
    }

    // ---- the settings -------------------------------------------------------------------------

    [Fact]
    public void Platform_tracking_is_on_unless_switched_off()
    {
        Assert.True(new EmailSettings().PlatformTrackingEnabled);
        Assert.True(EmailTrackingLinks.IsEnabled(new ConfigurationBuilder().Build()));

        var off = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Email:PlatformTrackingEnabled"] = "false" })
            .Build();
        Assert.False(EmailTrackingLinks.IsEnabled(off));
    }

    [Fact]
    public void The_signing_key_falls_back_to_the_webhook_secret_already_in_production()
    {
        var hook = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Email:AzureEventWebhookSecret"] = "hook" })
            .Build();
        Assert.Equal("hook", EmailTrackingLinks.SigningKey(hook));

        var own = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:AzureEventWebhookSecret"] = "hook",
                ["Email:TrackingSigningKey"] = "own"
            })
            .Build();
        Assert.Equal("own", EmailTrackingLinks.SigningKey(own));

        Assert.Null(EmailTrackingLinks.SigningKey(new ConfigurationBuilder().Build()));
    }

    // ---- the endpoints, against a real database -----------------------------------------------

    [Fact]
    public async Task The_pixel_stamps_opened_on_the_card_recipient_and_returns_the_image()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (recipientId, token) = await SeedSentCardAsync(db);

        var controller = NewController(db);
        var result = Assert.IsType<FileContentResult>(await controller.Open("ecard", token));
        Assert.Equal("image/gif", result.ContentType);
        Assert.True(result.FileContents.Length > 0);

        db.ChangeTracker.Clear();
        var row = await db.ECardRecipients.AsNoTracking().SingleAsync(r => r.Id == recipientId);
        Assert.NotNull(row.OpenedAt);
        Assert.NotNull(row.DeliveredAt);   // an open proves delivery; the tracker backfills it
        Assert.Null(row.ClickedAt);
        Assert.Equal("open", row.LastEvent);
    }

    [Fact]
    public async Task An_unknown_or_empty_token_still_gets_the_image_and_stamps_nothing()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        // A row that never got a token keeps the column default. An empty token in the URL must
        // NOT match it -- the same trap EmailPreferencesController.FindByTokenAsync guards against.
        var (recipientId, _) = await SeedSentCardAsync(db, withToken: false);

        var controller = NewController(db);
        Assert.IsType<FileContentResult>(await controller.Open("ecard", "no-such-token"));
        Assert.IsType<FileContentResult>(await controller.Open("ecard", ""));
        Assert.IsType<FileContentResult>(await controller.Open("nonsense", "abc"));

        db.ChangeTracker.Clear();
        var row = await db.ECardRecipients.AsNoTracking().SingleAsync(r => r.Id == recipientId);
        Assert.Null(row.OpenedAt);
    }

    [Fact]
    public async Task A_signed_click_stamps_clicked_and_opened_then_redirects()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (recipientId, token) = await SeedSentCardAsync(db);
        const string target = "https://www.example-adviser.test/news?x=1&y=2";
        var s = EmailTrackingLinks.Sign("ecard", token, target, Key);

        var controller = NewController(db);
        var result = Assert.IsType<RedirectResult>(await controller.Click("ecard", token, target, s));
        Assert.Equal(target, result.Url);
        Assert.False(result.Permanent);

        db.ChangeTracker.Clear();
        var row = await db.ECardRecipients.AsNoTracking().SingleAsync(r => r.Id == recipientId);
        Assert.NotNull(row.ClickedAt);
        Assert.NotNull(row.OpenedAt);      // a click is an open too
        Assert.Equal("click", row.LastEvent);
    }

    [Fact]
    public async Task A_click_whose_signature_does_not_match_is_refused_and_stamps_nothing()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (recipientId, token) = await SeedSentCardAsync(db);
        var s = EmailTrackingLinks.Sign("ecard", token, "https://good.example.test/", Key);

        var controller = NewController(db);
        Assert.IsType<BadRequestObjectResult>(await controller.Click("ecard", token, "https://evil.example.test/", s));
        Assert.IsType<BadRequestObjectResult>(await controller.Click("ecard", token, "https://good.example.test/", null));
        Assert.IsType<BadRequestObjectResult>(await controller.Click("ecard", token, null, s));

        db.ChangeTracker.Clear();
        var row = await db.ECardRecipients.AsNoTracking().SingleAsync(r => r.Id == recipientId);
        Assert.Null(row.ClickedAt);
        Assert.Null(row.OpenedAt);
    }

    [Fact]
    public async Task A_newsletter_open_goes_through_the_newsletter_recorder_and_rolls_up()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agent = await SeedAgentAsync(db);
        var newsletter = new NewsLetter { AgentUserId = agent.Id, Subject = "S", HtmlBody = "<p>x</p>" };
        db.NewsLetters.Add(newsletter);
        await db.SaveChangesAsync();
        var send = new NewsLetterSend
        {
            NewsLetterId = newsletter.Id,
            AgentUserId = agent.Id,
            Status = NewsLetterSendStatus.Sent,
            TotalRecipients = 1
        };
        db.NewsLetterSends.Add(send);
        await db.SaveChangesAsync();
        var token = Guid.NewGuid().ToString("N");
        var recipient = new NewsLetterRecipient
        {
            NewsLetterId = newsletter.Id,
            NewsLetterSendId = send.Id,
            Email = "reader@example.test",
            RecipientName = "Reader",
            Status = NewsLetterRecipientStatus.Sent,
            SentAt = DateTime.UtcNow,
            UnsubscribeToken = Guid.NewGuid().ToString("N"),
            TrackingToken = token
        };
        db.NewsLetterRecipients.Add(recipient);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var controller = NewController(db);
        Assert.IsType<FileContentResult>(await controller.Open("newsletter", token));

        db.ChangeTracker.Clear();
        var row = await db.NewsLetterRecipients.AsNoTracking().SingleAsync(r => r.Id == recipient.Id);
        Assert.Equal(NewsLetterRecipientStatus.Opened, row.Status);
        Assert.NotNull(row.OpenedAt);
        var rolledUp = await db.NewsLetterSends.AsNoTracking().SingleAsync(s => s.Id == send.Id);
        Assert.Equal(1, rolledUp.TotalOpened);
    }

    // ---- wiring pins --------------------------------------------------------------------------

    [Theory]
    [InlineData(@"src\IPRO.Email\NewsLetterDispatcher.cs", 2)]   // newsletters AND drip steps
    [InlineData(@"src\IPRO.Email\ECardDispatcher.cs", 1)]
    [InlineData(@"src\IPRO.Email\ELetterDispatcher.cs", 1)]
    [InlineData(@"src\IPRO.Email\PollDispatcher.cs", 1)]
    [InlineData(@"src\IPRO.Scheduler\DidYouKnowEmailDispatchJob.cs", 1)]
    public void Every_marketing_dispatcher_instruments_its_html(string file, int atLeast)
    {
        var src = File.ReadAllText(FindRepoFile(file));
        Assert.True(Regex.Matches(src, @"EmailTrackingLinks\.Instrument\(").Count >= atLeast,
            $"{file} must instrument its outbound HTML at least {atLeast} time(s)");
    }

    [Fact]
    public void The_tracking_paths_are_reserved_on_agent_domains()
    {
        // /t/c/... has no file extension, so without this an agent's custom domain hands it to the
        // public-site slug lookup and every tracked link in that agent's mail 404s.
        var src = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Program.cs"));
        Assert.Matches(new Regex("\"t\"\\s*=>\\s*true"), src);
    }

    [Fact]
    public void Production_gets_the_token_column_and_its_index_on_every_recipient_table()
    {
        var src = File.ReadAllText(FindRepoFile(@"src\IPRO.DataAccess\EmailDeliverySchema.cs"));
        Assert.Contains("TrackingToken", src);
        foreach (var table in new[]
                 {
                     "NewsLetterRecipients", "DripCampaignStepSends", "ECardRecipients",
                     "ELetterRecipients", "PollRecipients", "DidYouKnowEmailQueueItems"
                 })
        {
            Assert.Contains($"\"{table}\"", src);
        }
        Assert.Contains("_tracking_token", src);
    }

    [Fact]
    public void The_screens_count_platform_tracking_as_tracking_except_for_invoice_mail()
    {
        var controller = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\EmailActivityController.cs"));
        Assert.Contains("PlatformTrackingEnabled", controller);
        // Invoice mail is not instrumented (the invoice page records its own views), so its rows must
        // keep saying "not tracked" rather than showing a dash that reads as "nobody opened it".
        Assert.Contains("normalizedType != \"invoice\"", controller);
        var index = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\EmailActivity\Index.cshtml"));
        Assert.Contains("row.TypeKey == \"invoice\"", index);
    }

    [Fact]
    public void The_endpoint_is_anonymous_and_the_image_is_never_cached()
    {
        var src = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\EmailTrackingController.cs"));
        Assert.Contains("[AllowAnonymous]", src);
        Assert.Contains("no-store", src);
    }

    // ---- harness ------------------------------------------------------------------------------

    private static IPRO.Web.Controllers.EmailTrackingController NewController(IPRODbContext db)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Email:AzureEventWebhookSecret"] = Key })
            .Build();
        var consent = new StubConsent();
        var controller = new IPRO.Web.Controllers.EmailTrackingController(
            db,
            new NewsLetterService(new UnitOfWork(db), consent, db),
            new EmailDeliveryTracker(db, NullLogger<EmailDeliveryTracker>.Instance, consent),
            config,
            NullLogger<IPRO.Web.Controllers.EmailTrackingController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return controller;
    }

    private static async Task<AgentUser> SeedAgentAsync(IPRODbContext db)
    {
        var agent = new AgentUser
        {
            UserName = $"trk-{Guid.NewGuid():N}"[..20],
            Email = $"trk-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Track", LastName = "Agent",
            DomainName = $"trk-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario"
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent;
    }

    private static async Task<(int RecipientId, string Token)> SeedSentCardAsync(IPRODbContext db, bool withToken = true)
    {
        var agent = await SeedAgentAsync(db);
        var client = new Client
        {
            AgentUserId = agent.Id,
            FirstName = "Card", LastName = "Reader",
            Email = $"reader-{Guid.NewGuid():N}"[..14] + "@example.test"
        };
        db.Add(client);
        var card = new ECard { AgentUserId = agent.Id, Occasion = "Birthday", Subject = "Happy birthday" };
        db.Add(card);
        await db.SaveChangesAsync();

        var token = withToken ? Guid.NewGuid().ToString("N") : string.Empty;
        var recipient = new ECardRecipient
        {
            ECardId = card.Id,
            ClientId = client.Id,
            Email = client.Email,
            RecipientName = "Card Reader",
            Status = ECardRecipientStatuses.Sent,
            SentAt = DateTime.UtcNow,
            TrackingToken = token
        };
        db.Add(recipient);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (recipient.Id, token);
    }

    private static Dictionary<string, string> ParseQuery(string url)
    {
        var result = new Dictionary<string, string>();
        var q = url.IndexOf('?');
        if (q < 0) return result;
        foreach (var pair in url[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var name = eq < 0 ? pair : pair[..eq];
            var value = eq < 0 ? string.Empty : pair[(eq + 1)..];
            result[Uri.UnescapeDataString(name)] = Uri.UnescapeDataString(value);
        }
        return result;
    }

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }

    private sealed class StubConsent : IEmailConsentService
    {
        public bool IsSuppressed(Client client, EmailChannel channel, bool designSurvivesOptOut = false) => false;
        public Task<SuppressionResult> SuppressAllAsync(Client client, string source) => throw new NotSupportedException();
        public Task ResubscribeAsync(Client client) => throw new NotSupportedException();
        public Task<int> CancelSuppressedDripEnrollmentsAsync(int batchLimit = 500) => Task.FromResult(0);
        public Task<string> GetOrCreateTokenAsync(Client client) => Task.FromResult("tok");
        public string BuildPreferencesUrl(string token) => $"https://example.test/prefs/{token}";
    }
}
