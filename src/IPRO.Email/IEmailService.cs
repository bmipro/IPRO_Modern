namespace IPRO.Email;

public interface IEmailService
{
    Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null);
    Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null);
    Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null);
    Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData);
}

public record EmailRecipient(string Email, string Name);
public record EmailSendResult(bool Success, string Message)
{
    public static EmailSendResult Sent() => new(true, "Email sent.");
    public static EmailSendResult Failed(string message) => new(false, message);
}
