using System.Net;
using System.Text.RegularExpressions;
using IPRO.Entities;

namespace IPRO.Business.Services;

// Swaps friendly [Square Bracket] tokens for real per-recipient values at send time.
//
// Deliberately square brackets with spaces ("[First Name]") rather than a developer-ish
// {{first_name}}: agents type these by hand into a letter body, so they need to read like
// English and survive being retyped. Matching is case-insensitive and tolerant of extra
// inner spacing for the same reason.
public static class MergeFieldResolver
{
    public sealed record MergeField(string Token, string Description);

    // Shown in the editor as clickable chips. Order is the order agents see them.
    public static readonly MergeField[] AvailableFields =
    {
        new("[First Name]", "The client's first name"),
        new("[Last Name]", "The client's last name"),
        new("[Full Name]", "The client's first and last name"),
        new("[Company]", "The client's company, if recorded"),
        new("[Advisor Name]", "Your own name"),
        new("[Advisor Company]", "Your company name"),
        new("[Advisor Phone]", "Your business phone"),
        new("[Advisor Email]", "Your email address")
    };

    // Every substituted value is HTML-encoded: client names/companies are user-entered data
    // being injected into an HTML email body, so an apostrophe or an angle bracket in a real
    // name must render as text, never as markup.
    public static string ResolveHtml(string template, Client client, AgentUser agent) =>
        Resolve(template, client, agent, WebUtility.HtmlEncode);

    // Plain-text variant for subject lines, which are not HTML.
    public static string ResolveText(string template, Client client, AgentUser agent) =>
        Resolve(template, client, agent, value => value);

    private static string Resolve(string template, Client client, AgentUser agent, Func<string, string> escape)
    {
        if (string.IsNullOrWhiteSpace(template)) return template ?? string.Empty;

        var values = BuildValues(client, agent);

        // One pass over the source text, so a value that happens to contain something
        // token-shaped can never be re-expanded on a later pass.
        return Regex.Replace(template, @"\[\s*([A-Za-z]+(?:\s+[A-Za-z]+)*)\s*\]", match =>
        {
            var key = Normalize(match.Groups[1].Value);
            return values.TryGetValue(key, out var value) ? escape(value) : match.Value;
        });
    }

    private static Dictionary<string, string> BuildValues(Client client, AgentUser agent)
    {
        var clientFirst = client.FirstName?.Trim() ?? string.Empty;
        var clientLast = client.LastName?.Trim() ?? string.Empty;
        var agentName = $"{agent.FirstName} {agent.LastName}".Trim();

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["firstname"] = clientFirst,
            ["lastname"] = clientLast,
            ["fullname"] = $"{clientFirst} {clientLast}".Trim(),
            ["company"] = client.CompanyName?.Trim() ?? string.Empty,
            ["advisorname"] = agentName,
            ["advisorcompany"] = agent.CompanyName?.Trim() ?? string.Empty,
            ["advisorphone"] = agent.Phone?.Trim() ?? string.Empty,
            ["advisoremail"] = agent.Email?.Trim() ?? string.Empty
        };
    }

    private static string Normalize(string token) => token.Replace(" ", string.Empty).ToLowerInvariant();
}
