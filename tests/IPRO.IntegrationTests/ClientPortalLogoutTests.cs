using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 455 (2026-09-02). Logout in the client portal answered 400 Bad Request, in production, on
// the owner's first click. Mechanism: the logout form is rendered on a page whose controller
// carries [Authorize(AuthenticationSchemes = "ClientPortal")], so the antiforgery token in it is
// bound to the CLIENT's identity. ClientPortalAccountController is [AllowAnonymous] and Logout had
// no Authorize of its own, so when the POST arrived the authorization middleware never installed
// the client principal; HttpContext.User was the DEFAULT scheme's principal -- the agent's cookie
// when the owner was also signed in as an agent, otherwise anonymous. The antiforgery filter then
// compared the token's claims to a different user and refused with 400. The action must
// authenticate against the ClientPortal scheme so the token is validated against the identity that
// generated it. There is no in-process HTTP harness here, so the guarantee is pinned in source.
public class ClientPortalLogoutTests
{
    [Fact]
    public void Logout_authenticates_against_the_client_portal_scheme_before_the_antiforgery_check()
    {
        var src = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\ClientPortalAccountController.cs"));
        var logout = Regex.Match(src, @"((?:\s*\[[^\]]+\]\s*)+)public\s+async\s+Task<IActionResult>\s+Logout\(\)");
        Assert.True(logout.Success, "Logout action not found");
        var attributes = logout.Groups[1].Value;
        Assert.Contains("ValidateAntiForgeryToken", attributes);
        Assert.Contains("AuthenticationSchemes = \"ClientPortal\"", attributes);
    }

    [Fact]
    public void The_logout_form_still_carries_the_token_and_posts_to_the_account_controller()
    {
        var layout = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Shared\_ClientPortalLayout.cshtml"));
        var form = Regex.Match(layout, @"<form[^>]*asp-controller=""ClientPortalAccount""[^>]*asp-action=""Logout""[^>]*method=""post""[^>]*>(.*?)</form>", RegexOptions.Singleline);
        Assert.True(form.Success, "the client portal layout must post Logout to ClientPortalAccount");
        Assert.Contains("AntiForgeryToken()", form.Groups[1].Value);
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
