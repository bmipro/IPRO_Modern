using IPRO.Entities;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace IPRO.Web.Infrastructure;

/// <summary>
/// 578: the lang attributes of a public website page (WCAG 3.1.1 and 3.1.2). The page is in its own
/// language (WebsitePage.Language) or the website's (AgentWebsite.SiteLanguage); the header and footer
/// are the website's. iPro's own words (form labels, calculators, opening hours) are English, so on a
/// page in another language each such part says lang="en". The attribute helpers return null where
/// nothing needs saying, which Razor turns into no attribute: an English site's markup is unchanged.
/// Views/PublicWebsite/Index.cshtml puts the two languages in ViewData for every partial.
/// </summary>
public static class SiteLanguageMarkup
{
    public const string SiteKey = "SiteLanguage";
    public const string PageKey = "PageLanguage";

    public static void Set(ViewDataDictionary viewData, AgentWebsite website, WebsitePage? page)
    {
        viewData[SiteKey] = WebsiteLanguages.Normalize(website.SiteLanguage);
        viewData[PageKey] = WebsiteLanguages.Of(website, page);
    }

    public static string Site(ViewDataDictionary viewData) => WebsiteLanguages.Normalize(viewData[SiteKey] as string);

    /// <summary>The page's language: &lt;html lang&gt;.</summary>
    public static string Page(ViewDataDictionary viewData) =>
        viewData[PageKey] is string { Length: > 0 } page ? WebsiteLanguages.Normalize(page) : Site(viewData);

    /// <summary>For the header and footer, which are the website's: its language when the page's differs.</summary>
    public static string? SiteParts(ViewDataDictionary viewData) => Site(viewData) == Page(viewData) ? null : Site(viewData);

    /// <summary>For iPro's own English words in the page.</summary>
    public static string? English(ViewDataDictionary viewData) => Page(viewData) == WebsiteLanguages.English ? null : WebsiteLanguages.English;

    /// <summary>For iPro's own English words in the header or footer.</summary>
    public static string? EnglishInSiteParts(ViewDataDictionary viewData) => Site(viewData) == WebsiteLanguages.English ? null : WebsiteLanguages.English;

    /// <summary>Back to the page's language inside a part marked English (a page name in a 404 list).</summary>
    public static string? PageInsideEnglish(ViewDataDictionary viewData) => English(viewData) == null ? null : Page(viewData);

    // The few words that only a screen reader hears in the header (no visible English to mark), in the
    // website's own language.
    public static string SkipLink(string language) => language == WebsiteLanguages.French ? "Aller au contenu principal" : "Skip to main content";
    public static string MainNavigation(string language) => language == WebsiteLanguages.French ? "Navigation principale" : "Main navigation";
    public static string Submenu(string language, string pageName) => language == WebsiteLanguages.French ? $"Sous-menu {pageName}" : $"{pageName} submenu";
}
