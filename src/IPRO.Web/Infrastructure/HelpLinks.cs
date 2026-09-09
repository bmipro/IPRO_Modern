namespace IPRO.Web.Infrastructure;

public sealed record HelpLink(string Url, string Label);

// 468 (2026-09-09): which help guide -- and which section of it -- belongs to each portal screen.
// The topbar's help icon reads this by the current controller and action. Keyed by controller,
// with per-action refinements where a screen is really a section of a guide (the page editor's
// menu screen, the footer editor). Anything unmapped opens the Support index rather than a broken
// link, and HelpLinksTests fails the build when an agent-facing controller has no entry, when a
// slug does not exist, or when an anchor names a section the guide no longer has.
public static class HelpLinks
{
    public const string IndexUrl = "/portal/Support";

    private static HelpLink Article(string slug, string label, string? anchor = null) =>
        new($"/portal/Support/Article/{slug}{(anchor == null ? string.Empty : "#" + anchor)}", label);

    // Controller -> guide (the "*" action), plus action -> section refinements.
    private static readonly Dictionary<string, Dictionary<string, HelpLink>> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dashboard"] = new(StringComparer.OrdinalIgnoreCase) { ["*"] = Article("account-dashboard", "Help: the Dashboard", "use-the-dashboard") },
        ["Account"] = new(StringComparer.OrdinalIgnoreCase) { ["*"] = Article("account-dashboard", "Help: your account", "edit-the-agent-profile") },
        ["Team"] = new(StringComparer.OrdinalIgnoreCase) { ["*"] = Article("team-members", "Help: team member logins") },
        ["Billing"] = new(StringComparer.OrdinalIgnoreCase) { ["*"] = Article("billing-invoices", "Help: packages and billing") },

        ["Clients"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["*"] = Article("clients-followups", "Help: clients"),
            ["FollowUps"] = Article("clients-followups", "Help: follow-ups", "review-follow-ups"),
            ["FollowUpQueue"] = Article("clients-followups", "Help: follow-ups", "review-follow-ups"),
            ["Calendar"] = Article("calendar", "Help: the calendar"),
            ["Import"] = Article("clients-followups", "Help: importing clients", "import-clients-from-csv"),
        },
        ["GoogleCalendar"] = new(StringComparer.OrdinalIgnoreCase) { ["*"] = Article("calendar", "Help: Google Calendar", "connect-google-calendar") },
        ["PortalMessages"] = new(StringComparer.OrdinalIgnoreCase) { ["*"] = Article("client-portal", "Help: portal messages", "portal-messages") },
        ["PortalRequests"] = new(StringComparer.OrdinalIgnoreCase) { ["*"] = Article("client-portal", "Help: portal requests", "portal-requests-appointments") },
        ["ClientInvoices"] = new(StringComparer.OrdinalIgnoreCase) { ["*"] = Article("client-invoicing", "Help: client invoicing") },
        ["RecurringInvoices"] = new(StringComparer.OrdinalIgnoreCase) { ["*"] = Article("client-invoicing", "Help: recurring invoices", "recurring-invoices") },
        ["Documents"] = new(StringComparer.OrdinalIgnoreCase) { ["*"] = Article("agent-document-library", "Help: documents") },

        ["Newsletter"] = new(StringComparer.OrdinalIgnoreCase) { ["*"] = Article("newsletters-campaigns", "Help: newsletters", "create-a-newsletter") },
        ["Campaigns"] = new(StringComparer.OrdinalIgnoreCase) { ["*"] = Article("newsletters-campaigns", "Help: drip campaigns", "create-a-drip-campaign") },
        ["Articles"] = new(StringComparer.OrdinalIgnoreCase) { ["*"] = Article("articles", "Help: articles") },
        ["ECards"] = new(StringComparer.OrdinalIgnoreCase) { ["*"] = Article("ecards", "Help: e-cards") },
        ["ELetters"] = new(StringComparer.OrdinalIgnoreCase) { ["*"] = Article("eletters", "Help: e-letters") },
        ["SocialPosts"] = new(StringComparer.OrdinalIgnoreCase) { ["*"] = Article("social-posts", "Help: social posts") },
        ["Polls"] = new(StringComparer.OrdinalIgnoreCase) { ["*"] = Article("polls", "Help: polls") },
        ["MarketingCalendar"] = new(StringComparer.OrdinalIgnoreCase) { ["*"] = Article("marketing-calendar", "Help: the marketing calendar") },
        ["EmailActivity"] = new(StringComparer.OrdinalIgnoreCase) { ["*"] = Article("email-activity", "Help: email activity") },

        ["Website"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["*"] = Article("website-builder", "Help: your website", "open-website-management"),
            ["Domains"] = Article("domains-leads", "Help: domains", "add-a-custom-domain"),
        },
        ["WebsitePages"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["*"] = Article("website-builder", "Help: pages", "create-a-page"),
            ["Edit"] = Article("website-builder", "Help: editing a page", "add-a-content-block"),
            ["Navigation"] = Article("website-builder", "Help: the menu", "add-a-page-to-the-menu"),
            ["Header"] = Article("website-builder", "Help: the header", "configure-the-header"),
            ["Footer"] = Article("website-builder", "Help: the footer", "configure-the-footer"),
        },
        ["WebsiteAnalytics"] = new(StringComparer.OrdinalIgnoreCase) { ["*"] = Article("website-builder", "Help: website analytics", "review-website-analytics") },
        ["WebsiteLeads"] = new(StringComparer.OrdinalIgnoreCase) { ["*"] = Article("domains-leads", "Help: website leads", "review-website-leads") },
        ["Media"] = new(StringComparer.OrdinalIgnoreCase) { ["*"] = Article("image-library", "Help: the image library") },
        ["Forms"] = new(StringComparer.OrdinalIgnoreCase) { ["*"] = Article("forms", "Help: forms") },
        ["Testimonials"] = new(StringComparer.OrdinalIgnoreCase) { ["*"] = Article("testimonials", "Help: testimonials") },

        ["Support"] = new(StringComparer.OrdinalIgnoreCase) { ["*"] = new HelpLink(IndexUrl, "Help documentation") },
    };

    public static HelpLink For(string? controller, string? action)
    {
        if (string.IsNullOrWhiteSpace(controller) || !Map.TryGetValue(controller.Trim(), out var actions))
            return new HelpLink(IndexUrl, "Help documentation");
        if (!string.IsNullOrWhiteSpace(action) && actions.TryGetValue(action.Trim(), out var refined))
            return refined;
        return actions.TryGetValue("*", out var fallback) ? fallback : new HelpLink(IndexUrl, "Help documentation");
    }

    public static bool HasTarget(string controller) => Map.ContainsKey(controller);

    public static IEnumerable<(string Controller, string Action, HelpLink Link)> All() =>
        Map.SelectMany(c => c.Value.Select(a => (c.Key, a.Key, a.Value)));
}
