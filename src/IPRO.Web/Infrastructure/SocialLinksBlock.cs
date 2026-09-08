using IPRO.Entities;
using IPRO.Web.Models;

namespace IPRO.Web.Infrastructure;

// 465 (2026-09-08): what the Social links block renders. The links are the website's footer
// social links (My Website > Footer), in their footer order, with only absolute http(s) URLs kept:
// a footer row someone typed as "javascript:..." or without a scheme is dropped here rather than
// becoming an href. The block's layout variant picks icons only or icons with names.
public static class SocialLinksBlock
{
    public const string StyleIcons = "icons";
    public const string StyleLabels = "labels";

    public static SocialLinksBlockData Resolve(string? footerSettingsJson, string? layoutVariant)
    {
        var footer = WebsiteFooterSettings.FromJson(footerSettingsJson);
        var data = new SocialLinksBlockData
        {
            Style = string.Equals(layoutVariant?.Trim(), StyleLabels, StringComparison.OrdinalIgnoreCase) ? StyleLabels : StyleIcons
        };
        foreach (var link in footer.SocialLinks.OrderBy(l => l.SortOrder))
        {
            var url = (link.Url ?? string.Empty).Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
            if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) continue;
            data.Links.Add(new SocialLinkItem
            {
                Platform = link.Platform,
                Url = url,
                IconClass = SocialPlatformIcons.IconClass(link.Platform),
                Label = SocialPlatformIcons.Label(link.Platform)
            });
        }
        return data;
    }
}
