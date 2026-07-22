using System.Net;
using IPRO.Entities;

namespace IPRO.Business.Services;

public static class NewsletterHtmlComposer
{
    private const string DefaultAccent = "#1457d9";

    public static string Wrap(NewsLetter newsletter, AgentUser agent)
    {
        var accent = string.IsNullOrWhiteSpace(agent.PortalAccentColor) ? DefaultAccent : agent.PortalAccentColor;
        var edition = string.IsNullOrWhiteSpace(newsletter.Edition)
            ? $"{DateTime.UtcNow:MMMM yyyy} Newsletter"
            : newsletter.Edition!;
        var siteUrl = string.IsNullOrWhiteSpace(agent.DomainName) ? null : $"https://{agent.DomainName}";
        var agentName = $"{agent.FirstName} {agent.LastName}".Trim();

        var bannerRow = string.IsNullOrWhiteSpace(newsletter.BannerUrl)
            ? ""
            : $"""
              <tr><td style="padding:0;line-height:0;"><img src="{WebUtility.HtmlEncode(newsletter.BannerUrl)}" width="600" style="display:block;width:100%;max-width:600px;height:auto;border:0;" alt="" /></td></tr>
              """;

        var siteLinkHtml = siteUrl == null
            ? ""
            : $"""<a href="{WebUtility.HtmlEncode(siteUrl)}" style="color:#ffffff;text-decoration:none;font-size:13px;">{WebUtility.HtmlEncode(agent.DomainName)}</a>""";

        var contactLines = new List<string>();
        if (!string.IsNullOrWhiteSpace(agent.Phone)) contactLines.Add(WebUtility.HtmlEncode(agent.Phone));
        if (!string.IsNullOrWhiteSpace(agent.Email)) contactLines.Add($"""<a href="mailto:{WebUtility.HtmlEncode(agent.Email)}" style="color:#2563eb;text-decoration:none;">{WebUtility.HtmlEncode(agent.Email)}</a>""");
        if (siteUrl != null) contactLines.Add($"""<a href="{WebUtility.HtmlEncode(siteUrl)}" style="color:#2563eb;text-decoration:none;">{WebUtility.HtmlEncode(agent.DomainName)}</a>""");
        var contactLine = string.Join(" &nbsp;&bull;&nbsp; ", contactLines);

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
                    <tr>
                      <td style="padding:24px;color:#222222;font-size:14px;line-height:1.5;">
                        {newsletter.HtmlBody}
                      </td>
                    </tr>
                    <tr>
                      <td style="background:#f8fafc;border-top:1px solid #e2e8f0;padding:16px 24px;color:#475569;font-size:12px;">
                        <strong>{WebUtility.HtmlEncode(agentName)}</strong>{(string.IsNullOrWhiteSpace(agent.CompanyName) ? "" : $" &mdash; {WebUtility.HtmlEncode(agent.CompanyName)}")}
                        <br />
                        {contactLine}
                      </td>
                    </tr>
                  </table>
                </td>
              </tr>
            </table>
            """;
    }
}
