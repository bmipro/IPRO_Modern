namespace IPRO.Email;

// 593 (2026-10-21): local development only. This PC has no mail service (the local settings leave every
// provider's keys empty, so emails are dropped), and two-step sign-in cannot be set up without the code it
// emails. Email__Provider=Folder (Program.cs takes it only in the Development environment) writes each
// email as an .html file in Email__Folder instead: open the newest file to read the code.
public sealed class FolderEmailService : IEmailService
{
    private readonly string _folder;

    public FolderEmailService(string folder) => _folder = folder;

    public async Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
        (await SendDetailedAsync(toEmail, toName, subject, htmlBody, textBody, customArgs, replyToEmail, replyToName, listUnsubscribeUrl)).Success;

    public async Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
    {
        await WriteAsync(toEmail, subject, htmlBody);
        return EmailSendResult.Sent("folder");
    }

    public async Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null)
    {
        foreach (var recipient in recipients) await WriteAsync(recipient.Email, subject, htmlBody);
        return true;
    }

    public async Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData)
    {
        await WriteAsync(toEmail, "template " + templateId, System.Net.WebUtility.HtmlEncode(System.Text.Json.JsonSerializer.Serialize(templateData)));
        return true;
    }

    // "20261021-101502-123 pat@lee.example.com.html": the newest is last when sorted by name.
    private async Task WriteAsync(string toEmail, string subject, string htmlBody)
    {
        Directory.CreateDirectory(_folder);
        var safeTo = new string((toEmail ?? "unknown").Select(c => char.IsLetterOrDigit(c) || c is '@' or '.' or '-' or '_' ? c : '_').ToArray());
        var name = $"{DateTime.Now:yyyyMMdd-HHmmss-fff} {safeTo}.html";
        var page = $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>{System.Net.WebUtility.HtmlEncode(subject)}</title></head><body>" +
                   $"<p style=\"font-family:Arial,sans-serif;color:#555\">To: {System.Net.WebUtility.HtmlEncode(toEmail)}<br>Subject: {System.Net.WebUtility.HtmlEncode(subject)}</p><hr>{htmlBody}</body></html>";
        await File.WriteAllTextAsync(Path.Combine(_folder, name), page);
    }
}
