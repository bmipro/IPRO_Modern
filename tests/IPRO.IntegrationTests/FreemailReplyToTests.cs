using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Communication.Email;
using IPRO.Email;
using IPRO.Utility;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SendGrid;
using SendGrid.Helpers.Mail;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 440 (2026-08-31). Every client-facing sender sets Reply-To to the sending agent's own
// address so a client's reply reaches their adviser. When that address is free webmail the message
// ships as business-domain From + freemail Reply-To -- the header signature of business-email-
// compromise -- and SpamAssassin charges 2.503 for it (FREEMAIL_FORGED_REPLYTO). Measured on
// mail-tester the same day with everything else identical: 7.7/10 from an agent on Yahoo, 10/10
// from an agent on a business domain. A large share of independent Canadian advisers sign up with
// Gmail, so this is structural, not one account.
//
// Enforced at the PROVIDER seam rather than at the six call sites, so no future caller can
// reintroduce it and both providers behave identically: a freemail Reply-To is replaced by the
// configured support address. The adviser's own address stays in the signature as a mailto: link
// (ECardHtmlComposer et al.), so the client is still one tap from them.
//
// NOT done here, deliberately: carrying the adviser's name in the From display name. ACS rejects
// "Display Name <addr>" in senderAddress with a 400 -- the display name is fixed per MailFrom
// address in Azure -- so From stays "IPRO Advisers", exactly what it is today.
//
// This is a SEPARATE defect from the Gmail-delivery scare of the same day (439): re-sending from a
// business-domain agent scored 10/10 and was still absent from the one test mailbox, which turned
// out to be that mailbox. Fixing this buys 2.5 SpamAssassin points on every corporate mail server;
// it is not a deliverability cure and is not claimed as one.
public class FreemailReplyToTests
{
    // ---- the classifier -----------------------------------------------------------------------

    [Theory]
    [InlineData("bmotamed@yahoo.com")]           // the address that produced the 7.7
    [InlineData("someone@gmail.com")]
    [InlineData("SOMEONE@GMAIL.COM")]            // case-insensitive
    [InlineData("  someone@hotmail.com  ")]      // trimmed
    [InlineData("x@googlemail.com")]
    [InlineData("x@outlook.com")]
    [InlineData("x@live.ca")]
    [InlineData("x@yahoo.ca")]
    [InlineData("x@icloud.com")]
    [InlineData("x@aol.com")]
    [InlineData("x@protonmail.com")]
    public void Consumer_webmail_is_freemail(string email)
    {
        Assert.True(FreemailDomains.IsFreemail(email));
    }

    [Theory]
    [InlineData("michaeltran@alladvisers.com")]  // the address that produced the 10/10
    [InlineData("support@iproadvisers.com")]
    [InlineData("x@mail.yahoo.com")]             // exact registrable domain only, no substring guessing
    [InlineData("x@gmail.com.example.net")]
    [InlineData("x@notgmail.com")]
    public void A_business_domain_is_not_freemail(string email)
    {
        Assert.False(FreemailDomains.IsFreemail(email));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-address")]
    [InlineData("@gmail.com")]
    [InlineData("x@")]
    public void Garbage_is_not_freemail(string? email)
    {
        // A malformed address is not the freemail pattern. It is the caller's problem and the
        // provider will reject it on its own terms; this classifier must not throw on it.
        Assert.False(FreemailDomains.IsFreemail(email));
    }

    // ---- ACS honours it -----------------------------------------------------------------------

    [Fact]
    public async Task Azure_replaces_a_freemail_reply_to_with_the_support_address()
    {
        EmailMessage? captured = null;
        var service = BuildAzure(m => { captured = m; return Task.FromResult("op-1"); });

        var ok = await service.SendAsync("client@example.com", "Client", "s", "<p>x</p>",
            replyToEmail: "bmotamed@yahoo.com", replyToName: "Bahman Motamed");

        Assert.True(ok);
        Assert.NotNull(captured);
        var replyTo = Assert.Single(captured!.ReplyTo);
        Assert.Equal("support@iproadvisers.com", replyTo.Address);
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

    [Fact]
    public async Task Azure_falls_back_to_the_support_address_when_no_reply_to_is_given()
    {
        // The fallback the freemail case now relies on; pinned so it cannot quietly disappear.
        EmailMessage? captured = null;
        var service = BuildAzure(m => { captured = m; return Task.FromResult("op-1"); });

        await service.SendAsync("client@example.com", "Client", "s", "<p>x</p>");

        Assert.Equal("support@iproadvisers.com", Assert.Single(captured!.ReplyTo).Address);
    }

    // ---- SendGrid honours it (Email:Provider can flip back at any time) -----------------------

    [Fact]
    public async Task SendGrid_replaces_a_freemail_reply_to_with_the_support_address()
    {
        var client = new CapturingSendGridClient();
        var service = BuildSendGrid(client);

        var ok = await service.SendAsync("client@example.com", "Client", "s", "<p>x</p>",
            replyToEmail: "someone@gmail.com", replyToName: "Someone");

        Assert.True(ok);
        Assert.NotNull(client.LastMessage);
        Assert.Equal("support@iproadvisers.com", client.LastMessage!.ReplyTo?.Email);
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
