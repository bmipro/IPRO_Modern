using System.Net;
using IPRO.Entities;

namespace IPRO.Business.Services;

public static class ECardHtmlComposer
{
    private const string DefaultAccent = "#1457d9";

    // Builds a self-contained, table-based email HTML fragment: a gradient card panel (agent's own
    // accent color -> a per-occasion accent, matching NewsletterHtmlComposer's branding approach)
    // with the occasion headline and the agent's typed message overlaid on it, followed by a
    // contact-card footer -- same shape as Newsletter's footer, extended with Designation/Fax/Cell
    // to match the legacy e-card tool's full signature block.
    public static string Wrap(ECard card, AgentUser agent)
    {
        var agentAccent = string.IsNullOrWhiteSpace(agent.PortalAccentColor) ? DefaultAccent : agent.PortalAccentColor;
        var occasionAccent = ECardOccasions.AccentFor(card.Occasion);
        var headline = ECardOccasions.DisplayName(card.Occasion);
        var emoji = ECardOccasions.EmojiFor(card.Occasion);
        var agentName = $"{agent.FirstName} {agent.LastName}".Trim();
        var siteUrl = string.IsNullOrWhiteSpace(agent.DomainName) ? null : $"https://{agent.DomainName}";

        var messageHtml = string.IsNullOrWhiteSpace(card.Message)
            ? ""
            : $"""
              <tr><td style="padding:0 32px 36px;text-align:center;">
                <div style="display:inline-block;max-width:460px;padding:18px 22px;background:rgba(255,255,255,.14);border-radius:10px;color:#ffffff;font-size:15px;line-height:1.6;">
                  {WebUtility.HtmlEncode(card.Message).Replace("\n", "<br>")}
                </div>
              </td></tr>
              """;

        var photoCell = string.IsNullOrWhiteSpace(agent.PhotoUrl)
            ? ""
            : $"""
              <td width="52" style="padding-right:14px;vertical-align:top;">
                <img src="{WebUtility.HtmlEncode(agent.PhotoUrl)}" width="44" height="44" style="display:block;width:44px;height:44px;border-radius:50%;object-fit:cover;border:0;" alt="" />
              </td>
              """;

        var designationLine = string.Join(" · ", new[] { agent.Designation, agent.CompanyName }.Where(s => !string.IsNullOrWhiteSpace(s)));

        var contactLines = new List<string>();
        if (!string.IsNullOrWhiteSpace(agent.Phone)) contactLines.Add($"tel: {WebUtility.HtmlEncode(agent.Phone)}");
        if (!string.IsNullOrWhiteSpace(agent.BusinessFax)) contactLines.Add($"fax: {WebUtility.HtmlEncode(agent.BusinessFax)}");
        if (!string.IsNullOrWhiteSpace(agent.CellPhone)) contactLines.Add($"cell: {WebUtility.HtmlEncode(agent.CellPhone)}");
        var phoneLine = string.Join(" &nbsp;&bull;&nbsp; ", contactLines);

        var emailAndSiteLinks = new List<string>();
        if (!string.IsNullOrWhiteSpace(agent.Email))
            emailAndSiteLinks.Add($"""<a href="mailto:{WebUtility.HtmlEncode(agent.Email)}" style="color:{WebUtility.HtmlEncode(agentAccent)};text-decoration:none;">{WebUtility.HtmlEncode(agent.Email)}</a>""");
        if (siteUrl != null)
            emailAndSiteLinks.Add($"""<a href="{WebUtility.HtmlEncode(siteUrl)}" style="color:{WebUtility.HtmlEncode(agentAccent)};text-decoration:none;">{WebUtility.HtmlEncode(agent.DomainName)}</a>""");
        var linkLine = string.Join(" &nbsp;&bull;&nbsp; ", emailAndSiteLinks);

        return $"""
            <table cellpadding="0" cellspacing="0" border="0" width="100%" style="background:#f1f5f9;padding:24px 0;font-family:Arial,Helvetica,sans-serif;">
              <tr>
                <td align="center">
                  <table cellpadding="0" cellspacing="0" border="0" width="600" style="max-width:600px;background:#ffffff;border-radius:10px;overflow:hidden;">
                    <tr>
                      <td style="background:linear-gradient(135deg,{WebUtility.HtmlEncode(agentAccent)} 0%,{WebUtility.HtmlEncode(occasionAccent)} 100%);background-color:{WebUtility.HtmlEncode(agentAccent)};">
                        <table cellpadding="0" cellspacing="0" border="0" width="100%">
                          <tr><td style="padding:44px 32px 18px;text-align:center;font-size:44px;line-height:1;">{emoji}</td></tr>
                          <tr><td style="padding:0 32px 28px;text-align:center;color:#ffffff;font-size:30px;font-weight:bold;font-family:Georgia,serif;">{WebUtility.HtmlEncode(headline)}</td></tr>
                          {messageHtml}
                          <tr><td style="height:32px;"></td></tr>
                        </table>
                      </td>
                    </tr>
                    <tr>
                      <td style="padding:20px 24px;color:#475569;font-size:12px;">
                        <table cellpadding="0" cellspacing="0" border="0">
                          <tr>
                            {photoCell}
                            <td style="vertical-align:top;">
                              <strong style="color:#0f172a;font-size:14px;">{WebUtility.HtmlEncode(agentName)}</strong>
                              {(string.IsNullOrWhiteSpace(designationLine) ? "" : $"<br>{WebUtility.HtmlEncode(designationLine)}")}
                              {(string.IsNullOrWhiteSpace(phoneLine) ? "" : $"<br>{phoneLine}")}
                              {(string.IsNullOrWhiteSpace(linkLine) ? "" : $"<br>{linkLine}")}
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
}
