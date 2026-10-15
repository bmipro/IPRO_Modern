using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using IPRO.Entities;
using Xunit;

namespace IPRO.IntegrationTests.Accessibility;

// 578 (2026-10-15), audit finding 9 (WCAG 3.1.1 and 3.1.2): every page said lang="en", so a French
// website was read aloud with English pronunciation. A website now has a language (English or French)
// and a page can have its own; iPro's own words, which are English, say so on a French page.
public class SiteLanguage578Tests : IClassFixture<EdgeFixture>
{
    private readonly EdgeFixture _edge;
    public SiteLanguage578Tests(EdgeFixture edge) => _edge = edge;

    [Theory]
    [InlineData(PublicSitePages.Modern)]
    [InlineData(PublicSitePages.Classic)]
    [InlineData(PublicSitePages.Editorial)]
    public async Task A_French_website_says_so_and_marks_iPros_English_words(string design)
    {
        var site = PublicSitePages.Build(design, tweak: w => w.SiteLanguage = WebsiteLanguages.French);
        var html = await PublicSitePages.RenderAsync(site.Model("contact"));

        Assert.Contains("<html lang=\"fr\">", html);
        // The contact form, the opening hours and the footer's "Powered by" are iPro's English.
        Assert.Matches("<div class=\"website-lead-form-shell\"[^>]*lang=\"en\"", html);
        Assert.Contains("<div class=\"public-footer__powered\"><span lang=\"en\">Powered by", html);
        // The header is the website's own (French), so it needs nothing of its own; its hidden words are French.
        Assert.Matches("<header class=\"[^\"]*\">", html);
        Assert.Contains("aria-label=\"Navigation principale\"", html);
        Assert.Contains(">Aller au contenu principal</a>", html);

        // And it holds up in a browser: axe's language rules, and every element's language resolves.
        await _edge.OpenAsync(html, $"{design}-fr", 1280);
        Assert.Empty(await _edge.AxeOpenPageAsync("html-has-lang", "html-lang-valid", "valid-lang"));
        Assert.Equal("en", await _edge.Edge.EvaluateAsync<string>("document.querySelector('.website-lead-submit').closest('[lang]').getAttribute('lang')"));
    }

    [Fact]
    public async Task A_French_page_on_an_English_website_keeps_the_header_and_footer_English()
    {
        var site = PublicSitePages.Build(PublicSitePages.Modern);
        site.Pages.Single(p => p.Slug == "home").Language = WebsiteLanguages.French;
        var html = await PublicSitePages.RenderAsync(site.Model("home"));

        Assert.Contains("<html lang=\"fr\">", html);
        Assert.Matches("<header class=\"[^\"]*\" lang=\"en\">", html);
        Assert.Matches("<footer class=\"modern-footer\" lang=\"en\">", html);
        // Inside the English header, nothing more to say.
        Assert.Contains("<div class=\"public-footer__powered\"><span>Powered by", html);
        // In the French page, iPro's fixed words ("Services") are marked English.
        Assert.Contains("lang=\"en\">Services</div>", html);
    }

    [Fact]
    public async Task An_English_website_is_marked_English_and_nothing_else_changes()
    {
        var site = PublicSitePages.Build(PublicSitePages.Editorial);
        var html = await PublicSitePages.RenderAsync(site.Model("resources"));
        Assert.Contains("<html lang=\"en\">", html);
        Assert.Single(Regex.Matches(html, " lang=\""));
    }

    [Theory]
    [InlineData("fr", "fr")]
    [InlineData("FR ", "fr")]
    [InlineData("en", "en")]
    [InlineData("de", "en")]
    [InlineData(null, "en")]
    public void A_website_language_is_English_or_French(string? posted, string stored) =>
        Assert.Equal(stored, WebsiteLanguages.Normalize(posted));

    [Theory]
    [InlineData("", "")]
    [InlineData("fr", "fr")]
    [InlineData("en", "en")]
    [InlineData("es", "")]
    public void A_page_language_is_its_own_or_the_websites(string posted, string stored) =>
        Assert.Equal(stored, WebsiteLanguages.NormalizePage(posted));
}
