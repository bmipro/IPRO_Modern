using System.Net;
using IPRO.Entities;

namespace IPRO.Business.Services;

// Builds an e-card as three stacked panels: the card face (licensed artwork, or a generated
// gradient), the greeting, then the agent's contact block.
//
// The greeting is deliberately NOT set on top of the artwork. The first live send proved why:
// over a full-bleed illustration the text landed on busy mid-tones and was unreadable, and there
// is no reliable fix for that -- every design has its clear area somewhere different, agents type
// their own wording at unpredictable lengths, and the CSS that would carry a translucent scrim
// (rgba, background-size) is exactly what Outlook's Word rendering engine drops. Giving the
// greeting its own solid band costs a little of the original's layered look and buys text that is
// legible in every client, at every message length, on every design.
public static class ECardHtmlComposer
{
    private const string DefaultAccent = "#1457d9";

    public static string Wrap(ECard card, AgentUser agent, string baseUrl)
    {
        var template = ECardTemplateCatalog.Find(card.Occasion) ?? ECardTemplateCatalog.Default;
        var accent = string.IsNullOrWhiteSpace(agent.PortalAccentColor) ? DefaultAccent : agent.PortalAccentColor;

        var header = string.IsNullOrWhiteSpace(card.Subject) ? template.DefaultHeaderText : card.Subject;
        var message = string.IsNullOrWhiteSpace(card.Message) ? template.DefaultMessage : card.Message;

        var dark = template.IsDark;
        var shellBg = dark ? "#111111" : "#ffffff";
        var textColor = dark ? "#ffffff" : "#1f2937";
        var mutedColor = dark ? "#cfd4da" : "#5b6472";
        var width = template.IsArtwork ? Math.Min(template.Width, 620) : 600;

        var face = template.IsArtwork
            ? BuildArtworkFace($"{baseUrl.TrimEnd('/')}{template.Url}", width, template)
            : BuildGeneratedFace(template, accent);

        return $"""
            <table cellpadding="0" cellspacing="0" border="0" width="100%" style="background:#eef1f5;padding:24px 0;font-family:Arial,Helvetica,sans-serif;">
              <tr><td align="center">
                <table cellpadding="0" cellspacing="0" border="0" width="{width}" style="max-width:{width}px;background:{shellBg};border-radius:10px;overflow:hidden;">
                  {face}
                  {BuildGreeting(header, message, textColor, mutedColor)}
                  <tr><td style="height:20px;line-height:20px;font-size:0;">&nbsp;</td></tr>
                  <tr><td style="padding:0 34px 30px;">
                    {BuildContactBlock(agent, accent, textColor, mutedColor, dark)}
                  </td></tr>
                </table>
              </td></tr>
            </table>
            """;
    }

    private static string BuildArtworkFace(string artUrl, int width, ECardTemplate template) =>
        $"""
        <tr><td style="padding:0;line-height:0;font-size:0;">
          <img src="{WebUtility.HtmlEncode(artUrl)}" width="{width}" alt="{WebUtility.HtmlEncode(template.Name)}"
               style="display:block;width:100%;max-width:{width}px;height:auto;border:0;" />
        </td></tr>
        """;

    // The simple cards: a gradient from the agent's own accent to the card's, with the emoji as
    // the whole face. Outlook ignores the gradient and falls back to the flat accent, which is fine.
    private static string BuildGeneratedFace(ECardTemplate template, string agentAccent) =>
        $"""
        <tr>
          <td align="center" bgcolor="{WebUtility.HtmlEncode(agentAccent)}"
              style="background:linear-gradient(135deg,{WebUtility.HtmlEncode(agentAccent)} 0%,{WebUtility.HtmlEncode(template.Accent)} 100%);background-color:{WebUtility.HtmlEncode(agentAccent)};padding:52px 32px;font-size:60px;line-height:1;">
            {template.Emoji}
          </td>
        </tr>
        """;

    // The greeting always gets its own band on the card's solid ground -- never over the art.
    private static string BuildGreeting(string header, string message, string textColor, string mutedColor) =>
        $"""
        <tr><td style="padding:30px 34px 0;text-align:center;">
          <div style="font-family:Georgia,'Times New Roman',serif;font-style:italic;font-size:26px;line-height:1.25;color:{textColor};">{WebUtility.HtmlEncode(header)}</div>
          <div style="margin-top:12px;font-size:15px;line-height:1.65;color:{mutedColor};">{WebUtility.HtmlEncode(message).Replace("\n", "<br>")}</div>
        </td></tr>
        """;

    // Mirrors the legacy signature block: name, title, company, tel/fax/cell, email and website,
    // with the agent's photo to the right at the original 132px.
    private static string BuildContactBlock(AgentUser agent, string accent, string textColor, string mutedColor, bool dark)
    {
        // "Ms. Raniah Motamed" or "Raniah Motamed, CFP" -- see AgentNameFormatter.
        var agentName = AgentNameFormatter.FullName(agent);
        var linkColor = dark ? "#8fc0ff" : accent;
        // nowrap keeps "web site:" on one line on the narrower artwork cards (467px).
        var labelStyle = $"font-style:italic;font-weight:bold;white-space:nowrap;color:{mutedColor};";

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
                  <div><strong style="color:{textColor};font-size:14px;">{WebUtility.HtmlEncode(agentName)}</strong></div>
                  {(string.IsNullOrWhiteSpace(agent.CompanyName) ? "" : $"""<div style="color:{textColor};font-weight:bold;margin-bottom:6px;">{WebUtility.HtmlEncode(agent.CompanyName)}</div>""")}
                  <table cellpadding="0" cellspacing="0" border="0">{string.Concat(lines)}</table>
                </td>
                {photoCell}
              </tr>
            </table>
            """;
    }
}
