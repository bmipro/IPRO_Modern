using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using IPRO.Admin.Controllers;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace IPRO.IntegrationTests;

// 631 (2026-11-09), 588's follow-up for SuperAdmin. Seven "new" forms stored the whole posted record: the
// MVC binder fills whatever a request names, so a crafted post (never the form itself) could set what only
// the server should: a promotion or trial code's redemption count and cached PayPal plans, a package
// named around a code (a new BillingRule row), and websites named around a new website template (a site in
// any adviser's account, with a custom domain that never went through the domain check). Each form now
// builds the new record from the fields it shows. Editing already copied field by field.
public class PostedFields631Tests
{
    [Fact]
    public async Task A_new_promotion_code_is_its_typed_fields_and_nothing_the_request_names_around_it()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var packages = await db.BillingRules.CountAsync();

        var posted = await BindAsync<PromotionCode>(new()
        {
            ["Code"] = " save631 ", ["Description"] = "Launch offer", ["IsActive"] = "true", ["ExpiresAt"] = "2027-01-31",
            ["MaxRedemptions"] = "25", ["SetupFeeDiscountType"] = nameof(PromoDiscountType.PercentOff), ["SetupFeeDiscountValue"] = "100",

            ["Id"] = "0", ["RedemptionCount"] = "-500", ["PayPalPromoPlanIdMonthly"] = "P-PLANTED-M", ["PayPalPromoPlanIdAnnual"] = "P-PLANTED-A",
            ["CreatedAt"] = "2001-01-01T00:00:00",
            ["RestrictedBillingRule.PackageName"] = "Planted 631", ["RestrictedBillingRule.MonthlyPrice"] = "1",
        });
        Assert.Equal("P-PLANTED-M", posted.PayPalPromoPlanIdMonthly);   // the binder fills whatever is named
        Assert.NotNull(posted.RestrictedBillingRule);

        Assert.IsType<RedirectToActionResult>(await Prepare(new PromotionCodesController(new UnitOfWork(db), new NoAudit(), db)).Edit(posted, "monthly"));

        await using var check = testDb.CreateContext();
        Assert.Equal(packages, await check.BillingRules.CountAsync());
        var row = await check.PromotionCodes.AsNoTracking().SingleAsync(p => p.Code == "SAVE631");
        Assert.Equal(("Launch offer", true, new DateTime(2027, 1, 31), (int?)25), (row.Description, row.IsActive, row.ExpiresAt, row.MaxRedemptions));
        Assert.Equal((PromoDiscountType.PercentOff, 100m), (row.SetupFeeDiscountType, row.SetupFeeDiscountValue));
        Assert.Null(row.RestrictedBillingRuleId);
        Assert.Equal(0, row.RedemptionCount);
        Assert.Equal(string.Empty, row.PayPalPromoPlanIdMonthly);
        Assert.Equal(string.Empty, row.PayPalPromoPlanIdAnnual);
        Assert.True(row.CreatedAt > DateTime.UtcNow.AddMinutes(-10), $"CreatedAt {row.CreatedAt:o} came from the request");
        // 508's billing-period row still lands on the new code.
        Assert.Equal(row.Id, (await check.PromotionCodePeriodLimits.AsNoTracking().SingleAsync()).PromotionCodeId);
    }

    [Fact]
    public async Task A_new_trial_invite_code_is_its_typed_fields_and_unlocks_only_the_package_chosen()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var trial = new BillingRule { PackageName = "Trial 631", IsTrialPackage = true, IsActive = true };
        db.BillingRules.Add(trial);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var packages = await db.BillingRules.CountAsync();

        var posted = await BindAsync<TrialInviteCode>(new()
        {
            ["Code"] = "trial631", ["Description"] = "Pilot advisers", ["BillingRuleId"] = trial.Id.ToString(CultureInfo.InvariantCulture),
            ["IsActive"] = "true", ["MaxRedemptions"] = "3",

            ["RedemptionCount"] = "-1000", ["CreatedAt"] = "2001-01-01T00:00:00",
            ["BillingRule.PackageName"] = "Planted 631", ["BillingRule.IsTrialPackage"] = "true", ["BillingRule.TrialDays"] = "3650",
        });
        Assert.NotNull(posted.BillingRule);

        Assert.IsType<RedirectToActionResult>(await Prepare(new TrialInviteCodesController(new UnitOfWork(db), new NoAudit(), new ConfigurationBuilder().Build())).Edit(posted));

        await using var check = testDb.CreateContext();
        Assert.Equal(packages, await check.BillingRules.CountAsync());
        var row = await check.TrialInviteCodes.AsNoTracking().SingleAsync(c => c.Code == "TRIAL631");
        Assert.Equal((trial.Id, "Pilot advisers", true, (int?)3), (row.BillingRuleId, row.Description, row.IsActive, row.MaxRedemptions));
        Assert.Equal(0, row.RedemptionCount);
        Assert.True(row.CreatedAt > DateTime.UtcNow.AddMinutes(-10), $"CreatedAt {row.CreatedAt:o} came from the request");
    }

    [Fact]
    public async Task A_new_website_template_cannot_add_a_website_to_an_advisers_account()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "victim631", "victim631@example.com");
        await using var db = testDb.CreateContext();
        var sites = await db.AgentWebsites.CountAsync();

        var posted = await BindAsync<WebsiteTemplate>(new()
        {
            ["TemplateKey"] = "Plain 631", ["Name"] = "Plain 631", ["Description"] = "A test template", ["BusinessType"] = "",
            ["LayoutJson"] = "{\"renderer\":\"modern-professional\"}", ["IsActive"] = "true",

            ["CreatedAt"] = "2001-01-01T00:00:00",
            ["Websites[0].AgentUserId"] = agentId.ToString(CultureInfo.InvariantCulture),
            ["Websites[0].CustomDomain"] = "www.unchecked-631.example", ["Websites[0].SiteTitle"] = "Planted", ["Websites[0].IsPublished"] = "true",
        });
        Assert.Single(posted.Websites);   // the binder fills whatever is named

        var controller = Prepare(new WebsiteTemplatesController(new UnitOfWork(db), null!, NullLogger<WebsiteTemplatesController>.Instance,
            new ConfigurationBuilder().Build(), new NoAudit()));
        Assert.IsType<RedirectToActionResult>(await controller.Edit(posted));

        await using var check = testDb.CreateContext();
        Assert.Equal(sites, await check.AgentWebsites.CountAsync());
        Assert.False(await check.AgentWebsites.AnyAsync(w => w.CustomDomain == "www.unchecked-631.example"));
        var row = await check.WebsiteTemplates.AsNoTracking().SingleAsync(t => t.TemplateKey == "plain-631");
        Assert.Equal(("Plain 631", "A test template", true, false), (row.Name, row.Description, row.IsActive, row.IsDefault));
        Assert.StartsWith("{", row.LayoutJson);
        Assert.True(row.CreatedAt > DateTime.UtcNow.AddMinutes(-10), $"CreatedAt {row.CreatedAt:o} came from the request");
    }

    [Fact]
    public async Task The_four_content_libraries_keep_only_what_their_forms_show()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var old = "2001-01-01T00:00:00";

        var letter = await BindAsync<ELetterTemplate>(new()
        {
            ["Name"] = "Year end 631", ["Description"] = "d", ["Subject"] = "Your year end", ["Body"] = "Dear client,", ["IsActive"] = "true", ["SortOrder"] = "7",
            ["CreatedAt"] = old, ["UpdatedAt"] = old,
        });
        Assert.IsType<RedirectToActionResult>(await Prepare(new ELetterTemplatesController(db, new NoAudit())).Edit(letter));

        var newsletter = await BindAsync<NewsLetterTemplate>(new()
        {
            ["Name"] = "Spring 631", ["Description"] = "d", ["Subject"] = "Spring news", ["HtmlBody"] = "<p>Hello</p>", ["TextBody"] = "Hello", ["IsActive"] = "true", ["SortOrder"] = "3",
            ["CreatedAt"] = old,
        });
        Assert.IsType<RedirectToActionResult>(await Prepare(new NewsletterTemplatesController(new UnitOfWork(db), new NoAudit())).Edit(newsletter));

        var article = await BindAsync<WebsiteStarterArticle>(new()
        {
            ["BusinessType"] = "Accountants", ["Category"] = "Tax", ["Title"] = "RRSP 631", ["Summary"] = "s", ["Content"] = "<p>Contribute.</p>",
            ["ImageUrl"] = "https://images.example.test/rrsp.jpg", ["IsActive"] = "true", ["SortOrder"] = "2",
            ["CreatedAt"] = old, ["UpdatedAt"] = old,
        });
        Assert.IsType<RedirectToActionResult>(await Prepare(new WebsiteStarterArticlesController(db, new NoAudit())).Edit(article));

        var card = await BindAsync<ECardDesign>(new()
        {
            ["Key"] = "", ["Occasion"] = "Thanks", ["Name"] = "Panel 631", ["Kind"] = ECardArtKinds.Generated, ["Accent"] = "#1e3a8a", ["Emoji"] = "*",
            ["IsDark"] = "true", ["IsActive"] = "true", ["SortOrder"] = "5", ["DefaultHeaderText"] = "Thank you", ["DefaultMessage"] = "With thanks.",
            ["ImageUrl"] = "https://elsewhere.example/planted.jpg", ["Width"] = "9999", ["Height"] = "9999",
            ["CreatedAt"] = old, ["UpdatedAt"] = old,
        });
        Assert.IsType<RedirectToActionResult>(await Prepare(NewECardController(db)).Edit(card, null));

        await using var check = testDb.CreateContext();
        var since = DateTime.UtcNow.AddMinutes(-10);
        var l = await check.ELetterTemplates.AsNoTracking().SingleAsync(t => t.Name == "Year end 631");
        Assert.Equal(("year-end-631", "Your year end", "Dear client,", 7), (l.Key, l.Subject, l.Body, l.SortOrder));
        Assert.True(l.CreatedAt > since && l.UpdatedAt > since);
        var n = await check.NewsLetterTemplates.AsNoTracking().SingleAsync(t => t.Name == "Spring 631");
        Assert.Equal(("Spring news", "Hello", 3), (n.Subject, n.TextBody, n.SortOrder));
        Assert.Contains("Hello", n.HtmlBody);
        Assert.True(n.CreatedAt > since);
        var a = await check.WebsiteStarterArticles.AsNoTracking().SingleAsync(x => x.Title == "RRSP 631");
        Assert.Equal(("Accountants", "Tax", "https://images.example.test/rrsp.jpg", 2), (a.BusinessType, a.Category, a.ImageUrl, a.SortOrder));
        Assert.True(a.CreatedAt > since && a.UpdatedAt > since);
        var c = await check.ECardDesigns.AsNoTracking().SingleAsync(d => d.Name == "Panel 631");
        Assert.Equal(("thanks-panel-631", "#1e3a8a", "With thanks."), (c.Key, c.Accent, c.DefaultMessage));
        // A generated card has no picture: the posted address and size are not kept.
        Assert.Equal((string.Empty, 0, 0), (c.ImageUrl, c.Width, c.Height));
        Assert.True(c.CreatedAt > since && c.UpdatedAt > since);
    }

    [Fact]
    public async Task A_new_picture_card_needs_its_picture_uploaded_not_an_address_in_the_request()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var card = await BindAsync<ECardDesign>(new()
        {
            ["Occasion"] = "Birthday", ["Name"] = "Elsewhere 631", ["Kind"] = ECardArtKinds.Image, ["IsActive"] = "true",
            ["ImageUrl"] = "https://elsewhere.example/planted.jpg", ["Width"] = "800", ["Height"] = "600",
        });

        var controller = Prepare(NewECardController(db));
        var result = Assert.IsType<ViewResult>(await controller.Edit(card, null));
        Assert.True(controller.ModelState.ContainsKey("artwork"));
        Assert.Same(card, result.Model);

        await using var check = testDb.CreateContext();
        Assert.False(await check.ECardDesigns.AnyAsync(d => d.Name == "Elsewhere 631"));
    }

    // ---------------------------------------------------------------- every SuperAdmin action that takes a stored record

    // 588's tripwire for the portal, carried over: each action below was read and either builds a new record
    // from the typed fields or copies them onto one it loaded. A new action taking a stored record (or a view
    // model carrying one, two levels deep) fails here until it is read the same way and added.
    private static readonly string[] Reviewed =
    {
        "AdminUsersController.Create(AdminUser)",
        "ECardDesignsController.Edit(ECardDesign)",
        "ELetterTemplatesController.Edit(ELetterTemplate)",
        "NewsletterTemplatesController.Edit(NewsLetterTemplate)",
        "PromotionCodesController.Edit(PromotionCode)",
        "TrialInviteCodesController.Edit(TrialInviteCode)",
        "WebsiteStarterArticlesController.Edit(WebsiteStarterArticle)",
        "WebsiteTemplatesController.Edit(WebsiteTemplate)",
    };

    [Fact]
    public async Task Every_SuperAdmin_action_that_takes_a_stored_record_from_the_request_has_been_read_for_this()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var stored = db.Model.GetEntityTypes().Select(e => e.ClrType).ToHashSet();

        var found = new List<string>();
        foreach (var controller in typeof(PromotionCodesController).Assembly.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract))
        {
            foreach (var action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .Where(m => !m.IsSpecialName && m.GetCustomAttribute<NonActionAttribute>() == null))
            {
                foreach (var parameter in action.GetParameters())
                {
                    var record = StoredRecordIn(parameter.ParameterType, stored, depth: 0);
                    if (record != null) found.Add($"{controller.Name}.{action.Name}({record.Name})");
                }
            }
        }

        Assert.Equal(Reviewed, found.Distinct().OrderBy(f => f, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void No_SuperAdmin_action_that_takes_a_stored_record_adds_the_posted_one_itself()
    {
        var folder = Path.Combine(Lock583.RepoRoot(), "src", "IPRO.Admin", "Controllers");
        foreach (var entry in Reviewed)
        {
            var parts = Regex.Match(entry, @"^(\w+)\.(\w+)\((\w+)\)$").Groups;
            var source = File.ReadAllText(Path.Combine(folder, parts[1].Value + ".cs"));
            var signatures = Regex.Matches(source, $@"public async Task<IActionResult> {parts[2].Value}\({parts[3].Value} (\w+)");
            Assert.True(signatures.Count > 0, $"{entry} not found");
            foreach (Match signature in signatures)
            {
                var end = source.IndexOf("\n    public ", signature.Index + 1, StringComparison.Ordinal);
                var body = source[signature.Index..(end < 0 ? source.Length : end)];
                var posted = signature.Groups[1].Value;
                Assert.False(Regex.IsMatch(body, $@"(CreateAsync|AddAsync|AddRange|\.Add|\.Update|UpdateAsync|Attach)\({posted}\)"),
                    $"{entry} stores the posted {posted} itself; build the new record from the typed fields.");
            }
        }
    }

    // ---------------------------------------------------------------- harness

    private static ECardDesignsController NewECardController(IPRODbContext db) =>
        new(db, new ServiceCollection().BuildServiceProvider(), new NoAudit(), new ConfigurationBuilder().AddInMemoryCollection().Build());

    private static T Prepare<T>(T controller) where T : Controller
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Name, "admin631") }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, new NoTempData());
        return controller;
    }

    // A stored record, or a view model carrying one, that a request can fill (as in PostedFields588Tests).
    private static Type? StoredRecordIn(Type type, HashSet<Type> stored, int depth)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsArray) type = type.GetElementType()!;
        else if (type.IsGenericType && type.GetGenericArguments().Length == 1 && typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
            type = type.GetGenericArguments()[0];
        if (stored.Contains(type)) return type;
        if (depth >= 2 || type.IsPrimitive || type.IsEnum || type == typeof(string) || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true
            || type.Namespace?.StartsWith("Microsoft", StringComparison.Ordinal) == true)
            return null;
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite && p.GetSetMethod() != null))
        {
            var inner = StoredRecordIn(property.PropertyType, stored, depth + 1);
            if (inner != null) return inner;
        }
        return null;
    }

    // The request binder exactly as an action's parameter gets it (as in PostedFields588Tests).
    private static async Task<T> BindAsync<T>(Dictionary<string, StringValues> form) where T : class
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllersWithViews();
        await using var provider = services.BuildServiceProvider();
        var metadata = provider.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(typeof(T));
        var binder = provider.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext { Metadata = metadata });
        var action = new ActionContext(new DefaultHttpContext { RequestServices = provider }, new RouteData(), new ActionDescriptor());
        var values = new FormValueProvider(BindingSource.Form, new FormCollection(form), CultureInfo.InvariantCulture);
        var parameter = new ParameterDescriptor { Name = "model", ParameterType = typeof(T) };
        var result = await provider.GetRequiredService<ParameterBinder>().BindModelAsync(action, binder, values, parameter, metadata, value: null);
        Assert.True(result.IsModelSet);
        return (T)result.Model!;
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
}
