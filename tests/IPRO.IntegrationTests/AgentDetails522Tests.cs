using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace IPRO.IntegrationTests;

// 522 (2026-09-26): two things the SuperAdmin agent page got wrong on the first real customer.
// (1) The profile card printed "Package 3" -- 518 fixed the Agents list and left the Details
// page. (2) "Last Login: Never" for a customer who had been in the portal: Signup v2 signs the
// new adviser in and goes straight to checkout, but only the Login page wrote LastLoginAt.
public class AgentDetails522Tests
{
    [Fact]
    public void The_agent_page_names_the_package()
    {
        var view = Read(@"src\IPRO.Admin\Views\Agents\Details.cshtml");
        Assert.Contains("packageLookup.TryGetValue(Model.PackageId", view);
        Assert.DoesNotContain(">Package @Model.PackageId<", view);
    }

    [Fact]
    public void A_sign_up_is_the_first_login()
    {
        var source = Read(@"src\IPRO.Web\Controllers\AccountController.cs").Replace("\r\n", "\n");
        // The registration's sign-in (Signup v2: sign them in and go straight to payment) is
        // followed by the same last-login write the Login page makes.
        Assert.Matches(
            new Regex(@"await SignInAgentAsync\(agent, new AuthenticationProperties \{ IsPersistent = false \}\);\s*(//[^\n]*\n\s*)*await _agents\.UpdateLastLoginAsync\(agent\.Id\);"),
            source);
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
