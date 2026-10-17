using IPRO.Utility;
using Microsoft.Extensions.Logging;

namespace IPRO.Email;

// 585: email on a staging copy of iPro (StagingGuard). Only the addresses Email:OnlyTo names are sent to,
// through the real provider; any other is written to the log and NOT sent, and reported as sent, so a
// newsletter or a reminder job finishes its work on staging exactly as it would in production without
// one message reaching a real person. Sits in front of everything that sends (both apps register it
// last, around whatever IEmailService they built).
public sealed class RestrictedEmailService : IEmailService
{
    private readonly IEmailService _inner;
    private readonly IReadOnlyList<string> _onlyTo;
    private readonly ILogger _logger;

    public RestrictedEmailService(IEmailService inner, IReadOnlyList<string> onlyTo, ILogger logger)
    {
        _inner = inner;
        _onlyTo = onlyTo;
        _logger = logger;
    }

    public async Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
        (await SendDetailedAsync(toEmail, toName, subject, htmlBody, textBody, customArgs, replyToEmail, replyToName, listUnsubscribeUrl)).Success;

    public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
    {
        if (StagingGuard.MaySend(_onlyTo, toEmail))
            return _inner.SendDetailedAsync(toEmail, toName, subject, htmlBody, textBody, customArgs, replyToEmail, replyToName, listUnsubscribeUrl);
        _logger.LogInformation("Staging: an email \"{Subject}\" was not sent (its address is not in Email:OnlyTo)", subject);
        return Task.FromResult(EmailSendResult.Sent("staging-not-sent"));
    }

    public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null)
    {
        var allowed = recipients.Where(r => StagingGuard.MaySend(_onlyTo, r.Email)).ToList();
        var held = recipients.Count() - allowed.Count;
        if (held > 0) _logger.LogInformation("Staging: {Count} recipient(s) of \"{Subject}\" were not sent to", held, subject);
        return allowed.Count == 0 ? Task.FromResult(true) : _inner.SendBulkAsync(allowed, subject, htmlBody, textBody);
    }

    public Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData)
    {
        if (StagingGuard.MaySend(_onlyTo, toEmail)) return _inner.SendTemplateAsync(toEmail, toName, templateId, templateData);
        _logger.LogInformation("Staging: a template email ({TemplateId}) was not sent (its address is not in Email:OnlyTo)", templateId);
        return Task.FromResult(true);
    }
}

public static class RestrictedEmailRegistration
{
    // 585: wraps whatever IEmailService the app registered last in RestrictedEmailService, when
    // Email:OnlyTo names any address (a staging copy must; StagingGuard). Nothing changes otherwise.
    public static void RestrictEmailTo(this Microsoft.Extensions.DependencyInjection.IServiceCollection services, IReadOnlyList<string> onlyTo)
    {
        if (onlyTo.Count == 0) return;
        var registered = services.Last(d => d.ServiceType == typeof(IEmailService));
        services.Remove(registered);
        services.Add(new Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof(IEmailService), provider =>
        {
            var inner = registered.ImplementationFactory != null
                ? (IEmailService)registered.ImplementationFactory(provider)
                : registered.ImplementationInstance as IEmailService
                  ?? (IEmailService)Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance(provider, registered.ImplementationType!);
            var logger = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<ILoggerFactory>(provider).CreateLogger("IPRO.Email.Staging");
            return new RestrictedEmailService(inner, onlyTo, logger);
        }, registered.Lifetime));
    }
}
