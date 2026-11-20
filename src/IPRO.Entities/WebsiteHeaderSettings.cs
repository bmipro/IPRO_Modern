using System.Text.Json;

namespace IPRO.Entities;

public class WebsiteHeaderSettings
{
    public string Style { get; set; } = "standard";
    public string LogoPosition { get; set; } = "left";
    public string LogoSize { get; set; } = "medium";
    public bool Sticky { get; set; }
    public bool ShowPhone { get; set; }
    public bool ShowEmail { get; set; }
    public string ButtonText { get; set; } = string.Empty;
    public string ButtonUrl { get; set; } = string.Empty;
    public List<WebsiteCustomNavigationLink> CustomLinks { get; set; } = new();
    // 528: the announcement bar across the top of every page, kept with the header it sits above.
    public WebsiteAnnouncementBar Announcement { get; set; } = new();
    // 529: the promotional pop-up, kept here with the announcement bar (no new column; WebsitePopup).
    public WebsitePopup Popup { get; set; } = new();

    public static WebsiteHeaderSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            var value = JsonSerializer.Deserialize<WebsiteHeaderSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            value.Style = Normalize(value.Style, new[] { "standard", "compact", "transparent" }, "standard");
            value.LogoPosition = Normalize(value.LogoPosition, new[] { "left", "center" }, "left");
            value.LogoSize = Normalize(value.LogoSize, new[] { "small", "medium", "large" }, "medium");
            value.ButtonText = value.ButtonText?.Trim() ?? string.Empty;
            value.ButtonUrl = value.ButtonUrl?.Trim() ?? string.Empty;
            value.CustomLinks ??= new();
            value.Announcement = (value.Announcement ?? new()).Normalized();
            value.Popup = (value.Popup ?? new()).Normalized();
            return value;
        }
        catch (JsonException) { return new(); }
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    private static string Normalize(string? value, IEnumerable<string> allowed, string fallback)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return allowed.Contains(normalized, StringComparer.OrdinalIgnoreCase) ? normalized : fallback;
    }
}

// 528 (the owner, 2026-09-27, from Squarespace's "Announcement Bar"; built 2026-11-18): one line of plain text
// across the top of every page of the site, with an optional link, on the theme colour or a dark or light band
// (the text colour follows, so it is always readable), and optionally a close button that keeps it closed for
// seven days in the visitor's browser. Every package has it (PackageFeatureCodes.AnnouncementBar), and SuperAdmin
// can take it off a package. Shell furniture drawn by _PublicAnnouncementBar in the three designs, not a block.
public class WebsiteAnnouncementBar
{
    public const int MaxMessageLength = 160;
    public const int MaxLinkTextLength = 40;
    public const int MaxLinkUrlLength = 500;
    // The visitor's browser keeps this for seven days after the close button is pressed. Its value is the
    // bar's DismissKey, so a new message shows again. Named in the privacy policy (_LegalPrivacy.cshtml).
    public const string DismissCookie = "ipro_bar_closed";
    public const int DismissDays = 7;
    public static readonly string[] Styles = { "theme", "dark", "light" };

    public bool Enabled { get; set; }
    public string Message { get; set; } = string.Empty;
    public string LinkText { get; set; } = string.Empty;
    public string LinkUrl { get; set; } = string.Empty;
    public string Style { get; set; } = "theme";
    public bool Dismissible { get; set; } = true;

    // Shown at all: switched on with something to say.
    public bool IsShown => Enabled && Message.Length > 0;
    public bool HasLink => LinkText.Length > 0 && LinkUrl.Length > 0;

    // Eight hex characters of the message and link: what the close cookie remembers.
    public string DismissKey
    {
        get
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Message + "\n" + LinkText + "\n" + LinkUrl));
            return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
        }
    }

    public WebsiteAnnouncementBar Normalized()
    {
        Message = Clip(OneLine(Message), MaxMessageLength);
        LinkText = Clip(OneLine(LinkText), MaxLinkTextLength);
        LinkUrl = SafeLink(LinkUrl) ?? string.Empty;
        var style = Style?.Trim().ToLowerInvariant() ?? string.Empty;
        Style = Styles.Contains(style) ? style : "theme";
        return this;
    }

    // A page of the site ("/contact", never "//host" or "/\host", which leave it) or a full http(s) address.
    // Stricter than a button's rule on purpose (no tel: or mailto:): a bar link reaches every visitor on every
    // page. The page half is the shared SitePaths rule since 665.
    public static string? SafeLink(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0 || text.Length > MaxLinkUrlLength) return null;
        if (text.StartsWith('/')) return SitePaths.IsOwnPage(text) ? text : null;   // 665: the shared rule
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;
        return string.IsNullOrWhiteSpace(uri.Host) ? null : uri.AbsoluteUri;
    }

    private static string OneLine(string? value) =>
        string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max].TrimEnd();
}

public class WebsiteCustomNavigationLink
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}
