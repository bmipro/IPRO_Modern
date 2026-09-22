using System;
using System.IO;
using Xunit;

namespace IPRO.IntegrationTests;

// 513 (2026-09-22). /favicon.ico answered 404 on every public name and on the platform itself
// (polish item 12): every real browser visit produced one 404, and the tab showed a blank icon. The
// owner supplied the icon. It lives at /images/favicon.ico on the web app, NOT at /favicon.ico: the
// static-file middleware serves wwwroot for every host, and a file at the default path would put
// the platform's icon on every ADVISER's site, whose templates set no icon of their own (a later
// feature: the adviser's own). So the platform's own pages name the icon explicitly, and advisers'
// sites are untouched. SuperAdmin, which hosts nobody's site, uses the default path.
public class Favicon513Tests
{
    private static readonly byte[] IcoMagic = { 0x00, 0x00, 0x01, 0x00 };

    [Theory]
    [InlineData(@"src\IPRO.Web\wwwroot\images\favicon.ico")]
    [InlineData(@"src\IPRO.Admin\wwwroot\favicon.ico")]
    public void The_icon_files_are_real_icons(string path)
    {
        var bytes = File.ReadAllBytes(FindRepoFile(path));

        Assert.Equal(IcoMagic, bytes[..4]);
        Assert.InRange(bytes.Length, 500, 100_000);
    }

    [Fact]
    public void The_web_app_has_no_icon_at_the_default_path_so_advisers_sites_keep_their_own()
    {
        Assert.False(File.Exists(FindRepoFile(@"src\IPRO.Web\wwwroot\favicon.ico")));
    }

    [Theory]
    [InlineData(@"src\IPRO.Web\Views\Home\Index.cshtml")]
    [InlineData(@"src\IPRO.Web\Views\Home\Accountants.cshtml")]
    [InlineData(@"src\IPRO.Web\Views\Home\Mortgage.cshtml")]
    [InlineData(@"src\IPRO.Web\Views\Shared\_Layout.cshtml")]
    [InlineData(@"src\IPRO.Web\Views\Shared\_LegalLayout.cshtml")]
    [InlineData(@"src\IPRO.Web\Views\Account\Login.cshtml")]
    [InlineData(@"src\IPRO.Web\Views\Account\Register.cshtml")]
    [InlineData(@"src\IPRO.Web\Views\Account\ForgotPassword.cshtml")]
    [InlineData(@"src\IPRO.Web\Views\Account\ForgotPasswordConfirmation.cshtml")]
    [InlineData(@"src\IPRO.Web\Views\Account\ResetPassword.cshtml")]
    [InlineData(@"src\IPRO.Web\Views\Account\ChangePassword.cshtml")]
    public void Every_platform_page_names_the_icon(string view)
    {
        var source = File.ReadAllText(FindRepoFile(view));

        Assert.Contains("<link rel=\"icon\" href=\"/images/favicon.ico\" type=\"image/x-icon\">", source);
        Assert.DoesNotContain("href=\"/favicon.ico\"", source);
    }

    [Fact]
    public void SuperAdmin_names_its_icon()
    {
        Assert.Contains("<link rel=\"icon\" href=\"/favicon.ico\" type=\"image/x-icon\">", File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Views\Shared\_Layout.cshtml")));
    }

    [Fact]
    public void Control_advisers_site_templates_are_left_without_an_icon_link()
    {
        foreach (var template in new[] { "_ClassicSidebar.cshtml", "_EditorialVisual.cshtml", "_ModernProfessional.cshtml" })
            Assert.DoesNotContain("favicon", File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\PublicWebsite\" + template)));
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
