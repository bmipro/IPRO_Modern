namespace IPRO.Email;

public interface IEmailService
{
    Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null);
    Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null);
    Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null);
    Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData);
}

public record EmailRecipient(string Email, string Name);
public record EmailSendResult(bool Success, string Message, string? ProviderMessageId = null)
{
    public static EmailSendResult Sent(string? providerMessageId = null) => new(true, "Email sent.", providerMessageId);
    public static EmailSendResult Failed(string message) => new(false, message);

    // JOBS-5/JOBS-8 (2026-08-20): "SendGrid answered no" and "SendGrid never really answered" are
    // different outcomes. A 4xx rejection is permanent (bad address, bad payload) and retrying it
    // forever is spam; a timeout, socket error, 429 or 5xx is the network's problem and the send
    // deserves another attempt. Callers that retire work on failure must check IsTransient first.
    public static EmailSendResult FailedTransient(string message) => new(false, message) { IsTransient = true };
    public bool IsTransient { get; init; }
}
