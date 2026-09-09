using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace IPRO.Web.Infrastructure;

public record HelpArticle(string Slug, string Title, string ResourceFileName);

public static class HelpDocsService
{
    private static readonly ConcurrentDictionary<string, string> HtmlCache = new();

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private static readonly List<HelpArticle> Articles = new()
    {
        new HelpArticle("account-dashboard", "Agent Account, Profile, and Dashboard", "01_AGENT_ACCOUNT_AND_DASHBOARD.md"),
        new HelpArticle("clients-followups", "Clients, Account Types, Notes, and Follow-ups", "02_CLIENTS_AND_FOLLOWUPS.md"),
        new HelpArticle("newsletters-campaigns", "Newsletters and Campaigns", "03_NEWSLETTERS_AND_CAMPAIGNS.md"),
        new HelpArticle("website-builder", "Website Builder, Pages, Menus, Images, and Templates", "04_WEBSITE_BUILDER.md"),
        new HelpArticle("domains-leads", "Domains, SSL, Website Leads, and Lead Forms", "05_DOMAINS_AND_LEADS.md"),
        new HelpArticle("billing-invoices", "Packages, Billing, PayPal, and Invoices", "06_BILLING_AND_INVOICES.md"),
        new HelpArticle("client-invoicing", "Client Invoicing: Estimates, Invoices, and Recurring Billing", "10_CLIENT_INVOICING.md"),
        new HelpArticle("client-portal", "Client Portal: Login, Messages, Documents, and Appointments", "11_CLIENT_PORTAL.md"),
        new HelpArticle("agent-document-library", "Agent Document Library", "12_AGENT_DOCUMENT_LIBRARY.md"),
        new HelpArticle("social-posts", "Social Posts: Draft, Check Limits, and Track", "13_SOCIAL_MEDIA_POSTS.md"),
        new HelpArticle("testimonials", "Testimonials: Collect, Review, and Approve", "15_TESTIMONIALS.md"),
        new HelpArticle("polls", "Polls and Surveys: Build, Send, and View Results", "16_POLLS_AND_SURVEYS.md"),
        // 461 (2026-09-02): four agent-facing guides existed on disk but were never listed here.
        new HelpArticle("forms", "Forms: Build, Publish, and Collect Submissions", "17_FORMS.md"),
        new HelpArticle("ecards", "E-Cards: Designs, Occasions, and Sending", "18_ECARDS.md"),
        new HelpArticle("eletters", "E-Letters: Templates and Sending", "19_ELETTERS.md"),
        new HelpArticle("google-visibility", "Getting Found on Google", "21_AGENT_GOOGLE_VISIBILITY.md"),
        // 461 (second half, 2026-09-02): the eight guides that did not exist until today.
        new HelpArticle("email-activity", "Email Activity: What Was Sent and Whether It Arrived", "23_EMAIL_ACTIVITY.md"),
        new HelpArticle("marketing-calendar", "Marketing Calendar", "24_MARKETING_CALENDAR.md"),
        new HelpArticle("ai-daily-assistant", "AI Daily Assistant", "25_AI_DAILY_ASSISTANT.md"),
        new HelpArticle("did-you-know", "Did You Know: Article Teasers That Capture Leads", "26_DID_YOU_KNOW.md"),
        new HelpArticle("support-tickets", "Support and Help: Articles and Tickets", "27_SUPPORT_TICKETS.md"),
        new HelpArticle("calendar", "Calendar and Google Calendar", "28_CALENDAR_AND_GOOGLE_CALENDAR.md"),
        new HelpArticle("articles", "Articles: Write Once, Use Everywhere", "29_ARTICLES.md"),
        new HelpArticle("image-library", "Image Library: Images for Hero and Text Blocks", "30_IMAGE_LIBRARY.md"),
    };

    public static IReadOnlyList<HelpArticle> GetArticles() => Articles;

    public static HelpArticle? FindArticle(string slug) =>
        Articles.FirstOrDefault(a => string.Equals(a.Slug, slug, StringComparison.OrdinalIgnoreCase));

    // 466 (2026-09-08): every guide opens with "In this guide", a list of links to its sections,
    // whenever it has three or more. The owner could not find a new section that sat 24th in the
    // longest guide; a reader needs to see the guide's shape before its text. The anchors are the
    // ids Markdig's auto-identifier extension already puts on each heading, so a link and its
    // heading can never disagree.
    private const int TocMinimumSections = 3;

    public static string RenderArticle(string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);

        var sections = new List<(string Id, string Text)>();
        foreach (var heading in document.Descendants<HeadingBlock>())
        {
            if (heading.Level != 2) continue;
            var id = heading.GetAttributes().Id;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var text = new StringBuilder();
            if (heading.Inline != null)
            {
                foreach (var literal in heading.Inline.Descendants<LiteralInline>())
                    text.Append(literal.Content.ToString());
            }
            if (text.Length > 0) sections.Add((id, text.ToString()));
        }

        var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();
        var html = writer.ToString();

        if (sections.Count < TocMinimumSections) return html;

        var toc = new StringBuilder();
        toc.Append("<nav class=\"help-toc\" aria-label=\"In this guide\"><div class=\"help-toc__title\">In this guide</div><ol>");
        foreach (var (id, text) in sections)
        {
            toc.Append("<li><a href=\"#").Append(id).Append("\">").Append(System.Net.WebUtility.HtmlEncode(text)).Append("</a></li>");
        }
        toc.Append("</ol></nav>\n");

        // After the title when there is one, otherwise at the top.
        var titleEnd = html.IndexOf("</h1>", StringComparison.Ordinal);
        return titleEnd < 0
            ? toc + html
            : html[..(titleEnd + 5)] + "\n" + toc + html[(titleEnd + 5)..];
    }

    public static string? GetArticleHtml(string slug)
    {
        var article = FindArticle(slug);
        if (article == null) return null;

        return HtmlCache.GetOrAdd(article.Slug, _ =>
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream($"HelpDocs.{article.ResourceFileName}");
            if (stream == null) return "<p>This help article could not be loaded.</p>";

            using var reader = new StreamReader(stream);
            var markdown = reader.ReadToEnd();
            return RenderArticle(markdown);
        });
    }
}
