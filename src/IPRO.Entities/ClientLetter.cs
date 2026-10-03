using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace IPRO.Entities;

// 542 (2026-10-03): the emails an adviser's client gets about their business with the adviser -- an
// invoice or estimate, a payment reminder, the portal invitation, a testimonial request, an appointment
// answer -- read as a short letter from the adviser. The owner's Amazon pilot put an estimate in Yahoo's
// Spam with its links disabled: a heading, one sentence and one "View estimate" button, under a business
// name in the From line and a free-mail Reply-To, is what invoice fraud looks like to a filter. A letter
// opens with the client's name, says what it is about, and closes with the adviser's name, business,
// phone, email and address: what a client needs to recognise the sender and to reach them without the link.
public static class ClientLetter
{
    public const string Blue = "#1457d9";
    public const string Green = "#0f7a52";

    // `paragraphs` and `closing` are HTML; their callers encode what a person typed. The button and the
    // closing are optional; `withAddress` is off when a footer below already gives the address (533), and
    // `sentWith` is off when that footer already says "Sent with iPro".
    public static string Html(AgentUser? agent, string? clientFirstName, IEnumerable<string> paragraphs,
        string? buttonLabel = null, string? buttonUrl = null, string? closing = null, string? heading = null,
        string accent = Blue, bool greet = true, bool withAddress = true, bool sentWith = true)
    {
        var body = new List<string>();
        if (greet) body.Add($"<p>{Greeting(clientFirstName)}</p>");
        body.AddRange(paragraphs.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => $"<p>{p}</p>"));
        if (!string.IsNullOrWhiteSpace(buttonLabel) && !string.IsNullOrWhiteSpace(buttonUrl))
            body.Add($"""<p><a href="{buttonUrl}" style="display:inline-block;padding:11px 18px;background:{accent};color:white;text-decoration:none;border-radius:6px">{WebUtility.HtmlEncode(buttonLabel)}</a></p>""");
        if (!string.IsNullOrWhiteSpace(closing)) body.Add($"<p>{closing}</p>");

        var signOff = SignOff(agent, withAddress);
        if (signOff.Length > 0) body.Add($"""<p style="margin:18px 0 0">{signOff}</p>""");
        if (sentWith)
            body.Add($"""<p style="margin:22px 0 0;font-size:12px;color:#8a94a6">Sent with <a href="{PoweredBy.BrandUrl(agent?.BusinessType)}" style="color:#8a94a6;font-weight:600;text-decoration:none">{PoweredBy.Label}</a></p>""");

        var title = (heading ?? AdviserSender.BusinessName(agent)).Trim();
        var band = title.Length == 0 ? string.Empty
            : $"""<div style="padding:22px;background:{accent};color:white"><h1 style="margin:0;font-size:24px">{WebUtility.HtmlEncode(title)}</h1></div>""";

        return $"""
            <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#17223a">
              {band}
              <div style="padding:24px;border:1px solid #dce4ef;{(band.Length == 0 ? "" : "border-top:0;")}line-height:1.5">
                {string.Join("\n    ", body)}
              </div>
            </div>
            """;
    }

    // "If you have any questions, just reply to this email or call 416-555-1212." Replies reach the adviser
    // (AdviserSender.ReplyToEmail), so the reply needs no address of its own.
    public static string QuestionsLine(AgentUser? agent)
    {
        var phone = agent?.Phone?.Trim() ?? string.Empty;
        return phone.Length == 0
            ? "If you have any questions, just reply to this email."
            : $"If you have any questions, just reply to this email or call {WebUtility.HtmlEncode(phone)}.";
    }

    private static string Greeting(string? firstName)
    {
        var name = firstName?.Trim() ?? string.Empty;
        return name.Length == 0 ? "Hello," : $"Hi {WebUtility.HtmlEncode(name)},";
    }

    // The adviser's name, their title, the business, phone and email, and the mailing address -- whichever
    // are on file, one to a line.
    private static string SignOff(AgentUser? agent, bool withAddress)
    {
        if (agent == null) return string.Empty;
        var lines = new List<string>();
        var person = $"{agent.FirstName} {agent.LastName}".Trim();
        if (person.Length > 0) lines.Add(WebUtility.HtmlEncode(person));
        if (!string.IsNullOrWhiteSpace(agent.Designation)) lines.Add(WebUtility.HtmlEncode(agent.Designation.Trim()));
        var business = AdviserSender.BusinessName(agent);
        if (business.Length > 0 && business != person) lines.Add(WebUtility.HtmlEncode(business));
        var reach = new[] { agent.Phone, agent.Email }.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => WebUtility.HtmlEncode(v!.Trim())).ToList();
        if (reach.Count > 0) lines.Add(string.Join(" &middot; ", reach));
        if (withAddress)
        {
            var address = AdviserSender.MailingAddress(agent);
            if (address.Length > 0) lines.Add(WebUtility.HtmlEncode(address));
        }
        return lines.Count == 0 ? string.Empty : "Best regards,<br>" + string.Join("<br>", lines);
    }
}
