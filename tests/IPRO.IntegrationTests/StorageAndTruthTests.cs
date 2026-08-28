using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Web.Controllers;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// M9 + M10 + M20 (launch runway Phase 2, Wave B, 2026-08-27), plus the owner-requested invoice
// print button.
//
// M9: article images counted AGAINST the shared FileUploadCapacity pool (AgentStorageUsage sums
// ImageSizeBytes) but their upload path never CHECKED it -- the one door that stayed open after
// documents, gallery photos and portal uploads all learned to refuse. The register's second
// clause ("ImageSizeBytes never resets when an image is removed") is VOID: no remove-image path
// exists -- an image can only be replaced (size overwritten) or the article deleted (row gone).
//
// M10: every storage display used `LimitValue ?? 0` while enforcement used `?? 1024`, so a blank
// limit -- a supported configuration -- rendered "of 0 MB" under a working 1024 MB quota.
//
// M20: the reconciliation doc claimed "ResumePayment deliberately not guarded", which is false
// and backwards -- it IS guarded, after the void, so divergence costs the checkout AND refuses.
public class StorageAndTruthTests
{
    // ------------------------------------------------------------------- M9: the open door --

    [Fact]
    public async Task M9_an_article_image_over_the_quota_is_refused()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        // 1 MB limit, 0.9 MB already used by a document: a 200 KB image must be refused.
        var seed = await SeedAgentAsync(db, limitMb: 1, usedDocumentBytes: 900 * 1024);

        var controller = NewController(db, seed.AgentId);
        var result = await controller.Create(
            new Article { Title = "Over quota" }, PngFormFile(200 * 1024));

        // Pre-fix: the article saved with its image, quota never consulted.
        Assert.IsType<ViewResult>(result);
        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.Articles.CountAsync(a => a.AgentUserId == seed.AgentId));
    }

    [Fact]
    public async Task M9_an_image_that_fits_still_uploads()
    {
        // The other direction: the check must not refuse an upload that fits.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedAgentAsync(db, limitMb: 10, usedDocumentBytes: 900 * 1024);

        var controller = NewController(db, seed.AgentId);
        var result = await controller.Create(
            new Article { Title = "Fits fine" }, PngFormFile(200 * 1024));

        Assert.IsType<RedirectToActionResult>(result);
        db.ChangeTracker.Clear();
        var article = await db.Articles.SingleAsync(a => a.AgentUserId == seed.AgentId);
        Assert.Equal(200 * 1024, article.ImageSizeBytes);
    }

    [Fact]
    public async Task M9_a_replacement_is_judged_on_the_net_change()
    {
        // An agent AT their limit replacing a large image with a smaller one must succeed --
        // the outgoing bytes leave the pool as the new ones arrive. A check that ignored the
        // replaced size would lock every full account out of shrinking its own usage.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedAgentAsync(db, limitMb: 1, usedDocumentBytes: 0);
        var article = new Article
        {
            AgentUserId = seed.AgentId, Title = "Existing",
            ImageUrl = "https://blob.example.test/article-media/old.png",
            ImageSizeBytes = 900 * 1024,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.Add(article);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var controller = NewController(db, seed.AgentId);
        var result = await controller.Edit(
            new Article { Id = article.Id, Title = "Existing" }, PngFormFile(300 * 1024));

        Assert.IsType<RedirectToActionResult>(result);
        db.ChangeTracker.Clear();
        var after = await db.Articles.SingleAsync(a => a.Id == article.Id);
        Assert.Equal(300 * 1024, after.ImageSizeBytes);
    }

    // ---------------------------------------------------------------- M10: display honesty --

    [Fact]
    public void M10_the_display_falls_back_to_the_enforced_default()
    {
        // Pre-fix there was no display helper at all -- every surface hand-rolled `?? 0`.
        Assert.Equal(AgentStorageUsage.DefaultLimitMb, AgentStorageUsage.DisplayLimitMb(null));
        Assert.Equal(500, AgentStorageUsage.DisplayLimitMb(500));
        // Display and enforcement must agree by construction, not by coincidence.
        Assert.Equal(AgentStorageUsage.LimitBytes(null),
            (long)AgentStorageUsage.DisplayLimitMb(null) * 1024 * 1024);
    }

    [Fact]
    public void M10_no_storage_surface_hand_rolls_the_zero_fallback_any_more()
    {
        // The drift pin: the defect existed because six call sites each invented their own
        // fallback. Any storage surface writing `LimitValue ?? 0` again turns this red.
        // (TeamController's `?? 0` is team SEATS, a different quantity -- deliberately exempt.)
        var storageSurfaces = new[]
        {
            @"src\IPRO.Web\Controllers\DocumentsController.cs",
            @"src\IPRO.Web\Controllers\ClientsController.cs",
            @"src\IPRO.Web\Controllers\WebsitePagesController.cs",
            @"src\IPRO.Web\Controllers\ArticlesController.cs",
            @"src\IPRO.Web\Views\Documents\Index.cshtml",
            @"src\IPRO.Web\Views\WebsitePages\Edit.cshtml"
        };
        foreach (var relative in storageSurfaces)
        {
            var text = File.ReadAllText(FindRepoFile(relative));
            Assert.False(text.Contains("LimitValue ?? 0"),
                $"{relative} renders a storage limit with `?? 0` -- use AgentStorageUsage.DisplayLimitMb so display matches enforcement");
        }
    }

    // --------------------------------------------------------------------- M20: doc truth --

    [Fact]
    public void M20_the_reconciliation_doc_no_longer_claims_resume_is_unguarded()
    {
        var text = File.ReadAllText(FindRepoFile(@"DOCS\AUDIT_RECONCILIATION_2026-08-17.md"));
        Assert.Contains("CORRECTED 2026-08-27, M20", text);
        // The correction deliberately QUOTES the stricken sentence for the record, so the claim
        // may only appear inside that bracketed block. Remove the block, then nothing outside it
        // may still assert the falsehood. Pre-fix: the bare sentence stood as fact.
        var start = text.IndexOf("[CORRECTED 2026-08-27, M20", StringComparison.Ordinal);
        var end = text.IndexOf(']', start);
        var outside = text.Remove(start, end - start + 1);
        Assert.DoesNotContain("ResumePayment deliberately not guarded", outside);
    }

    // ------------------------------------------------------------- invoice print (owner ask) --

    [Fact]
    public void Invoice_view_carries_the_print_button_and_prints_only_the_card()
    {
        // Owner request 2026-08-27: print / save-as-PDF for every invoice under Revenue.
        // Pinned so the button and its print styling never silently vanish.
        var razor = File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Views\Reports\Invoice.cshtml"));
        Assert.Contains("id=\"print-invoice\"", razor);
        Assert.Contains("window.print()", razor);
        Assert.Contains("@media print", razor.Replace("@@media print", "@media print"));
        Assert.Contains(".admin-sidebar", razor);   // the chrome is stripped from the printout
        Assert.Contains(".admin-topbar", razor);
    }

    // ------------------------------------------------------------------------------ plumbing --

    private sealed record Seed(int AgentId);

    private static async Task<Seed> SeedAgentAsync(IPRODbContext db, int? limitMb, long usedDocumentBytes)
    {
        var rule = new BillingRule { PackageName = $"ST-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        db.Add(rule);
        await db.SaveChangesAsync();
        // The articles screen gates on Newsletters; the quota reads FileUploadCapacity.
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.Newsletters, FeatureName = "Newsletters", IsIncluded = true });
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.FileUploadCapacity, FeatureName = "Storage", IsIncluded = true, LimitValue = limitMb });

        var agent = new AgentUser
        {
            UserName = $"st-{Guid.NewGuid():N}"[..20],
            Email = $"st-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Storage", LastName = "Test",
            DomainName = $"st-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario"
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        agent.PackageId = rule.Id;
        db.Add(new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id, BillingRuleId = rule.Id, Amount = 60m,
            Status = BillingStatus.Active, Period = BillingPeriod.Monthly,
            StartDate = DateTime.UtcNow.AddDays(-10), NextBillingDate = DateTime.UtcNow.AddDays(20)
        });

        if (usedDocumentBytes > 0)
        {
            db.Add(new AgentDocument
            {
                AgentUserId = agent.Id, FileName = "existing.pdf",
                FileSizeBytes = usedDocumentBytes
            });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Seed(agent.Id);
    }

    private static ArticlesController NewController(IPRODbContext db, int agentId)
    {
        var controller = new ArticlesController(
            db,
            new PackageEntitlementService(new UnitOfWork(db), db),
            new NullBlob(),
            new NullAi());
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
            context, new NullTempData());
        return controller;
    }

    // A real PNG signature followed by padding: passes the magic-byte sniff at the target size.
    private static FormFile PngFormFile(int sizeBytes)
    {
        var bytes = new byte[sizeBytes];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "image", "cover.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png"
        };
    }

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }

    private sealed class NullAi : IPRO.Business.Interfaces.IAiSuggestionService
    {
        public Task<IPRO.Business.Interfaces.AiActionReasonResult> GenerateActionReasonAsync(string situation, System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult(IPRO.Business.Interfaces.AiActionReasonResult.Empty);
        public Task<IPRO.Business.Interfaces.AiActionReasonResult> DraftSocialPostAsync(string topic, System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult(IPRO.Business.Interfaces.AiActionReasonResult.Empty);
        public Task<IPRO.Business.Interfaces.AiNewsletterDraftResult> DraftNewsletterAsync(string topic, System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult(IPRO.Business.Interfaces.AiNewsletterDraftResult.Empty);
        public Task<IPRO.Business.Interfaces.AiBlogPostDraftResult> DraftBlogPostAsync(string topic, System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult(IPRO.Business.Interfaces.AiBlogPostDraftResult.Empty);
    }

    private sealed class NullBlob : IPRO.Utility.IBlobStorageService
    {
        public Task<string> UploadAsync(Stream fileStream, string fileName, string containerName, string contentType, bool isPrivate) =>
            Task.FromResult($"https://blob.example.test/{containerName}/{Guid.NewGuid():N}-{fileName}");
        public Task<bool> DeleteAsync(string blobUrl) => Task.FromResult(true);
        public Task<Stream?> DownloadAsync(string blobUrl) => Task.FromResult<Stream?>(null);
        public Task<List<string>> ListAsync(string containerName) => Task.FromResult(new List<string>());
        public string GetPublicUrl(string containerName, string fileName) => $"https://blob.example.test/{containerName}/{fileName}";
        public Task EnsureContainerAccessAsync(string containerName, bool isPrivate) => Task.CompletedTask;
    }

    private sealed class NullTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
