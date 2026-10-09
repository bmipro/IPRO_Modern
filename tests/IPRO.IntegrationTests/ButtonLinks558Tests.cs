using System;
using System.IO;
using IPRO.Web.Infrastructure;
using Xunit;

namespace IPRO.IntegrationTests;

// 558 (2026-10-09). The owner, on the bakery's home page: "when I change it to tel:(the) number and
// click on save block. It does not get saved", then "all the give us a calls even in the menu/submenu
// have changed". The bakery writes its number "(416)-886-0458"; the rule for a tel: link wanted a
// digit straight after "tel:", so "tel:(416)-886-0458" was dropped without a word, the page said
// "Content block saved.", and each button fell back to /contact.
public class ButtonLinks558Tests
{
    private static string Web(string? value) =>
        value != null && (value.StartsWith("https://") || value.StartsWith("http://")) ? value : string.Empty;

    [Theory]
    [InlineData("tel:(416)-886-0458", "tel:(416)-886-0458")]          // the bakery's own way of writing it
    [InlineData("tel:+1 (416) 886-0458", "tel:+1 (416) 886-0458")]
    [InlineData("TEL:416-886-0458", "TEL:416-886-0458")]
    [InlineData("  (416)-886-0458 ", "tel:(416)-886-0458")]           // a number typed on its own
    [InlineData("+1 416 886 0458", "tel:+1 416 886 0458")]
    [InlineData("info@lavenuebakery.example", "mailto:info@lavenuebakery.example")]
    [InlineData("mailto:info@lavenuebakery.example", "mailto:info@lavenuebakery.example")]
    [InlineData("/contact", "/contact")]
    [InlineData("https://order.example.test/", "https://order.example.test/")]
    [InlineData("", "")]
    public void A_link_a_button_may_carry_is_kept(string typed, string kept) =>
        Assert.Equal(kept, ButtonLinks.Normalize(typed, Web));

    [Theory]
    [InlineData("tel:javascript:alert(1)")]
    [InlineData("javascript:alert(1)")]
    [InlineData("tel:(the) number")]
    [InlineData("tel:")]
    [InlineData("2026")]                         // too few digits to be a number to call
    [InlineData("mailto:<script>@x.y")]
    [InlineData("a@b")]
    [InlineData("call us")]
    public void Anything_else_is_still_dropped(string typed) =>
        Assert.Equal(string.Empty, ButtonLinks.Normalize(typed, Web));

    [Fact]
    public void A_link_that_is_not_kept_is_said_so()
    {
        var message = ButtonLinks.NotKept(" tel:(the) number ");
        Assert.StartsWith("Saved, but the button link \"tel:(the) number\" was not kept", message);
        Assert.Contains("tel:416-555-0199", message);

        var controller = Read(@"src\IPRO.Web\Controllers\WebsitePagesController.cs");
        Assert.Contains("if (!string.IsNullOrWhiteSpace(buttonUrl) && string.IsNullOrEmpty(block.ButtonUrl))", controller);
        Assert.Contains("TempData[\"Error\"] = IPRO.Web.Infrastructure.ButtonLinks.NotKept(buttonUrl);", controller);
        // One rule for every button a block or the header carries.
        Assert.Contains("private static string NormalizeLink(string? value) => IPRO.Web.Infrastructure.ButtonLinks.Normalize(value, NormalizeUrl);", controller);
        Assert.DoesNotContain("Regex PhoneLink", controller);
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative)).Replace("\r\n", "\n");
    }
}
