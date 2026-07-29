using System.Net;
using IPRO.Entities;

namespace IPRO.Business.Services;

// A letterhead, not a card: restrained accent rule at the top, the agent's identity block,
// then the letter body set as readable prose, then a signature block. Table-based and
// inline-styled for the same email-client reasons as NewsletterHtmlComposer.
public static class ELetterHtmlComposer
{
    private const string DefaultAccent = "#1457d9";

    // client == null renders the letter with its merge tokens left visible, which is what the
    // editor's preview pane wants. A real send always passes the actual recipient.
    public static string Wrap(ELetter letter, AgentUser agent, Client? client)
    {
        var accent = string.IsNullOrWhiteSpace(agent.PortalAccentColor) ? DefaultAccent : agent.PortalAccentColor;
        // "Ms. Raniah Motamed" or "Raniah Motamed, CFP" -- see AgentNameFormatter.
        var agentName = AgentNameFormatter.FullName(agent);
        var siteUrl = string.IsNullOrWhiteSpace(agent.DomainName) ? null : $"https://{agent.DomainName}";

        var resolvedBody = client == null
            ? WebUtility.HtmlEncode(letter.Body)
            : MergeFieldResolver.ResolveHtml(WebUtility.HtmlEncode(letter.Body), client, agent);

        var bodyParagraphs = string.Concat(
            resolvedBody
                .Replace("\r\n", "\n")
                .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                .Select(paragraph => $"""<p style="margin:0 0 16px;">{paragraph.Trim().Replace("\n", "<br />")}</p>"""));

        var letterheadLines = new List<string>();
        if (!string.IsNullOrWhiteSpace(agent.CompanyName)) letterheadLines.Add($"""<div style="font-size:17px;font-weight:bold;color:#0f172a;">{WebUtility.HtmlEncode(agent.CompanyName)}</div>""");
        if (!string.IsNullOrWhiteSpace(agent.GetSingleLineAddress())) letterheadLines.Add($"""<div style="font-size:12px;color:#64748b;margin-top:2px;">{WebUtility.HtmlEncode(agent.GetSingleLineAddress())}</div>""");

        var photoCell = string.IsNullOrWhiteSpace(agent.PhotoUrl)
            ? ""
            : $"""
              <td width="58" style="padding-right:14px;vertical-align:top;">
                <img src="{WebUtility.HtmlEncode(agent.PhotoUrl)}" width="48" height="48" style="display:block;width:48px;height:48px;border-radius:50%;object-fit:cover;border:0;" alt="" />
              </td>
              """;

        var signatureDetails = new List<string>();
        if (!string.IsNullOrWhiteSpace(agent.Phone)) signatureDetails.Add($"tel: {WebUtility.HtmlEncode(agent.Phone)}");
        if (!string.IsNullOrWhiteSpace(agent.CellPhone)) signatureDetails.Add($"cell: {WebUtility.HtmlEncode(agent.CellPhone)}");
        if (!string.IsNullOrWhiteSpace(agent.Email)) signatureDetails.Add($"""<a href="mailto:{WebUtility.HtmlEncode(agent.Email)}" style="color:{WebUtility.HtmlEncode(accent)};text-decoration:none;">{WebUtility.HtmlEncode(agent.Email)}</a>""");
        if (siteUrl != null) signatureDetails.Add($"""<a href="{WebUtility.HtmlEncode(siteUrl)}" style="color:{WebUtility.HtmlEncode(accent)};text-decoration:none;">{WebUtility.HtmlEncode(agent.DomainName)}</a>""");

        return $"""
            <table cellpadding="0" cellspacing="0" border="0" width="100%" style="background:#f1f5f9;padding:24px 0;font-family:Arial,Helvetica,sans-serif;">
              <tr>
                <td align="center">
                  <table cellpadding="0" cellspacing="0" border="0" width="620" style="max-width:620px;background:#ffffff;">
                    <tr><td style="height:5px;background:{WebUtility.HtmlEncode(accent)};line-height:0;font-size:0;">&nbsp;</td></tr>
                    <tr>
                      <td style="padding:28px 40px 20px;border-bottom:1px solid #e2e8f0;">
                        {string.Concat(letterheadLines)}
                      </td>
                    </tr>
                    <tr>
                      <td style="padding:32px 40px;color:#1f2937;font-size:15px;line-height:1.7;">
                        {bodyParagraphs}
                      </td>
                    </tr>
                    <tr>
                      <td style="padding:0 40px 32px;">
                        <table cellpadding="0" cellspacing="0" border="0" style="border-top:1px solid #e2e8f0;padding-top:18px;width:100%;">
                          <tr>
                            <td style="padding-top:18px;">
                              <table cellpadding="0" cellspacing="0" border="0">
                                <tr>
                                  {photoCell}
                                  <td style="vertical-align:top;color:#475569;font-size:12px;line-height:1.6;">
                                    <strong style="color:#0f172a;font-size:14px;">{WebUtility.HtmlEncode(agentName)}</strong>
                                    {(signatureDetails.Count == 0 ? "" : $"<br />{string.Join(" &nbsp;&bull;&nbsp; ", signatureDetails)}")}
                                  </td>
                                </tr>
                              </table>
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
