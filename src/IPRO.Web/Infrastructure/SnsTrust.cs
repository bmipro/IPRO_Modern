using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace IPRO.Web.Infrastructure;

// 531: Amazon SES reports bounces, complaints and deliveries through an SNS topic that posts to
// /SesEmailEvents. Anyone can post to a public URL, so nothing in a message is believed until it is
// proven to be Amazon's: the topic is ours (SesEmailEventsController checks it against the setting),
// and the message is signed by a certificate served from sns.<region>.amazonaws.com over HTTPS.
// https://docs.aws.amazon.com/sns/latest/dg/sns-verify-signature-of-message.html
public sealed record SnsEnvelope(
    string Type,
    string MessageId,
    string TopicArn,
    string? Subject,
    string Message,
    string Timestamp,
    string SignatureVersion,
    string Signature,
    string SigningCertUrl,
    string? SubscribeUrl,
    string? Token);

public static class SnsMessages
{
    public static SnsEnvelope? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string? Read(string name) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

            var type = Read("Type");
            var messageId = Read("MessageId");
            var topicArn = Read("TopicArn");
            var message = Read("Message");
            var timestamp = Read("Timestamp");
            var version = Read("SignatureVersion");
            var signature = Read("Signature");
            var certUrl = Read("SigningCertURL");
            if (type is null || messageId is null || topicArn is null || message is null || timestamp is null
                || version is null || signature is null || certUrl is null)
                return null;

            return new SnsEnvelope(type, messageId, topicArn, Read("Subject"), message, timestamp, version, signature, certUrl,
                Read("SubscribeURL"), Read("Token"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The exact text Amazon signs: name, newline, value, newline, for a fixed list of fields in a
    // fixed order. A notification's Subject is signed only when it has one.
    public static string StringToSign(SnsEnvelope envelope)
    {
        var text = new StringBuilder();
        void Add(string name, string? value) => text.Append(name).Append('\n').Append(value).Append('\n');

        Add("Message", envelope.Message);
        Add("MessageId", envelope.MessageId);
        if (envelope.Type == "Notification")
        {
            if (envelope.Subject != null) Add("Subject", envelope.Subject);
            Add("Timestamp", envelope.Timestamp);
            Add("TopicArn", envelope.TopicArn);
            Add("Type", envelope.Type);
        }
        else
        {
            Add("SubscribeURL", envelope.SubscribeUrl);
            Add("Timestamp", envelope.Timestamp);
            Add("Token", envelope.Token);
            Add("TopicArn", envelope.TopicArn);
            Add("Type", envelope.Type);
        }
        return text.ToString();
    }

    // "arn:aws:sns:ca-central-1:354245663230:ipro-ses-events" -> "ca-central-1".
    public static string? RegionOf(string? topicArn)
    {
        var parts = (topicArn ?? string.Empty).Split(':');
        return parts.Length >= 6 && parts[0] == "arn" && parts[2] == "sns" && parts[3].Length > 0 ? parts[3] : null;
    }

    // Only https://sns.<the topic's region>.amazonaws.com -- no look-alike host, no other scheme or port.
    public static bool IsTrustedAwsUrl(string? url, string? region)
    {
        if (string.IsNullOrWhiteSpace(region) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttps
               && uri.IsDefaultPort
               && string.IsNullOrEmpty(uri.UserInfo)
               && string.Equals(uri.Host, $"sns.{region}.amazonaws.com", StringComparison.OrdinalIgnoreCase);
    }
}

public interface ISnsTrust
{
    Task<bool> VerifyAsync(SnsEnvelope envelope);
    Task<bool> ConfirmSubscriptionAsync(string subscribeUrl);
}

public sealed class SnsTrust : ISnsTrust
{
    private readonly Func<string, Task<X509Certificate2?>> _loadCertificate;
    private readonly Func<string, Task<bool>> _visit;
    private readonly ConcurrentDictionary<string, X509Certificate2> _certificates = new(StringComparer.Ordinal);

    public SnsTrust(IHttpClientFactory http)
        : this(url => LoadCertificateAsync(http, url), url => VisitAsync(http, url))
    {
    }

    // Tests hand in their own certificate and record the confirmation instead of calling Amazon.
    public SnsTrust(Func<string, Task<X509Certificate2?>> loadCertificate, Func<string, Task<bool>> visit)
    {
        _loadCertificate = loadCertificate;
        _visit = visit;
    }

    public async Task<bool> VerifyAsync(SnsEnvelope envelope)
    {
        if (!SnsMessages.IsTrustedAwsUrl(envelope.SigningCertUrl, SnsMessages.RegionOf(envelope.TopicArn))) return false;

        HashAlgorithmName hash;
        if (envelope.SignatureVersion == "1") hash = HashAlgorithmName.SHA1;
        else if (envelope.SignatureVersion == "2") hash = HashAlgorithmName.SHA256;
        else return false;

        byte[] signature;
        try { signature = Convert.FromBase64String(envelope.Signature); }
        catch (FormatException) { return false; }

        if (!_certificates.TryGetValue(envelope.SigningCertUrl, out var certificate))
        {
            certificate = await _loadCertificate(envelope.SigningCertUrl);
            if (certificate == null) return false;
            _certificates[envelope.SigningCertUrl] = certificate;
        }

        using var rsa = certificate.GetRSAPublicKey();
        return rsa != null && rsa.VerifyData(Encoding.UTF8.GetBytes(SnsMessages.StringToSign(envelope)), signature, hash, RSASignaturePadding.Pkcs1);
    }

    // Following the SubscribeURL is how an SNS subscription is confirmed. Only ever an SNS address.
    public Task<bool> ConfirmSubscriptionAsync(string subscribeUrl)
    {
        if (!Uri.TryCreate(subscribeUrl, UriKind.Absolute, out var uri)) return Task.FromResult(false);
        var host = uri.Host;
        var isSns = uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
                    && host.StartsWith("sns.", StringComparison.OrdinalIgnoreCase)
                    && host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase)
                    && host.Count(c => c == '.') == 3;
        return isSns ? _visit(subscribeUrl) : Task.FromResult(false);
    }

    private static async Task<X509Certificate2?> LoadCertificateAsync(IHttpClientFactory http, string url)
    {
        try
        {
            using var client = http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            var pem = await client.GetStringAsync(url);
            return X509Certificate2.CreateFromPem(pem);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> VisitAsync(IHttpClientFactory http, string url)
    {
        try
        {
            using var client = http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            using var response = await client.GetAsync(url);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
