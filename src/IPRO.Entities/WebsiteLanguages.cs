namespace IPRO.Entities;

/// <summary>
/// 578: the language a public website (AgentWebsite.SiteLanguage) or one of its pages
/// (WebsitePage.Language, empty = the website's) is written in, so a screen reader reads it with the
/// right voice (WCAG 3.1.1 and 3.1.2). English unless the owner picks French.
/// </summary>
public static class WebsiteLanguages
{
    public const string English = "en";
    public const string French = "fr";

    /// <summary>A website's language: French when asked for, English otherwise.</summary>
    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), French, StringComparison.OrdinalIgnoreCase) ? French : English;

    /// <summary>A page's own language: English or French, or empty for the website's.</summary>
    public static string NormalizePage(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        English => English,
        French => French,
        _ => string.Empty
    };

    /// <summary>The language a page is in: its own when it has one, else its website's.</summary>
    public static string Of(AgentWebsite website, WebsitePage? page)
    {
        var own = NormalizePage(page?.Language);
        return own.Length > 0 ? own : Normalize(website.SiteLanguage);
    }
}
