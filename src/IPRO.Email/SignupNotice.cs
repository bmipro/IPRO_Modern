using System.Globalization;
using System.Net;
using System.Text;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IPRO.Email;

// 551 (2026-10-05): L'Avenue Boulangerie signed up and nobody at iPro was told -- the owner: "as an admin to
// this system I did not get any email saying that someone has registered ... Lets do it so I/admin get an
// email". Two notices to iPro's own mailbox: one when an adviser registers (sent before PayPal, so a
// registrant who never pays is still news), one when their subscription starts. The address is
// Signups:NotificationEmail when set, else Support:NotificationEmail -- the mailbox the support-ticket
// notices already reach. A notice never fails a sign-up or a payment: SendAsync logs and swallows.
public static class SignupNotice
{
    public const string RecipientSetting = "Signups:NotificationEmail";
    public const string AdminBaseUrlSetting = "Signups:AdminBaseUrl";
    public const string DefaultAdminBaseUrl = "https://admin.iproadvisers.com";

    public sealed record Registration(
        int AgentId, string FirstName, string LastName, string CompanyName, string BusinessType,
        string Email, string Phone, string City, string Province,
        string PackageName, string Period, decimal RecurringPrice, decimal SetupFee, string Code,
        string? TrialCode, DateTime? TrialEndsAt, string CameFrom, DateTime RegisteredAtUtc);

    public sealed record Started(
        int AgentId, string FirstName, string LastName, string CompanyName, string Email,
        string PackageName, string Period, decimal FirstBillTotal, string Code, bool Again, bool NoCost, DateTime StartedAtUtc);

    public sealed record Notice(string Subject, string Html, string Text);

    public static string? Recipient(IConfiguration configuration)
    {
        foreach (var candidate in new[] { configuration[RecipientSetting], configuration["Support:NotificationEmail"] })
        {
            var address = candidate?.Trim();
            if (string.IsNullOrEmpty(address)) continue;
            if (address.Contains("CHANGE_THIS", StringComparison.OrdinalIgnoreCase)) continue;
            if (!address.Contains('@')) continue;
            return address;
        }
        return null;
    }

    public static Notice ForRegistration(Registration r, IConfiguration configuration)
    {
        var name = $"{r.FirstName} {r.LastName}".Trim();
        var company = string.IsNullOrWhiteSpace(r.CompanyName) ? name : r.CompanyName.Trim();
        var when = LocalMoment(r.RegisteredAtUtc, configuration);
        var trial = !string.IsNullOrWhiteSpace(r.TrialCode);
        var plan = trial ? $"{r.PackageName}, free trial" : $"{r.PackageName}, {r.Period.ToLowerInvariant()}";
        var price = trial ? "no charge during the trial" : PriceText(r.RecurringPrice, r.Period, r.SetupFee);
        var next = trial
            ? $"Free trial through invitation {r.TrialCode}, until {LocalDate(r.TrialEndsAt, configuration)}. There is no payment step."
            : "They are on their way to PayPal. You will get a second email when their subscription starts; no second email means they stopped before paying.";
        var link = AgentLink(r.AgentId, configuration);
        var location = string.Join(", ", new[] { r.City, r.Province }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));

        var rows = new (string Label, string Value)[]
        {
            ("Business type", r.BusinessType),
            ("Plan", plan),
            ("Price", price),
            ("Code entered", string.IsNullOrWhiteSpace(r.Code) ? "(none)" : r.Code.Trim()),
            ("Email", r.Email),
            ("Phone", r.Phone),
            ("Location", location),
            ("Came from", r.CameFrom),
        };
        var lead = $"{name} of {company} signed up on {when}.";
        return new Notice(
            $"New sign-up: {company} ({name})",
            Html("New sign-up", lead, rows, next, link),
            Text(lead, rows, next, link));
    }

    public static Notice ForStart(Started s, IConfiguration configuration)
    {
        var name = $"{s.FirstName} {s.LastName}".Trim();
        var company = string.IsNullOrWhiteSpace(s.CompanyName) ? name : s.CompanyName.Trim();
        var when = LocalMoment(s.StartedAtUtc, configuration);
        var link = AgentLink(s.AgentId, configuration);
        var lead = s.NoCost
            ? $"{company}'s subscription started on {when}. No charge: code {s.Code} covers it."
            : $"{company}'s subscription started. PayPal approved it on {when}. First bill: ${Money(s.FirstBillTotal)} (tax included). The payment itself shows on the invoice when PayPal settles it, usually within minutes.";
        var rows = new (string Label, string Value)[]
        {
            ("Adviser", name),
            ("Email", s.Email),
            ("Plan", $"{s.PackageName}, {s.Period.ToLowerInvariant()}"),
            ("Code", string.IsNullOrWhiteSpace(s.Code) ? "(none)" : s.Code.Trim()),
        };
        var history = s.Again
            ? "They have had a subscription before (a return after cancelling, or the second half of a plan change)."
            : "A new customer.";
        return new Notice(
            $"{(s.Again ? "Subscription restarted" : "Subscription started")}: {company} ({name})",
            Html(s.Again ? "Subscription restarted" : "Subscription started", lead, rows, history, link),
            Text(lead, rows, history, link));
    }

    // Where a self-registered adviser came from (512's PlatformSignupOrigin), in words.
    public static string CameFrom(PlatformSignupOrigin? origin)
    {
        if (origin == null) return "not recorded";
        var source = origin.Source.Trim();
        var medium = origin.Medium.Trim();
        var campaign = origin.Campaign.Trim();
        if (source.Length > 0 || medium.Length > 0 || campaign.Length > 0)
        {
            var details = new List<string>();
            if (medium.Length > 0) details.Add(medium);
            if (campaign.Length > 0) details.Add($"campaign {campaign}");
            var named = source.Length > 0 ? SourceName(source) : "A tagged link";
            return details.Count == 0 ? named : $"{named} ({string.Join(", ", details)})";
        }
        if (origin.ReferrerHost.Trim().Length > 0) return $"a link on {origin.ReferrerHost.Trim()}";
        return "typed the address or used a bookmark (no referring site)";
    }

    public static async Task<bool> SendAsync(IEmailService email, IConfiguration configuration, ILogger logger, Notice notice)
    {
        var to = Recipient(configuration);
        if (to == null)
        {
            logger.LogWarning("Sign-up notice not sent ({Subject}): no Signups:NotificationEmail or Support:NotificationEmail is set.", notice.Subject);
            return false;
        }
        try
        {
            var sent = await email.SendAsync(to, "iPro", notice.Subject, notice.Html, notice.Text);
            if (!sent) logger.LogWarning("Sign-up notice was not sent ({Subject}).", notice.Subject);
            return sent;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sign-up notice failed ({Subject}).", notice.Subject);
            return false;
        }
    }

    private static readonly Dictionary<string, string> KnownSources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["google"] = "Google", ["bing"] = "Bing", ["linkedin"] = "LinkedIn", ["facebook"] = "Facebook",
        ["instagram"] = "Instagram", ["youtube"] = "YouTube", ["twitter"] = "X (Twitter)", ["x"] = "X (Twitter)",
    };

    private static string SourceName(string source) =>
        KnownSources.TryGetValue(source, out var known) ? known : char.ToUpperInvariant(source[0]) + source[1..];

    private static string PriceText(decimal recurring, string period, decimal setupFee)
    {
        var cycle = string.Equals(period, "Annually", StringComparison.OrdinalIgnoreCase) ? "a year" : "a month";
        var setup = setupFee > 0 ? $"setup fee ${Money(setupFee)}" : "no setup fee";
        return $"${Money(recurring)} {cycle}, {setup} (before any code, plus tax)";
    }

    private static string Money(decimal amount) => amount.ToString("N2", CultureInfo.InvariantCulture);

    private static string AgentLink(int agentId, IConfiguration configuration)
    {
        var baseUrl = configuration[AdminBaseUrlSetting];
        if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = DefaultAdminBaseUrl;
        return $"{baseUrl.Trim().TrimEnd('/')}/Agents/Details/{agentId}";
    }

    private static string LocalMoment(DateTime utc, IConfiguration configuration)
    {
        var local = AgentLocalTime.FromUtc(utc, AgentLocalTime.Normalize(configuration["Admin:TimeZone"]));
        var hour = local.Hour % 12 == 0 ? 12 : local.Hour % 12;
        return $"{local.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture)} at {hour}:{local:mm} {(local.Hour < 12 ? "a.m." : "p.m.")}";
    }

    private static string LocalDate(DateTime? utc, IConfiguration configuration) =>
        utc.HasValue
            ? AgentLocalTime.FromUtc(utc.Value, AgentLocalTime.Normalize(configuration["Admin:TimeZone"])).ToString("MMMM d, yyyy", CultureInfo.InvariantCulture)
            : "(no end date)";

    private static string Html(string heading, string lead, IEnumerable<(string Label, string Value)> rows, string closing, string link)
    {
        static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
        var table = new StringBuilder();
        foreach (var (label, value) in rows)
        {
            table.Append("<tr><td style=\"padding:6px 12px 6px 0;color:#5b6475;white-space:nowrap;vertical-align:top\">")
                .Append(E(label)).Append("</td><td style=\"padding:6px 0;color:#17223a\">")
                .Append(E(string.IsNullOrWhiteSpace(value) ? "-" : value)).Append("</td></tr>");
        }
        return $"""
            <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#17223a">
              <div style="padding:18px 22px;background:#193f82;color:#ffffff"><h1 style="margin:0;font-size:20px">{E(heading)}</h1></div>
              <div style="padding:22px;border:1px solid #dce4ef;border-top:0">
                <p style="margin:0 0 14px">{E(lead)}</p>
                <table style="border-collapse:collapse;font-size:14px;margin:0 0 14px">{table}</table>
                <p style="margin:0 0 18px">{E(closing)}</p>
                <p style="margin:0"><a href="{E(link)}" style="display:inline-block;padding:10px 16px;background:#193f82;color:#ffffff;text-decoration:none;border-radius:6px">Open in SuperAdmin</a></p>
              </div>
            </div>
            """;
    }

    private static string Text(string lead, IEnumerable<(string Label, string Value)> rows, string closing, string link)
    {
        var text = new StringBuilder().AppendLine(lead).AppendLine();
        foreach (var (label, value) in rows) text.Append(label).Append(": ").AppendLine(string.IsNullOrWhiteSpace(value) ? "-" : value);
        return text.AppendLine().AppendLine(closing).AppendLine().Append("Open in SuperAdmin: ").AppendLine(link).ToString();
    }
}
