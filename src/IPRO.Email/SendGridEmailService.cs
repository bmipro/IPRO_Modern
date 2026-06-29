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

    public async Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null)
    {
        try
        {
            var client = new SendGridClient(_settings.SendGridApiKey);
            var msg = MailHelper.CreateSingleEmail(
                new EmailAddress(_settings.FromEmail, _settings.FromName),
                new EmailAddress(toEmail, toName),
                subject, textBody ?? string.Empty, htmlBody);
            msg.SetReplyTo(new EmailAddress(_settings.ReplyToEmail));
            var response = await client.SendEmailAsync(msg);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {Email}", toEmail);
            return false;
        }
    }

    public async Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null)
    {
        try
        {
            var client = new SendGridClient(_settings.SendGridApiKey);
            var from = new EmailAddress(_settings.FromEmail, _settings.FromName);
            var tos = recipients.Select(r => new EmailAddress(r.Email, r.Name)).ToList();
            var msg = MailHelper.CreateSingleEmailToMultipleRecipients(
                from, tos, subject, textBody ?? string.Empty, htmlBody);
            var response = await client.SendEmailAsync(msg);
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
            var client = new SendGridClient(_settings.SendGridApiKey);
            var msg = new SendGridMessage
            {
                From = new EmailAddress(_settings.FromEmail, _settings.FromName),
                TemplateId = templateId
            };
            msg.AddTo(new EmailAddress(toEmail, toName));
            msg.SetTemplateData(templateData);
            var response = await client.SendEmailAsync(msg);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send template email {TemplateId} to {Email}", templateId, toEmail);
            return false;
        }
    }
}
