using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace IPRO.IntegrationTests;

// 617: a second press of Sign in posted the same form after the first had signed the person in. That form's
// token belongs to nobody, the signed-in person is somebody, and the page said 400. The button now says it is
// working and cannot be pressed again, and someone already signed in does not get the sign-in page at all.
public class SignInOnePress617Tests
{
    [Theory]
    [InlineData(@"src\IPRO.Admin\Views\Admin\Login.cshtml", "Signing in...")]
    [InlineData(@"src\IPRO.Web\Views\Account\Login.cshtml", "Signing in...")]
    [InlineData(@"src\IPRO.Web\Views\TwoStep\Verify.cshtml", "Checking...")]
    public void The_button_says_it_is_working_and_cannot_be_pressed_again(string view, string words)
    {
        var page = Lock583.Read(view);
        Assert.Contains($"data-pressed=\"{words}\"", page);
        Assert.Contains("<script nonce=\"@Context.GetCspNonce()\">", page);   // the sites' script rule refuses one without it
        Assert.Contains("button.disabled = true;", page);
        // The browser's Back button brings the page back as it was left: it works again there.
        Assert.Contains("window.addEventListener('pageshow'", page);
    }

    [Fact]
    public async Task The_portals_sign_in_page_shows_the_button_with_its_words()
    {
        var page = await TwoStep593.RenderWebAsync("~/Views/Account/Login.cshtml", (object?)null);
        Assert.Contains("data-pressed=\"Signing in...\">Sign In</button>", page);
    }

    [Fact]
    public async Task Someone_already_signed_in_goes_to_the_dashboard_and_not_to_the_sign_in_page()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await Lock583.SeedAgentAsync(testDb, "weekly", "weekly@example.com");
        var browser = new TwoStepBrowser(testDb);
        Assert.IsType<ViewResult>(await browser.AccountAsync(c => Task.FromResult(c.Login())));

        await browser.SignInWithPasswordAsync("weekly");
        var there = Assert.IsType<RedirectResult>(await browser.AccountAsync(c => Task.FromResult(c.Login())));
        Assert.Equal("/portal/Dashboard", there.Url);
    }

    [Fact]
    public void SuperAdmin_does_the_same()
    {
        Assert.Contains("[HttpGet] public IActionResult Login() => User?.Identity?.IsAuthenticated == true ? Redirect(\"/\") : View();",
            Lock583.Read(@"src\IPRO.Admin\Controllers\AdminController.cs"));
    }
}
