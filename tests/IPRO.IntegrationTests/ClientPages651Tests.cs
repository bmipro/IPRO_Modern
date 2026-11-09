using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Web.Controllers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// 651 (634's second). The client list loaded the whole book with its account types: at 3,000 clients a
// 4-5 MB page with an anti-forgery token per row. Now the database counts, orders and pages it, the search
// and filters ride along in the page links, and one form with one token serves every Delete button.
public class ClientPages651Tests
{
    private const int PageSize = ClientsController.ClientListPageSize;

    [Fact]
    public async Task The_list_is_paged_in_the_database_newest_first_and_only_the_advisers_own()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var mine = await SeedAgentAsync(db, "mine");
        var theirs = await SeedAgentAsync(db, "theirs");
        var start = new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc);
        // 120 of mine, the first ten added in the same second (the Id must break that tie), and 5 of theirs.
        var myIds = await SeedClientsAsync(db, mine, 120, i => i < 10 ? start : start.AddMinutes(i));
        var theirIds = await SeedClientsAsync(db, theirs, 5, i => start.AddMinutes(500 + i));
        db.ChangeTracker.Clear();

        var expected = await db.Clients.AsNoTracking().Where(c => c.AgentUserId == mine)
            .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).Select(c => c.Id).ToListAsync();
        Assert.Equal(120, expected.Count);

        var (page1, bag1) = await IndexAsync(db, mine, page: 1);
        Assert.Equal(PageSize, page1.Count);
        Assert.Equal(1, bag1.Page);
        Assert.Equal(3, bag1.PageCount);
        Assert.Equal(120, bag1.MatchingCount);

        var (page2, _) = await IndexAsync(db, mine, page: 2);
        var (page3, bag3) = await IndexAsync(db, mine, page: 3);
        Assert.Equal(120 - 2 * PageSize, page3.Count);
        Assert.Equal(3, bag3.Page);

        // Every client of mine exactly once, in the list's order, and none of theirs.
        var walked = page1.Concat(page2).Concat(page3).Select(c => c.Id).ToList();
        Assert.Equal(expected, walked);
        Assert.Empty(walked.Intersect(theirIds));
        Assert.Equal(myIds.OrderBy(x => x), walked.OrderBy(x => x));

        // A page past the end shows the last page; a page before the start shows the first.
        var (past, pastBag) = await IndexAsync(db, mine, page: 99);
        Assert.Equal(3, pastBag.Page);
        Assert.Equal(page3.Select(c => c.Id), past.Select(c => c.Id));
        var (before, beforeBag) = await IndexAsync(db, mine, page: -4);
        Assert.Equal(1, beforeBag.Page);
        Assert.Equal(page1.Select(c => c.Id), before.Select(c => c.Id));

        // An empty book is one empty page.
        var nobody = await SeedAgentAsync(db, "nobody");
        var (none, noneBag) = await IndexAsync(db, nobody, page: 1);
        Assert.Empty(none);
        Assert.Equal(1, noneBag.PageCount);
        Assert.Equal(0, noneBag.MatchingCount);
    }

    [Fact]
    public async Task The_search_and_filters_are_counted_and_paged_and_each_row_has_its_account_types()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var mine = await SeedAgentAsync(db, "mine");
        var start = new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc);
        var ids = await SeedClientsAsync(db, mine, 70, i => start.AddMinutes(i));
        var gold = new ClientCategory { AgentUserId = mine, Name = "Gold" };
        var silver = new ClientCategory { AgentUserId = mine, Name = "Silver" };
        db.AddRange(gold, silver);
        await db.SaveChangesAsync();
        // 55 Gold clients (two pages), every fifth also Silver; three called Zedwick; 20 subscribed.
        var clients = await db.Clients.Where(c => c.AgentUserId == mine).OrderBy(c => c.Id).ToListAsync();
        for (var i = 0; i < clients.Count; i++)
        {
            if (i < 55) clients[i].Categories.Add(gold);
            if (i < 55 && i % 5 == 0) clients[i].Categories.Add(silver);
            if (i is 3 or 33 or 63) clients[i].LastName = "Zedwick";
            clients[i].IsNewsletterSubscribed = i < 20;
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var (goldPage1, goldBag) = await IndexAsync(db, mine, page: 1, accountTypeId: gold.Id);
        Assert.Equal(55, goldBag.MatchingCount);
        Assert.Equal(2, goldBag.PageCount);
        Assert.Equal(PageSize, goldPage1.Count);
        Assert.All(goldPage1, c => Assert.Contains(c.Categories, cat => cat.Id == gold.Id));
        var (goldPage2, _) = await IndexAsync(db, mine, page: 2, accountTypeId: gold.Id);
        Assert.Equal(5, goldPage2.Count);
        // The account types are loaded with the page's rows (the badges), every one of them.
        var both = goldPage1.Concat(goldPage2).Where(c => c.Categories.Count == 2).ToList();
        Assert.Equal(11, both.Count);

        var (zed, zedBag) = await IndexAsync(db, mine, page: 1, search: "  Zedwick ");
        Assert.Equal(3, zedBag.MatchingCount);
        Assert.Equal(3, zed.Count);
        Assert.Equal("Zedwick", zedBag.Search);

        var (subscribed, subBag) = await IndexAsync(db, mine, page: 1, newsletter: "yes");
        Assert.Equal(20, subBag.MatchingCount);
        Assert.All(subscribed, c => Assert.True(c.IsNewsletterSubscribed));
        var (unsubscribed, unsubBag) = await IndexAsync(db, mine, page: 1, newsletter: "no");
        Assert.Equal(50, unsubBag.MatchingCount);
        Assert.Equal(PageSize, unsubscribed.Count);

        // The header still counts the whole book.
        Assert.Equal(70, goldBag.TotalCount);
        Assert.Equal(70, ids.Count);
    }

    [Fact]
    public void The_list_reads_one_page_and_its_page_carries_one_token()
    {
        var controller = Read(@"src\IPRO.Web\Controllers\ClientsController.cs");
        Assert.Contains("public async Task<IActionResult> Index(string? search, int? accountTypeId, string? newsletter, int page = 1)", controller);
        Assert.Contains("var query = _db.Clients.AsNoTracking()\n            .Where(c => c.AgentUserId == AgentId);", controller);
        Assert.Contains(".OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id)\n            .Skip((page - 1) * ClientListPageSize).Take(ClientListPageSize)", controller);
        Assert.DoesNotContain("return View(await query.OrderByDescending(c => c.CreatedAt).ToListAsync());", controller);

        var view = Read(@"src\IPRO.Web\Views\Clients\Index.cshtml");
        // Two tokens on the page: the shared Delete form's and the import form's; none inside the rows.
        Assert.Equal(2, Regex.Matches(view, @"@Html\.AntiForgeryToken\(\)").Count);
        Assert.Contains("<form id=\"clientDeleteForm\" method=\"post\" class=\"d-none\">@Html.AntiForgeryToken()</form>", view);
        Assert.Contains("<button type=\"submit\" form=\"clientDeleteForm\" formaction=\"@Url.Action(\"Delete\", new { id = c.Id })\" class=\"btn btn-sm btn-outline-danger js-confirm-submit\"", view);
        Assert.DoesNotContain("<form asp-action=\"Delete\"", view);
        var rows = view[view.IndexOf("@foreach (var c in Model)", StringComparison.Ordinal)..view.IndexOf("</tbody>", StringComparison.Ordinal)];
        Assert.DoesNotContain("AntiForgeryToken", rows);
        // The page links keep the search and both filters.
        Assert.Contains("if (!string.IsNullOrEmpty(searchText)) parts.Add(\"search=\" + Uri.EscapeDataString(searchText));", view);
        Assert.Contains("if (selectedAccountTypeId.HasValue) parts.Add(\"accountTypeId=\" + selectedAccountTypeId.Value);", view);
        Assert.Contains("if (!string.IsNullOrEmpty(selectedNewsletter)) parts.Add(\"newsletter=\" + Uri.EscapeDataString(selectedNewsletter));", view);
        Assert.Contains("<nav aria-label=\"Client list pages\"", view);
        // The shared confirm still reads the clicked button, which a button outside its form still is.
        Assert.Contains("const submitter = event.submitter;", Read(@"src\IPRO.Web\Views\Shared\_Layout.cshtml"));
    }

    // ---- harness ------------------------------------------------------------------------------

    private sealed record Bag(int Page, int PageCount, int MatchingCount, int TotalCount, string? Search);

    private static async Task<(List<Client> Rows, Bag Bag)> IndexAsync(IPRODbContext db, int agentId, int page,
        string? search = null, int? accountTypeId = null, string? newsletter = null)
    {
        var uow = new UnitOfWork(db);
        var controller = Lock583.Prepare(new ClientsController(new ClientService(uow, db), null!, uow, db, new Lock583.GrantAll(), null!, null!, null!,
                new EphemeralDataProtectionProvider(), Lock583.Config,
                new EmailConsentService(db, new ConfigurationBuilder().Build(), NullLogger<EmailConsentService>.Instance, Array.Empty<IUnsubscribeNotifier>())),
            Lock583.User(new Claim(ClaimTypes.NameIdentifier, agentId.ToString(CultureInfo.InvariantCulture))));
        var result = Assert.IsType<ViewResult>(await controller.Index(search, accountTypeId, newsletter, page));
        var rows = Assert.IsAssignableFrom<IEnumerable<Client>>(result.Model).ToList();
        var bag = controller.ViewData;
        return (rows, new Bag((int)bag["Page"]!, (int)bag["PageCount"]!, (int)bag["MatchingCount"]!, (int)bag["TotalCount"]!, bag["Search"] as string));
    }

    private static async Task<int> SeedAgentAsync(IPRODbContext db, string tag)
    {
        var rule = new BillingRule { PackageName = $"P651-{Guid.NewGuid():N}"[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = $"p651{tag}-{Guid.NewGuid():N}"[..20],
            Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Morgan", LastName = "Reid",
            DomainName = $"p651-{Guid.NewGuid():N}"[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static async Task<List<int>> SeedClientsAsync(IPRODbContext db, int agentId, int count, Func<int, DateTime> addedAt)
    {
        var clients = Enumerable.Range(0, count).Select(i => new Client
        {
            AgentUserId = agentId,
            FirstName = "Client",
            LastName = $"N{i:D4}",
            Email = $"c{i}-{Guid.NewGuid():N}@example.test",
            CreatedAt = addedAt(i)
        }).ToList();
        db.AddRange(clients);
        await db.SaveChangesAsync();
        return clients.Select(c => c.Id).ToList();
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative)).Replace("\r\n", "\n");
    }
}
