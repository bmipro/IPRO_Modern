using Azure;
using Azure.Communication.Email;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IPRO.Email;

// Azure Communication Services implementation of IEmailService, adopted 2026-08-30 after Twilio
// suspended the SendGrid account without notice mid-runway (TODO item 431). SendGridEmailService
// stays in the tree; Program.cs switches on EmailSettings.Provider so a provider incident is a
// config flip, not a deploy.
//
// The transient/permanent classification below is the SAME contract the SendGrid implementation
// earned through H7 and JOBS-5/8: account-level failures (401/403/429/5xx, missing config) are
// "not right now" and the send deserves another attempt; payload/recipient 4xxs are permanent
// because retrying the same send IS spam. The drip and newsletter retry machinery depends on
// this split staying identical across providers.
public class AzureEmailService : IEmailService
{
    private readonly EmailSettings _settings;
    private readonly ILogger<AzureEmailService> _logger;

    public AzureEmailService(IOptions<EmailSettings> settings, ILogger<AzureEmailService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    // Same seam pattern as SendGridEmailService.ClientFactory: production builds the real client;
    // tests substitute a stub so the REAL classification below is what gets exercised.
    internal Func<string, EmailClient> ClientFactory = connectionString => new EmailClient(connectionString);

    public async Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
        (await SendDetailedAsync(toEmail, toName, subject, htmlBody, textBody, customArgs, replyToEmail, replyToName, listUnsubscribeUrl)).Success;

    public async Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
    {
        try
        {
            if (!IsConfigured())
            {
                _logger.LogWarning("Azure email is not configured. Email to {Email} was not sent.", toEmail);
                return EmailSendResult.FailedTransient("Azure email is not configured. Check Email__AzureCommunicationConnectionString in Azure app settings.");
            }

            if (string.IsNullOrWhiteSpace(_settings.FromEmail))
            {
                return EmailSendResult.FailedTransient("Sender email is missing. Check Email__FromEmail in Azure app settings.");
            }

            if (string.IsNullOrWhiteSpace(toEmail))
            {
                return EmailSendResult.Failed("Recipient email is missing.");
            }

            var message = BuildMessage(new[] { new EmailRecipient(toEmail, toName) }, subject, htmlBody, textBody);
            if (!string.IsNullOrWhiteSpace(replyToEmail))
            {
                message.ReplyTo.Add(new EmailAddress(replyToEmail, string.IsNullOrWhiteSpace(replyToName) ? null : replyToName));
            }
            else if (!string.IsNullOrWhiteSpace(_settings.ReplyToEmail))
            {
                message.ReplyTo.Add(new EmailAddress(_settings.ReplyToEmail));
            }
            if (customArgs != null)
            {
                // SendGrid echoed custom args back in its events; ACS does not, so the event wave
                // correlates on the operation id instead. The tags still ride as headers so the
                // message itself stays self-describing (and nothing is lost if ACS grows echo
                // support later). Header names must be token-safe, hence the x-ipro- prefix.
                foreach (var arg in customArgs.Where(a => !string.IsNullOrWhiteSpace(a.Key)))
                {
                    message.Headers[$"x-ipro-{arg.Key}"] = arg.Value ?? string.Empty;
                }
            }
            if (!string.IsNullOrWhiteSpace(listUnsubscribeUrl))
            {
                // RFC 8058 one-click unsubscribe - required by Gmail/Yahoo for bulk senders to
                // avoid spam-folder placement, and a strong deliverability signal regardless.
                message.Headers["List-Unsubscribe"] = $"<{listUnsubscribeUrl}>";
                message.Headers["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click";
            }

            var client = ClientFactory(_settings.AzureCommunicationConnectionString);
            // WaitUntil.Started: we want the accepted operation id, not a poll to final delivery --
            // delivery outcomes arrive through the event pipeline, exactly as they did for SendGrid.
            var operation = await client.SendAsync(WaitUntil.Started, message);
            return EmailSendResult.Sent(operation.Id);
        }
        catch (RequestFailedException ex)
        {
            _logger.LogWarning(ex, "Azure email rejected send to {Email}. Status: {Status}", toEmail, ex.Status);
            var failureMessage = $"Azure email rejected the send. Status: {ex.Status}. {Summarize(ex.Message)}";
            return ex.Status is 429 or 401 or 403 || ex.Status >= 500 || ex.Status == 0
                ? EmailSendResult.FailedTransient(failureMessage)
                : EmailSendResult.Failed(failureMessage);
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
        var recipientList = recipients.ToList();
        try
        {
            if (!IsConfigured())
            {
                _logger.LogWarning("Azure email is not configured. Bulk email to {Count} recipients was not sent.", recipientList.Count);
                return false;
            }

            var message = BuildMessage(recipientList, subject, htmlBody, textBody);
            var client = ClientFactory(_settings.AzureCommunicationConnectionString);
            await client.SendAsync(WaitUntil.Started, message);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send bulk email to {Count} recipients", recipientList.Count);
            return false;
        }
    }

    public Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData)
    {
        // Provider-hosted templates were a SendGrid feature this product never adopted (zero
        // callers when the migration happened); every email body is built in our own code. Honest
        // failure beats a silent pretend-send if a caller ever appears.
        _logger.LogWarning("SendTemplateAsync is not supported by the Azure email provider (template {TemplateId} to {Email} was not sent). Build the body in code and use SendAsync.", templateId, toEmail);
        return Task.FromResult(false);
    }

    private EmailMessage BuildMessage(IEnumerable<EmailRecipient> recipients, string subject, string htmlBody, string? textBody)
    {
        var content = new EmailContent(subject) { Html = htmlBody, PlainText = textBody ?? string.Empty };
        var to = recipients.Select(r => new EmailAddress(r.Email, r.Name)).ToList();
        return new EmailMessage(_settings.FromEmail, new EmailRecipients(to), content);
    }

    private bool IsConfigured() =>
        !string.IsNullOrWhiteSpace(_settings.AzureCommunicationConnectionString)
        && _settings.AzureCommunicationConnectionString.Contains("endpoint=", StringComparison.OrdinalIgnoreCase);

    private static string Summarize(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "No detail was returned.";
        message = message.ReplaceLineEndings(" ").Trim();
        return message.Length > 500 ? message[..500] : message;
    }
}
