using System;
using System.IO;
using IPRO.Email;
using Xunit;

namespace IPRO.IntegrationTests;

// 511 (2026-09-21). The owner's rule for a brand domain in anything a person READS -- the capitals
// separate the words, the www. stays: www.iProAdvisers.com -- extended the same afternoon to email
// addresses ("yes apply the capitals to the email addresses too"): billing@iProAdvisers.com on the
// invoice, support@ on the home page, privacy@ on the legal pages. DISPLAY only. What a machine reads
// keeps the configured value exactly: the From and Reply-To the mail provider checks (it matches the
// sender against its own list), every mailto: link, every setting. Every defect test observed RED on
// the pre-fix code.
public class BrandCapitals511Tests
{
    [Theory]
    [InlineData("billing@iproadvisers.com", "billing@iProAdvisers.com")]
    [InlineData("support@IPROADVISERS.COM", "support@iProAdvisers.com")]
    [InlineData("training@IProAdvisers.com", "training@iProAdvisers.com")]
    [InlineData("www.iproaccountants.com", "www.iProAccountants.com")]
    [InlineData("ipromortgages.com", "iProMortgages.com")]
    [InlineData("Write to privacy@iproadvisers.com or see www.iproadvisers.com.", "Write to privacy@iProAdvisers.com or see www.iProAdvisers.com.")]
    public void A_brand_domain_is_written_with_its_capitals_wherever_it_sits_in_the_text(string input, string expected)
    {
        Assert.Equal(expected, BrandText.WithCapitals(input));
    }

    [Theory]
    [InlineData("michaeltran@alladvisers.com")]
    [InlineData("someone@gmail.com")]
    [InlineData("www.iProAdvisers.com")]
    [InlineData("bahmanmotamed.247advisers.com")]
    [InlineData("notiproadvisers.community")]
    [InlineData("")]
    public void Control_anything_else_is_left_exactly_as_it_was(string input)
    {
        Assert.Equal(input, BrandText.WithCapitals(input));
    }

    [Fact]
    public void Control_nothing_is_not_an_error()
    {
        Assert.Equal(string.Empty, BrandText.WithCapitals(null));
    }

    // ---- the places a person reads an address ---------------------------------------------------

    [Fact]
    public void The_invoice_page_and_the_invoice_email_show_the_companys_address_and_site_with_capitals()
    {
        var page = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Billing\Invoice.cshtml"));
        Assert.Contains("@IPRO.Email.BrandText.WithCapitals(companyEmail)", page);
        Assert.Contains("@IPRO.Email.BrandText.WithCapitals(companyWebsite)", page);

        var service = File.ReadAllText(FindRepoFile(@"src\IPRO.Billing\PayPalBillingService.cs"));
        Assert.Contains("WebUtility.HtmlEncode(BrandText.WithCapitals(companyEmail))", service);
        Assert.Contains("WebUtility.HtmlEncode(BrandText.WithCapitals(companyWebsite))", service);
    }

    [Theory]
    [InlineData(@"src\IPRO.Web\Views\Shared\_LegalPrivacy.cshtml", "privacyEmail", 2)]
    [InlineData(@"src\IPRO.Web\Views\Shared\_LegalTerms.cshtml", "supportEmail", 1)]
    public void The_legal_pages_show_the_address_with_capitals_and_link_the_configured_one(string view, string variable, int places)
    {
        var source = File.ReadAllText(FindRepoFile(view));
        var shown = "<a href=\"mailto:@" + variable + "\">@IPRO.Email.BrandText.WithCapitals(" + variable + ")</a>";

        Assert.Equal(places, source.Split(shown).Length - 1);
        Assert.DoesNotContain("<a href=\"mailto:@" + variable + "\">@" + variable + "</a>", source);
    }

    [Fact]
    public void The_home_page_and_SuperAdmins_advice_write_the_addresses_with_capitals()
    {
        var home = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Home\Index.cshtml"));
        Assert.Contains("<a href=\"mailto:support@@iproadvisers.com\">support@@iProAdvisers.com</a>", home);

        var emailSetup = File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Views\EmailSetup\Index.cshtml"));
        Assert.Contains("billing@iProAdvisers.com", emailSetup);
        Assert.Contains("support@iProAdvisers.com", emailSetup);
        Assert.DoesNotContain("@iproadvisers.com</div>", emailSetup);
    }

    // ---- what a machine reads is not touched -------------------------------------------------------

    [Fact]
    public void Control_the_addresses_mail_is_sent_from_and_replied_to_stay_as_configured()
    {
        var settings = new EmailSettings();
        Assert.Equal("no-reply@iproadvisers.com", settings.FromEmail);
        Assert.Equal("support@iproadvisers.com", settings.ReplyToEmail);

        var appsettings = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\appsettings.json"));
        Assert.DoesNotContain("iProAdvisers.com\"", appsettings.Replace("www.iProAdvisers.com\"", ""));
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
