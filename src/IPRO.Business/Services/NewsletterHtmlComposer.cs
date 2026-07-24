using System.Net;
using IPRO.Entities;

namespace IPRO.Business.Services;

public static class NewsletterHtmlComposer
{
    private const string DefaultAccent = "#1457d9";

    public static string Wrap(NewsLetter newsletter, AgentUser agent, string baseUrl, IEnumerable<NewsLetterArticle>? articles = null, IEnumerable<NewsLetterCta>? sidebarCtas = null)
    {
        var accent = string.IsNullOrWhiteSpace(agent.PortalAccentColor) ? DefaultAccent : agent.PortalAccentColor;
        var edition = string.IsNullOrWhiteSpace(newsletter.Edition)
            ? $"{DateTime.UtcNow:MMMM yyyy} Newsletter"
            : newsletter.Edition!;
        var siteUrl = string.IsNullOrWhiteSpace(agent.DomainName) ? null : $"https://{agent.DomainName}";
        var agentName = $"{agent.FirstName} {agent.LastName}".Trim();

        var absoluteBannerUrl = ToAbsoluteUrl(newsletter.BannerUrl, baseUrl);
        var bannerRow = string.IsNullOrWhiteSpace(absoluteBannerUrl)
            ? ""
            : $"""
              <tr><td style="padding:0;line-height:0;"><img src="{WebUtility.HtmlEncode(absoluteBannerUrl)}" width="600" style="display:block;width:100%;max-width:600px;height:auto;border:0;" alt="" /></td></tr>
              """;

        var siteLinkHtml = siteUrl == null
            ? ""
            : $"""<a href="{WebUtility.HtmlEncode(siteUrl)}" style="color:#ffffff;text-decoration:none;font-size:13px;">{WebUtility.HtmlEncode(agent.DomainName)}</a>""";

        var contactLines = new List<string>();
        if (!string.IsNullOrWhiteSpace(agent.Phone)) contactLines.Add(WebUtility.HtmlEncode(agent.Phone));
        if (!string.IsNullOrWhiteSpace(agent.Email)) contactLines.Add($"""<a href="mailto:{WebUtility.HtmlEncode(agent.Email)}" style="color:#2563eb;text-decoration:none;">{WebUtility.HtmlEncode(agent.Email)}</a>""");
        if (siteUrl != null) contactLines.Add($"""<a href="{WebUtility.HtmlEncode(siteUrl)}" style="color:#2563eb;text-decoration:none;">{WebUtility.HtmlEncode(agent.DomainName)}</a>""");
        var contactLine = string.Join(" &nbsp;&bull;&nbsp; ", contactLines);

        var absolutePhotoUrl = ToAbsoluteUrl(agent.PhotoUrl, baseUrl);
        var photoCell = string.IsNullOrWhiteSpace(absolutePhotoUrl)
            ? ""
            : $"""
              <td width="48" style="padding-right:12px;vertical-align:top;">
                <img src="{WebUtility.HtmlEncode(absolutePhotoUrl)}" width="40" height="40" style="display:block;width:40px;height:40px;border-radius:50%;object-fit:cover;border:0;" alt="" />
              </td>
              """;

        var articleCardsHtml = string.Concat((articles ?? Enumerable.Empty<NewsLetterArticle>())
            .OrderBy(a => a.SortOrder)
            .Select(article =>
            {
                var absoluteArticleImage = ToAbsoluteUrl(article.ImageUrl, baseUrl);
                var imageHtml = string.IsNullOrWhiteSpace(absoluteArticleImage)
                    ? ""
                    : $"""<img src="{WebUtility.HtmlEncode(absoluteArticleImage)}" width="552" style="display:block;width:100%;max-width:552px;height:auto;border-radius:6px;border:0;margin-bottom:12px;" alt="" />""";
                return $"""
                    <div style="margin-top:24px;padding-top:24px;border-top:1px solid #e2e8f0;">
                      {imageHtml}
                      <h3 style="margin:0 0 8px;color:{WebUtility.HtmlEncode(accent)};font-size:17px;">{WebUtility.HtmlEncode(article.Title)}</h3>
                      <div>{article.Content}</div>
                    </div>
                    """;
            }));

        var ctaList = (sidebarCtas ?? Enumerable.Empty<NewsLetterCta>()).OrderBy(c => c.SortOrder).ToList();
        var hasSidebar = ctaList.Count > 0;
        var sidebarCtaButtons = string.Concat(ctaList.Select(cta => $"""
            <tr><td style="padding-bottom:10px;">
              <a href="{WebUtility.HtmlEncode(cta.Url)}" style="display:block;padding:10px 12px;background:{WebUtility.HtmlEncode(accent)};color:#ffffff;text-decoration:none;font-size:13px;font-weight:bold;border-radius:5px;text-align:center;">{WebUtility.HtmlEncode(cta.Label)}</a>
            </td></tr>
            """));

        var mainColumnInner = $"""
            {newsletter.HtmlBody}
            {articleCardsHtml}
            """;

        var bodyRow = hasSidebar
            ? $"""
              <tr>
                <td style="padding:24px;color:#222222;font-size:14px;line-height:1.5;">
                  <table cellpadding="0" cellspacing="0" border="0" width="100%">
                    <tr>
                      <td width="380" style="vertical-align:top;">{mainColumnInner}</td>
                      <td width="20"></td>
                      <td width="152" style="vertical-align:top;background:#f8fafc;border:1px solid #e2e8f0;border-radius:6px;padding:16px;">
                        <table cellpadding="0" cellspacing="0" border="0" width="100%">{sidebarCtaButtons}</table>
                      </td>
                    </tr>
                  </table>
                </td>
              </tr>
              """
            : $"""
              <tr>
                <td style="padding:24px;color:#222222;font-size:14px;line-height:1.5;">
                  {mainColumnInner}
                </td>
              </tr>
              """;

        return $"""
            <table cellpadding="0" cellspacing="0" border="0" width="100%" style="background:#f1f5f9;padding:24px 0;font-family:Arial,Helvetica,sans-serif;">
              <tr>
                <td align="center">
                  <table cellpadding="0" cellspacing="0" border="0" width="600" style="max-width:600px;background:#ffffff;">
                    {bannerRow}
                    <tr>
                      <td style="background:{WebUtility.HtmlEncode(accent)};padding:14px 20px;">
                        <table cellpadding="0" cellspacing="0" border="0" width="100%">
                          <tr>
                            <td style="color:#ffffff;font-size:15px;font-weight:bold;">{WebUtility.HtmlEncode(edition)}</td>
                            <td align="right">{siteLinkHtml}</td>
                          </tr>
                        </table>
                      </td>
                    </tr>
                    {bodyRow}
                    <tr>
                      <td style="background:#f8fafc;border-top:1px solid #e2e8f0;padding:16px 24px;color:#475569;font-size:12px;">
                        <table cellpadding="0" cellspacing="0" border="0">
                          <tr>
                            {photoCell}
                            <td style="vertical-align:top;">
                              <strong>{WebUtility.HtmlEncode(agentName)}</strong>{(string.IsNullOrWhiteSpace(agent.CompanyName) ? "" : $" &mdash; {WebUtility.HtmlEncode(agent.CompanyName)}")}
                              <br />
                              {contactLine}
                            </td>
                          </tr>
                        </table>
                      </td>
                    </tr>
                  </table>
                </td>
              </tr>
            </table>
            """;
    }

    private static string? ToAbsoluteUrl(string? url, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        return $"{baseUrl.TrimEnd('/')}/{url.TrimStart('/')}";
    }
}
