using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 475 (2026-09-10). The owner could not add a starter article under Mortgage: the add button
// sat at the top of a long page, Business Type was a free-text box (one typo and the article files
// under a vertical of its own), and nothing pre-filled. Now every group on the list has its own
// Add button that opens the form with that business type and category filled in and the next sort
// order, and the Business Type field offers the known verticals plus every value already in use
// (a datalist: pick one, or type a new vertical once and it is offered from then on). The same
// list backs the Business Type field on starter pages.
public class StarterArticlesEditorTests
{
    [Fact]
    public async Task The_business_type_list_offers_all_the_known_verticals_and_anything_already_in_use()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var custom = ($"Dentists-{Guid.NewGuid():N}")[..20];
        db.WebsiteStarterArticles.Add(new WebsiteStarterArticle { BusinessType = custom, Title = "Floss daily", Content = "<p>Yes.</p>" });
        db.WebsiteStarterArticles.Add(new WebsiteStarterArticle { BusinessType = " mortgage ", Title = "A typo in the type", Content = "<p>.</p>" });
        await db.SaveChangesAsync();

        var list = await StarterBusinessTypes.ListAsync(db);

        Assert.Equal("All", list[0]);
        Assert.Equal(new[] { "Accountants", "Insurance / Financial", "Mortgage" }, list.Skip(1).Take(3).ToArray());
        Assert.Contains(custom, list);
        Assert.Equal(1, list.Count(t => string.Equals(t, "Mortgage", StringComparison.OrdinalIgnoreCase))); // trimmed and case-folded, no duplicate
        Assert.Equal(list.Count, list.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task Add_from_a_group_opens_the_form_filled_in_with_the_next_sort_order()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var vertical = ($"T475-{Guid.NewGuid():N}")[..20];
        db.WebsiteStarterArticles.Add(new WebsiteStarterArticle { BusinessType = vertical, Category = "Personal", Title = "One", Content = "<p>1</p>", SortOrder = 0 });
        db.WebsiteStarterArticles.Add(new WebsiteStarterArticle { BusinessType = vertical, Category = "Personal", Title = "Two", Content = "<p>2</p>", SortOrder = 4 });
        db.WebsiteStarterArticles.Add(new WebsiteStarterArticle { BusinessType = vertical, Category = null, Title = "Loose", Content = "<p>3</p>", SortOrder = 9 });
        await db.SaveChangesAsync();

        var controller = NewController(db);

        var inGroup = Assert.IsType<ViewResult>(await controller.Create(vertical, "Personal"));
        var model = Assert.IsType<WebsiteStarterArticle>(inGroup.Model);
        Assert.Equal(vertical, model.BusinessType);
        Assert.Equal("Personal", model.Category);
        Assert.Equal(5, model.SortOrder);
        Assert.True(model.IsActive);
        Assert.NotNull(inGroup.ViewData["BusinessTypes"]);

        var uncategorized = Assert.IsType<WebsiteStarterArticle>(Assert.IsType<ViewResult>(await controller.Create(vertical, null)).Model);
        Assert.Null(uncategorized.Category);
        Assert.Equal(10, uncategorized.SortOrder);

        var plain = Assert.IsType<WebsiteStarterArticle>(Assert.IsType<ViewResult>(await controller.Create(null, null)).Model);
        Assert.Equal("All", plain.BusinessType);
    }

    [Fact]
    public void The_list_has_an_add_button_per_group_and_the_forms_offer_the_verticals()
    {
        var index = File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Views\WebsiteStarterArticles\Index.cshtml"));
        Assert.Contains("/WebsiteStarterArticles/Create?businessType=", index);

        var edit = File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Views\WebsiteStarterArticles\Edit.cshtml"));
        Assert.Contains("list=\"businessTypes\"", edit);
        Assert.Contains("<datalist id=\"businessTypes\">", edit);

        var page = File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Views\StarterContent\Edit.cshtml"));
        Assert.Contains("list=\"businessTypes\"", page);
        Assert.Contains("<datalist id=\"businessTypes\">", page);
    }

    // ---- harness ------------------------------------------------------------------------------

    private static IPRO.Admin.Controllers.WebsiteStarterArticlesController NewController(IPRODbContext db)
    {
        var controller = new IPRO.Admin.Controllers.WebsiteStarterArticlesController(db, new NoAudit());
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Name, "admin") }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(ctx, new NoTempData());
        return controller;
    }

    private sealed class NoAudit : IAdminAuditLogService
    {
        public Task LogAsync(int adminUserId, string adminUsername, string action, string details) => Task.CompletedTask;
    }

    private sealed class NoTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
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
