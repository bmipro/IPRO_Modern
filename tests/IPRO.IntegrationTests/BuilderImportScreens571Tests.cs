using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.Admin.Controllers;
using IPRO.Admin.Models;
using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.Business.SiteImport;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Utility;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPRO.IntegrationTests;

// 571: the screens around the importer. A SuperAdmin uploads a package for a customer, reviews what
// it would do, looks at each page as it would be, brings it in and can put the previous site back.
// The rules of the door, the plan and the write are in BuilderImport571Tests; here it is the path a
// person takes, and who may take it.
public class BuilderImportScreens571Tests
{
    [Fact]
    public async Task A_refused_package_is_listed_reason_by_reason_and_nothing_is_stored()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var site = await BuilderImport571Tests.SeedSiteAsync(db);
        var blob = new BuilderImport571Tests.MemoryBlob();
        var audit = new RecordingAudit();
        var controller = Controller(db, blob, audit);
        var (template, files) = BuilderImport571Tests.Designer();
        template["pages"]![0]!["blocks"]![0]!["heading"] = "";          // a Hero with no heading
        template["pages"]![1]!["slug"] = "portal";                      // an address iPro keeps

        var result = await controller.Upload(site.AgentUserId, Upload(BuilderImport571Tests.Zip(template, files), "bad-site.zip"));

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Index", view.ViewName);
        var model = Assert.IsType<BuilderImportIndexViewModel>(view.Model);
        Assert.Equal("bad-site.zip", model.RefusedFileName);
        Assert.Equal(2, model.Problems.Count);
        Assert.Contains(model.Problems, p => p.Rule == "4.1" && p.Where == "Page \"home\", section 0");
        Assert.Contains(model.Problems, p => p.Rule == "3" && p.Message.Contains("iPro keeps for itself"));
        Assert.Empty(blob.Files);
        Assert.Equal(0, await db.WebsiteBuilderImports.CountAsync());
        Assert.Equal(0, await db.WebsiteMediaAssets.CountAsync());
        Assert.Contains(audit.Entries, e => e.Action == "BuilderImportRefused" && e.Details.Contains("bad-site.zip") && e.Details.Contains("2 problem(s)"));
    }

    [Fact]
    public async Task The_path_upload_review_bring_in_put_back()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var site = await BuilderImport571Tests.SeedSiteAsync(db);
        var blob = new BuilderImport571Tests.MemoryBlob();
        var audit = new RecordingAudit();
        var menuId = await db.WebsitePages.Where(p => p.AgentWebsiteId == site.Id && p.Slug == "menu").Select(p => p.Id).SingleAsync();

        // 1. Upload: stored, the site untouched, on to the review.
        var uploaded = Assert.IsType<RedirectToActionResult>(
            await Controller(db, blob, audit).Upload(site.AgentUserId, Upload(BuilderImport571Tests.Zip(BuilderImport571Tests.Designer()), "bakery.zip")));
        Assert.Equal("Review", uploaded.ActionName);
        var importId = (int)uploaded.RouteValues!["id"]!;
        Assert.Equal(2, await db.WebsitePages.CountAsync(p => p.AgentWebsiteId == site.Id));
        Assert.Contains(audit.Entries, e => e.Action == "BuilderImportStored" && e.Details.Contains("The site is unchanged"));
        db.ChangeTracker.Clear();

        // 2. Review: the plan, and a look address that carries the key.
        var review = Assert.IsType<BuilderImportReviewViewModel>(
            Assert.IsType<ViewResult>(await Controller(db, blob, audit).Review(importId, design: null, without: null)).Model);
        Assert.NotNull(review.Plan);
        Assert.Empty(review.Plan!.Problems);
        Assert.Equal(PageChange.Replaced, review.Plan.Pages.Single(p => p.Page.IsHomePage).Change);
        Assert.Equal(new[] { WebsiteBlockTypes.Reviews }, review.Plan.Pages.Single(p => p.Page.IsHomePage).SectionsLost);
        Assert.Equal(PageChange.Kept, review.Plan.Pages.Single(p => p.Page.Slug == "menu").Change);
        Assert.Equal(3, review.Images.Count(i => i.Created));
        var key = (await db.WebsiteBuilderImports.AsNoTracking().SingleAsync()).PreviewKey;
        Assert.Equal(48, key.Length);
        Assert.Equal($"https://portal.example.test/PublicWebsite/PreviewBuilderImport?id={importId}&key={key}&page=", review.LookAddress);
        db.ChangeTracker.Clear();

        // The choices travel in the review's own address, and the look's address carries them too.
        var chosen = Assert.IsType<RedirectToActionResult>(Controller(db, blob, audit).Choose(importId, design: null, without: new[] { menuId }));
        Assert.Equal(menuId.ToString(), chosen.RouteValues!["without"]);
        var narrowed = Assert.IsType<BuilderImportReviewViewModel>(
            Assert.IsType<ViewResult>(await Controller(db, blob, audit).Review(importId, null, menuId.ToString())).Model);
        Assert.Equal(PageChange.Removed, narrowed.Plan!.Pages.Single(p => p.Page.Slug == "menu").Change);
        Assert.EndsWith($"&without={menuId}&page=", narrowed.LookAddress);
        Assert.Equal(key, (await db.WebsiteBuilderImports.AsNoTracking().SingleAsync()).PreviewKey);   // the same key while it lasts
        db.ChangeTracker.Clear();

        // 3. Bring it in.
        var applied = Assert.IsType<RedirectToActionResult>(await Controller(db, blob, audit).Apply(importId, design: null, without: null));
        Assert.Equal("Index", applied.ActionName);
        db.ChangeTracker.Clear();
        Assert.Equal(new[] { "about", "contact", "home", "menu", "resources" },
            await db.WebsitePages.Where(p => p.AgentWebsiteId == site.Id).Select(p => p.Slug).OrderBy(s => s).ToListAsync());
        var row = await db.WebsiteBuilderImports.AsNoTracking().SingleAsync();
        Assert.Equal(WebsiteBuilderImportStatus.Applied, row.Status);
        Assert.Equal("owner", row.AppliedBy);
        Assert.Equal(string.Empty, row.PreviewKey);   // the look closes once the package is on the site
        Assert.Contains(audit.Entries, e => e.Action == "BuilderImportApplied" && e.Details.Contains("can be put back"));

        // The list offers to put the previous site back, for this package only.
        var index = Assert.IsType<BuilderImportIndexViewModel>(Assert.IsType<ViewResult>(await Controller(db, blob, audit).Index(site.AgentUserId)).Model);
        Assert.Equal(importId, index.RestorableId);
        Assert.Single(index.Imports);
        // A review of a package that is no longer waiting goes back to the list.
        Assert.IsType<RedirectToActionResult>(await Controller(db, blob, audit).Review(importId, null, null));
        db.ChangeTracker.Clear();

        // 4. Put the previous site back.
        Assert.IsType<RedirectToActionResult>(await Controller(db, blob, audit).Restore(importId));
        db.ChangeTracker.Clear();
        Assert.Equal(new[] { "home", "menu" }, await db.WebsitePages.Where(p => p.AgentWebsiteId == site.Id).Select(p => p.Slug).OrderBy(s => s).ToListAsync());
        Assert.Equal(WebsiteBuilderImportStatus.Restored, (await db.WebsiteBuilderImports.AsNoTracking().SingleAsync()).Status);
        Assert.Contains(audit.Entries, e => e.Action == "BuilderImportRestored");
        var after = Assert.IsType<BuilderImportIndexViewModel>(Assert.IsType<ViewResult>(await Controller(db, blob, audit).Index(site.AgentUserId)).Model);
        Assert.Null(after.RestorableId);   // nothing is on the site from the builder any more
    }

    [Fact]
    public async Task A_package_can_be_dropped_and_an_empty_upload_is_said_so()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var site = await BuilderImport571Tests.SeedSiteAsync(db);
        var blob = new BuilderImport571Tests.MemoryBlob();
        var audit = new RecordingAudit();

        var none = Controller(db, blob, audit);
        Assert.IsType<RedirectToActionResult>(await none.Upload(site.AgentUserId, null));
        Assert.Contains("Choose the package", (string)none.TempData["Error"]!);

        var uploaded = (RedirectToActionResult)await Controller(db, blob, audit).Upload(site.AgentUserId, Upload(BuilderImport571Tests.Zip(BuilderImport571Tests.Designer()), "site.zip"));
        var importId = (int)uploaded.RouteValues!["id"]!;
        db.ChangeTracker.Clear();

        Assert.IsType<RedirectToActionResult>(await Controller(db, blob, audit).Discard(importId));
        db.ChangeTracker.Clear();
        Assert.Equal(WebsiteBuilderImportStatus.Discarded, (await db.WebsiteBuilderImports.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0, await db.WebsiteMediaAssets.CountAsync());
        Assert.Equal(3, blob.Deleted.Count);
        Assert.Contains(audit.Entries, e => e.Action == "BuilderImportDiscarded");

        // A customer without a website, and an unknown customer.
        Assert.IsType<NotFoundResult>(await Controller(db, blob, audit).Upload(987654, Upload(new byte[] { 1 }, "x.zip")));
        Assert.IsType<NotFoundResult>(await Controller(db, blob, audit).Index(987654));
        Assert.IsType<NotFoundResult>(await Controller(db, blob, audit).Review(987654, null, null));
    }

    [Fact]
    public void Only_a_SuperAdmin_reaches_it_and_every_change_is_a_guarded_post()
    {
        var type = typeof(BuilderImportsController);
        var policy = Assert.Single(type.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal("SuperAdmin", policy.Policy);

        foreach (var name in new[] { "Upload", "Choose", "Apply", "Discard", "Restore" })
        {
            var action = type.GetMethod(name)!;
            Assert.NotNull(action.GetCustomAttribute<HttpPostAttribute>());
            Assert.NotNull(action.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        }
        // The upload takes the contract's 40 MB and no more than a form's worth on top.
        var limit = type.GetMethod("Upload")!.GetCustomAttribute<RequestSizeLimitAttribute>();
        Assert.NotNull(limit);

        // The agent's page shows the way in to a SuperAdmin and a disabled button to anyone else.
        var details = File.ReadAllText(Path.Combine(RepoRoot(), "src", "IPRO.Admin", "Views", "Agents", "Details.cshtml"));
        Assert.Contains("href=\"/BuilderImports?agentId=@Model.Id\"", details);
        Assert.Contains("disabled title=\"SuperAdmin only: brings a site in from the Template Builder", details);
    }

    [Fact]
    public async Task The_two_screens_render_from_their_real_views()
    {
        // The admin screens need a SuperAdmin sign-in, so nobody sees them before the owner does.
        // This renders the two real views with real models, which catches what a build cannot: a
        // null in the markup, a missing partial, a tag helper that throws.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var site = await BuilderImport571Tests.SeedSiteAsync(db);
        var blob = new BuilderImport571Tests.MemoryBlob();
        var audit = new RecordingAudit();
        var menuId = await db.WebsitePages.Where(p => p.AgentWebsiteId == site.Id && p.Slug == "menu").Select(p => p.Id).SingleAsync();

        // The list after a refused upload.
        var (bad, files) = BuilderImport571Tests.Designer();
        bad["pages"]![0]!["blocks"]![0]!["heading"] = "";
        var refused = (ViewResult)await Controller(db, blob, audit).Upload(site.AgentUserId, Upload(BuilderImport571Tests.Zip(bad, files), "bad-site.zip"));
        var refusedHtml = await RenderAsync("~/Views/BuilderImports/Index.cshtml", (BuilderImportIndexViewModel)refused.Model!);
        Assert.Contains("bad-site.zip", refusedHtml);
        Assert.Contains("was not taken", refusedHtml);
        Assert.Contains("A Hero section needs a heading.", refusedHtml);
        Assert.Contains("Page &quot;home&quot;, section 0", refusedHtml);
        Assert.Contains("No package has been uploaded for this site yet.", refusedHtml);
        Assert.Contains("name=\"__RequestVerificationToken\"", refusedHtml);

        // The review of a package that passed, with one page ticked for removal.
        var uploaded = (RedirectToActionResult)await Controller(db, blob, audit).Upload(site.AgentUserId, Upload(BuilderImport571Tests.Zip(BuilderImport571Tests.Designer()), "bakery.zip"));
        var importId = (int)uploaded.RouteValues!["id"]!;
        db.ChangeTracker.Clear();
        var review = (BuilderImportReviewViewModel)((ViewResult)await Controller(db, blob, audit).Review(importId, null, menuId.ToString())).Model!;
        var reviewHtml = await RenderAsync("~/Views/BuilderImports/Review.cshtml", review);
        Assert.Contains("bakery.zip", reviewHtml);
        Assert.Contains("Nothing on the site has changed yet.", reviewHtml);
        Assert.Contains("The builder cannot carry these, so they go: <strong>Review Badge</strong>", reviewHtml);
        Assert.Contains("Will be removed", reviewHtml);
        Assert.Contains($"name=\"without\" value=\"{menuId}\"", reviewHtml);
        Assert.Contains("PreviewBuilderImport?id=" + importId, reviewHtml);
        Assert.Contains("&amp;page=contact", reviewHtml);
        Assert.Contains("This site is published: bringing it in is live at once.", reviewHtml);
        Assert.Contains("formaction=\"/BuilderImports/Choose/" + importId + "\"", reviewHtml);
        db.ChangeTracker.Clear();

        // The list with a package on the site.
        await Controller(db, blob, audit).Apply(importId, null, null);
        db.ChangeTracker.Clear();
        var index = (BuilderImportIndexViewModel)((ViewResult)await Controller(db, blob, audit).Index(site.AgentUserId)).Model!;
        var indexHtml = await RenderAsync("~/Views/BuilderImports/Index.cshtml", index);
        Assert.Contains("On the site", indexHtml);
        Assert.Contains("Put the previous site back", indexHtml);
        Assert.Contains("4 pages, 8 sections", indexHtml);

        // For the eye, when a folder is named: the same markup as files (never set in the gate).
        var folder = Environment.GetEnvironmentVariable("BUILDER_SCREENS_OUT");
        if (!string.IsNullOrEmpty(folder))
        {
            File.WriteAllText(Path.Combine(folder, "refused.html"), Shell(refusedHtml));
            File.WriteAllText(Path.Combine(folder, "review.html"), Shell(reviewHtml));
            File.WriteAllText(Path.Combine(folder, "list.html"), Shell(indexHtml));
        }
    }

    // ---------------- the look ----------------

    [Fact]
    public async Task The_look_is_the_site_as_it_would_be_and_writes_nothing()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var site = await BuilderImport571Tests.SeedSiteAsync(db);
        var classic = new WebsiteTemplate { TemplateKey = "classic-sidebar", Name = "Classic", IsActive = true };
        db.Add(classic);
        await db.SaveChangesAsync();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var package = BuilderPackageReader.Read(BuilderImport571Tests.Zip(BuilderImport571Tests.Designer()), await BuilderImportService.ContextAsync(db, site, 0));
        var import = await BuilderImportService.StoreAsync(db, blob, site, package, "site.zip", "owner");
        var menuId = await db.WebsitePages.Where(p => p.AgentWebsiteId == site.Id && p.Slug == "menu").Select(p => p.Id).SingleAsync();
        db.ChangeTracker.Clear();

        var home = await BuilderImportPreviewBuilder.BuildAsync(db, import, pageSlug: null, design: null, without: null);
        Assert.NotNull(home);
        Assert.True(home!.CurrentPage!.IsHomePage);
        Assert.Equal("Plan with confidence, at every stage", home.CurrentPage.Blocks.OrderBy(b => b.SortOrder).First().Heading);
        Assert.Equal(new[] { "home", "about", "contact", "resources", "menu" }, home.Pages.Select(p => p.Slug));
        Assert.True(blob.Files.ContainsKey(home.Website.LogoUrl), "the look shows the package's logo");
        Assert.Equal(site.TemplateId, home.Website.TemplateId);
        Assert.NotNull(home.Website.AgentUser);
        // Every section has an id of its own, as the views that key their data by section need.
        var blocks = home.Pages.SelectMany(p => p.Blocks).ToList();
        Assert.Equal(blocks.Count, blocks.Select(b => b.Id).Distinct().Count());
        Assert.Single(home.BlogByBlockId);

        var contact = await BuilderImportPreviewBuilder.BuildAsync(db, import, "/Contact/", classic.Id, menuId.ToString());
        Assert.Equal("contact", contact!.CurrentPage!.Slug);
        Assert.Equal("classic-sidebar", contact.Website.Template.TemplateKey);
        Assert.DoesNotContain(contact.Pages, p => p.Slug == "menu");

        // A page that is not there shows the home page; a design that is not there leaves the site's.
        var missing = await BuilderImportPreviewBuilder.BuildAsync(db, import, "no-such-page", 987654, "abc,,-3");
        Assert.True(missing!.CurrentPage!.IsHomePage);
        Assert.Equal(site.TemplateId, missing.Website.TemplateId);

        // Nothing was written by looking.
        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.WebsitePages.CountAsync(p => p.AgentWebsiteId == site.Id));
        var stored = await db.AgentWebsites.AsNoTracking().SingleAsync(w => w.Id == site.Id);
        Assert.Equal(site.LogoUrl, stored.LogoUrl);
        Assert.Equal(site.TemplateId, stored.TemplateId);
    }

    [Fact]
    public async Task The_look_opens_only_with_its_key_only_for_an_hour_and_only_while_the_package_waits()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var site = await BuilderImport571Tests.SeedSiteAsync(db);
        var package = BuilderPackageReader.Read(BuilderImport571Tests.Zip(BuilderImport571Tests.Designer()), await BuilderImportService.ContextAsync(db, site, 0));
        var import = await BuilderImportService.StoreAsync(db, new BuilderImport571Tests.MemoryBlob(), site, package, "site.zip", "owner");
        var now = DateTime.UtcNow;

        Assert.False(BuilderImportService.LookIsOpen(import, "", now));   // no look was ever opened
        var key = await BuilderImportService.LookKeyAsync(db, import);
        Assert.True(BuilderImportService.LookKeyLooksRight(key));
        Assert.Equal(key, await BuilderImportService.LookKeyAsync(db, import));

        Assert.True(BuilderImportService.LookIsOpen(import, key, now));
        Assert.False(BuilderImportService.LookIsOpen(import, null, now));
        Assert.False(BuilderImportService.LookIsOpen(import, key.ToUpperInvariant(), now));
        Assert.False(BuilderImportService.LookIsOpen(import, new string('0', 48), now));
        Assert.False(BuilderImportService.LookIsOpen(import, key[..47], now));
        Assert.False(BuilderImportService.LookIsOpen(import, key, now.Add(BuilderImportService.LookLifetime).AddMinutes(1)));

        // A key about to lapse is replaced when the review screen is opened again.
        import.PreviewKeyExpiresAt = now.AddMinutes(5);
        var renewed = await BuilderImportService.LookKeyAsync(db, import);
        Assert.NotEqual(key, renewed);
        Assert.False(BuilderImportService.LookIsOpen(import, key, now));

        // Once the package is on the site, or dropped, its look is gone.
        Assert.True((await BuilderImportService.ApplyAsync(db, import, new HashSet<int>(), null, "owner")).Ok);
        Assert.False(BuilderImportService.LookIsOpen(import, renewed, now));
        import.Status = WebsiteBuilderImportStatus.Discarded;
        import.PreviewKey = renewed;
        import.PreviewKeyExpiresAt = now.AddHours(1);
        Assert.False(BuilderImportService.LookIsOpen(import, renewed, now));
    }

    [Fact]
    public void The_public_address_of_the_look_asks_the_database_nothing_without_a_well_formed_key()
    {
        // Scanners probe every public address all day. The look's action must turn away anything that
        // is not an id with a 48-digit key before it reads a row.
        var controller = File.ReadAllText(Path.Combine(RepoRoot(), "src", "IPRO.Web", "Controllers", "PublicWebsiteController.cs"));
        var start = controller.IndexOf("public async Task<IActionResult> PreviewBuilderImport(", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = controller[start..controller.IndexOf("// Canonical host selection.", start, StringComparison.Ordinal)];
        var guard = body.IndexOf("LookKeyLooksRight(key)) return NotFound();", StringComparison.Ordinal);
        var read = body.IndexOf("_db.", StringComparison.Ordinal);
        Assert.True(guard > 0 && read > guard, "the key's shape is checked before the database is read");
        Assert.Contains("LookIsOpen(import, key, DateTime.UtcNow)) return NotFound();", body);
        Assert.Contains("ViewBag.IsTemplatePreview = true;", body);            // noindex, forms switched off
        Assert.Contains("\"noindex, nofollow\"", body);
        Assert.DoesNotContain("TrackPageViewAsync", body);                     // a look is not a visit

        // The banner the three designs show on a preview says what THIS preview is.
        Assert.Contains("ViewBag.PreviewBanner = \"A look at a Template Builder package before it is brought in.", body);
        foreach (var design in new[] { "_ModernProfessional", "_ClassicSidebar", "_EditorialVisual" })
        {
            var view = File.ReadAllText(Path.Combine(RepoRoot(), "src", "IPRO.Web", "Views", "PublicWebsite", design + ".cshtml"));
            Assert.Contains("@(ViewBag.PreviewBanner as string ?? \"Template preview using your current pages and content. Nothing has been changed.\")", view);
        }

        Assert.False(BuilderImportService.LookKeyLooksRight("../../etc/passwd"));
        Assert.False(BuilderImportService.LookKeyLooksRight(new string('g', 48)));
        Assert.True(BuilderImportService.LookKeyLooksRight(new string('a', 48)));
    }

    // ---------------- helpers ----------------

    private static BuilderImportsController Controller(IPRODbContext db, IBlobStorageService blob, IAdminAuditLogService audit)
    {
        var services = new ServiceCollection().AddSingleton(blob).BuildServiceProvider();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:PortalBaseUrl"] = "https://portal.example.test/" })
            .Build();
        var controller = new BuilderImportsController(db, new PackageEntitlementService(new UnitOfWork(db), db), audit, configuration, services);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Name, "owner"), new Claim("Role", "SuperAdmin")
            }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(context, new NullTempData());
        return controller;
    }

    // The view's own markup (no layout), rendered by the real Razor engine from the admin app's
    // compiled views, as a signed-in SuperAdmin.
    private static async Task<string> RenderAsync<TModel>(string viewPath, TModel model)
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(new Microsoft.AspNetCore.Builder.WebApplicationOptions
        {
            ApplicationName = typeof(BuilderImportsController).Assembly.GetName().Name,
            ContentRootPath = Path.Combine(RepoRoot(), "src", "IPRO.Admin"),
            EnvironmentName = "Production"
        });
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(BuilderImportsController).Assembly);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var http = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "owner"), new Claim("Role", "SuperAdmin") }, "test"))
        };
        // An endpoint on the request is what tells MVC to make links the endpoint-routing way; with
        // no routes mapped here a form's own address comes out empty, which a rendering check can live with.
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "render"));
        var actionContext = new ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var engine = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Mvc.Razor.IRazorViewEngine>();
        var found = engine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: false);
        Assert.True(found.Success, $"{viewPath} was not found among the admin app's compiled views");
        var viewData = new Microsoft.AspNetCore.Mvc.ViewFeatures.ViewDataDictionary<TModel>(
            new Microsoft.AspNetCore.Mvc.ModelBinding.EmptyModelMetadataProvider(), new Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary()) { Model = model };
        var tempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(http, new NullTempData());
        await using var writer = new StringWriter();
        await found.View!.RenderAsync(new Microsoft.AspNetCore.Mvc.Rendering.ViewContext(
            actionContext, found.View, viewData, tempData, writer, new Microsoft.AspNetCore.Mvc.ViewFeatures.HtmlHelperOptions()));
        return writer.ToString();
    }

    // The admin layout's stylesheets around a view's markup, so the file can be looked at.
    private static string Shell(string body) =>
        "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
        "<link href=\"https://cdn.jsdelivr.net/npm/bootstrap@5.3.0/dist/css/bootstrap.min.css\" rel=\"stylesheet\">" +
        "<link href=\"https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.5.0/css/all.min.css\" rel=\"stylesheet\">" +
        "</head><body style=\"background:#f4f6fb\"><div class=\"container-fluid py-4\" style=\"max-width:1280px\">" + body + "</div></body></html>";

    private static IFormFile Upload(byte[] bytes, string name) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "package", name) { Headers = new HeaderDictionary(), ContentType = "application/zip" };

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private sealed class RecordingAudit : IAdminAuditLogService
    {
        public List<(string Action, string Details)> Entries { get; } = new();
        public Task LogAsync(int adminUserId, string adminUsername, string action, string details)
        {
            Entries.Add((action, details));
            return Task.CompletedTask;
        }
    }

    private sealed class NullTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
