using System.Net;
using IPRO.Entities;

namespace IPRO.Business.Services;

// Rebuilds the legacy e-card composition: artwork panel with the greeting set over (or under) it,
// then the agent's contact block. Structure follows the original 2010 templates recovered from the
// legacy database -- 24px italic serif headline, bold sub-line, contact table with a 132px photo --
// but rendered as one hero image plus HTML rather than the original's sliced-GIF tables, which no
// longer survive modern mail clients.
public static class ECardHtmlComposer
{
    private const string DefaultAccent = "#1457d9";

    public static string Wrap(ECard card, AgentUser agent, string baseUrl)
    {
        var template = ECardTemplateCatalog.Find(card.Occasion) ?? ECardTemplateCatalog.Default;
        var accent = string.IsNullOrWhiteSpace(agent.PortalAccentColor) ? DefaultAccent : agent.PortalAccentColor;
        var artUrl = $"{baseUrl.TrimEnd('/')}{template.Url}";

        var header = string.IsNullOrWhiteSpace(card.Subject) ? template.DefaultHeaderText : card.Subject;
        var message = string.IsNullOrWhiteSpace(card.Message) ? template.DefaultMessage : card.Message;

        var dark = template.IsDark;
        var shellBg = dark ? "#000000" : "#ffffff";
        var textColor = dark ? "#ffffff" : "#1f2937";
        var mutedColor = dark ? "#d6d6d6" : "#5b6472";
        var width = Math.Min(template.Width, 620);

        var artPanel = template.Layout switch
        {
            ECardLayouts.DarkOverlay => BuildOverlayPanel(artUrl, header, message, width, template, "#ffffff", "#ffffff", 44),
            ECardLayouts.LightOverlay => BuildOverlayPanel(artUrl, header, message, width, template, "#12305e", "#25405f", 34),
            _ => BuildBannerPanel(artUrl, header, message, width, textColor, mutedColor),
        };

        return $"""
            <table cellpadding="0" cellspacing="0" border="0" width="100%" style="background:#eef1f5;padding:24px 0;font-family:Arial,Helvetica,sans-serif;">
              <tr><td align="center">
                <table cellpadding="0" cellspacing="0" border="0" width="{width}" style="max-width:{width}px;background:{shellBg};border-radius:10px;overflow:hidden;">
                  {artPanel}
                  <tr><td style="height:26px;line-height:26px;font-size:0;">&nbsp;</td></tr>
                  <tr><td style="padding:0 34px 30px;">
                    {BuildContactBlock(agent, accent, textColor, mutedColor, dark)}
                  </td></tr>
                </table>
              </td></tr>
            </table>
            """;
    }

    // Greeting sits on the artwork. The art is both a background-image (so text can sit over it)
    // and, for clients that drop backgrounds, the panel keeps a solid fallback colour underneath.
    private static string BuildOverlayPanel(string artUrl, string header, string message, int width,
        ECardTemplate template, string headerColor, string messageColor, int topPad)
    {
        var height = (int)Math.Round(template.Height * (width / (double)template.Width));
        return $"""
            <tr>
              <td background="{WebUtility.HtmlEncode(artUrl)}" bgcolor="{(template.IsDark ? "#000000" : "#f3e9ea")}" width="{width}" height="{height}"
                  style="background-image:url('{WebUtility.HtmlEncode(artUrl)}');background-size:{width}px {height}px;background-repeat:no-repeat;background-position:center top;width:{width}px;height:{height}px;">
                <table cellpadding="0" cellspacing="0" border="0" width="100%">
                  <tr><td style="padding:{topPad}px 30px 0;text-align:center;">
                    <div style="font-family:Georgia,'Times New Roman',serif;font-style:italic;font-size:24px;line-height:1.25;color:{headerColor};">{WebUtility.HtmlEncode(header)}</div>
                    <div style="margin-top:12px;font-size:14px;font-weight:bold;line-height:1.5;color:{messageColor};">{WebUtility.HtmlEncode(message)}</div>
                  </td></tr>
                </table>
              </td>
            </tr>
            """;
    }

    // Artwork already carries its own lettering, so the greeting goes beneath it instead.
    private static string BuildBannerPanel(string artUrl, string header, string message, int width,
        string textColor, string mutedColor)
    {
        return $"""
            <tr><td style="padding:0;line-height:0;font-size:0;">
              <img src="{WebUtility.HtmlEncode(artUrl)}" width="{width}" alt="" style="display:block;width:100%;max-width:{width}px;height:auto;border:0;" />
            </td></tr>
            <tr><td style="padding:26px 34px 0;text-align:center;">
              <div style="font-family:Georgia,'Times New Roman',serif;font-style:italic;font-size:24px;line-height:1.25;color:{textColor};">{WebUtility.HtmlEncode(header)}</div>
              <div style="margin-top:10px;font-size:14px;line-height:1.6;color:{mutedColor};">{WebUtility.HtmlEncode(message)}</div>
            </td></tr>
            """;
    }

    // Mirrors the legacy signature block: name, title, company, tel/fax/cell, email and website,
    // with the agent's photo to the right at the original 132px.
    private static string BuildContactBlock(AgentUser agent, string accent, string textColor, string mutedColor, bool dark)
    {
        var agentName = $"{agent.FirstName} {agent.LastName}".Trim();
        var linkColor = dark ? "#8fc0ff" : accent;
        var labelStyle = $"font-style:italic;font-weight:bold;color:{mutedColor};";

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(agent.Phone))
            lines.Add($"""<tr><td style="{labelStyle}padding-right:10px;">tel:</td><td style="color:{textColor};">{WebUtility.HtmlEncode(agent.Phone)}</td></tr>""");
        if (!string.IsNullOrWhiteSpace(agent.BusinessFax))
            lines.Add($"""<tr><td style="{labelStyle}padding-right:10px;">fax:</td><td style="color:{textColor};">{WebUtility.HtmlEncode(agent.BusinessFax)}</td></tr>""");
        if (!string.IsNullOrWhiteSpace(agent.CellPhone))
            lines.Add($"""<tr><td style="{labelStyle}padding-right:10px;">cell:</td><td style="color:{textColor};">{WebUtility.HtmlEncode(agent.CellPhone)}</td></tr>""");
        if (!string.IsNullOrWhiteSpace(agent.Email))
            lines.Add($"""<tr><td style="{labelStyle}padding-right:10px;">email:</td><td><a href="mailto:{WebUtility.HtmlEncode(agent.Email)}" style="color:{linkColor};text-decoration:none;">{WebUtility.HtmlEncode(agent.Email)}</a></td></tr>""");
        if (!string.IsNullOrWhiteSpace(agent.DomainName))
            lines.Add($"""<tr><td style="{labelStyle}padding-right:10px;">web site:</td><td><a href="https://{WebUtility.HtmlEncode(agent.DomainName)}" style="color:{linkColor};text-decoration:none;">{WebUtility.HtmlEncode(agent.DomainName)}</a></td></tr>""");

        var photoCell = string.IsNullOrWhiteSpace(agent.PhotoUrl)
            ? ""
            : $"""
              <td width="140" align="right" style="vertical-align:top;">
                <img src="{WebUtility.HtmlEncode(agent.PhotoUrl)}" width="132" alt="" style="display:block;width:132px;height:auto;border:3px solid #ffffff;" />
              </td>
              """;

        return $"""
            <table cellpadding="0" cellspacing="0" border="0" width="100%">
              <tr>
                <td style="vertical-align:top;font-size:12px;line-height:1.9;">
                  <div><strong style="color:{textColor};font-size:14px;">{WebUtility.HtmlEncode(agentName)}</strong>{(string.IsNullOrWhiteSpace(agent.Designation) ? "" : $"""<span style="color:{mutedColor};font-size:11px;">&nbsp;{WebUtility.HtmlEncode(agent.Designation)}</span>""")}</div>
                  {(string.IsNullOrWhiteSpace(agent.CompanyName) ? "" : $"""<div style="color:{textColor};font-weight:bold;margin-bottom:6px;">{WebUtility.HtmlEncode(agent.CompanyName)}</div>""")}
                  <table cellpadding="0" cellspacing="0" border="0">{string.Concat(lines)}</table>
                </td>
                {photoCell}
              </tr>
            </table>
            """;
    }
}
