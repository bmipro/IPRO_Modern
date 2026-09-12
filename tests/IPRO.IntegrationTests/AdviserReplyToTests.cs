using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Communication.Email;
using IPRO.Email;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SendGrid;
using SendGrid.Helpers.Mail;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 480 (2026-09-12). Reply-To on every client-facing message is the sending adviser's own
// address, whatever domain it is at. 440 (2026-09-01) had replaced a free-webmail address (Gmail,
// Yahoo, Hotmail...) with the support address to avoid SpamAssassin's FREEMAIL_FORGED_REPLYTO
// (+2.5 on receivers that run it); the owner's drip test on 09-12 showed the cost of that: a
// client's reply to their adviser landed at support@iproadvisers.com, which would have to be
// watched around the clock and relayed by hand, for every client of every adviser on Gmail. A
// reply that reaches the adviser outweighs a partial spam score, so the substitution is gone from
// both providers and so is the classifier. The support address remains only the fallback when no
// Reply-To is given at all.
public class AdviserReplyToTests
{
    // ---- ACS ------------------------------------------------------------------------------------

    [Fact]
    public async Task Azure_keeps_a_webmail_reply_to_including_the_name()
    {
        EmailMessage? captured = null;
        var service = BuildAzure(m => { captured = m; return Task.FromResult("op-1"); });

        var ok = await service.SendAsync("client@example.com", "Client", "s", "<p>x</p>",
            replyToEmail: "bmotamed@yahoo.com", replyToName: "Bahman Motamed");

        Assert.True(ok);
        Assert.NotNull(captured);
        var replyTo = Assert.Single(captured!.ReplyTo);
        Assert.Equal("bmotamed@yahoo.com", replyTo.Address);
        Assert.Equal("Bahman Motamed", replyTo.DisplayName);
    }

    [Fact]
    public async Task Azure_keeps_a_business_reply_to_including_the_name()
    {
        EmailMessage? captured = null;
        var service = BuildAzure(m => { captured = m; return Task.FromResult("op-1"); });

        await service.SendAsync("client@example.com", "Client", "s", "<p>x</p>",
            replyToEmail: "michaeltran@alladvisers.com", replyToName: "Michael Tran");

        var replyTo = Assert.Single(captured!.ReplyTo);
        Assert.Equal("michaeltran@alladvisers.com", replyTo.Address);
        Assert.Equal("Michael Tran", replyTo.DisplayName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Azure_falls_back_to_the_support_address_only_when_no_reply_to_is_given(string? replyToEmail)
    {
        EmailMessage? captured = null;
        var service = BuildAzure(m => { captured = m; return Task.FromResult("op-1"); });

        await service.SendAsync("client@example.com", "Client", "s", "<p>x</p>", replyToEmail: replyToEmail);

        Assert.Equal("support@iproadvisers.com", Assert.Single(captured!.ReplyTo).Address);
    }

    // ---- SendGrid (Email:Provider can flip back at any time; the two seams must not drift) ------

    [Fact]
    public async Task SendGrid_keeps_a_webmail_reply_to_including_the_name()
    {
        var client = new CapturingSendGridClient();
        var service = BuildSendGrid(client);

        var ok = await service.SendAsync("client@example.com", "Client", "s", "<p>x</p>",
            replyToEmail: "someone@gmail.com", replyToName: "Someone");

        Assert.True(ok);
        Assert.NotNull(client.LastMessage);
        Assert.Equal("someone@gmail.com", client.LastMessage!.ReplyTo?.Email);
        Assert.Equal("Someone", client.LastMessage.ReplyTo?.Name);
    }

    [Fact]
    public async Task SendGrid_keeps_a_business_reply_to_including_the_name()
    {
        var client = new CapturingSendGridClient();
        var service = BuildSendGrid(client);

        await service.SendAsync("client@example.com", "Client", "s", "<p>x</p>",
            replyToEmail: "michaeltran@alladvisers.com", replyToName: "Michael Tran");

        Assert.Equal("michaeltran@alladvisers.com", client.LastMessage!.ReplyTo?.Email);
        Assert.Equal("Michael Tran", client.LastMessage.ReplyTo?.Name);
    }

    [Fact]
    public async Task SendGrid_falls_back_to_the_support_address_only_when_no_reply_to_is_given()
    {
        var client = new CapturingSendGridClient();
        var service = BuildSendGrid(client);

        await service.SendAsync("client@example.com", "Client", "s", "<p>x</p>");

        Assert.Equal("support@iproadvisers.com", client.LastMessage!.ReplyTo?.Email);
    }

    // ---- nothing classifies webmail any more -------------------------------------------------

    [Fact]
    public void No_provider_seam_substitutes_the_reply_to()
    {
        Assert.False(File.Exists(FindRepoFile(@"src\IPRO.Utility\FreemailDomains.cs")), "the freemail classifier should be gone");
        foreach (var provider in new[] { @"src\IPRO.Email\AzureEmailService.cs", @"src\IPRO.Email\SendGridEmailService.cs" })
        {
            var source = File.ReadAllText(FindRepoFile(provider));
            Assert.DoesNotContain("IsFreemail", source);
            Assert.DoesNotContain("FreemailDomains", source);
        }
    }

    // ---- builders -----------------------------------------------------------------------------

    private static AzureEmailService BuildAzure(Func<EmailMessage, Task<string>> sendCore)
    {
        var settings = Options.Create(new EmailSettings
        {
            Provider = "Azure",
            AzureCommunicationConnectionString = "endpoint=https://x.canada.communication.azure.com/;accesskey=abc",
            FromEmail = "support@iproadvisers.com",
            FromName = "IPRO Advisers",
            ReplyToEmail = "support@iproadvisers.com"
        });
        var service = new AzureEmailService(settings, NullLogger<AzureEmailService>.Instance);
        service.ClientFactory = _ => new StubEmailClient(sendCore);
        return service;
    }

    private static SendGridEmailService BuildSendGrid(ISendGridClient client)
    {
        var settings = Options.Create(new EmailSettings
        {
            Provider = "SendGrid",
            SendGridApiKey = "SG.test-key",
            FromEmail = "support@iproadvisers.com",
            FromName = "IPRO Advisers",
            ReplyToEmail = "support@iproadvisers.com"
        });
        var service = new SendGridEmailService(settings, NullLogger<SendGridEmailService>.Instance);
        service.ClientFactory = _ => client;
        return service;
    }

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }

    private sealed class StubEmailClient : EmailClient
    {
        private readonly Func<EmailMessage, Task<string>> _sendCore;
        public StubEmailClient(Func<EmailMessage, Task<string>> sendCore) => _sendCore = sendCore;

        public override async Task<EmailSendOperation> SendAsync(WaitUntil wait, EmailMessage message, CancellationToken cancellationToken = default)
        {
            var id = await _sendCore(message);
            return new StubOperation(id);
        }

        private sealed class StubOperation : EmailSendOperation
        {
            private readonly string _id;
            public StubOperation(string id) => _id = id;
            public override string Id => _id;
        }
    }

    private sealed class CapturingSendGridClient : ISendGridClient
    {
        public SendGridMessage? LastMessage;

        public string UrlPath { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string MediaType { get; set; } = string.Empty;

        public System.Net.Http.Headers.AuthenticationHeaderValue AddAuthorization(KeyValuePair<string, string> header) =>
            new("Bearer", "test");

        public Task<SendGrid.Response> MakeRequest(HttpRequestMessage request, CancellationToken cancellationToken = default) =>
            Task.FromResult(Accepted());

        public Task<SendGrid.Response> RequestAsync(BaseClient.Method method, string? requestBody = null,
            string? queryParams = null, string? urlPath = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Accepted());

        public Task<SendGrid.Response> SendEmailAsync(SendGridMessage msg, CancellationToken cancellationToken = default)
        {
            LastMessage = msg;
            return Task.FromResult(Accepted());
        }

        private static SendGrid.Response Accepted()
        {
            // Real headers: the success path reads X-Message-Id (see DripRecoveryTests for the
            // NRE this once masked).
            var carrier = new HttpResponseMessage(HttpStatusCode.Accepted);
            carrier.Headers.TryAddWithoutValidation("X-Message-Id", "stub-message-id");
            return new SendGrid.Response(HttpStatusCode.Accepted, new StringContent(string.Empty), carrier.Headers);
        }
    }
}
