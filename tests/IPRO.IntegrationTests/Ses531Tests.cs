using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Web.Controllers;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IPRO.IntegrationTests;

// 531 (2026-09-30), the email move: Amazon SES (Canada, Central) granted production access overnight
// (50,000 a day, 14 a second). Email an adviser sends to their own clients moves off Azure
// Communication Services, which retires on 2028-09-30, stream by stream: "notify" (invoices,
// reminders, portal invites, appointments, testimonial requests) and "news" (newsletters, drips,
// cards, letters, polls, Did You Know). Each email names the adviser's business as its sender
// ("Global Business Solution via iPro" -- 530 step two), replies go to the adviser, each adviser is
// an SES tenant, and Amazon's bounce and complaint reports reach Email Activity and the suppression
// list through a verified SNS subscription. Off until Email__Ses__Streams names a stream.
public class Ses531Tests
{
    private static AgentUser Adviser(int id = 42) => new()
    {
        Id = id, FirstName = "Bahman", LastName = "Motamed", CompanyName = "Global Business Solution",
        Email = "bm@example.test", BusinessType = "Accountants"
    };

    private static SesSettings Configured(string streams = "notify,news") => new()
    {
        AccessKeyId = "AKIATESTTESTTESTTEST", SecretAccessKey = "test-secret", AccountId = "354245663230",
        Streams = streams
    };

    // ---- who it is from, which stream it travels on -------------------------------------------------

    [Fact]
    public void The_sender_name_and_tags_say_whose_email_it_is_and_which_stream_carries_it()
    {
        Assert.Equal("Global Business Solution via iPro", AdviserSender.SenderName("Global Business Solution"));
        Assert.Equal("iPro", AdviserSender.SenderName("  "));

        var tags = AdviserSender.Tags(Adviser(), EmailStreams.News, new Dictionary<string, string> { ["ipro_entity"] = "newsletter" });
        Assert.Equal("news", tags["ipro_stream"]);
        Assert.Equal("42", tags["agent_user_id"]);
        Assert.Equal("newsletter", tags["ipro_entity"]);

        var bare = AdviserSender.Tags(null, EmailStreams.Notify);
        Assert.Equal("notify", bare["ipro_stream"]);
        Assert.False(bare.ContainsKey("agent_user_id"));
    }

    [Fact]
    public void Addresses_are_quoted_and_encoded_the_way_mail_expects()
    {
        Assert.Equal("\"Global Business Solution via iPro\" <mail@notify.iproadvisers.com>",
            SesAddress.Format("Global Business Solution via iPro", "mail@notify.iproadvisers.com"));
        Assert.Equal("\"Smith \\\"Sons\\\" Ltd\" <a@b.test>", SesAddress.Format("Smith \"Sons\" Ltd", "a@b.test"));
        Assert.Equal("a@b.test", SesAddress.Format("  ", "a@b.test"));
        Assert.Equal("a@b.test", SesAddress.Format(null, " a@b.test "));

        var accented = SesAddress.Format("Café Planning", "a@b.test");
        Assert.StartsWith("=?UTF-8?B?", accented);
        Assert.EndsWith("?= <a@b.test>", accented);
        var encoded = accented["=?UTF-8?B?".Length..accented.IndexOf("?=", StringComparison.Ordinal)];
        Assert.Equal("Café Planning", Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
    }

    // ---- the send itself ----------------------------------------------------------------------------

    [Fact]
    public async Task A_client_email_goes_out_in_the_advisers_name_on_its_stream_and_tenant()
    {
        var stub = new StubSes();
        var ses = NewSes(stub, Configured());

        var result = await ses.SendAsync(EmailStreams.Notify, "ann@example.test", "Ann Client", "Invoice INV-1 from Global Business Solution",
            "<p>Hi</p>", "Hi", AdviserSender.Tags(Adviser(), EmailStreams.Notify, new Dictionary<string, string> { ["ipro_entity"] = "invoice", ["odd key!"] = "x y" }),
            "bm@example.test", "Global Business Solution", "https://app.test/email-preferences/tok");

        Assert.True(result.Success);
        Assert.Equal("ses-msg-1", result.ProviderMessageId);
        var sent = Assert.Single(stub.Sent);
        Assert.Equal("\"Global Business Solution via iPro\" <mail@notify.iproadvisers.com>", sent.FromEmailAddress);
        Assert.Equal("\"Ann Client\" <ann@example.test>", Assert.Single(sent.Destination.ToAddresses));
        Assert.Equal("\"Global Business Solution\" <bm@example.test>", Assert.Single(sent.ReplyToAddresses));
        Assert.Equal("Invoice INV-1 from Global Business Solution", sent.Content.Simple.Subject.Data);
        Assert.Equal("<p>Hi</p>", sent.Content.Simple.Body.Html.Data);
        Assert.Equal("Hi", sent.Content.Simple.Body.Text.Data);
        Assert.Contains(sent.Content.Simple.Headers, h => h.Name == "List-Unsubscribe" && h.Value == "<https://app.test/email-preferences/tok>");
        Assert.Contains(sent.Content.Simple.Headers, h => h.Name == "List-Unsubscribe-Post" && h.Value == "List-Unsubscribe=One-Click");
        Assert.Equal("ipro-notify", sent.ConfigurationSetName);
        Assert.Equal("adviser-42", sent.TenantName);
        Assert.Contains(sent.EmailTags, t => t.Name == "ipro_stream" && t.Value == "notify");
        Assert.Contains(sent.EmailTags, t => t.Name == "agent_user_id" && t.Value == "42");
        Assert.Contains(sent.EmailTags, t => t.Name == "ipro_entity" && t.Value == "invoice");
        Assert.Contains(sent.EmailTags, t => t.Name == "odd_key_" && t.Value == "x_y");   // SES allows letters, digits, _ and - only

        // The news stream sends from its own subdomain and configuration set.
        await ses.SendAsync(EmailStreams.News, "ann@example.test", "Ann", "S", "<p>x</p>", null,
            AdviserSender.Tags(Adviser(), EmailStreams.News), "bm@example.test", "Global Business Solution", null);
        var news = stub.Sent[1];
        Assert.Equal("\"Global Business Solution via iPro\" <mail@news.iproadvisers.com>", news.FromEmailAddress);
        Assert.Equal("ipro-news", news.ConfigurationSetName);
        Assert.Null(news.Content.Simple.Body.Text);                           // no empty text part
        Assert.True(news.Content.Simple.Headers == null || news.Content.Simple.Headers.Count == 0);
    }

    [Fact]
    public async Task Tenants_are_made_once_with_both_addresses_and_both_configuration_sets()
    {
        var stub = new StubSes { TenantAlreadyExists = true };
        var ses = NewSes(stub, Configured());

        await ses.SendAsync(EmailStreams.Notify, "a@example.test", "A", "S", "<p>x</p>", null, AdviserSender.Tags(Adviser(7), EmailStreams.Notify), null, null, null);
        await ses.SendAsync(EmailStreams.News, "b@example.test", "B", "S", "<p>x</p>", null, AdviserSender.Tags(Adviser(7), EmailStreams.News), null, null, null);

        Assert.Equal(new[] { "adviser-7" }, stub.TenantsCreated);   // once, and "already exists" is fine
        Assert.Equal(new[]
        {
            "adviser-7 arn:aws:ses:ca-central-1:354245663230:identity/notify.iproadvisers.com",
            "adviser-7 arn:aws:ses:ca-central-1:354245663230:identity/news.iproadvisers.com",
            "adviser-7 arn:aws:ses:ca-central-1:354245663230:configuration-set/ipro-notify",
            "adviser-7 arn:aws:ses:ca-central-1:354245663230:configuration-set/ipro-news",
        }, stub.Associations);
        Assert.All(stub.Sent, s => Assert.Equal("adviser-7", s.TenantName));

        // A tenant SES will not create (a missing permission) never stops the email: it goes without one.
        var denied = new StubSes { TenantDenied = true };
        var result = await NewSes(denied, Configured()).SendAsync(EmailStreams.Notify, "a@example.test", "A", "S", "<p>x</p>", null,
            AdviserSender.Tags(Adviser(8), EmailStreams.Notify), null, null, null);
        Assert.True(result.Success);
        Assert.Null(Assert.Single(denied.Sent).TenantName);
    }

    [Fact]
    public void Amazon_failures_keep_the_retry_contract_every_dispatcher_depends_on()
    {
        Assert.True(SesEmailService.Classify(new TooManyRequestsException("slow down")).IsTransient);
        Assert.True(SesEmailService.Classify(new LimitExceededException("quota")).IsTransient);
        Assert.True(SesEmailService.Classify(new AccountSuspendedException("suspended")).IsTransient);
        Assert.True(SesEmailService.Classify(new SendingPausedException("paused")).IsTransient);
        Assert.True(SesEmailService.Classify(new MailFromDomainNotVerifiedException("mail from")).IsTransient);
        Assert.True(SesEmailService.Classify(new NotFoundException("no configuration set")).IsTransient);
        Assert.True(SesEmailService.Classify(new InternalServiceErrorException("oops")).IsTransient);
        Assert.True(SesEmailService.Classify(new HttpRequestException("socket")).IsTransient);

        var rejected = SesEmailService.Classify(new MessageRejectedException("Email address is not verified."));
        Assert.False(rejected.Success);
        Assert.False(rejected.IsTransient);                                   // retrying the same send is spam
        Assert.Contains("Email address is not verified.", rejected.Message);
        Assert.False(SesEmailService.Classify(new BadRequestException("bad")).IsTransient);
    }

    // ---- the switch ---------------------------------------------------------------------------------

    [Fact]
    public void Only_tagged_client_email_on_a_switched_on_stream_goes_to_Amazon()
    {
        var notifyTags = AdviserSender.Tags(Adviser(), EmailStreams.Notify);
        var newsTags = AdviserSender.Tags(Adviser(), EmailStreams.News);

        Assert.Null(RoutingEmailService.SesStream(null, Configured()));                                   // iPro's own mail
        Assert.Null(RoutingEmailService.SesStream(new Dictionary<string, string> { ["ipro_entity"] = "x" }, Configured()));
        Assert.Null(RoutingEmailService.SesStream(notifyTags, Configured(streams: "")));                  // the default: off
        Assert.Equal("notify", RoutingEmailService.SesStream(notifyTags, Configured(streams: "notify")));
        Assert.Null(RoutingEmailService.SesStream(newsTags, Configured(streams: "notify")));
        Assert.Equal("news", RoutingEmailService.SesStream(newsTags, Configured(streams: " notify ; news ")));

        var noKeys = Configured();
        noKeys.SecretAccessKey = "";
        Assert.Null(RoutingEmailService.SesStream(notifyTags, noKeys));                                  // never a half-configured send

        // A pilot moves only the listed advisers' mail.
        var pilot = Configured(streams: "notify");
        pilot.PilotAgentIds = "7, 42";
        Assert.Equal("notify", RoutingEmailService.SesStream(notifyTags, pilot));                       // adviser 42
        Assert.Null(RoutingEmailService.SesStream(AdviserSender.Tags(Adviser(8), EmailStreams.Notify), pilot));
        Assert.Null(RoutingEmailService.SesStream(AdviserSender.Tags(null, EmailStreams.Notify), pilot));
    }

    [Fact]
    public async Task The_router_sends_through_Amazon_or_the_current_provider_and_nothing_else_changes()
    {
        var primary = new RecordingEmail();
        var stub = new StubSes();
        var settings = new EmailSettings { Ses = Configured(streams: "notify") };
        var router = new RoutingEmailService(primary, NewSes(stub, settings.Ses), Options.Create(settings), NullLogger<RoutingEmailService>.Instance);

        await router.SendDetailedAsync("a@example.test", "A", "Reset your password", "<p>x</p>");                       // iPro's own
        await router.SendDetailedAsync("c@example.test", "C", "Newsletter", "<p>x</p>", customArgs: AdviserSender.Tags(Adviser(), EmailStreams.News));
        var viaSes = await router.SendDetailedAsync("b@example.test", "B", "Invoice", "<p>x</p>", customArgs: AdviserSender.Tags(Adviser(), EmailStreams.Notify),
            replyToEmail: "bm@example.test", replyToName: "Global Business Solution");

        Assert.Equal(new[] { "a@example.test", "c@example.test" }, primary.To);
        Assert.Equal("\"B\" <b@example.test>", Assert.Single(Assert.Single(stub.Sent).Destination.ToAddresses));
        Assert.Equal("ses-msg-1", viaSes.ProviderMessageId);
        Assert.True(await router.SendBulkAsync(new[] { new EmailRecipient("x@example.test", "X") }, "S", "<p>x</p>"));
        Assert.Equal(3, primary.To.Count);
    }

    [Fact]
    public void Every_client_send_names_its_stream_and_its_adviser()
    {
        var expected = new (string File, string Stream, int Sends)[]
        {
            (@"src\IPRO.Web\Controllers\ClientInvoicesController.cs", "EmailStreams.Notify", 2),
            (@"src\IPRO.Scheduler\OverdueInvoiceReminderJob.cs", "EmailStreams.Notify", 1),
            (@"src\IPRO.Web\Controllers\ClientsController.cs", "EmailStreams.Notify", 1),
            (@"src\IPRO.Web\Controllers\PortalRequestsController.cs", "EmailStreams.Notify", 2),
            (@"src\IPRO.Web\Controllers\TestimonialsController.cs", "EmailStreams.Notify", 1),
            (@"src\IPRO.Email\NewsLetterDispatcher.cs", "EmailStreams.News", 3),
            (@"src\IPRO.Email\ECardDispatcher.cs", "EmailStreams.News", 1),
            (@"src\IPRO.Email\ELetterDispatcher.cs", "EmailStreams.News", 1),
            (@"src\IPRO.Email\PollDispatcher.cs", "EmailStreams.News", 1),
            (@"src\IPRO.Scheduler\DidYouKnowEmailDispatchJob.cs", "EmailStreams.News", 1),
        };
        foreach (var (file, stream, sends) in expected)
        {
            var src = Read(file);
            var calls = SendCalls(src);
            Assert.True(calls.Count == sends, $"{file}: expected {sends} client send(s), found {calls.Count}");
            // Each send carries the tags inline, or the drip's shared customArgs built by them.
            foreach (var call in calls)
                Assert.True(call.Contains("AdviserSender.Tags(", StringComparison.Ordinal) || call.Contains("customArgs: customArgs", StringComparison.Ordinal),
                    $"{file}: a client send without its stream and adviser:\n{call}");
            Assert.Contains($"AdviserSender.Tags(", src);
            Assert.Contains(stream, src);
            var other = stream == "EmailStreams.Notify" ? "EmailStreams.News" : "EmailStreams.Notify";
            Assert.DoesNotContain(other, src);
        }

        // Both apps register the router in front of the current provider; the web app runs every job.
        var web = Read(@"src\IPRO.Web\Program.cs");
        Assert.Contains("RoutingEmailService", web);
        Assert.Contains("SesEmailService", web);
        Assert.Contains("ISnsTrust", web);
    }

    // ---- Amazon's reports: SNS, verified -------------------------------------------------------------

    [Fact]
    public void An_SNS_message_is_signed_over_its_canonical_form_from_an_Amazon_certificate()
    {
        var note = SnsMessages.Parse(Envelope("Notification", "{\"a\":1}", subject: "Sub"))!;
        Assert.Equal(
            "Message\n{\"a\":1}\nMessageId\nm-1\nSubject\nSub\nTimestamp\n2026-09-30T12:00:00.000Z\nTopicArn\n" + TopicArn + "\nType\nNotification\n",
            SnsMessages.StringToSign(note));

        var confirm = SnsMessages.Parse(Envelope("SubscriptionConfirmation", "You have chosen to subscribe"))!;
        Assert.Equal(
            "Message\nYou have chosen to subscribe\nMessageId\nm-1\nSubscribeURL\nhttps://sns.ca-central-1.amazonaws.com/?Action=ConfirmSubscription&Token=t\n" +
            "Timestamp\n2026-09-30T12:00:00.000Z\nToken\nt\nTopicArn\n" + TopicArn + "\nType\nSubscriptionConfirmation\n",
            SnsMessages.StringToSign(confirm));

        Assert.True(SnsMessages.IsTrustedAwsUrl("https://sns.ca-central-1.amazonaws.com/SimpleNotificationService-abc.pem", "ca-central-1"));
        Assert.False(SnsMessages.IsTrustedAwsUrl("http://sns.ca-central-1.amazonaws.com/x.pem", "ca-central-1"));
        Assert.False(SnsMessages.IsTrustedAwsUrl("https://sns.ca-central-1.amazonaws.com.evil.test/x.pem", "ca-central-1"));
        Assert.False(SnsMessages.IsTrustedAwsUrl("https://evil.test/sns.ca-central-1.amazonaws.com/x.pem", "ca-central-1"));
        Assert.False(SnsMessages.IsTrustedAwsUrl("https://sns.us-east-1.amazonaws.com/x.pem", "ca-central-1"));
        Assert.Equal("ca-central-1", SnsMessages.RegionOf(TopicArn));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    public async Task The_signature_is_checked_with_the_certificate_Amazon_serves(string version)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=sns.amazonaws.com", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var publicOnly = new X509Certificate2(cert.Export(X509ContentType.Cert));

        var unsigned = SnsMessages.Parse(Envelope("Notification", "{\"eventType\":\"Delivery\"}", signatureVersion: version))!;
        var hash = version == "1" ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256;
        var signature = Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(SnsMessages.StringToSign(unsigned)), hash, RSASignaturePadding.Pkcs1));
        var signed = unsigned with { Signature = signature };

        var fetched = new List<string>();
        var trust = new SnsTrust(url => { fetched.Add(url); return Task.FromResult<X509Certificate2?>(publicOnly); }, _ => Task.FromResult(true));
        Assert.True(await trust.VerifyAsync(signed));
        Assert.False(await trust.VerifyAsync(signed with { Message = "{\"eventType\":\"Bounce\"}" }));           // tampered
        Assert.False(await trust.VerifyAsync(signed with { SigningCertUrl = "https://evil.test/cert.pem" }));   // not Amazon's
        Assert.False(await trust.VerifyAsync(signed with { SignatureVersion = "3" }));
        Assert.All(fetched, url => Assert.StartsWith("https://sns.ca-central-1.amazonaws.com/", url));
    }

    [Theory]
    [InlineData("Delivery", null, "delivered", false)]
    [InlineData("Bounce", "Permanent", "bounce", true)]
    [InlineData("Bounce", "Transient", "deferred", false)]
    [InlineData("Bounce", "Undetermined", "deferred", false)]
    [InlineData("Complaint", null, "spamreport", false)]
    [InlineData("Reject", null, "dropped", false)]
    [InlineData("RenderingFailure", null, "dropped", false)]
    [InlineData("DeliveryDelay", null, null, false)]   // 538: Amazon is still trying; the outcome is its own event
    [InlineData("Send", null, "processed", false)]
    [InlineData("Open", null, null, false)]
    [InlineData("SomethingNew", null, null, false)]
    public void Amazons_events_map_onto_what_the_recorders_already_speak(string eventType, string? bounceType, string? mapped, bool hardBounce)
    {
        Assert.Equal(mapped, SesEmailEventsController.MapEvent(eventType, bounceType));
        Assert.Equal(hardBounce, SesEmailEventsController.IsHardBounce(eventType, bounceType));
    }

    // ---- against MySQL: a bounce, a complaint and a delivery reaching the records -------------------

    [Fact]
    public async Task A_bounce_or_a_complaint_from_Amazon_suppresses_the_client_and_a_delivery_is_recorded()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (bounced, complained, delivered) = await SeedThreeNewsletterRecipientsAsync(db);
        var trust = new FakeTrust();

        Assert.IsType<OkResult>(await PostAsync(db, trust, Envelope("Notification", SesEvent("Bounce", "ses-bounce", "\"bounce\":{\"bounceType\":\"Permanent\",\"bounceSubType\":\"General\",\"bouncedRecipients\":[{\"emailAddress\":\"x@example.test\",\"diagnosticCode\":\"smtp; 550 5.1.1 user unknown\"}],\"timestamp\":\"2026-09-30T12:00:00.000Z\"}"))));
        Assert.IsType<OkResult>(await PostAsync(db, trust, Envelope("Notification", SesEvent("Complaint", "ses-complaint", "\"complaint\":{\"complainedRecipients\":[{\"emailAddress\":\"y@example.test\"}],\"complaintFeedbackType\":\"abuse\",\"timestamp\":\"2026-09-30T12:00:00.000Z\"}"))));
        Assert.IsType<OkResult>(await PostAsync(db, trust, Envelope("Notification", SesEvent("Delivery", "ses-delivery", "\"delivery\":{\"timestamp\":\"2026-09-30T12:00:00.000Z\",\"recipients\":[\"z@example.test\"]}"))));
        Assert.IsType<OkResult>(await PostAsync(db, trust, Envelope("Notification", SesEvent("Delivery", "ses-unknown-message", "\"delivery\":{}"))));   // untracked: fine
        db.ChangeTracker.Clear();

        var rows = await db.NewsLetterRecipients.AsNoTracking().Where(r => new[] { bounced.RecipientId, complained.RecipientId, delivered.RecipientId }.Contains(r.Id)).ToListAsync();
        Assert.Equal(NewsLetterRecipientStatus.Bounced, rows.Single(r => r.Id == bounced.RecipientId).Status);
        Assert.Contains("550 5.1.1", rows.Single(r => r.Id == bounced.RecipientId).FailureReason);
        Assert.Equal(NewsLetterRecipientStatus.Unsubscribed, rows.Single(r => r.Id == complained.RecipientId).Status);
        Assert.Equal(NewsLetterRecipientStatus.Delivered, rows.Single(r => r.Id == delivered.RecipientId).Status);

        var clients = await db.Clients.AsNoTracking().Where(c => new[] { bounced.ClientId, complained.ClientId, delivered.ClientId }.Contains(c.Id)).ToListAsync();
        Assert.NotNull(clients.Single(c => c.Id == bounced.ClientId).EmailOptOutAt);       // the address does not exist
        Assert.NotNull(clients.Single(c => c.Id == complained.ClientId).EmailOptOutAt);    // "this is spam" means stop
        Assert.Null(clients.Single(c => c.Id == delivered.ClientId).EmailOptOutAt);
    }

    [Fact]
    public async Task The_endpoint_refuses_anything_it_cannot_trust_and_confirms_only_its_own_topic()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var body = Envelope("Notification", SesEvent("Delivery", "m", "\"delivery\":{}"));

        Assert.IsType<UnauthorizedResult>(await PostAsync(db, new FakeTrust(), body, secret: "wrong"));
        Assert.IsType<UnauthorizedResult>(await PostAsync(db, new FakeTrust(), body, configuredSecret: ""));
        // A plain 403, never Forbid(): on this app that would be a cookie challenge, a redirect to sign-in.
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(await PostAsync(db, new FakeTrust(), Envelope("Notification", "{}", topicArn: "arn:aws:sns:ca-central-1:999999999999:someone-else"))).StatusCode);
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(await PostAsync(db, new FakeTrust { Valid = false }, body)).StatusCode);
        Assert.IsType<BadRequestResult>(await PostAsync(db, new FakeTrust(), "not json"));

        var trust = new FakeTrust();
        Assert.IsType<OkResult>(await PostAsync(db, trust, Envelope("SubscriptionConfirmation", "subscribe?")));
        Assert.Equal(new[] { "https://sns.ca-central-1.amazonaws.com/?Action=ConfirmSubscription&Token=t" }, trust.Confirmed);
    }

    [Fact]
    public void The_endpoint_is_anonymous_but_secret_and_signature_protected_and_the_correlation_is_shared()
    {
        var src = Read(@"src\IPRO.Web\Controllers\SesEmailEventsController.cs");
        Assert.Contains("[AllowAnonymous]", src);
        Assert.Contains("IgnoreAntiforgeryToken", src);
        Assert.Contains("FixedTimeEquals", src);
        Assert.Contains("VerifyAsync(", src);
        Assert.Contains("EventTopicArn", src);
        Assert.Contains("EmailEventCorrelation", src);
        Assert.Contains("EmailEventCorrelation", Read(@"src\IPRO.Web\Controllers\AzureEmailEventsController.cs"));
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private const string TopicArn = "arn:aws:sns:ca-central-1:354245663230:ipro-ses-events";

    private static SesEmailService NewSes(StubSes stub, SesSettings ses)
    {
        var options = Options.Create(new EmailSettings { Ses = ses });
        return new SesEmailService(options, new SesTenants(options, NullLogger<SesTenants>.Instance), new SesPacer(options),
            NullLogger<SesEmailService>.Instance) { ClientFactory = () => stub };
    }

    private static string Envelope(string type, string message, string? subject = null, string topicArn = TopicArn, string signatureVersion = "2")
    {
        var fields = new Dictionary<string, string?>
        {
            ["Type"] = type,
            ["MessageId"] = "m-1",
            ["TopicArn"] = topicArn,
            ["Subject"] = subject,
            ["Message"] = message,
            ["Timestamp"] = "2026-09-30T12:00:00.000Z",
            ["SignatureVersion"] = signatureVersion,
            ["Signature"] = "c2ln",
            ["SigningCertURL"] = "https://sns.ca-central-1.amazonaws.com/SimpleNotificationService-abc.pem",
        };
        if (type != "Notification")
        {
            fields["SubscribeURL"] = "https://sns.ca-central-1.amazonaws.com/?Action=ConfirmSubscription&Token=t";
            fields["Token"] = "t";
        }
        return JsonSerializer.Serialize(fields.Where(f => f.Value != null).ToDictionary(f => f.Key, f => f.Value));
    }

    private static string SesEvent(string eventType, string messageId, string detail) =>
        "{\"eventType\":\"" + eventType + "\",\"mail\":{\"timestamp\":\"2026-09-30T11:59:00.000Z\",\"messageId\":\"" + messageId +
        "\",\"tags\":{\"ipro_entity\":[\"newsletter\"]}}," + detail + "}";

    private static async Task<IActionResult> PostAsync(IPRODbContext db, ISnsTrust trust, string body, string secret = "s3cret", string configuredSecret = "s3cret")
    {
        var consent = new EmailConsentService(db, new ConfigurationBuilder().Build(), NullLogger<EmailConsentService>.Instance, Array.Empty<IUnsubscribeNotifier>());
        var uow = new IPRO.DataAccess.Repositories.UnitOfWork(db);
        var settings = new EmailSettings { Ses = new SesSettings { EventSecret = configuredSecret, EventTopicArn = TopicArn } };
        var controller = new SesEmailEventsController(db, new NewsLetterService(uow, consent, db),
            new EmailDeliveryTracker(db, NullLogger<EmailDeliveryTracker>.Instance, consent), consent,
            Options.Create(settings), trust, NullLogger<SesEmailEventsController>.Instance);
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString("?secret=" + Uri.EscapeDataString(secret));
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        ctx.Request.ContentLength = Encoding.UTF8.GetByteCount(body);
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        return await controller.Index();
    }

    private static async Task<((int RecipientId, int ClientId) Bounced, (int RecipientId, int ClientId) Complained, (int RecipientId, int ClientId) Delivered)> SeedThreeNewsletterRecipientsAsync(IPRODbContext db)
    {
        var agent = new AgentUser
        {
            UserName = $"t531-{Guid.NewGuid():N}"[..20], Email = $"{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Ses", LastName = "Agent", CompanyName = "Ses Co", DomainName = $"t531-{Guid.NewGuid():N}"[..24]
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        var newsletter = new NewsLetter { AgentUserId = agent.Id, Subject = "October", HtmlBody = "<p>x</p>" };
        db.Add(newsletter);
        await db.SaveChangesAsync();

        async Task<(int, int)> One(string messageId)
        {
            var client = new Client { AgentUserId = agent.Id, FirstName = "C", LastName = messageId, Email = $"{Guid.NewGuid():N}"[..12] + "@example.test" };
            db.Add(client);
            await db.SaveChangesAsync();
            var recipient = new NewsLetterRecipient
            {
                NewsLetterId = newsletter.Id, ClientId = client.Id, Email = client.Email, RecipientName = "C",
                UnsubscribeToken = Guid.NewGuid().ToString("N"), SendGridMessageId = messageId, Status = NewsLetterRecipientStatus.Sent
            };
            db.Add(recipient);
            await db.SaveChangesAsync();
            return (recipient.Id, client.Id);
        }

        var result = (await One("ses-bounce"), await One("ses-complaint"), await One("ses-delivery"));
        db.ChangeTracker.Clear();
        return result;
    }

    // The same reading of a send call as AdviserSender530Tests: from the call to its closing ");".
    private static List<string> SendCalls(string source)
    {
        var calls = new List<string>();
        var index = 0;
        while (true)
        {
            var detailed = source.IndexOf(".SendDetailedAsync(", index, StringComparison.Ordinal);
            var plain = source.IndexOf("_email.SendAsync(", index, StringComparison.Ordinal);
            var start = detailed < 0 ? plain : plain < 0 ? detailed : Math.Min(detailed, plain);
            if (start < 0) return calls;
            var end = source.IndexOf(");", start, StringComparison.Ordinal);
            if (end < 0) return calls;
            calls.Add(source[start..(end + 2)]);
            index = end + 2;
        }
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var path = Path.Combine(dir!, relative);
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    // Amazon's own client class, with the five calls 531 makes answered in memory. Nothing leaves the test.
    private sealed class StubSes : AmazonSimpleEmailServiceV2Client
    {
        public StubSes() : base(new BasicAWSCredentials("test", "test"), RegionEndpoint.CACentral1) { }
        public List<SendEmailRequest> Sent { get; } = new();
        public List<string> TenantsCreated { get; } = new();
        public List<string> Associations { get; } = new();
        public bool TenantAlreadyExists { get; init; }
        public bool TenantDenied { get; init; }

        public override Task<SendEmailResponse> SendEmailAsync(SendEmailRequest request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request);
            return Task.FromResult(new SendEmailResponse { MessageId = $"ses-msg-{Sent.Count}" });
        }

        public override Task<CreateTenantResponse> CreateTenantAsync(CreateTenantRequest request, CancellationToken cancellationToken = default)
        {
            if (TenantDenied) throw new AmazonSimpleEmailServiceV2Exception("User is not authorized to perform: ses:CreateTenant") { StatusCode = HttpStatusCode.Forbidden };
            TenantsCreated.Add(request.TenantName);
            if (TenantAlreadyExists) throw new AlreadyExistsException("Tenant already exists");
            return Task.FromResult(new CreateTenantResponse { TenantName = request.TenantName });
        }

        public override Task<CreateTenantResourceAssociationResponse> CreateTenantResourceAssociationAsync(CreateTenantResourceAssociationRequest request, CancellationToken cancellationToken = default)
        {
            Associations.Add($"{request.TenantName} {request.ResourceArn}");
            return Task.FromResult(new CreateTenantResourceAssociationResponse());
        }
    }

    private sealed class FakeTrust : ISnsTrust
    {
        public bool Valid { get; init; } = true;
        public List<string> Confirmed { get; } = new();
        public Task<bool> VerifyAsync(SnsEnvelope envelope) => Task.FromResult(Valid);
        public Task<bool> ConfirmSubscriptionAsync(string subscribeUrl)
        {
            Confirmed.Add(subscribeUrl);
            return Task.FromResult(true);
        }
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
            return Task.FromResult(EmailSendResult.Sent($"acs-{To.Count}"));
        }
        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null)
        {
            To.AddRange(recipients.Select(r => r.Email));
            return Task.FromResult(true);
        }
        public Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData) => Task.FromResult(false);
    }
}
