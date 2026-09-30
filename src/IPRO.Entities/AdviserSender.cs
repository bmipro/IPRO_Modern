using System;

namespace IPRO.Entities;

// 530 (2026-09-29): who an adviser's client sees an email as coming from. The owner: clients get
// mail from "IPRO Advisers", "which they will have no idea as who iproadvisers is". The sender name
// itself is fixed per registered sender on the current email service, so step one works through
// what every email already has: its subject names the adviser's business, and replies go to the
// adviser -- never to iPro's support mailbox. Every email to a client asks this class, so the day
// the sender name can carry the business ("Global Business Solution via iPro") the change is here.
public static class AdviserSender
{
    // The name a client knows the adviser by: the business, else the person; empty when neither is on file.
    public static string BusinessName(AgentUser? agent)
    {
        if (agent == null) return string.Empty;
        if (!string.IsNullOrWhiteSpace(agent.CompanyName)) return agent.CompanyName.Trim();
        return $"{agent.FirstName} {agent.LastName}".Trim();
    }

    // An adviser-written subject -- a newsletter, a drip step, a card, a letter, a poll, an article --
    // led by the business, unless it already names it or there is no name to give.
    public static string Subject(AgentUser? agent, string? subject)
    {
        var text = (subject ?? string.Empty).Trim();
        var business = BusinessName(agent);
        if (business.Length == 0) return text;
        if (text.Contains(business, StringComparison.OrdinalIgnoreCase)) return text;
        return text.Length == 0 ? business : $"{business}: {text}";
    }

    // Replies go to the adviser. Null only when the adviser has no email on file, and then the
    // email service's own reply address applies, as before.
    public static string? ReplyToEmail(AgentUser? agent) =>
        string.IsNullOrWhiteSpace(agent?.Email) ? null : agent.Email.Trim();

    public static string? ReplyToName(AgentUser? agent)
    {
        var business = BusinessName(agent);
        return business.Length == 0 ? null : business;
    }

    // 533: the mailing address Canada's anti-spam law asks every commercial email to carry -- the
    // Profile's company address, city, province, postal code and country on one line. Empty when the
    // Profile holds no street, city or postal code: the country alone (it defaults to Canada) is not
    // an address anyone could write to.
    public static string MailingAddress(AgentUser? agent)
    {
        if (agent == null) return string.Empty;
        var located = new[] { agent.CompanyAddress, agent.City, agent.PostalCode }.Any(part => !string.IsNullOrWhiteSpace(part));
        return located ? agent.GetSingleLineAddress().Trim() : string.Empty;
    }

    // Whether the street line is on file; the marketing send pages ask for it while it is missing.
    public static bool HasStreetAddress(AgentUser? agent) => !string.IsNullOrWhiteSpace(agent?.CompanyAddress);
}
