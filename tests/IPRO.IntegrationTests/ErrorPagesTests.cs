using System;
using System.IO;
using System.Text.RegularExpressions;
using IPRO.Utility;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 456 (2026-09-02). Only unhandled exceptions had a page of ours; every other status (the
// client-portal logout's 400, any 404) returned an empty body and the browser showed its own
// page. Now: one branded page per status, keeping the real status code, only for requests that
// want HTML and are not machine endpoints (webhooks, health), with a way back that depends on
// where the visitor was. Platform-level first; agent-site-themed pages are a later item (458).
public class ErrorPagesTests
{
    // ---- who gets an HTML page ------------------------------------------------------------------

    [Theory]
    [InlineData("/portal/Clients/Details/5", "text/html,application/xhtml+xml,*/*;q=0.8", true)]
    [InlineData("/ClientPortal/Messages", "text/html", true)]
    [InlineData("/no-such-page", "text/html", true)]
    [InlineData("/portal/Clients", "application/json", false)]          // an API-style caller
    [InlineData("/portal/Clients", "*/*", false)]                        // no explicit HTML wish
    [InlineData("/portal/Clients", "", false)]
    [InlineData("/AzureEmailEvents", "text/html", false)]                // Event Grid
    [InlineData("/Newsletter/SendGridEvents", "text/html", false)]       // SendGrid (rollback path)
    [InlineData("/billing/webhook", "text/html", false)]                 // PayPal
    [InlineData("/health", "text/html", false)]
    [InlineData("/health/version", "text/html", false)]
    [InlineData("/hangfire/jobs", "text/html", false)]                   // has its own UI
    [InlineData("/error/404", "text/html", false)]                       // never re-enter ourselves
    public void Only_browsers_on_page_paths_get_the_html_page(string path, string accept, bool expected)
    {
        Assert.Equal(expected, StatusPagePolicy.ShouldRender(path, accept));
    }

    // ---- the way back depends on where the visitor was ----------------------------------------

    [Theory]
    [InlineData("/ClientPortal/Messages", "/ClientPortal", "client portal")]
    [InlineData("/clientportalaccount/login", "/ClientPortal", "client portal")]
    [InlineData("/portal/Clients/Details/5", "/portal", "your portal")]
    [InlineData("/Portal", "/portal", "your portal")]
    [InlineData("/Account/Login", "/", "home page")]
    [InlineData("/some-public-page", "/", "home page")]
    [InlineData("", "/", "home page")]
    public void The_back_link_returns_the_visitor_to_the_area_they_were_in(string path, string href, string labelFragment)
    {
        var back = StatusPagePolicy.BackLink(path);
        Assert.Equal(href, back.Href);
        Assert.Contains(labelFragment, back.Label, StringComparison.OrdinalIgnoreCase);
    }

    // ---- every code has words, and unknown codes are not lied about ---------------------------

    [Theory]
    [InlineData(400, "could not be processed")]
    [InlineData(403, "access")]
    [InlineData(404, "not found")]
    [InlineData(405, "not available")]
    [InlineData(500, "went wrong")]
    public void Each_status_has_a_plain_english_title(int code, string fragment)
    {
        var text = StatusPagePolicy.Describe(code);
        Assert.Contains(fragment, text.Title, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(text.Message));
    }

    [Fact]
    public void An_unknown_status_shows_its_number_rather_than_a_made_up_reason()
    {
        var text = StatusPagePolicy.Describe(418);
        Assert.Contains("418", text.Title + text.Message);
    }

    // ---- the controller keeps the real status code ---------------------------------------------

    [Fact]
    public void The_web_error_controller_keeps_the_status_code_and_renders_the_page()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/error/404";
        var controller = new IPRO.Web.Controllers.ErrorController
        {
            ControllerContext = new ControllerContext { HttpContext = ctx }
        };

        var result = Assert.IsType<ViewResult>(controller.Status(404));

        Assert.Equal(404, ctx.Response.StatusCode);
        var model = Assert.IsType<StatusPageModel>(result.Model);
        Assert.Equal(404, model.Code);
        Assert.Contains("not found", model.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/", model.BackHref);
    }

    [Fact]
    public void The_admin_error_controller_keeps_the_status_code_and_points_home()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/error/403";
        var controller = new IPRO.Admin.Controllers.ErrorController
        {
            ControllerContext = new ControllerContext { HttpContext = ctx }
        };

        var result = Assert.IsType<ViewResult>(controller.Status(403));

        Assert.Equal(403, ctx.Response.StatusCode);
        var model = Assert.IsType<StatusPageModel>(result.Model);
        Assert.Equal(403, model.Code);
        Assert.Equal("/", model.BackHref);
    }

    // ---- wiring pins ---------------------------------------------------------------------------

    [Fact]
    public void Both_apps_render_status_pages_only_through_the_policy()
    {
        foreach (var app in new[] { @"src\IPRO.Web\Program.cs", @"src\IPRO.Admin\Program.cs" })
        {
            var src = File.ReadAllText(FindRepoFile(app));
            Assert.Contains("UseStatusCodePagesWithReExecute(\"/error/{0}\")", src);
            Assert.Contains("StatusPagePolicy.ShouldRender(", src);
            // The status-code middleware must sit before routing, or the re-executed request never
            // reaches the ErrorController.
            Assert.True(src.IndexOf("UseStatusCodePagesWithReExecute", StringComparison.Ordinal) < src.IndexOf("app.UseRouting()", StringComparison.Ordinal),
                $"{app}: status pages must be registered before UseRouting");
        }
    }

    [Fact]
    public void The_error_path_is_never_shadowed_by_an_agents_public_website()
    {
        // On an agent host a bare path is the public site; /error/404 must reach ErrorController.
        var src = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Program.cs"));
        var m = Regex.Match(src, @"static bool IsNeverShadowedPrefix\(string segment\)(.*?)\n\};", RegexOptions.Singleline);
        Assert.True(m.Success, "IsNeverShadowedPrefix not found");
        Assert.Contains("\"error\"", m.Groups[1].Value);
    }

    [Fact]
    public void The_pages_are_branded_and_offer_a_way_back()
    {
        var web = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Error\Status.cshtml"));
        Assert.Contains("IPRO Advisers", web);
        Assert.Contains("Model.BackHref", web);
        Assert.Contains("Model.Title", web);
        Assert.Contains("Model.Code", web);

        var admin = File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Views\Error\Status.cshtml"));
        Assert.Contains("IPRO", admin);
        Assert.Contains("Model.BackHref", admin);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }
}
