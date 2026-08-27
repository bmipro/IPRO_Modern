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

    // H7 test seam, mirroring PublicHostGuard.ResolveHook: production always builds the real
    // client; tests substitute a stub HTTP conversation so the REAL classification below is what
    // gets exercised, not a copy of it living in the test.
    internal Func<string, ISendGridClient> ClientFactory = key => new SendGridClient(key);

    public async Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
        (await SendDetailedAsync(toEmail, toName, subject, htmlBody, textBody, customArgs, replyToEmail, replyToName, listUnsubscribeUrl)).Success;

    public async Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
    {
        try
        {
            if (!IsConfigured())
            {
                // H7: configuration is an ACCOUNT-level condition, not a verdict on this email.
                // An admin sets the key and every queued send should then proceed; classifying
                // this as permanent turned "the key is missing right now" into "kill the work
                // forever" — one bad deploy of app settings failed every due drip enrollment
                // with no way back.
                _logger.LogWarning("SendGrid email is not configured. Email to {Email} was not sent.", toEmail);
                return EmailSendResult.FailedTransient("SendGrid is not configured. Check Email__SendGridApiKey in Azure app settings.");
            }

            if (string.IsNullOrWhiteSpace(_settings.FromEmail))
            {
                // H7: same reasoning — a missing sender is fixed in config, not by discarding work.
                return EmailSendResult.FailedTransient("Sender email is missing. Check Email__FromEmail in Azure app settings.");
            }

            if (string.IsNullOrWhiteSpace(toEmail))
            {
                return EmailSendResult.Failed("Recipient email is missing.");
            }

            var client = ClientFactory(_settings.SendGridApiKey);
            var msg = MailHelper.CreateSingleEmail(
                new EmailAddress(_settings.FromEmail, _settings.FromName),
                new EmailAddress(toEmail, toName),
                subject, textBody ?? string.Empty, htmlBody);
            if (!string.IsNullOrWhiteSpace(replyToEmail))
            {
                msg.SetReplyTo(new EmailAddress(replyToEmail, string.IsNullOrWhiteSpace(replyToName) ? null : replyToName));
            }
            else if (!string.IsNullOrWhiteSpace(_settings.ReplyToEmail))
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
            if (!string.IsNullOrWhiteSpace(listUnsubscribeUrl))
            {
                // RFC 8058 one-click unsubscribe - required by Gmail/Yahoo for bulk senders to
                // avoid spam-folder placement, and a strong deliverability signal regardless.
                msg.AddHeader("List-Unsubscribe", $"<{listUnsubscribeUrl}>");
                msg.AddHeader("List-Unsubscribe-Post", "List-Unsubscribe=One-Click");
            }

            var response = await client.SendEmailAsync(msg);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Body.ReadAsStringAsync();
                _logger.LogWarning("SendGrid rejected email to {Email}. Status: {StatusCode}. Body: {Body}", toEmail, response.StatusCode, body);
                var failureMessage = $"SendGrid rejected the email. Status: {(int)response.StatusCode} {response.StatusCode}. {SummarizeBody(body)}";
                // JOBS-5/8: 429 and 5xx are "not right now", not "never" -- transient, retryable.
                // H7: 401 and 403 join them. They mean the ACCOUNT is broken (rotated/revoked
                // key, exhausted credits, unverified sender) — the recipient was never the
                // problem, and the account being fixed is exactly the outcome to wait for.
                // Classifying them permanent meant one key rotation marked every due drip
                // enrollment Failed on its first attempt, and recovery was re-enrolling — which
                // re-sends every prior step. What stays permanent: the recipient/payload 4xxs
                // (400 bad payload, 413 too large...), where retrying the same send IS spam.
                var statusCode = (int)response.StatusCode;
                return statusCode is 429 or 401 or 403 || statusCode >= 500
                    ? EmailSendResult.FailedTransient(failureMessage)
                    : EmailSendResult.Failed(failureMessage);
            }

            var messageId = response.Headers.TryGetValues("X-Message-Id", out var values)
                ? values.FirstOrDefault()
                : null;
            return EmailSendResult.Sent(messageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {Email}", toEmail);
            // The HTTP conversation itself failed (timeout, DNS, socket): outcome unknown, retryable.
            return EmailSendResult.FailedTransient($"Email send failed: {ex.Message}");
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

            var client = ClientFactory(_settings.SendGridApiKey);
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

            var client = ClientFactory(_settings.SendGridApiKey);
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
