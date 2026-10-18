using System.Net;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.Extensions.Configuration;

namespace IPRO.Email;

// 585: the emails of the new builder (Beta) while iPro's staff publish what advisers send (the owner's
// decision 3 C): iPro's own mailbox hears that a site is waiting, and the adviser hears when it is on
// their website, or that it was dropped. Sent to SignupNotice.Recipient and to the adviser's own
// address; never fails the step that sends them.
public static class BuilderBetaNotice
{
    public static SignupNotice.Notice SentForReview(AgentUser agent, WebsiteBuilderImport import, IConfiguration configuration)
    {
        var name = $"{agent.FirstName} {agent.LastName}".Trim();
        var company = string.IsNullOrWhiteSpace(agent.CompanyName) ? name : agent.CompanyName.Trim();
        var adminBase = configuration[SignupNotice.AdminBaseUrlSetting];
        if (string.IsNullOrWhiteSpace(adminBase)) adminBase = SignupNotice.DefaultAdminBaseUrl;
        var link = $"{adminBase.Trim().TrimEnd('/')}/BuilderImports/Review/{import.Id}";
        var lead = $"{company} ({name}) sent their website from the new builder (Beta). It is waiting for review: {import.PageCount} page(s), {import.NewImageCount} new picture(s). Nothing on their website has changed.";
        var closing = "Open it in SuperAdmin, look at each page, then bring it in. They get an email when it is live.";
        return new SignupNotice.Notice($"Website sent for review: {company}", Html("Website sent for review", lead, closing, link, "Review it in SuperAdmin"), $"{lead}\n\n{closing}\n\n{link}\n");
    }

    // sitePublished: whether visitors see the website yet. If not, the changes are on it, waiting for
    // the adviser to publish it, and the email says so rather than "live".
    public static SignupNotice.Notice Published(AgentUser agent, IConfiguration configuration, bool sitePublished = true)
    {
        var link = $"{WebAppUrlHelper.GetWebAppBaseUrl(configuration)}/portal/Website";
        var first = string.IsNullOrWhiteSpace(agent.FirstName) ? "Hello" : $"Hello {agent.FirstName.Trim()}";
        var lead = sitePublished
            ? $"{first}, the changes you made in the new website builder are on your website now."
            : $"{first}, the changes you made in the new website builder are on your website. Your website is not published yet, so visitors see them once you publish it under My Website.";
        var closing = "If something is not right, open My Website in iPro: under \"New builder (Beta)\", \"Put my previous site back\" undoes it. You can keep changing your pages in the page editor as before.";
        var subject = sitePublished ? "Your website changes are live" : "Your website changes are in place";
        return new SignupNotice.Notice(subject, Html(subject, lead, closing, link, "Open My Website"), $"{lead}\n\n{closing}\n\n{link}\n");
    }

    // iPro's staff dropped what the adviser sent: it will not be published, and their draft is still theirs.
    public static SignupNotice.Notice Dropped(AgentUser agent, WebsiteBuilderImport import, IConfiguration configuration)
    {
        var link = $"{WebAppUrlHelper.GetWebAppBaseUrl(configuration)}/portal/Website";
        var first = string.IsNullOrWhiteSpace(agent.FirstName) ? "Hello" : $"Hello {agent.FirstName.Trim()}";
        var sent = IPRO.DataAccess.AgentLocalTime.FromUtc(import.CreatedAt, agent.TimeZone).ToString("MMMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture);
        var lead = $"{first}, iPro's team did not publish the site you sent from the new website builder on {sent}. Your website has not changed.";
        var closing = "Your draft is still in the builder: open it from My Website, change what is needed and send it again. If you are not sure why it was not published, reply to this email.";
        return new SignupNotice.Notice("Your website changes were not published", Html("Your website changes were not published", lead, closing, link, "Open My Website"), $"{lead}\n\n{closing}\n\n{link}\n");
    }

    private static string Html(string heading, string lead, string closing, string link, string button)
    {
        static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
        return $"""
            <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#17223a">
              <div style="padding:18px 22px;background:#193f82;color:#ffffff"><h1 style="margin:0;font-size:20px">{E(heading)}</h1></div>
              <div style="padding:22px;border:1px solid #dce4ef;border-top:0">
                <p style="margin:0 0 14px">{E(lead)}</p>
                <p style="margin:0 0 18px">{E(closing)}</p>
                <p style="margin:0"><a href="{E(link)}" style="display:inline-block;padding:10px 16px;background:#193f82;color:#ffffff;text-decoration:none;border-radius:6px">{E(button)}</a></p>
              </div>
            </div>
            """;
    }
}
