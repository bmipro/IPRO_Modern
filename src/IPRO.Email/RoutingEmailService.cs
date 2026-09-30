using IPRO.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IPRO.Email;

// 531 (2026-09-30): the one switch between email providers. An email to an adviser's client carries
// AdviserSender.Tags -- its stream and its adviser. Once that stream is listed in Email__Ses__Streams
// (and the SES keys are present, and the adviser is in the pilot when there is one), it goes through
// Amazon SES; everything else -- iPro's own mail to advisers, a stream still switched off, a
// half-configured SES -- goes through the current provider exactly as before. Rolling back is
// clearing the setting.
public sealed class RoutingEmailService : IEmailService
{
    private readonly IEmailService _primary;
    private readonly SesEmailService _ses;
    private readonly EmailSettings _settings;
    private readonly ILogger<RoutingEmailService> _logger;

    public RoutingEmailService(IEmailService primary, SesEmailService ses, IOptions<EmailSettings> settings, ILogger<RoutingEmailService> logger)
    {
        _primary = primary;
        _ses = ses;
        _settings = settings.Value;
        _logger = logger;
    }

    // The SES stream this email goes out on, or null for the current provider.
    public static string? SesStream(IDictionary<string, string>? customArgs, SesSettings ses)
    {
        if (customArgs == null || !customArgs.TryGetValue(AdviserSender.StreamTag, out var tagged) || string.IsNullOrWhiteSpace(tagged))
            return null;
        var stream = tagged.Trim().ToLowerInvariant();
        if (stream != EmailStreams.Notify && stream != EmailStreams.News) return null;
        if (!ses.StreamEnabled(stream) || !ses.IsConfigured) return null;

        // A pilot: while Email__Ses__PilotAgentIds lists advisers, only their mail moves.
        var pilot = (ses.PilotAgentIds ?? string.Empty).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pilot.Length > 0)
        {
            customArgs.TryGetValue(AdviserSender.AdviserTag, out var adviser);
            if (string.IsNullOrWhiteSpace(adviser) || !pilot.Contains(adviser.Trim(), StringComparer.Ordinal)) return null;
        }
        return stream;
    }

    public async Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
        (await SendDetailedAsync(toEmail, toName, subject, htmlBody, textBody, customArgs, replyToEmail, replyToName, listUnsubscribeUrl)).Success;

    public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
    {
        var stream = SesStream(customArgs, _settings.Ses);
        if (stream != null)
            return _ses.SendAsync(stream, toEmail, toName, subject, htmlBody, textBody, customArgs, replyToEmail, replyToName, listUnsubscribeUrl);

        if (customArgs != null && customArgs.TryGetValue(AdviserSender.StreamTag, out var tagged)
            && _settings.Ses.StreamEnabled(tagged) && !_settings.Ses.IsConfigured)
        {
            _logger.LogWarning("Email stream {Stream} is switched to Amazon SES but its keys are missing; sending through {Provider} instead.",
                tagged, _settings.Provider);
        }
        return _primary.SendDetailedAsync(toEmail, toName, subject, htmlBody, textBody, customArgs, replyToEmail, replyToName, listUnsubscribeUrl);
    }

    // Only iPro's own notices use these; they stay on the current provider.
    public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null) =>
        _primary.SendBulkAsync(recipients, subject, htmlBody, textBody);

    public Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData) =>
        _primary.SendTemplateAsync(toEmail, toName, templateId, templateData);
}
