using System;

namespace IPRO.Entities;

// 661 (2026-11-18, the owner's decisions): "Ask for a Google review". The professional pastes the review link
// from their Google Business Profile once, on the Profile page; a button on a client's page then emails that
// client the link, and the page a client sees after writing a testimonial offers it too. No automatic sending.
//
// Registered financial advisers face rules on testimonials and reviews, so for them it is OFF until they turn
// it on (beside a note that their dealer's or regulator's rules come first); every other profession has it on.
//
// Its own small table, NOT columns on AgentUsers, for AgentFollowUpReminder's reason: a column the model expects
// and the table lacks fails every AgentUsers query, sign-in included. No row means "never saved": no link, and
// the profession's default for the switch.
public class AgentGoogleReviewSettings
{
    public int AgentUserId { get; set; }
    public string? ReviewUrl { get; set; }
    public bool AskEnabled { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public AgentUser AgentUser { get; set; } = null!;
}

// One row per request sent, so the client's page can say when the client was last asked, and a second press
// the same day is refused rather than emailing the client twice.
public class ClientGoogleReviewRequest
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public int ClientId { get; set; }
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
}

public static class GoogleReviews
{
    public const int MaxUrlLength = 500;

    // A request is refused within this long of the last one to the same client: a double press, or a second
    // email the client would read as pushy.
    public static readonly TimeSpan RepeatGap = TimeSpan.FromHours(24);

    // The business types offered at sign-up are "Accountants", "Insurance / Financial", "Mortgage" and
    // "Generic" (StarterBusinessTypes); insurance and financial advisers share one. Matched loosely because
    // older accounts carry other spellings ("Financial Advisor", "Wealth Management"); a mortgage or
    // accounting type is never caught by it.
    public static bool IsRegulatedAdviser(string? businessType)
    {
        var type = businessType?.Trim() ?? string.Empty;
        return type.Contains("financial", StringComparison.OrdinalIgnoreCase)
            || type.Contains("insurance", StringComparison.OrdinalIgnoreCase)
            || type.Contains("wealth", StringComparison.OrdinalIgnoreCase)
            || type.Contains("invest", StringComparison.OrdinalIgnoreCase);
    }

    // The switch before the professional has ever saved it.
    public static bool DefaultAskEnabled(string? businessType) => !IsRegulatedAdviser(businessType);

    // An absolute http(s) address, or null when the text is empty or not one. The link opens Google's review
    // box, so nothing relative or of another scheme is kept.
    public static string? NormalizeUrl(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0 || text.Length > MaxUrlLength) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;
        if (string.IsNullOrWhiteSpace(uri.Host) || !uri.Host.Contains('.')) return null;
        return uri.AbsoluteUri.Length > MaxUrlLength ? null : uri.AbsoluteUri;
    }
}
