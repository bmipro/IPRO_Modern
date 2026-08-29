using System;
using System.IO;
using System.Linq;
using Xunit;

namespace IPRO.IntegrationTests;

// Phase 3 front-door truth sweep, code fixes (2026-08-29). 380 claims were extracted from
// /Preview, Register, the KB and the legal pages and verified against the product truth pack;
// these pin the three code-level findings. (The KB additions and legal pages shipped separately;
// the sweep's full record is in the session docs.)
public class FrontDoorTruthTests
{
    [Fact]
    public void The_orphaned_register_success_page_is_gone()
    {
        // The v1 signup receipt: unreachable since signup v2 (2026-08-13) except by direct GET,
        // where it fabricated Sample() data, described a removed temporary-password ceremony and
        // ALWAYS rendered a false "confirmation email could not be sent" warning. Three FALSE
        // claims on one page -- deleted, not patched.
        Assert.False(File.Exists(FindRepoFile(@"src\IPRO.Web\Views\Account\RegisterSuccess.cshtml")),
            "RegisterSuccess.cshtml is back -- it was deleted by the truth sweep");

        var actions = typeof(IPRO.Web.Controllers.AccountController)
            .GetMethods().Select(m => m.Name);
        Assert.DoesNotContain("RegisterSuccess", actions);

        // The welcome model/template must SURVIVE the deletion -- they build the real welcome
        // email for admin-created agents. Deleting them with the page was the near-miss.
        Assert.True(File.Exists(FindRepoFile(@"src\IPRO.Web\Models\RegistrationWelcomeTemplate.cs")));
    }

    [Fact]
    public void The_preview_ai_card_tiers_come_from_the_database()
    {
        // "Included in Platinum & Broker plans" was hardcoded -- true today, silently wrong the
        // day SuperAdmin regrades AiDailyAssistant. The controller now queries the same rows
        // that gate the real feature, and the card renders whatever they say.
        var controller = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\PreviewController.cs"));
        Assert.Contains("PackageFeatureCodes.AiDailyAssistant", controller);
        Assert.Contains("ViewBag.AiTierNames", controller);

        // ...and only PUBLIC tiers. The first deploy of this fix listed "Platinum Trial" and
        // "QA Platinum (Daily)" to prospects, because the join took every billing rule with the
        // feature. Caught on the live page 2026-08-29. The query must apply the same three-flag
        // filter as the homepage pricing grid (IsActive, !IsTrialPackage, !IsHiddenTestPackage --
        // BillingRule.cs: a real visitor must never be able to browse to a hidden test package).
        var tierQuery = controller[controller.IndexOf("ViewBag.AiTierNames")..];
        tierQuery = tierQuery[..tierQuery.IndexOf("ToListAsync")];
        Assert.Contains("IsActive", tierQuery);
        Assert.Contains("IsTrialPackage", tierQuery);
        Assert.Contains("IsHiddenTestPackage", tierQuery);

        var card = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Preview\_MockAiAssistantCard.cshtml"));
        Assert.DoesNotContain("Included in Platinum &amp; Broker plans", card);
        Assert.Contains("ViewBag.AiTierNames", card);
    }

    [Fact]
    public void The_preview_package_card_honours_the_setup_fee_waiver()
    {
        // The card showed the fee flat while the homepage and the CHECKOUT honour the waiver --
        // a prospect saw a charge that signup would not make. Same IsSetupFeeWaivedOn call as
        // everywhere else: the one method that decides what PayPal charges.
        var card = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Preview\_PackageContextCard.cshtml"));
        Assert.Contains("IsSetupFeeWaivedOn(DateTime.UtcNow)", card);
        Assert.Contains("setup fee waived", card);
        // The unwaived branch survives -- the fix must not hide a REAL fee either.
        Assert.Contains("one-time setup", card);
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
