using System;
using System.Globalization;
using System.IO;
using IPRO.Admin.Controllers;
using IPRO.DataAccess;
using IPRO.Entities;
using Xunit;

namespace IPRO.IntegrationTests;

// 518 (2026-09-23). Nine items of TODO 504's launch-weekend polish list, done at the owner's word
// ("lets do the 505 and 504"), plus the print layout of the invoices: a four-line client invoice
// spilled onto a second page, and he wants six to eight lines on one. Every defect test observed
// RED on the pre-fix code (the helpers existed with the old behaviour for the red run).
public class Polish518Tests
{
    private const string Eastern = "(GMT-05:00) Eastern Time (US & Canada)";

    // ---- (1) a meeting request is called one --------------------------------------------------------

    [Theory]
    [InlineData("Contact", "/request-meeting", null, "Meeting request")]
    [InlineData("CustomForm", "/contact", "Request Meeting", "Meeting request")]
    [InlineData("Contact", "/contact", "Contact Us", "Contact request")]
    [InlineData("CustomForm", "/forms/intake", "Intake", "Form submission")]
    [InlineData("Newsletter", "/", null, "Newsletter signup")]
    [InlineData("LeadMagnet", "/guide", null, "Lead magnet download")]
    [InlineData("DidYouKnow", "/", null, "Contact request")]
    public void A_lead_is_named_by_what_the_visitor_did(string type, string page, string? title, string expected) =>
        Assert.Equal(expected, WebsiteLeadText.KindLabel(type, page, title));

    [Fact]
    public void The_leads_list_the_export_and_the_dashboard_use_that_name()
    {
        Assert.Contains("WebsiteLeadText.KindLabel(", Read(@"src\IPRO.Web\Views\WebsiteLeads\Index.cshtml"));
        Assert.Contains("WebsiteLeadText.KindLabel(", Read(@"src\IPRO.Web\Controllers\WebsiteLeadsController.cs"));
        Assert.Contains("WebsiteLeadText.KindLabel(", Read(@"src\IPRO.Web\Views\Dashboard\Index.cshtml"));
    }

    // ---- (2) the package's name, not "Package 3" ----------------------------------------------------

    [Fact]
    public void The_agents_list_names_the_package()
    {
        Assert.Contains("ViewBag.PackageNames", Read(@"src\IPRO.Admin\Controllers\AgentsController.cs"));
        var view = Read(@"src\IPRO.Admin\Views\Agents\Index.cshtml");
        Assert.Contains("packageNames.TryGetValue(a.PackageId", view);
        Assert.DoesNotContain(">Package @a.PackageId<", view);
    }

    // ---- (3)(4)(5) the Billing page says one thing at a time ------------------------------------------

    [Fact]
    public void The_billing_page_shows_one_truth_at_a_time()
    {
        var view = Read(@"src\IPRO.Web\Views\Billing\Index.cshtml");
        Assert.Contains("else if (paidThrough != null && subscription == null)", view);   // (4) the cancelled banner only without an active plan
        Assert.Contains("else if (subscription == null && paidThrough == null)", view);   // (3) not under the paid-through banner
        Assert.Contains("Complimentary plan &mdash; nothing is billed.", view);            // (5) a comped plan has no next billing
        Assert.Contains("string.IsNullOrWhiteSpace(subscription.PayPalSubscriptionId) && nextChargeAmount == 0", view);
    }

    // ---- (6) agent details times in the platform's zone -----------------------------------------------

    [Fact]
    public void Agent_details_times_are_the_platforms_own()
    {
        Assert.Contains("ViewBag.Zone = AdminClock.Zone(", Read(@"src\IPRO.Admin\Controllers\AgentsController.cs"));
        var view = Read(@"src\IPRO.Admin\Views\Agents\Details.cshtml");
        Assert.Contains("AdminClock.Format(utc, zone)", view);
        Assert.Contains("LocalTime(Model.LastLoginAt.Value)", view);
        Assert.Contains("LocalTime(change.CreatedAt)", view);
        Assert.DoesNotContain(".ToLocalTime()", view);                 // the server's "local" is UTC
        Assert.DoesNotContain("ToString(\"MMM d h:mm tt\")", view);
        Assert.DoesNotContain("ToString(\"MMM d, yyyy h:mm tt\")", view);
    }

    // ---- (7) the webhook hint ---------------------------------------------------------------------------

    [Theory]
    [InlineData("https://app.iproadvisers.com/Billing/PayPalReturn", "", null, "https://app.iproadvisers.com/Billing/Webhook")]
    [InlineData("", "https://app.iproadvisers.com/Billing/Cancel", null, "https://app.iproadvisers.com/Billing/Webhook")]
    [InlineData("", "", "https://app.iproadvisers.com", "https://app.iproadvisers.com/Billing/Webhook")]
    [InlineData("", "", "https://app.iproadvisers.com/", "https://app.iproadvisers.com/Billing/Webhook")]
    [InlineData("", "", "YOUR_PORTAL_URL", "https://ipro-prod-web.azurewebsites.net/Billing/Webhook")]   // an unset placeholder: the last resort stays
    [InlineData("", "", null, "https://ipro-prod-web.azurewebsites.net/Billing/Webhook")]
    public void The_webhook_hint_names_the_public_address(string returnUrl, string cancelUrl, string? portal, string expected) =>
        Assert.Equal(expected, PayPalSetupController.BuildWebhookUrl(returnUrl, cancelUrl, portal));

    // ---- (9) whose day it is ----------------------------------------------------------------------------

    [Fact]
    public void Today_is_the_persons_day_not_the_servers()
    {
        var lateEvening = new DateTime(2026, 9, 23, 0, 42, 0, DateTimeKind.Utc);   // 8:42 p.m. on the 22nd in Toronto

        Assert.Equal(new DateTime(2026, 9, 22), PlatformDay.Today(lateEvening, Eastern));
        Assert.Equal(new DateTime(2026, 9, 22), PlatformDay.Today(lateEvening, "(GMT-08:00) Pacific Time (US & Canada)"));
        Assert.Equal(new DateTime(2026, 9, 22), PlatformDay.Today(lateEvening, null));   // Eastern when unset
    }

    [Fact]
    public void The_follow_up_pages_and_the_dashboard_ask_for_the_agents_day()
    {
        foreach (var file in new[]
                 {
                     @"src\IPRO.Web\Controllers\DashboardController.cs", @"src\IPRO.Web\Controllers\ClientsController.cs",
                     @"src\IPRO.Web\Views\Dashboard\Index.cshtml", @"src\IPRO.Web\Views\Clients\FollowUps.cshtml",
                     @"src\IPRO.Web\Views\Clients\FollowUpQueue.cshtml", @"src\IPRO.Web\Views\Clients\Details.cshtml",
                     @"src\IPRO.Web\Views\Clients\Calendar.cshtml"
                 })
            Assert.DoesNotContain("DateTime.Today", Read(file));
        Assert.Contains("ViewBag.Today = today;", Read(@"src\IPRO.Web\Controllers\ClientsController.cs"));
        Assert.Contains("var today = AgentTimeZoneHelper.FromUtc(DateTime.UtcNow, agentTimeZone).Date;", Read(@"src\IPRO.Web\Controllers\DashboardController.cs"));
    }

    // ---- (13) a code works through the whole of its last day ----------------------------------------------

    [Theory]
    [InlineData("2026-10-01T03:00:00Z", false)]   // 11 p.m. on the 30th in Toronto: still the last day
    [InlineData("2026-10-01T04:30:00Z", true)]    // half past midnight on 1 October: over
    [InlineData("2026-09-30T01:00:00Z", false)]   // 9 p.m. on the 29th: the old rule already refused here
    [InlineData("2026-09-15T12:00:00Z", false)]
    public void A_code_works_through_the_whole_of_its_last_day(string nowUtc, bool expired)
    {
        var now = DateTime.Parse(nowUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

        Assert.Equal(expired, PlatformDay.HasExpired(new DateTime(2026, 9, 30), now, Eastern));
        Assert.False(PlatformDay.HasExpired(null, now, Eastern));   // no expiry: never
    }

    [Fact]
    public void Both_code_checks_use_that_rule()
    {
        Assert.Contains("PlatformDay.HasExpired(promo.ExpiresAt, DateTime.UtcNow, _configuration[\"Admin:TimeZone\"])", Read(@"src\IPRO.Billing\PayPalBillingService.cs"));
        Assert.Contains("PlatformDay.HasExpired(invite.ExpiresAt, DateTime.UtcNow, _configuration[\"Admin:TimeZone\"])", Read(@"src\IPRO.Web\Controllers\AccountController.cs"));
    }

    // ---- the print layout ---------------------------------------------------------------------------------

    [Fact]
    public void The_printed_invoice_keeps_eight_lines_on_one_page()
    {
        var css = Read(@"src\IPRO.Web\wwwroot\css\invoice.css");
        var print = css[css.IndexOf("@media print", StringComparison.Ordinal)..];
        Assert.Contains("/* 518 print: eight lines on one Letter page.", print);
        Assert.Contains("min-height: 0;", print);                     // the page is not forced to a full sheet
        Assert.Contains("padding: 0.35in 0.5in;", print);
        Assert.Contains("td {\n    padding: 6px 8px;", print.Replace("\r\n", "\n"));
        Assert.Contains("break-inside: avoid;", print);
        Assert.DoesNotContain("min-height: 11in;", print);
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative));
    }
}
