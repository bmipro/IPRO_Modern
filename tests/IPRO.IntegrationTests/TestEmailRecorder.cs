using System.Collections.Generic;
using System.Threading.Tasks;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.AspNetCore.Http;

namespace IPRO.IntegrationTests;

// 678: an IEmailService for tests that need to see what was sent (or only need one to construct a controller).
public sealed class TestEmailRecorder : IEmailService
{
    public List<(string To, string Subject, string Html, IDictionary<string, string>? Tags)> Sent { get; } = new();
    public EmailSendResult? Answer { get; init; }

    public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null,
        IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
    {
        Sent.Add((toEmail, subject, htmlBody, customArgs));
        return Task.FromResult(Answer ?? EmailSendResult.Sent($"msg-{Sent.Count}"));
    }

    public async Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null,
        IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
        (await SendDetailedAsync(toEmail, toName, subject, htmlBody, textBody, customArgs, replyToEmail, replyToName, listUnsubscribeUrl)).Success;

    public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null) => Task.FromResult(true);

    public Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData) => Task.FromResult(true);

    // 678 (audit D-2): a request on the platform's own address (tests run with no App:BaseUrl, so localhost).
    public static DefaultHttpContext PlatformRequest(string host = "localhost")
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        return context;
    }
}
