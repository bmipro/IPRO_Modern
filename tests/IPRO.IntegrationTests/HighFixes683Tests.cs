using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.Admin.Controllers;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Scheduler;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// 683, the whole-code audit of 2026-11-26: three of its High findings, each "one person's value stops it for everyone".
//
// W02-02 and S01-01, the nightly reminder job. The life-event form's limits (a label of 200, a reminder of 1 to 90
// days) were the browser's only. A hand-made post could store a reminder of two billion days, and working out its date
// threw for the whole run; and a label of 200 plus the client's two names made a follow-up title longer than its
// column, which the database refused, with every other reminder in the same save. Either way one adviser's row
// stopped every adviser's birthday and life-event reminders, every night, until someone found the row.
//
// A03-01, starter articles. SuperAdmin accepted a title of 200 and a summary of 500; a page holds a label of 100 and a
// description of 320. One long summary made MySQL refuse the Resources pages of every adviser of that business type.
//
// (ACC-1 is tested in DomainOwnership592Tests, KEY-1 in KeyVault681Tests.)
public class HighFixes683Tests
{
    private static readonly DateTime Today = new(2026, 11, 8);

    [Fact]
    public async Task One_advisers_impossible_lead_time_or_long_label_no_longer_stops_everyones_reminders()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using (var db = testDb.CreateContext())
        {
            var agentId = await SeedAgentAsync(db);
            var avery = Client(agentId, "Avery", "Reid", null);
            var longNamed = Client(agentId, new string('F', 80), new string('L', 80), new DateTime(1980, 11, 10));   // birthday in two days
            db.Clients.AddRange(avery, longNamed);
            await db.SaveChangesAsync();
            db.ClientLifeEvents.Add(LifeEvent(avery.Id, "Anniversary", new DateTime(2010, 11, 30), 30));
            db.ClientLifeEvents.Add(LifeEvent(longNamed.Id, new string('x', 200), new DateTime(2012, 11, 12), 7));
            await db.SaveChangesAsync();
            // What a hand-made post could store while the limit was only the browser's.
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO `ClientLifeEvents` (`ClientId`, `EventType`, `Label`, `EventDate`, `ReminderDaysBefore`, `IsActive`, `CreatedAt`) " +
                "VALUES ({0}, 0, 'Two billion days', '2011-06-15', 2000000000, 1, UTC_TIMESTAMP(6))", avery.Id);
        }

        await RunAsync(testDb, Today);

        await using (var check = testDb.CreateContext())
        {
            var made = await check.ClientFollowUps.AsNoTracking().ToListAsync();
            Assert.Equal(4, made.Count);
            Assert.Contains(made, f => f.Title == "Anniversary: Avery Reid" && f.DueAt == new DateTime(2026, 11, 30));
            Assert.Contains(made, f => f.Title == "Two billion days: Avery Reid" && f.DueAt == new DateTime(2027, 6, 15));

            var cut = Assert.Single(made, f => f.Title.StartsWith("xxxx", StringComparison.Ordinal));
            Assert.Equal(ClientLifeEventReminderJob.FollowUpTitleLength, cut.Title.Length);
            Assert.EndsWith("...", cut.Title);

            var birthday = Assert.Single(made, f => f.Title.Contains("Birthday: FFFF", StringComparison.Ordinal));
            Assert.Equal(ClientLifeEventReminderJob.FollowUpTitleLength, birthday.Title.Length);
            Assert.Equal(new DateTime(2026, 11, 10), birthday.DueAt);

            Assert.All(await check.ClientLifeEvents.AsNoTracking().ToListAsync(), e => Assert.NotNull(e.LastReminderYear));
            Assert.Equal(2026, (await check.Clients.AsNoTracking().SingleAsync(c => c.DateOfBirth != null)).LastBirthdayReminderYear);
        }
    }

    // Whatever else the database may refuse one day: the rest of the batch is made, and the refused row is still due.
    [Fact]
    public async Task A_reminder_the_database_refuses_is_left_for_tomorrow_and_the_rest_are_made()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using (var db = testDb.CreateContext())
        {
            var agentId = await SeedAgentAsync(db);
            var avery = Client(agentId, "Avery", "Reid", null);
            var blair = Client(agentId, "Blair", "Reid", new DateTime(1975, 11, 10));
            var refused = Client(agentId, "Refused", "Reid", new DateTime(1981, 11, 11));
            db.Clients.AddRange(avery, blair, refused);
            await db.SaveChangesAsync();
            db.ClientLifeEvents.Add(LifeEvent(avery.Id, "Anniversary", new DateTime(2010, 11, 30), 30));
            db.ClientLifeEvents.Add(LifeEvent(avery.Id, "Refused by the database", new DateTime(2011, 11, 12), 7));
            db.ClientLifeEvents.Add(LifeEvent(avery.Id, "Policy renewal", new DateTime(2020, 11, 13), 7));
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER trg_683_refuse BEFORE INSERT ON ClientFollowUps FOR EACH ROW " +
                "BEGIN IF NEW.Title LIKE '%Refused%' THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'refused for the test'; END IF; END");
        }

        await RunAsync(testDb, Today);   // threw before, with nothing made

        await using (var check = testDb.CreateContext())
        {
            var made = (await check.ClientFollowUps.AsNoTracking().Select(f => f.Title).ToListAsync()).OrderBy(t => t, StringComparer.Ordinal).ToList();
            Assert.Equal(3, made.Count);
            Assert.Contains("Anniversary: Avery Reid", made);
            Assert.Contains("Policy renewal: Avery Reid", made);
            Assert.Contains(made, t => t.EndsWith("Birthday: Blair Reid", StringComparison.Ordinal));
            Assert.Null((await check.ClientLifeEvents.AsNoTracking().SingleAsync(e => e.Label == "Refused by the database")).LastReminderYear);
            Assert.Null((await check.Clients.AsNoTracking().SingleAsync(c => c.FirstName == "Refused")).LastBirthdayReminderYear);
            Assert.Equal(2026, (await check.ClientLifeEvents.AsNoTracking().SingleAsync(e => e.Label == "Policy renewal")).LastReminderYear);
            await check.Database.ExecuteSqlRawAsync("DROP TRIGGER trg_683_refuse");
        }

        // The next night, with the cause gone: the two left behind are made, and nothing is made twice.
        await RunAsync(testDb, Today.AddDays(1));

        await using (var after = testDb.CreateContext())
        {
            var made = await after.ClientFollowUps.AsNoTracking().Select(f => f.Title).ToListAsync();
            Assert.Equal(5, made.Count);
            Assert.Contains("Refused by the database: Avery Reid", made);
            Assert.Contains(made, t => t.EndsWith("Birthday: Refused Reid", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_forms_limits_are_the_servers_too()
    {
        Assert.Equal(7, ClientLifeEvent.ReminderDays(0));
        Assert.Equal(7, ClientLifeEvent.ReminderDays(-3));
        Assert.Equal(30, ClientLifeEvent.ReminderDays(30));
        Assert.Equal(90, ClientLifeEvent.ReminderDays(90));
        Assert.Equal(90, ClientLifeEvent.ReminderDays(91));
        Assert.Equal(90, ClientLifeEvent.ReminderDays(int.MaxValue));

        // A row already stored with any number is a decision, never an error.
        Assert.True(ClientLifeEventReminderJob.IsDue(new DateTime(2011, 6, 15), int.MaxValue, null, Today));
        Assert.False(ClientLifeEventReminderJob.IsDue(new DateTime(2011, 6, 15), int.MinValue, null, Today));

        Assert.Equal("Anniversary: Avery Reid", ClientLifeEventReminderJob.FollowUpTitle("Anniversary: Avery Reid"));
        Assert.Equal(160, ClientLifeEventReminderJob.FollowUpTitle(new string('a', 160)).Length);
        Assert.Equal(new string('a', 157) + "...", ClientLifeEventReminderJob.FollowUpTitle(new string('a', 161)));
        // Never half of a two-part character (the birthday cake is one) at the cut.
        var cake = char.ConvertFromUtf32(0x1F382);
        var cutAtCake = ClientLifeEventReminderJob.FollowUpTitle(new string('a', 156) + cake + new string('b', 20));
        Assert.Equal(new string('a', 156) + "...", cutAtCake);

        var clients = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\ClientsController.cs"));
        Assert.Contains("if (label.Length > ClientLifeEvent.MaxLabelLength) label = label[..ClientLifeEvent.MaxLabelLength].TrimEnd();", clients);
        Assert.Contains("ReminderDaysBefore = ClientLifeEvent.ReminderDays(reminderDaysBefore)", clients);
        // The form shows the same numbers.
        var form = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Clients\Details.cshtml"));
        Assert.Contains($"name=\"label\" class=\"form-control\" maxlength=\"{ClientLifeEvent.MaxLabelLength}\"", form);
        Assert.Contains($"name=\"reminderDaysBefore\" class=\"form-control\" value=\"{ClientLifeEvent.DefaultReminderDays}\" min=\"1\" max=\"{ClientLifeEvent.MaxReminderDays}\"", form);
    }

    [Fact]
    public async Task A_long_starter_article_no_longer_stops_the_pages_of_every_adviser_of_its_business_type()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await WebsiteTemplateSeeder.SeedAsync(db);
        var (agentId, website) = await SeedAgentWithWebsiteAsync(db);
        // What SuperAdmin's form accepted: the table's own limits (a title of 200, a summary of 500).
        db.WebsiteStarterArticles.AddRange(
            new WebsiteStarterArticle { BusinessType = "All", Title = new string('T', 200), Summary = new string('s', 500), Content = "<p>One.</p>", SortOrder = 1 },
            new WebsiteStarterArticle { BusinessType = "All", Category = new string('C', 130), Title = "Filed " + new string('u', 190), Summary = new string('s', 500), Content = "<p>Two.</p>", SortOrder = 2 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await WebsiteStarterResourcesHelper.EnsureResourcesAsync(db, website, agentId);   // MySQL refused the pages before
        db.ChangeTracker.Clear();

        var pages = await db.WebsitePages.AsNoTracking().Where(p => p.AgentWebsiteId == website.Id).ToListAsync();
        Assert.Equal(4, pages.Count);   // Resources, the article's page, the category's page, the article under it
        Assert.All(pages, p =>
        {
            Assert.True(p.Title.Length <= 160, "title");
            Assert.True(p.NavigationLabel.Length <= 100, "label");
            Assert.True((p.MetaTitle ?? "").Length <= 180, "search title");
            Assert.True((p.MetaDescription ?? "").Length <= 320, "description");
            Assert.True(p.Slug.Length <= 120, "address");
        });
        Assert.Equal(3, pages.Count(p => p.NavigationLabel.Length == 100 && p.NavigationLabel.EndsWith("...", StringComparison.Ordinal)));
        Assert.Equal(pages.Count, pages.Select(p => p.Slug).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        // The articles themselves keep every character.
        var titles = await db.Articles.AsNoTracking().Where(a => a.AgentUserId == agentId).Select(a => a.Title).ToListAsync();
        Assert.Equal(new[] { 196, 200 }, titles.Select(t => t.Length).OrderBy(n => n).ToArray());
    }

    [Fact]
    public void What_is_written_to_a_page_is_cut_to_what_the_page_holds()
    {
        Assert.Equal("", WebsiteStarterResourcesHelper.Fit(null, 10));
        Assert.Equal("abc", WebsiteStarterResourcesHelper.Fit("  abc ", 10));
        Assert.Equal(new string('a', 100), WebsiteStarterResourcesHelper.Fit(new string('a', 100), 100));
        Assert.Equal(new string('a', 97) + "...", WebsiteStarterResourcesHelper.Fit(new string('a', 101), 100));
        Assert.Equal("word...", WebsiteStarterResourcesHelper.Fit("word   and more than fits", 10));   // no space left before the dots
        var cake = char.ConvertFromUtf32(0x1F382);
        Assert.Equal(new string('a', 96) + "...", WebsiteStarterResourcesHelper.Fit(new string('a', 96) + cake + new string('b', 20), 100));

        // The page's columns, as the model has them.
        using var db = new IPRODbContext(new DbContextOptionsBuilder<IPRODbContext>().UseMySql("Server=none;Database=none", new MySqlServerVersion(new Version(8, 0, 44))).Options);
        var page = db.Model.FindEntityType(typeof(WebsitePage))!;
        Assert.Equal(WebsiteStarterResourcesHelper.PageLabelLength, page.FindProperty(nameof(WebsitePage.NavigationLabel))!.GetMaxLength());
        Assert.Equal(WebsiteStarterResourcesHelper.PageTitleLength, page.FindProperty(nameof(WebsitePage.Title))!.GetMaxLength());
        Assert.Equal(WebsiteStarterResourcesHelper.PageMetaTitleLength, page.FindProperty(nameof(WebsitePage.MetaTitle))!.GetMaxLength());
        Assert.Equal(WebsiteStarterResourcesHelper.PageMetaDescriptionLength, page.FindProperty(nameof(WebsitePage.MetaDescription))!.GetMaxLength());
        Assert.Equal(ClientLifeEventReminderJob.FollowUpTitleLength, db.Model.FindEntityType(typeof(ClientFollowUp))!.FindProperty(nameof(ClientFollowUp.Title))!.GetMaxLength());
    }

    [Fact]
    public async Task SuperAdmin_refuses_a_starter_article_longer_than_a_page_holds_and_says_why()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var controller = Prepare(new WebsiteStarterArticlesController(db, new NoAudit()));
        var refused = await controller.Edit(new WebsiteStarterArticle
        {
            BusinessType = "All", Category = new string('C', 101), Title = new string('T', 101), Summary = new string('s', 321), Content = "<p>Words.</p>"
        });

        Assert.IsType<ViewResult>(refused);
        Assert.Equal(3, controller.ModelState.ErrorCount);
        Assert.Contains("Keep the title to 100 characters", controller.ModelState[nameof(WebsiteStarterArticle.Title)]!.Errors.Single().ErrorMessage);
        Assert.Contains("This one has 101.", controller.ModelState[nameof(WebsiteStarterArticle.Title)]!.Errors.Single().ErrorMessage);
        Assert.Contains("Keep the summary to 320 characters", controller.ModelState[nameof(WebsiteStarterArticle.Summary)]!.Errors.Single().ErrorMessage);
        Assert.Contains("Keep the category to 100 characters", controller.ModelState[nameof(WebsiteStarterArticle.Category)]!.Errors.Single().ErrorMessage);
        Assert.Empty(await db.WebsiteStarterArticles.AsNoTracking().ToListAsync());

        var atTheLimit = Prepare(new WebsiteStarterArticlesController(db, new NoAudit()));
        Assert.IsType<RedirectToActionResult>(await atTheLimit.Edit(new WebsiteStarterArticle
        {
            BusinessType = "All", Category = new string('C', 100), Title = new string('T', 100), Summary = new string('s', 320), Content = "<p>Words.</p>"
        }));
        Assert.Single(await db.WebsiteStarterArticles.AsNoTracking().ToListAsync());
    }

    // The library iPro ships must pass the form's new limits, or SuperAdmin could not save an article it opens.
    [Fact]
    public async Task Every_starter_article_iPro_ships_fits_a_page()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await WebsiteTemplateSeeder.SeedAsync(db);
        await WebsiteStarterContentSeeder.SeedAsync(db);
        await WebsiteStarterContentSeeder.SeedNavV2AdditionsAsync(db);
        await WebsiteStarterContentSeeder.SeedGenericEditionAsync(db);
        await WebsiteStarterArticleSeeder.SeedAsync(db);
        db.ChangeTracker.Clear();

        var shipped = await db.WebsiteStarterArticles.AsNoTracking().ToListAsync();
        Assert.True(shipped.Count > 20, $"only {shipped.Count} starter articles were seeded");
        var tooLong = shipped
            .Where(a => a.Title.Length > WebsiteStarterResourcesHelper.PageLabelLength
                        || a.Summary.Length > WebsiteStarterResourcesHelper.PageMetaDescriptionLength
                        || (a.Category ?? "").Length > WebsiteStarterResourcesHelper.PageLabelLength)
            .Select(a => $"{a.BusinessType}: {a.Title} (title {a.Title.Length}, summary {a.Summary.Length})")
            .ToList();
        Assert.True(tooLong.Count == 0, string.Join("\n", tooLong));
    }

    // ---- harness ------------------------------------------------------------------------------

    private static async Task RunAsync(TestDatabase testDb, DateTime today)
    {
        await using var db = testDb.CreateContext();
        await new ClientLifeEventReminderJob(db, new EveryoneEntitled(), NullLogger<ClientLifeEventReminderJob>.Instance).RunForDayAsync(today);
    }

    private static Client Client(int agentId, string firstName, string lastName, DateTime? birth) => new()
    {
        AgentUserId = agentId,
        FirstName = firstName,
        LastName = lastName,
        Email = $"{Guid.NewGuid():N}@example.test",
        DateOfBirth = birth
    };

    private static ClientLifeEvent LifeEvent(int clientId, string label, DateTime date, int daysBefore) => new()
    {
        ClientId = clientId,
        Label = label,
        EventDate = date,
        ReminderDaysBefore = daysBefore,
        IsActive = true
    };

    private static async Task<int> SeedAgentAsync(IPRODbContext db, string businessType = "Generic")
    {
        var rule = new BillingRule { PackageName = ($"T683-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t683-{Guid.NewGuid():N}")[..20],
            Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Morgan", LastName = "Reid",
            DomainName = ($"t683-{Guid.NewGuid():N}")[..24],
            Country = "Canada", Province = "Ontario",
            PackageId = rule.Id, IsActive = true, BusinessType = businessType
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static async Task<(int AgentId, AgentWebsite Website)> SeedAgentWithWebsiteAsync(IPRODbContext db)
    {
        var agentId = await SeedAgentAsync(db, StarterBusinessTypes.Generic);
        var template = await db.WebsiteTemplates.FirstAsync(t => t.TemplateKey == WebsiteTemplateSeeder.DefaultTemplateKey);
        var website = new AgentWebsite { AgentUserId = agentId, TemplateId = template.Id, SiteTitle = "Reid Studio" };
        db.AgentWebsites.Add(website);
        await db.SaveChangesAsync();
        return (agentId, website);
    }

    private static T Prepare<T>(T controller) where T : Controller
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Name, "admin683") }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, new NoTempData());
        return controller;
    }

    private sealed class EveryoneEntitled : IPackageEntitlementService
    {
        public Task<PackageFeatureAccess> GetAccessAsync(int agentId, string featureCode) => throw new NotSupportedException();
        public Task<bool> HasAccessAsync(int agentId, string featureCode) => throw new NotSupportedException();
        public Task<Dictionary<int, bool>> HasAccessBulkAsync(IEnumerable<int> agentIds, string featureCode) =>
            Task.FromResult(agentIds.Distinct().ToDictionary(id => id, _ => true));
        public Task<bool> IsAccessGatedAsync(int agentId) => Task.FromResult(false);
    }

    private sealed class NoAudit : IAdminAuditLogService
    {
        public Task LogAsync(int adminUserId, string adminUsername, string action, string details) => Task.CompletedTask;
    }

    private sealed class NoTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "IPRO.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }
}
