using System.Globalization;
using System.Net;
using System.Text;
using Amazon;
using Amazon.Runtime;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using IPRO.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IPRO.Email;

// 531 (2026-09-30): Amazon SES in Canada (Central), for the email an adviser sends to their own
// clients. Microsoft retires Azure Communication Services on 2028-09-30 and keeps iPro on 100 emails
// an hour; SES granted production access on 2026-09-30 (50,000 a day, 14 a second).
//
// What an email looks like here: From "<business> via iPro" <mail@notify|news.iproadvisers.com> (530
// step two -- the sender name per email ACS cannot give), Reply-To the adviser, the stream's
// configuration set (whose event destination reports bounces, complaints and deliveries), the
// adviser's tenant, and our tags, which SES echoes back in those reports.
//
// The retry contract is the one ACS and SendGrid keep (JOBS-5/8): account-level and setup failures,
// throttles and 5xx are "not right now" (transient); a rejected message or a bad request is permanent,
// because sending the same thing again is spam. Every dispatcher's pause-and-resume depends on it.
public class SesEmailService
{
    private readonly EmailSettings _settings;
    private readonly SesTenants _tenants;
    private readonly SesPacer _pacer;
    private readonly ILogger<SesEmailService> _logger;
    private readonly object _clientLock = new();
    private IAmazonSimpleEmailServiceV2? _client;

    public SesEmailService(IOptions<EmailSettings> settings, SesTenants tenants, SesPacer pacer, ILogger<SesEmailService> logger)
    {
        _settings = settings.Value;
        _tenants = tenants;
        _pacer = pacer;
        _logger = logger;
    }

    // The same seam as AzureEmailService.ClientFactory: production builds one real client (thread-safe,
    // meant to be reused); tests hand in Amazon's own client class with its calls answered in memory.
    internal Func<IAmazonSimpleEmailServiceV2>? ClientFactory { get; set; }

    private IAmazonSimpleEmailServiceV2 Client()
    {
        if (ClientFactory != null) return ClientFactory();
        lock (_clientLock)
        {
            var ses = _settings.Ses;
            return _client ??= new AmazonSimpleEmailServiceV2Client(
                new BasicAWSCredentials(ses.AccessKeyId, ses.SecretAccessKey), RegionEndpoint.GetBySystemName(ses.Region));
        }
    }

    public async Task<EmailSendResult> SendAsync(string stream, string toEmail, string toName, string subject, string htmlBody, string? textBody,
        IDictionary<string, string>? tags, string? replyToEmail, string? replyToName, string? listUnsubscribeUrl)
    {
        var ses = _settings.Ses;
        if (!ses.IsConfigured)
            return EmailSendResult.FailedTransient("Amazon SES is not configured. Check Email__Ses__AccessKeyId and Email__Ses__SecretAccessKey in Azure app settings.");
        if (string.IsNullOrWhiteSpace(toEmail))
            return EmailSendResult.Failed("Recipient email is missing.");

        try
        {
            var client = Client();
            var agentId = tags != null && tags.TryGetValue(AdviserSender.AdviserTag, out var raw)
                          && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;
            var tenant = await _tenants.EnsureAsync(client, agentId);
            var request = BuildRequest(ses, stream, toEmail, toName, subject, htmlBody, textBody, tags, replyToEmail ?? NullIfBlank(_settings.ReplyToEmail),
                replyToEmail == null ? null : replyToName, replyToName, listUnsubscribeUrl, tenant);

            await _pacer.WaitTurnAsync();
            var response = await client.SendEmailAsync(request);
            return EmailSendResult.Sent(response.MessageId);
        }
        catch (Exception ex)
        {
            var result = Classify(ex);
            if (ex is TooManyRequestsException or LimitExceededException) _pacer.Hold(TimeSpan.FromSeconds(1));
            if (ex is AccountSuspendedException or SendingPausedException)
                _logger.LogError(ex, "Amazon SES has paused sending ({Stream}); email to {Email} was not sent.", stream, toEmail);
            else
                _logger.LogWarning(ex, "Amazon SES did not send to {Email} ({Stream}): {Message}", toEmail, stream, result.Message);
            return result;
        }
    }

    internal static SendEmailRequest BuildRequest(SesSettings ses, string stream, string toEmail, string toName, string subject, string htmlBody,
        string? textBody, IDictionary<string, string>? tags, string? replyToEmail, string? replyToName, string? businessName,
        string? listUnsubscribeUrl, string? tenant)
    {
        var headers = new List<MessageHeader>();
        if (!string.IsNullOrWhiteSpace(listUnsubscribeUrl))
        {
            // RFC 8058 one-click unsubscribe, as on every other provider.
            headers.Add(new MessageHeader { Name = "List-Unsubscribe", Value = $"<{listUnsubscribeUrl}>" });
            headers.Add(new MessageHeader { Name = "List-Unsubscribe-Post", Value = "List-Unsubscribe=One-Click" });
        }

        return new SendEmailRequest
        {
            FromEmailAddress = SesAddress.Format(AdviserSender.SenderName(businessName), $"{ses.SenderLocalPart}@{ses.DomainFor(stream)}"),
            Destination = new Destination { ToAddresses = new List<string> { SesAddress.Format(toName, toEmail) } },
            ReplyToAddresses = string.IsNullOrWhiteSpace(replyToEmail) ? null : new List<string> { SesAddress.Format(replyToName, replyToEmail) },
            Content = new EmailContent
            {
                Simple = new Message
                {
                    Subject = new Content { Data = subject ?? string.Empty, Charset = "UTF-8" },
                    Body = new Body
                    {
                        Html = new Content { Data = htmlBody ?? string.Empty, Charset = "UTF-8" },
                        Text = string.IsNullOrWhiteSpace(textBody) ? null : new Content { Data = textBody, Charset = "UTF-8" }
                    },
                    Headers = headers.Count == 0 ? null : headers
                }
            },
            ConfigurationSetName = ses.ConfigurationSetFor(stream),
            EmailTags = Tags(tags),
            TenantName = tenant
        };
    }

    // SES tags allow ASCII letters, digits, underscores and dashes, 256 characters each, and no empty
    // value; ours are ids and names, so the rare stray character becomes an underscore.
    private static List<MessageTag>? Tags(IDictionary<string, string>? tags)
    {
        if (tags == null || tags.Count == 0) return null;
        var list = tags
            .Select(t => new MessageTag { Name = TagText(t.Key), Value = TagText(t.Value) })
            .Where(t => t.Name.Length > 0 && t.Value.Length > 0)
            .Take(50)
            .ToList();
        return list.Count == 0 ? null : list;
    }

    private static string TagText(string? value)
    {
        var text = new string((value ?? string.Empty).Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        return text.Length > 256 ? text[..256] : text;
    }

    public static EmailSendResult Classify(Exception ex)
    {
        var detail = Summarize(ex.Message);
        return ex switch
        {
            MessageRejectedException => EmailSendResult.Failed($"Amazon SES rejected the message. {detail}"),
            BadRequestException => EmailSendResult.Failed($"Amazon SES refused the request. {detail}"),
            TooManyRequestsException or LimitExceededException => EmailSendResult.FailedTransient($"Amazon SES asked us to slow down. {detail}"),
            AccountSuspendedException or SendingPausedException => EmailSendResult.FailedTransient($"Amazon SES has paused sending. {detail}"),
            MailFromDomainNotVerifiedException or NotFoundException => EmailSendResult.FailedTransient($"The Amazon SES setup is incomplete. {detail}"),
            InternalServiceErrorException => EmailSendResult.FailedTransient($"Amazon SES had an internal error. {detail}"),
            AmazonServiceException ase when (int)ase.StatusCode is >= 400 and < 500
                && ase.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                => EmailSendResult.Failed($"Amazon SES refused the send ({(int)ase.StatusCode}). {detail}"),
            // Access denied, 5xx, a socket or a timeout: the account or the network, never this message.
            _ => EmailSendResult.FailedTransient($"Amazon SES send failed: {detail}")
        };
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Summarize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "No detail was returned.";
        message = message.ReplaceLineEndings(" ").Trim();
        return message.Length > 500 ? message[..500] : message;
    }
}

// An address for an SES header: "Display Name" <address>, the name as an RFC 2047 encoded-word when
// it is not plain ASCII (an adviser named "Café Planning"). Line breaks never survive into a header.
public static class SesAddress
{
    public static string Format(string? displayName, string email)
    {
        var address = (email ?? string.Empty).Trim();
        var name = (displayName ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (name.Length == 0) return address;

        if (name.All(c => c >= 0x20 && c <= 0x7E))
            return $"\"{name.Replace("\\", "\\\\").Replace("\"", "\\\"")}\" <{address}>";

        return $"{EncodedWords(name)} <{address}>";
    }

    // Each encoded-word stays within RFC 2047's 75 characters, split on whole characters.
    private static string EncodedWords(string name)
    {
        var words = new List<string>();
        var chunk = new StringBuilder();
        foreach (var rune in name.EnumerateRunes())
        {
            var candidate = chunk.ToString() + rune;
            if (Encoding.UTF8.GetByteCount(candidate) > 45 && chunk.Length > 0)
            {
                words.Add(Word(chunk.ToString()));
                chunk.Clear();
            }
            chunk.Append(rune.ToString());
        }
        if (chunk.Length > 0) words.Add(Word(chunk.ToString()));
        return string.Join(" ", words);

        static string Word(string text) => $"=?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}?=";
    }
}
