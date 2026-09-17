using System;
using System.IO;
using System.Text.RegularExpressions;
using IPRO.Utility;
using Xunit;

namespace IPRO.IntegrationTests;

// 492 (2026-09-17). The owner asked whether anything from the auditors' reviews had been left
// behind. The full reconciliation (DOCS/AUDIT_RECONCILIATION_2026-09-17.md) found every Critical and
// High closed and a tail of Lows that the August waves never circled back to. These are the ones
// small enough to close before launch, plus the owner's three small items, in one batch. Each pin
// names the finding it closes so the reconciliation can be re-run against this file.
public class PreLaunchAuditRemainderTests
{
    // ---- security ---------------------------------------------------------------------------

    [Fact]
    public void M6_residual_the_footer_editor_script_carries_the_csp_nonce()
    {
        // The legal-link picker was silently dead in every CSP-enforcing browser.
        var src = Read(@"src\IPRO.Web\Views\WebsitePages\Footer.cshtml");
        Assert.DoesNotMatch(new Regex(@"<script>\s"), src);
        Assert.Contains("<script nonce=\"@Context.GetCspNonce()\">", src);
    }

    [Theory]
    [InlineData(@"src\IPRO.Web\Views\Shared\_RichEditor.cshtml")]
    [InlineData(@"src\IPRO.Admin\Views\Shared\_RichEditor.cshtml")]
    public void L11_the_rich_editor_sanitises_stored_html_before_rendering_it(string file)
    {
        var src = Read(file);
        Assert.DoesNotContain("@Html.Raw(Model ?? string.Empty)", src);
        Assert.Contains("HtmlContentSanitizer.Sanitize(Model", src);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("[2001:4860:4860::8888]")]
    [InlineData("127.0.0.1")]
    public void L1_any_ip_literal_is_refused_as_a_custom_domain(string host)
    {
        // The guard's own comment promised this; the code only refused private literals.
        Assert.True(PublicHostGuard.IsBlockedHost(host));
    }

    [Fact]
    public void L1_a_real_hostname_is_still_allowed()
    {
        Assert.False(PublicHostGuard.IsBlockedHost("www.example-adviser.com"));
    }

    [Theory]
    [InlineData("user@example.com", "www.example.com")]
    [InlineData("https://someone@www.example.com/path", "www.example.com")]
    public void L1_normalise_domain_drops_a_userinfo_prefix(string typed, string expected)
    {
        Assert.Equal(expected, IPRO.Web.Controllers.WebsiteController.NormalizeDomain(typed));
    }

    [Fact]
    public void WEB_L2_the_lead_magnet_token_is_bound_to_the_agent_and_the_download_checks_it()
    {
        var src = Read(@"src\IPRO.Web\Controllers\PublicWebsiteController.cs");
        Assert.Contains("Protect($\"{agentDocumentId}|{website.AgentUserId}|{expiresAt}\")", src);
        Assert.Contains("d.Id == documentId && d.AgentUserId == agentUserId", src);
        Assert.DoesNotContain("FirstOrDefaultAsync(d => d.Id == documentId)", src);
    }

    // ---- correctness ------------------------------------------------------------------------

    [Fact]
    public void LB1_sibling_newsletter_test_sends_use_the_canonical_base_url()
    {
        var src = Read(@"src\IPRO.Web\Controllers\NewsletterController.cs");
        Assert.DoesNotContain("GetRequestBaseUrl", src);
        Assert.Contains("NewsletterHtmlComposer.Wrap(nl, agent, IPRO.Utility.WebAppUrlHelper.GetWebAppBaseUrl(_configuration)", src);
    }

    [Fact]
    public void L8_drip_consent_is_checked_against_a_fresh_read_of_the_client()
    {
        var src = Read(@"src\IPRO.Scheduler\DripCampaignJob.cs");
        Assert.Contains("_db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == enrollment.ClientId)", src);
        Assert.Contains("IsSuppressed(clientNow, EmailChannel.DripCampaign)", src);
        Assert.DoesNotContain("IsSuppressed(enrollment.Client, EmailChannel.DripCampaign)", src);
    }

    [Fact]
    public void BILLING12_an_agent_cannot_write_their_own_promotion_code()
    {
        var controller = Read(@"src\IPRO.Web\Controllers\AccountController.cs");
        Assert.DoesNotContain("agent.PromotionCode = model.PromotionCode", controller);
        var view = Read(@"src\IPRO.Web\Views\Account\Profile.cshtml");
        Assert.Matches(new Regex(@"asp-for=""PromotionCode""[^>]*readonly"), view);
    }

    [Theory]
    [InlineData(@"src\IPRO.Email\PollDispatcher.cs")]
    [InlineData(@"src\IPRO.Scheduler\TrialReminderJob.cs")]
    public void SO_MIN7_no_file_carries_its_own_copy_of_the_base_url(string file)
    {
        var src = Read(file);
        Assert.DoesNotContain("ipro-prod-web.azurewebsites.net", src);
        Assert.Contains("WebAppUrlHelper.GetWebAppBaseUrl(_configuration)", src);
    }

    // ---- operations ---------------------------------------------------------------------------

    [Fact]
    public void L5_domain_removal_honours_the_automation_switch()
    {
        var src = Read(@"src\IPRO.Utility\AzureDomainAutomationService.cs");
        var remove = src.IndexOf("public async Task<AzureDomainAutomationResult> RemoveDomainAsync(", StringComparison.Ordinal);
        Assert.True(remove > 0);
        var body = src[remove..];
        var enabledCheck = body.IndexOf("if (!_options.Enabled)", StringComparison.Ordinal);
        var firstHttp = body.IndexOf("ManagementUri(", StringComparison.Ordinal);
        Assert.True(enabledCheck > 0 && enabledCheck < firstHttp, "RemoveDomainAsync must refuse before touching Azure when automation is disabled");
    }

    [Fact]
    public void Hangfire_deletes_a_job_once_its_retries_are_exhausted()
    {
        // Owner's item: exhausted jobs stayed on the dashboard until deleted by hand.
        var src = Read(@"src\IPRO.Web\Program.cs");
        Assert.Contains("OnAttemptsExceeded = AttemptsExceededAction.Delete", src);
    }

    // ---- product ------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"src\IPRO.Admin\Views\Agents\Details.cshtml")]
    [InlineData(@"src\IPRO.Admin\Views\Agents\Index.cshtml")]
    public void ADMIN11_the_reset_confirmation_describes_what_the_action_does(string file)
    {
        var src = Read(file);
        Assert.DoesNotContain("to their last name", src);
        Assert.Contains("random temporary password", src);
    }

    [Fact]
    public void ADMIN12_tax_rate_edits_are_logged_with_before_and_after()
    {
        var src = Read(@"src\IPRO.Admin\Controllers\TaxRatesController.cs");
        Assert.DoesNotContain("$\"Bulk-updated {model.Rates.Count} province tax rate(s)\"", src);
        Assert.Contains("changes", src);
        Assert.Contains("->", src);
    }

    [Fact]
    public void The_client_edit_form_explains_an_ignored_newsletter_tick()
    {
        // Owner's item: the switch silently does nothing for a client who unsubscribed from everything.
        var src = Read(@"src\IPRO.Web\Views\Clients\Edit.cshtml");
        Assert.Contains("Model.EmailOptOutAt.HasValue", src);
        Assert.Contains("unsubscribed from all email", src);
    }

    [Fact]
    public void The_bare_admin_address_lands_on_the_dashboard_or_the_sign_in_page()
    {
        // Owner's item: /Admin/ answered 404 on the admin host. AdminController now has an Index that
        // sends the visitor to the root; the cookie's LoginPath takes it from there when signed out.
        var src = Read(@"src\IPRO.Admin\Controllers\AdminController.cs");
        Assert.Matches(new Regex(@"public IActionResult Index\(\)\s*=>\s*Redirect\(""/""\)"), src);
    }

    // ---- harness ------------------------------------------------------------------------------

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative));
    }
}
