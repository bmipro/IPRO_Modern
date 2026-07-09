using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace IPRO.Email;

public class SendGridEmailService : IEmailService
{
    private readonly EmailSettings _settings;
    private readonly ILogger<SendGridEmailService> _logger;

    public SendGridEmailService(IOptions<EmailSettings> settings, ILogger<SendGridEmailService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null) =>
        (await SendDetailedAsync(toEmail, toName, subject, htmlBody, textBody, customArgs)).Success;

    public async Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null)
    {
        try
        {
            if (!IsConfigured())
            {
                _logger.LogWarning("SendGrid email is not configured. Email to {Email} was not sent.", toEmail);
                return EmailSendResult.Failed("SendGrid is not configured. Check Email__SendGridApiKey in Azure app settings.");
            }

            if (string.IsNullOrWhiteSpace(_settings.FromEmail))
            {
                return EmailSendResult.Failed("Sender email is missing. Check Email__FromEmail in Azure app settings.");
            }

            if (string.IsNullOrWhiteSpace(toEmail))
            {
                return EmailSendResult.Failed("Recipient email is missing.");
            }

            var client = new SendGridClient(_settings.SendGridApiKey);
            var msg = MailHelper.CreateSingleEmail(
                new EmailAddress(_settings.FromEmail, _settings.FromName),
                new EmailAddress(toEmail, toName),
                subject, textBody ?? string.Empty, htmlBody);
            if (!string.IsNullOrWhiteSpace(_settings.ReplyToEmail))
            {
                msg.SetReplyTo(new EmailAddress(_settings.ReplyToEmail));
            }
            if (customArgs != null)
            {
                foreach (var arg in customArgs.Where(a => !string.IsNullOrWhiteSpace(a.Key)))
                {
                    msg.AddCustomArg(arg.Key, arg.Value ?? string.Empty);
                }
            }

            var response = await client.SendEmailAsync(msg);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Body.ReadAsStringAsync();
                _logger.LogWarning("SendGrid rejected email to {Email}. Status: {StatusCode}. Body: {Body}", toEmail, response.StatusCode, body);
                return EmailSendResult.Failed($"SendGrid rejected the email. Status: {(int)response.StatusCode} {response.StatusCode}. {SummarizeBody(body)}");
            }

            var messageId = response.Headers.TryGetValues("X-Message-Id", out var values)
                ? values.FirstOrDefault()
                : null;
            return EmailSendResult.Sent(messageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {Email}", toEmail);
            return EmailSendResult.Failed($"Email send failed: {ex.Message}");
        }
    }

    public async Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null)
    {
        try
        {
            if (!IsConfigured())
            {
                _logger.LogWarning("SendGrid email is not configured. Bulk email to {Count} recipients was not sent.", recipients.Count());
                return false;
            }

            var client = new SendGridClient(_settings.SendGridApiKey);
            var from = new EmailAddress(_settings.FromEmail, _settings.FromName);
            var tos = recipients.Select(r => new EmailAddress(r.Email, r.Name)).ToList();
            var msg = MailHelper.CreateSingleEmailToMultipleRecipients(
                from, tos, subject, textBody ?? string.Empty, htmlBody);
            var response = await client.SendEmailAsync(msg);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Body.ReadAsStringAsync();
                _logger.LogWarning("SendGrid rejected bulk email to {Count} recipients. Status: {StatusCode}. Body: {Body}", recipients.Count(), response.StatusCode, body);
            }

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send bulk email to {Count} recipients", recipients.Count());
            return false;
        }
    }

    public async Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData)
    {
        try
        {
            if (!IsConfigured())
            {
                _logger.LogWarning("SendGrid email is not configured. Template email {TemplateId} to {Email} was not sent.", templateId, toEmail);
                return false;
            }

            var client = new SendGridClient(_settings.SendGridApiKey);
            var msg = new SendGridMessage
            {
                From = new EmailAddress(_settings.FromEmail, _settings.FromName),
                TemplateId = templateId
            };
            msg.AddTo(new EmailAddress(toEmail, toName));
            msg.SetTemplateData(templateData);
            var response = await client.SendEmailAsync(msg);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Body.ReadAsStringAsync();
                _logger.LogWarning("SendGrid rejected template email {TemplateId} to {Email}. Status: {StatusCode}. Body: {Body}", templateId, toEmail, response.StatusCode, body);
            }

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send template email {TemplateId} to {Email}", templateId, toEmail);
            return false;
        }
    }

    private bool IsConfigured() =>
        !string.IsNullOrWhiteSpace(_settings.SendGridApiKey)
        && !_settings.SendGridApiKey.Contains("YOUR_SENDGRID_KEY", StringComparison.OrdinalIgnoreCase)
        && _settings.SendGridApiKey.StartsWith("SG.", StringComparison.Ordinal);

    private static string SummarizeBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "No response body was returned.";
        }

        body = body.ReplaceLineEndings(" ").Trim();
        return body.Length > 500 ? body[..500] : body;
    }
}
