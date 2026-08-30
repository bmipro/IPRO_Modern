using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Communication.Email;
using IPRO.Email;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IPRO.IntegrationTests;

// Email provider migration (2026-08-30, TODO 431): Twilio suspended the production SendGrid
// account without notice, killing every outbound email mid-runway. AzureEmailService is the
// replacement; these tests pin that it honours the SAME transient/permanent contract the
// SendGrid implementation earned through H7 and JOBS-5/8 -- the drip/newsletter retry machinery
// depends on that classification, whichever provider is behind the seam.
public class AzureEmailProviderTests
{
    private static AzureEmailService Build(Func<EmailMessage, Task<string>>? sendCore = null, string? connectionString = "endpoint=https://x.canada.communication.azure.com/;accesskey=abc")
    {
        var settings = Options.Create(new EmailSettings
        {
            Provider = "Azure",
            AzureCommunicationConnectionString = connectionString ?? string.Empty
        });
        var service = new AzureEmailService(settings, NullLogger<AzureEmailService>.Instance);
        if (sendCore != null)
        {
            service.ClientFactory = _ => new StubEmailClient(sendCore);
        }
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

    [Fact]
    public async Task Missing_connection_string_is_transient_not_a_verdict_on_the_email()
    {
        // H7: configuration is an ACCOUNT-level condition. Classifying it permanent once turned
        // "the key is missing right now" into "kill the drip enrollment forever".
        var service = Build(connectionString: "");
        var result = await service.SendDetailedAsync("a@example.com", "A", "s", "<p>x</p>");
        Assert.False(result.Success);
        Assert.True(result.IsTransient);
        Assert.Contains("not configured", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(401, true)]   // account broken (bad key / suspended) -- wait for the fix
    [InlineData(403, true)]
    [InlineData(429, true)]   // throttled -- not right now, not never
    [InlineData(500, true)]
    [InlineData(400, false)]  // bad payload/recipient -- retrying IS spam
    [InlineData(413, false)]
    public async Task Provider_rejections_keep_the_H7_transient_permanent_split(int status, bool expectTransient)
    {
        var service = Build(_ => throw new RequestFailedException(status, $"status {status}"));
        var result = await service.SendDetailedAsync("a@example.com", "A", "s", "<p>x</p>");
        Assert.False(result.Success);
        Assert.Equal(expectTransient, result.IsTransient);
    }

    [Fact]
    public async Task A_successful_send_reports_the_operation_id_as_the_provider_message_id()
    {
        // The delivery recorders key on ProviderMessageId; ACS events correlate on operation id,
        // so this is the join key for the event wave.
        var service = Build(_ => Task.FromResult("op-abc-123"));
        var result = await service.SendDetailedAsync("a@example.com", "A", "s", "<p>x</p>");
        Assert.True(result.Success);
        Assert.Equal("op-abc-123", result.ProviderMessageId);
    }

    [Fact]
    public async Task The_message_carries_every_field_the_sendgrid_path_carried()
    {
        EmailMessage? captured = null;
        var service = Build(m => { captured = m; return Task.FromResult("op-1"); });

        var ok = await service.SendAsync(
            "client@example.com", "Client Name", "The subject", "<p>html</p>", "plain",
            customArgs: new Dictionary<string, string> { ["ipro_entity"] = "ecard", ["recipient_id"] = "42" },
            replyToEmail: "michael@example.com", replyToName: "Michael",
            listUnsubscribeUrl: "https://app.iproadvisers.com/u/tok");

        Assert.True(ok);
        Assert.NotNull(captured);
        Assert.Equal("The subject", captured!.Content.Subject);
        Assert.Equal("<p>html</p>", captured.Content.Html);
        Assert.Equal("plain", captured.Content.PlainText);
        Assert.Equal("client@example.com", captured.Recipients.To.Single().Address);
        Assert.Equal("michael@example.com", captured.ReplyTo.Single().Address);
        // RFC 8058 one-click unsubscribe -- Gmail/Yahoo bulk-sender requirement, provider-agnostic.
        Assert.Equal("<https://app.iproadvisers.com/u/tok>", captured.Headers["List-Unsubscribe"]);
        Assert.Equal("List-Unsubscribe=One-Click", captured.Headers["List-Unsubscribe-Post"]);
        // customArgs ride as headers so the correlation tags survive the provider swap.
        Assert.Equal("ecard", captured.Headers["x-ipro-ipro_entity"]);
        Assert.Equal("42", captured.Headers["x-ipro-recipient_id"]);
    }

    [Fact]
    public async Task Bulk_send_reaches_every_recipient()
    {
        EmailMessage? captured = null;
        var service = Build(m => { captured = m; return Task.FromResult("op-bulk"); });
        var ok = await service.SendBulkAsync(
            new[] { new EmailRecipient("a@example.com", "A"), new EmailRecipient("b@example.com", "B") },
            "s", "<p>x</p>");
        Assert.True(ok);
        Assert.Equal(2, captured!.Recipients.To.Count);
    }

    [Fact]
    public void Both_apps_switch_provider_on_configuration_not_on_a_recompile()
    {
        // The registration must branch on EmailSettings.Provider in BOTH entry points -- the whole
        // point of keeping two implementations is that a provider incident is a config flip.
        foreach (var app in new[] { @"src\IPRO.Web\Program.cs", @"src\IPRO.Admin\Program.cs" })
        {
            var program = File.ReadAllText(FindRepoFile(app));
            Assert.Contains("AzureEmailService", program);
            Assert.Contains("SendGridEmailService", program);
            Assert.Contains("Email:Provider", program);
        }
        // And the default stays SendGrid so nothing flips until production config says so.
        Assert.Equal("SendGrid", new EmailSettings().Provider);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }
}
