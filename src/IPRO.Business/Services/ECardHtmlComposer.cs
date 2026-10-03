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
    public static string WrapText(ECard card, AgentUser agent, ECardDesign template, string? unsubscribeUrl = null)
    {
        var (header, message) = Greeting(card, template);
        var name = $"{agent.FirstName} {agent.LastName}".Trim();

        var lines = new List<string> { header, string.Empty, message, string.Empty, "--" };

        if (!string.IsNullOrWhiteSpace(name)) lines.Add(name);
        if (!string.IsNullOrWhiteSpace(agent.CompanyName)) lines.Add(agent.CompanyName);
        if (!string.IsNullOrWhiteSpace(agent.Phone)) lines.Add($"Tel: {agent.Phone}");
        if (!string.IsNullOrWhiteSpace(agent.Email)) lines.Add(agent.Email);
        if (!string.IsNullOrWhiteSpace(agent.DomainName)) lines.Add(agent.DomainName);

        // 533: the same closing lines as the HTML footer -- the business, its mailing address, the
        // way out, and iPro sending on its behalf.
        lines.Add(string.Empty);
        lines.Add(SenderFooter.Text(agent, unsubscribeUrl, SenderFooterKind.Client));

        return string.Join("\n", lines);
    }

    // The design is passed in rather than looked up: the composer runs inside the dispatcher's
    // per-send loop and inside the preview action, both of which already have a DbContext open.
    public static string Wrap(ECard card, AgentUser agent, ECardDesign template, string baseUrl)
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

        return $"""
            <table cellpadding="0" cellspacing="0" border="0" width="100%" style="background:#eef1f5;padding:24px 0;font-family:Arial,Helvetica,sans-serif;">
              <tr><td align="center">
                <table cellpadding="0" cellspacing="0" border="0" width="{width}" style="max-width:{width}px;background:{shellBg};border-radius:10px;overflow:hidden;">
                  {face}
                  {greeting}
                  <tr><td style="height:20px;line-height:20px;font-size:0;">&nbsp;</td></tr>
                  <tr><td style="padding:0 34px 30px;">
                    {BuildContactBlock(agent, accent, textColor, mutedColor, dark)}
                  </td></tr>
                </table>
              </td></tr>
            </table>
            """;
    }

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
