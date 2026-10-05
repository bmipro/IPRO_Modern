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
//
// 546: the exception is artwork that already carries its words (ECardGreetingStyles). There the
// picture is the text, designed and approved as such, and a band below would print it twice.
public static class ECardHtmlComposer
{
    private const string DefaultAccent = "#1457d9";

    // The widest a card is drawn; larger art is scaled down to it.
    private const int MaxCardWidth = 620;

    // 546: art narrower than this (the supplied Norooz goldfish is 361 px) keeps its own size --
    // enlarging it would blur it -- and sits matted on the card's ground, in a card at least
    // MinMattedCardWidth wide so the contact block and photo still fit beside each other. The
    // narrowest art before 2026 (the anniversary roses, 467 px) is above the line and unchanged.
    private const int MattedBelow = 460;
    private const int MinMattedCardWidth = 480;
    private const int MatMargin = 24;

    // The plain-text alternative part.
    //
    // An e-card is a large image carrying about ten words, which is one of the oldest and heaviest
    // spam signals there is -- on 2026-08-08 every e-card sent to a cPanel/SpamAssassin host arrived
    // with ***SPAM*** prepended to the subject while the text-based e-letters to the same mailbox
    // went straight to the inbox. A message offering only an HTML part makes that worse; newsletters
    // and polls have always sent both, and cards and letters sent neither.
    //
    // This does not "fix spam" on its own -- scoring is probabilistic and the image ratio is the
    // bigger term -- but a multipart/alternative message with real text is table stakes, and it is
    // also what a screen reader and a text-only client get.
    // 552: siteHost is the address the adviser's clients are shown (AgentSiteAddress: their live custom domain, written
    // their way); null keeps the free <name>.247advisers.com address.
    public static string WrapText(ECard card, AgentUser agent, ECardDesign template, string? unsubscribeUrl = null, string? siteHost = null)
    {
        var (header, message) = Greeting(card, template);
        var name = $"{agent.FirstName} {agent.LastName}".Trim();

        var lines = new List<string> { header, string.Empty, message, string.Empty, "--" };

        if (!string.IsNullOrWhiteSpace(name)) lines.Add(name);
        if (!string.IsNullOrWhiteSpace(agent.CompanyName)) lines.Add(agent.CompanyName);
        if (!string.IsNullOrWhiteSpace(agent.Phone)) lines.Add($"Tel: {agent.Phone}");
        if (!string.IsNullOrWhiteSpace(agent.Email)) lines.Add(agent.Email);
        var site = SiteHost(agent, siteHost);
        if (site.Length > 0) lines.Add(site);

        // 533: the same closing lines as the HTML footer -- the business, its mailing address, the
        // way out, and iPro sending on its behalf.
        lines.Add(string.Empty);
        lines.Add(SenderFooter.Text(agent, unsubscribeUrl, SenderFooterKind.Client));

        return string.Join("\n", lines);
    }

    // The design is passed in rather than looked up: the composer runs inside the dispatcher's
    // per-send loop and inside the preview action, both of which already have a DbContext open.
    public static string Wrap(ECard card, AgentUser agent, ECardDesign template, string baseUrl, string? siteHost = null)
    {
        var accent = string.IsNullOrWhiteSpace(agent.PortalAccentColor) ? DefaultAccent : agent.PortalAccentColor;

        var (header, message) = Greeting(card, template);

        var dark = template.IsDark;
        var shellBg = dark ? "#111111" : "#ffffff";
        var textColor = dark ? "#ffffff" : "#1f2937";
        var mutedColor = dark ? "#cfd4da" : "#5b6472";

        // A design uploaded before 546 never recorded its picture's size (Width 0), which drew the
        // picture zero pixels wide; an unknown size now takes the full card width.
        var artWidth = template.IsArtwork && template.Width > 0 ? Math.Min(template.Width, MaxCardWidth) : MaxCardWidth;
        var matted = template.IsArtwork && template.Width > 0 && template.Width < MattedBelow;
        var width = !template.IsArtwork ? 600
            : matted ? Math.Max(artWidth + 2 * MatMargin, MinMattedCardWidth)
            : artWidth;

        var face = template.IsArtwork
            ? BuildArtworkFace(template.AbsoluteImageUrl(baseUrl), artWidth, matted, ArtAltText(template))
            : BuildGeneratedFace(template, accent);

        // The greeting band: the title and the message below the picture, the message alone under
        // lettering art, nothing under a picture that carries the whole greeting.
        var greeting = template.MessageIsInPicture ? string.Empty
            : BuildGreeting(template.TitleIsInPicture ? null : header, message, textColor, mutedColor);

        // 548: the card fills a phone's width, up to its own, instead of being drawn at a fixed width
        // that the phone then shrinks to fit: the picture scales and the words keep their size. Outlook
        // on Windows ignores max-width, so a table only it reads (the [if mso] comments) holds the card
        // at its width there, as before.
        return $"""
            <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="background:#eef1f5;padding:24px 0;font-family:Arial,Helvetica,sans-serif;">
              <tr><td align="center">
                <!--[if mso]><table role="presentation" cellpadding="0" cellspacing="0" border="0" width="{width}" align="center"><tr><td><![endif]-->
                <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="width:100%;max-width:{width}px;background:{shellBg};border-radius:10px;overflow:hidden;">
                  {face}
                  {greeting}
                  <tr><td style="height:20px;line-height:20px;font-size:0;">&nbsp;</td></tr>
                  <tr><td style="padding:0 {ContactPadding}px 30px;font-size:0;">
                    {BuildContactBlock(agent, accent, textColor, mutedColor, dark, width - 2 * ContactPadding, siteHost)}
                  </td></tr>
                </table>
                <!--[if mso]></td></tr></table><![endif]-->
              </td></tr>
            </table>
            """;
    }

    private const int ContactPadding = 34;

    // The photo's column beside the contact details: the 132 px photo and its 3 px frame.
    private const int PhotoColumn = 140;

    // 546: what the card says. The agent's subject is the title and their message the message, each
    // falling back to the design's own -- except where the picture already holds the words: lettering
    // art's title is the design's title, and a greeting printed inside the picture is the design's
    // whole greeting, whatever was typed (agents cannot change text inside a picture). The plain-text
    // part and the alt text then say what the picture says.
    private static (string Header, string Message) Greeting(ECard card, ECardDesign template)
    {
        var header = template.TitleIsInPicture || string.IsNullOrWhiteSpace(card.Subject) ? template.DefaultHeaderText : card.Subject;
        var message = template.MessageIsInPicture || string.IsNullOrWhiteSpace(card.Message) ? template.DefaultMessage : card.Message;
        return (header, message);
    }

    // The design's name describes a text-free picture. Where the picture holds words, those words
    // are its content: the alt text is what a reader with images blocked, or a screen reader, gets.
    private static string ArtAltText(ECardDesign template)
    {
        if (!template.TitleIsInPicture) return template.Name;
        var title = template.DefaultHeaderText.Trim();
        if (!template.MessageIsInPicture) return title;
        // "Thank You. With sincere appreciation..." -- a title without its own stop gets one.
        var stop = title.Length == 0 || ".!?".Contains(title[^1]) ? " " : ". ";
        return (title + stop + template.DefaultMessage.Trim()).Trim();
    }

    private static string BuildArtworkFace(string artUrl, int width, bool matted, string alt) =>
        matted
            ? $"""
              <tr><td align="center" style="padding:{MatMargin}px {MatMargin}px 0;line-height:0;font-size:0;">
                <img src="{WebUtility.HtmlEncode(artUrl)}" width="{width}" alt="{WebUtility.HtmlEncode(alt)}"
                     style="display:block;margin:0 auto;width:100%;max-width:{width}px;height:auto;border:0;" />
              </td></tr>
              """
            : $"""
              <tr><td style="padding:0;line-height:0;font-size:0;">
                <img src="{WebUtility.HtmlEncode(artUrl)}" width="{width}" alt="{WebUtility.HtmlEncode(alt)}"
                     style="display:block;width:100%;max-width:{width}px;height:auto;border:0;" />
              </td></tr>
              """;

    // The simple cards: a gradient from the agent's own accent to the card's, with the emoji as
    // the whole face. Outlook ignores the gradient and falls back to the flat accent, which is fine.
    private static string BuildGeneratedFace(ECardDesign template, string agentAccent) =>
        $"""
        <tr>
          <td align="center" bgcolor="{WebUtility.HtmlEncode(agentAccent)}"
              style="background:linear-gradient(135deg,{WebUtility.HtmlEncode(agentAccent)} 0%,{WebUtility.HtmlEncode(template.Accent)} 100%);background-color:{WebUtility.HtmlEncode(agentAccent)};padding:52px 32px;font-size:60px;line-height:1;">
            {template.Emoji}
          </td>
        </tr>
        """;

    // The greeting always gets its own band on the card's solid ground -- never over the art. A null
    // header is lettering art's: the title is in the picture just above, so only the message is set.
    private static string BuildGreeting(string? header, string message, string textColor, string mutedColor) =>
        $"""
        <tr><td style="padding:30px 34px 0;text-align:center;">
          {(header == null ? "" : $"""<div style="font-family:Georgia,'Times New Roman',serif;font-style:italic;font-size:26px;line-height:1.25;color:{textColor};margin-bottom:12px;">{WebUtility.HtmlEncode(header)}</div>""")}
          <div style="font-size:15px;line-height:1.65;color:{mutedColor};">{WebUtility.HtmlEncode(message).Replace("\n", "<br>")}</div>
        </td></tr>
        """;

    // 552: the address shown for the adviser's site -- the caller's (AgentSiteAddress) or the free one.
    internal static string SiteHost(AgentUser agent, string? siteHost) =>
        string.IsNullOrWhiteSpace(siteHost) ? (agent.DomainName ?? string.Empty).Trim() : siteHost.Trim();

    // Mirrors the legacy signature block: name, title, company, tel/fax/cell, email and website,
    // with the agent's photo to the right at the original 132px.
    //
    // 548: the details and the photo are two inline blocks, side by side where the card has room and
    // the photo under the details on a phone -- they wrap on their own, with no media query (Gmail
    // drops a <style> in an email's body). Outlook on Windows ignores inline-block, so a table only it
    // reads keeps the two side by side there. The cell around them has font-size 0, so the space
    // between two inline blocks cannot push the photo onto the next line on a computer.
    private static string BuildContactBlock(AgentUser agent, string accent, string textColor, string mutedColor, bool dark, int contentWidth, string? siteHost)
    {
        // "Ms. Raniah Motamed" or "Raniah Motamed, CFP" -- see AgentNameFormatter.
        var agentName = AgentNameFormatter.FullName(agent);
        var linkColor = dark ? "#8fc0ff" : accent;
        // nowrap keeps "web site:" on one line on the narrower artwork cards (467px).
        var labelStyle = $"font-style:italic;font-weight:bold;white-space:nowrap;color:{mutedColor};";

        // A long address breaks rather than pushing the card wider than a phone.
        const string wrap = "overflow-wrap:anywhere;word-break:break-word;";
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(agent.Phone))
            lines.Add($"""<tr><td style="{labelStyle}padding-right:10px;">tel:</td><td style="color:{textColor};">{WebUtility.HtmlEncode(agent.Phone)}</td></tr>""");
        if (!string.IsNullOrWhiteSpace(agent.BusinessFax))
            lines.Add($"""<tr><td style="{labelStyle}padding-right:10px;">fax:</td><td style="color:{textColor};">{WebUtility.HtmlEncode(agent.BusinessFax)}</td></tr>""");
        if (!string.IsNullOrWhiteSpace(agent.CellPhone))
            lines.Add($"""<tr><td style="{labelStyle}padding-right:10px;">cell:</td><td style="color:{textColor};">{WebUtility.HtmlEncode(agent.CellPhone)}</td></tr>""");
        if (!string.IsNullOrWhiteSpace(agent.Email))
            lines.Add($"""<tr><td style="{labelStyle}padding-right:10px;">email:</td><td style="{wrap}"><a href="mailto:{WebUtility.HtmlEncode(agent.Email)}" style="color:{linkColor};text-decoration:none;">{WebUtility.HtmlEncode(agent.Email)}</a></td></tr>""");
        var site = SiteHost(agent, siteHost);
        if (site.Length > 0)
            lines.Add($"""<tr><td style="{labelStyle}padding-right:10px;">web site:</td><td style="{wrap}"><a href="https://{WebUtility.HtmlEncode(site)}" style="color:{linkColor};text-decoration:none;">{WebUtility.HtmlEncode(site)}</a></td></tr>""");

        var details = $"""
            <div style="font-size:12px;line-height:1.9;text-align:left;">
              <div><strong style="color:{textColor};font-size:14px;">{WebUtility.HtmlEncode(agentName)}</strong></div>
              {(string.IsNullOrWhiteSpace(agent.CompanyName) ? "" : $"""<div style="color:{textColor};font-weight:bold;margin-bottom:6px;">{WebUtility.HtmlEncode(agent.CompanyName)}</div>""")}
              <table role="presentation" cellpadding="0" cellspacing="0" border="0">{string.Concat(lines)}</table>
            </div>
            """;
        if (string.IsNullOrWhiteSpace(agent.PhotoUrl)) return details;

        // Two pixels of slack: a mail app that zooms can round the card a pixel narrower, and on a
        // computer the photo must not drop under the details.
        var detailsWidth = Math.Max(contentWidth - PhotoColumn - 2, 160);
        return $"""
            <!--[if mso]><table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%"><tr><td valign="top"><![endif]--><div style="display:inline-block;width:100%;max-width:{detailsWidth}px;vertical-align:top;">{details}</div><!--[if mso]></td><td width="{PhotoColumn}" align="right" valign="top"><![endif]--><div style="display:inline-block;width:{PhotoColumn}px;vertical-align:top;">
              <img src="{WebUtility.HtmlEncode(agent.PhotoUrl)}" width="132" alt="" style="display:block;margin-left:auto;width:132px;height:auto;border:3px solid #ffffff;" />
            </div><!--[if mso]></td></tr></table><![endif]-->
            """;
    }
}
