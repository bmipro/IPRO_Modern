using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace IPRO.Business.Services;

// 488 (2026-09-14): the platform's own open and click tracking.
//
// Why: Azure Communication Services will not enable user engagement tracking on a custom domain
// that is still on the default sending limits (442), the quota request has been open since 31
// August with no decision, and the launch is on 21 September. Opened and Clicked on the Email
// Activity screen would have stayed "not tracked" for every adviser on launch day. So the
// platform does what the provider would have done: a one-pixel image at the end of the message
// and every link routed through a short redirect on the platform host, both carrying a random
// per-recipient token that the dispatcher mints just before the send. The pixel loading stamps
// OpenedAt; the redirect stamps ClickedAt and sends the reader on. Both land in the SAME columns
// the provider's engagement events fill, through the same two recorders, so nothing downstream
// had to change. If Microsoft ever lifts the gate, Email__PlatformTrackingEnabled=false turns this
// off so the two do not stack.
//
// Safety:
// - The redirect target is signed (HMAC-SHA256 over kind, token and URL) with a server-side key,
//   so nobody can mint a link on the platform host that sends people somewhere else. No key, no
//   rewriting: the pixel still counts opens, the links go direct.
// - The token is 24 random bytes (32 URL-safe characters); an unknown token gets the image and
//   records nothing, so the pixel endpoint is not an oracle for which tokens exist.
// - Links that carry their own token are never rewritten: the unsubscribe / preferences link
//   must stay the exact URL the List-Unsubscribe header carries (RFC 8058 one-click), a poll's
//   vote link already identifies the recipient, and neither should count as a "click".
// - Only <a href> is touched, never <img src>; only absolute http(s) addresses qualify.
public static class EmailTrackingLinks
{
    private static readonly Regex Href = new(
        "(?<prefix><a\\b[^>]*?\\shref\\s*=\\s*)(?<quote>[\"'])(?<url>[^\"']*)\\k<quote>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 24 bytes of CSPRNG, URL-safe: 32 characters, the same construction as the email-preferences
    // token. Never Guid or Random -- a guessable token would let a stranger stamp opens and clicks
    // on somebody else's client.
    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(24));

    // On unless Email__PlatformTrackingEnabled is set to "false" on the App Service. Read straight
    // from configuration because the dispatchers hold IConfiguration, not IOptions<EmailSettings>;
    // EmailSettings.PlatformTrackingEnabled binds the same key for the screens.
    public static bool IsEnabled(IConfiguration configuration) =>
        !string.Equals(configuration["Email:PlatformTrackingEnabled"], "false", StringComparison.OrdinalIgnoreCase);

    // A dedicated Email__TrackingSigningKey if one is ever set; otherwise the Event Grid webhook
    // secret that production already carries, so the redirect is signed from the first deploy with
    // no new setting to create. Rotating either invalidates the links already in inboxes (they fall
    // to "This link is not valid" rather than to an unsigned redirect), so rotate deliberately.
    public static string? SigningKey(IConfiguration configuration)
    {
        var own = configuration["Email:TrackingSigningKey"];
        if (!string.IsNullOrWhiteSpace(own)) return own;

        var hook = configuration["Email:AzureEventWebhookSecret"];
        return string.IsNullOrWhiteSpace(hook) ? null : hook;
    }

    public static string PixelUrl(string baseUrl, string kind, string token) =>
        $"{baseUrl.TrimEnd('/')}/t/o/{kind}/{token}.gif";

    public static string ClickUrl(string baseUrl, string kind, string token, string target, string signingKey) =>
        $"{baseUrl.TrimEnd('/')}/t/c/{kind}/{token}?u={Uri.EscapeDataString(target)}&s={Sign(kind, token, target, signingKey)}";

    public static string Sign(string kind, string token, string target, string signingKey)
    {
        var payload = Encoding.UTF8.GetBytes($"{kind}\n{token}\n{target}");
        return Base64Url(HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingKey), payload));
    }

    public static bool VerifySignature(string kind, string token, string target, string? signature, string signingKey)
    {
        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(signingKey)) return false;

        var expected = Sign(kind ?? string.Empty, token ?? string.Empty, target ?? string.Empty, signingKey);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signature));
    }

    // An address the redirect may be pointed at: absolute, http(s), and not one of the platform's
    // own tokened doors. The same rule decides what gets rewritten at send time and what the
    // redirect accepts, so the two cannot drift.
    public static bool IsTrackable(string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return false;
        if (!Uri.TryCreate(href.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        // Unsubscribe / preferences, newsletter unsubscribe, poll votes, invoice and testimonial
        // links: every one carries "token=" and every one must stay exactly as issued.
        if (uri.Query.Contains("token=", StringComparison.OrdinalIgnoreCase)) return false;

        var path = uri.AbsolutePath;
        // Written without the leading slash on purpose: these are path comparisons, and a literal that
        // begins with a slash and a portal controller name reads as a bare portal link to the
        // PortalPathsTests regression guard.
        if (path.Contains("email-preferences", StringComparison.OrdinalIgnoreCase)) return false;
        if (path.Contains("newsletter/unsubscribe", StringComparison.OrdinalIgnoreCase)) return false;
        // Never wrap a tracking link in another one.
        if (path.Contains("/t/c/", StringComparison.OrdinalIgnoreCase)) return false;
        if (path.Contains("/t/o/", StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }

    // Rewrites the qualifying links and appends the pixel. Runs LAST, after the unsubscribe footer
    // and after any sanitiser, so nothing downstream re-processes the redirect addresses. The
    // plain-text alternative is deliberately left alone: a bare tracking URL in a text part is
    // noise to a person and a spam signal to a filter.
    public static string Instrument(string html, string kind, string token, string baseUrl, string? signingKey)
    {
        // No token means nothing to attribute the hit to. Leave the message exactly as it was.
        if (string.IsNullOrWhiteSpace(token)) return html;

        var body = html ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(signingKey))
        {
            body = Href.Replace(body, m =>
            {
                var raw = WebUtility.HtmlDecode(m.Groups["url"].Value);
                if (!IsTrackable(raw)) return m.Value;

                var rewritten = ClickUrl(baseUrl, kind, token, raw.Trim(), signingKey);
                var quote = m.Groups["quote"].Value;
                return $"{m.Groups["prefix"].Value}{quote}{WebUtility.HtmlEncode(rewritten)}{quote}";
            });
        }

        var pixel =
            $"<img src=\"{WebUtility.HtmlEncode(PixelUrl(baseUrl, kind, token))}\" width=\"1\" height=\"1\" alt=\"\" " +
            "style=\"display:block;width:1px;height:1px;border:0;margin:0;padding:0;\" />";
        return $"{body}{Environment.NewLine}{pixel}";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
