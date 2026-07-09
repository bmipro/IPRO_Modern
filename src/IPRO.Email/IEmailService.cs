namespace IPRO.Email;

public interface IEmailService
{
    Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null);
    Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null);
    Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null);
    Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData);
}

public record EmailRecipient(string Email, string Name);
public record EmailSendResult(bool Success, string Message, string? ProviderMessageId = null)
{
    public static EmailSendResult Sent(string? providerMessageId = null) => new(true, "Email sent.", providerMessageId);
    public static EmailSendResult Failed(string message) => new(false, message);
}
