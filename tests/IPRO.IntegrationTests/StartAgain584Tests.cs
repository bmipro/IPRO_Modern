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
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IPRO.IntegrationTests;

// 584: SuperAdmin's "Start the website again". Every page of an adviser's website goes and the
// starter pack of the adviser's CURRENT business type comes in its place at once, written by sign-up's
// own provisioning (moved from IPRO.Web to IPRO.DataAccess for it). The site as it was is kept the way
// a Template Builder package keeps it, and "Put the previous pages back" undoes it.
public class StartAgain584Tests
{
    private const string Insurance = "Insurance / Financial";
    private const string Mortgage = "Mortgage";

    // ---------------- the full starter set, for every business type ----------------

    [Theory]
    [InlineData("Accountants", Insurance)]
    [InlineData(Insurance, Mortgage)]
    [InlineData(Mortgage, Insurance)]
    [InlineData("Generic", Mortgage)]
    public async Task The_site_gets_the_full_starter_set_a_new_sign_up_of_that_business_type_gets(string to, string from)
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedStarterContentAsync(db);
        var rule = await PackageAsync(db);
        var demo = await SignUpAsync(db, from, rule);
        var reference = await SignUpAsync(db, to, rule);
        await SetBusinessTypeAsync(db, demo.AgentId, to);

        var result = await StartAgainAsync(db, demo.AgentId);
        Assert.True(result.Ok, string.Join(" ", result.Problems));
        db.ChangeTracker.Clear();

        var expected = await ShapeAsync(db, reference.AgentId, reference.SiteId);
        var actual = await ShapeAsync(db, demo.AgentId, demo.SiteId);
        Assert.NotEmpty(expected);
        Assert.Equal(expected, actual);

        // Nothing waits for the adviser's next visit: Request Meeting, Resources and the home page are there.
        var pages = await db.WebsitePages.AsNoTracking().Where(p => p.AgentWebsiteId == demo.SiteId).Include(p => p.Blocks).ToListAsync();
        Assert.Single(pages, p => p.IsHomePage);
        Assert.Contains(pages, p => p.Slug == "resources");
        var meeting = pages.SingleOrDefault(p => p.Slug == "request-meeting");
        if (meeting != null)
        {
            // The business type's own meeting form, as a form of the adviser's.
            var formId = WebsiteFormSettings.FromJson(meeting.Blocks.Single(b => b.BlockType == WebsiteBlockTypes.Form).SettingsJson).WebsiteFormId;
            var form = await db.WebsiteForms.AsNoTracking().SingleAsync(f => f.Id == formId);
            Assert.Equal(demo.AgentId, form.AgentUserId);
            var template = (await db.WebsiteStarterForms.AsNoTracking()
                    .Where(f => f.IsActive && f.Title == WebsiteStarterFormSeeder.MeetingFormTitle && (f.BusinessType == to || f.BusinessType == "All")).ToListAsync())
                .OrderByDescending(f => f.BusinessType == to).First();
            Assert.Equal(
                await db.WebsiteStarterFormFields.AsNoTracking().Where(f => f.WebsiteStarterFormId == template.Id).OrderBy(f => f.SortOrder).Select(f => f.Label).ToListAsync(),
                await db.WebsiteFormFields.AsNoTracking().Where(f => f.WebsiteFormId == form.Id).OrderBy(f => f.SortOrder).Select(f => f.Label).ToListAsync());
        }
    }

    // ---------------- nothing outside the pages is touched ----------------

    [Fact]
    public async Task Nothing_outside_the_websites_pages_changes()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedStarterContentAsync(db);
        var demo = await SignUpAsync(db, Insurance, await PackageAsync(db));
        await DressSiteAsync(db, demo);
        await SetBusinessTypeAsync(db, demo.AgentId, Mortgage);
        db.ChangeTracker.Clear();

        var siteBefore = SiteText(await db.AgentWebsites.AsNoTracking().SingleAsync(w => w.Id == demo.SiteId));
        var agentBefore = await db.AgentUsers.AsNoTracking().SingleAsync(a => a.Id == demo.AgentId);
        var formsBefore = await FormsTextAsync(db, demo.AgentId);
        var articlesBefore = await db.Articles.AsNoTracking().Where(a => a.AgentUserId == demo.AgentId).OrderBy(a => a.Id)
            .Select(a => $"{a.Id}|{a.Title}|{a.Summary}|{a.Content}|{a.IsPublished}|{a.PublishedAt}").ToListAsync();
        var media = await db.WebsiteMediaAssets.AsNoTracking().Where(m => m.AgentWebsiteId == demo.SiteId).Select(m => m.BlobUrl).ToListAsync();
        var domains = await db.AgentDomains.AsNoTracking().Where(d => d.AgentWebsiteId == demo.SiteId).Select(d => d.DomainName).ToListAsync();
        var clients = await db.Clients.AsNoTracking().Where(c => c.AgentUserId == demo.AgentId).Select(c => c.Email).ToListAsync();
        var leads = await db.WebsiteLeads.AsNoTracking().Where(l => l.AgentUserId == demo.AgentId).Select(l => $"{l.Id}|{l.Email}|{l.SourcePage}").ToListAsync();

        var result = await StartAgainAsync(db, demo.AgentId, unpublish: false);
        Assert.True(result.Ok, string.Join(" ", result.Problems));
        db.ChangeTracker.Clear();

        Assert.Equal(siteBefore, SiteText(await db.AgentWebsites.AsNoTracking().SingleAsync(w => w.Id == demo.SiteId)));
        var agentAfter = await db.AgentUsers.AsNoTracking().SingleAsync(a => a.Id == demo.AgentId);
        Assert.Equal((agentBefore.UserName, agentBefore.BusinessType, agentBefore.PackageId, agentBefore.IsActive), (agentAfter.UserName, agentAfter.BusinessType, agentAfter.PackageId, agentAfter.IsActive));
        // Every form stays as it was; the mortgage meeting form is added beside them.
        var formsAfter = await FormsTextAsync(db, demo.AgentId);
        Assert.All(formsBefore, f => Assert.Contains(f, formsAfter));
        Assert.Equal(formsBefore.Count + result.FormsAdded, formsAfter.Count);
        Assert.Equal(1, result.FormsAdded);
        // The adviser's articles are as they were (the box was not ticked); the pack's new ones are added.
        var articlesAfter = await db.Articles.AsNoTracking().Where(a => a.AgentUserId == demo.AgentId).OrderBy(a => a.Id)
            .Select(a => $"{a.Id}|{a.Title}|{a.Summary}|{a.Content}|{a.IsPublished}|{a.PublishedAt}").ToListAsync();
        Assert.Equal(articlesBefore, articlesAfter.Take(articlesBefore.Count));
        Assert.Equal(articlesBefore.Count + result.ArticlesAdded, articlesAfter.Count);
        Assert.Equal(media, await db.WebsiteMediaAssets.AsNoTracking().Where(m => m.AgentWebsiteId == demo.SiteId).Select(m => m.BlobUrl).ToListAsync());
        Assert.Equal(domains, await db.AgentDomains.AsNoTracking().Where(d => d.AgentWebsiteId == demo.SiteId).Select(d => d.DomainName).ToListAsync());
        Assert.Equal(clients, await db.Clients.AsNoTracking().Where(c => c.AgentUserId == demo.AgentId).Select(c => c.Email).ToListAsync());
        Assert.Equal(leads, await db.WebsiteLeads.AsNoTracking().Where(l => l.AgentUserId == demo.AgentId).Select(l => $"{l.Id}|{l.Email}|{l.SourcePage}").ToListAsync());
    }

    // ---------------- the put-back ----------------

    [Fact]
    public async Task Putting_the_previous_pages_back_restores_them_exactly_and_leaves_the_logo_and_design()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedStarterContentAsync(db);
        var demo = await SignUpAsync(db, Insurance, await PackageAsync(db));
        await DressSiteAsync(db, demo);
        await SetBusinessTypeAsync(db, demo.AgentId, Mortgage);
        db.ChangeTracker.Clear();
        var before = await PagesTextAsync(db, demo.SiteId);
        var publishedBefore = await PublishedArticlesAsync(db, demo.AgentId);

        var audit = new RecordingAudit();
        var logger = new RecordingLogger();
        var plan = (await SiteStartAgainService.PlanAsync(db, demo.AgentId))!;
        Assert.NotEmpty(plan.OtherPackArticles);
        var post = await Controller(db, audit, logger).StartAgain(demo.AgentId, demo.UserName, unpublishOtherPackArticles: true, plan: plan.Fingerprint);
        Assert.IsType<RedirectToActionResult>(post);
        db.ChangeTracker.Clear();
        Assert.NotEqual(before, await PagesTextAsync(db, demo.SiteId));
        Assert.Contains(audit.Entries, e => e.Action == "AgentWebsiteStartedAgain" && e.Details.Contains("removed") && e.Details.Contains("unpublished"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("started agent") && e.Message.Contains("Mortgage"));

        // The adviser changes the logo and the design afterwards; the put-back leaves both.
        var otherTemplate = new WebsiteTemplate { TemplateKey = $"t584-{Guid.NewGuid():N}"[..20], Name = "Other", IsActive = true };
        db.Add(otherTemplate);
        await db.SaveChangesAsync();
        await db.AgentWebsites.Where(w => w.Id == demo.SiteId).ExecuteUpdateAsync(u => u
            .SetProperty(w => w.LogoUrl, "https://blob.example.test/agent-logos/new-logo.png")
            .SetProperty(w => w.TemplateId, otherTemplate.Id));
        db.ChangeTracker.Clear();

        var startedAgain = await SiteStartAgainService.PutBackableAsync(db, demo.SiteId);
        Assert.NotNull(startedAgain);
        Assert.Equal(before.Count(l => !l.StartsWith("   ", StringComparison.Ordinal)), await SiteStartAgainService.PagesKeptAsync(db, startedAgain!.Id));
        var back = await Controller(db, audit, logger).PutBackStartAgain(demo.AgentId, startedAgain.Id);
        Assert.IsType<RedirectToActionResult>(back);
        db.ChangeTracker.Clear();

        Assert.Equal(before, await PagesTextAsync(db, demo.SiteId));
        var site = await db.AgentWebsites.AsNoTracking().SingleAsync(w => w.Id == demo.SiteId);
        Assert.Equal("https://blob.example.test/agent-logos/new-logo.png", site.LogoUrl);
        Assert.Equal(otherTemplate.Id, site.TemplateId);
        // The insurance articles it took off are back on the website; the mortgage ones it added are off it.
        Assert.Equal(publishedBefore, await PublishedArticlesAsync(db, demo.AgentId));
        Assert.Null(await SiteStartAgainService.PutBackableAsync(db, demo.SiteId));
        Assert.Equal(WebsiteBuilderImportStatus.Restored, (await db.WebsiteBuilderImports.AsNoTracking().SingleAsync(i => i.Id == startedAgain.Id)).Status);
        Assert.Contains(audit.Entries, e => e.Action == "AgentWebsiteStartAgainPutBack");
    }

    [Fact]
    public async Task Two_start_agains_go_back_one_at_a_time_and_the_old_pages_pictures_stay_protected()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedStarterContentAsync(db);
        var demo = await SignUpAsync(db, Insurance, await PackageAsync(db));
        await DressSiteAsync(db, demo);
        db.ChangeTracker.Clear();
        var original = await PagesTextAsync(db, demo.SiteId);

        await SetBusinessTypeAsync(db, demo.AgentId, Mortgage);
        Assert.True((await StartAgainAsync(db, demo.AgentId)).Ok);
        db.ChangeTracker.Clear();
        // A picture only the old pages used is still "in use" while their kept copy can come back.
        Assert.True(await BlobReferences.IsReferencedAsync(db, OwnPicture));
        var mortgage = await PagesTextAsync(db, demo.SiteId);

        await SetBusinessTypeAsync(db, demo.AgentId, "Accountants");
        Assert.True((await StartAgainAsync(db, demo.AgentId)).Ok);
        db.ChangeTracker.Clear();

        var newest = (await SiteStartAgainService.PutBackableAsync(db, demo.SiteId))!;
        Assert.True((await BuilderImportService.RestoreAsync(db, await db.WebsiteBuilderImports.SingleAsync(i => i.Id == newest.Id))).Ok);
        db.ChangeTracker.Clear();
        Assert.Equal(mortgage, await PagesTextAsync(db, demo.SiteId));

        var first = (await SiteStartAgainService.PutBackableAsync(db, demo.SiteId))!;
        Assert.NotEqual(newest.Id, first.Id);
        Assert.True((await BuilderImportService.RestoreAsync(db, await db.WebsiteBuilderImports.SingleAsync(i => i.Id == first.Id))).Ok);
        db.ChangeTracker.Clear();
        Assert.Equal(original, await PagesTextAsync(db, demo.SiteId));
        Assert.Null(await SiteStartAgainService.PutBackableAsync(db, demo.SiteId));
    }

    // ---------------- who may, and the confirmations ----------------

    [Fact]
    public async Task A_support_admin_is_refused_and_nothing_changes()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedStarterContentAsync(db);
        var demo = await SignUpAsync(db, Insurance, await PackageAsync(db));
        await SetBusinessTypeAsync(db, demo.AgentId, Mortgage);
        db.ChangeTracker.Clear();
        var before = await PagesTextAsync(db, demo.SiteId);
        var plan = (await SiteStartAgainService.PlanAsync(db, demo.AgentId))!;

        var support = Controller(db, new RecordingAudit(), new RecordingLogger(), AdminRoles.Support);
        Assert.IsType<ForbidResult>(await support.StartAgain(demo.AgentId));
        Assert.IsType<ForbidResult>(await support.StartAgain(demo.AgentId, demo.UserName, true, plan.Fingerprint));
        db.ChangeTracker.Clear();
        Assert.Equal(before, await PagesTextAsync(db, demo.SiteId));
        Assert.False(await db.WebsiteBuilderImports.AnyAsync(i => i.AgentWebsiteId == demo.SiteId));

        // And the put-back, once a SuperAdmin has started the site again.
        Assert.True((await StartAgainAsync(db, demo.AgentId)).Ok);
        db.ChangeTracker.Clear();
        var started = (await SiteStartAgainService.PutBackableAsync(db, demo.SiteId))!;
        var after = await PagesTextAsync(db, demo.SiteId);
        Assert.IsType<ForbidResult>(await support.PutBackStartAgain(demo.AgentId, started.Id));
        db.ChangeTracker.Clear();
        Assert.Equal(after, await PagesTextAsync(db, demo.SiteId));

        // The policy on each action (the server's own gate), and the button support admins see is disabled.
        foreach (var method in typeof(AgentsController).GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name is "StartAgain" or "PutBackStartAgain"))
        {
            Assert.Contains("SuperAdmin", method.GetCustomAttributes<AuthorizeAttribute>().Select(a => a.Policy));
        }
        var view = File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Views\Agents\Details.cshtml"));
        foreach (var label in new[] { "Start the website again</a>", "Put the previous pages back</button>" })
        {
            var at = view.IndexOf(label, StringComparison.Ordinal);
            Assert.True(at > 0, label);
            var window = view[Math.Max(0, at - 1200)..(at + 600)];
            Assert.Contains("AdminRoles.SuperAdmin", window);
            Assert.Contains("disabled title=\"SuperAdmin only", window);
        }
    }

    [Fact]
    public async Task A_wrong_username_is_refused_and_the_screen_says_why()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedStarterContentAsync(db);
        var demo = await SignUpAsync(db, Insurance, await PackageAsync(db));
        await SetBusinessTypeAsync(db, demo.AgentId, Mortgage);
        db.ChangeTracker.Clear();
        var before = await PagesTextAsync(db, demo.SiteId);
        var plan = (await SiteStartAgainService.PlanAsync(db, demo.AgentId))!;
        var audit = new RecordingAudit();

        foreach (var typed in new[] { "", "someone-else", demo.UserName.ToUpperInvariant(), demo.UserName[..^1] })
        {
            var result = await Controller(db, audit, new RecordingLogger()).StartAgain(demo.AgentId, typed, true, plan.Fingerprint);
            var shown = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<AgentStartAgainViewModel>(shown.Model);
            Assert.Contains(model.Problems, p => p.Contains("The username typed is not", StringComparison.Ordinal) && p.Contains("Nothing was changed", StringComparison.Ordinal));
            db.ChangeTracker.Clear();
            Assert.Equal(before, await PagesTextAsync(db, demo.SiteId));
        }
        Assert.False(await db.WebsiteBuilderImports.AnyAsync(i => i.AgentWebsiteId == demo.SiteId));
        Assert.DoesNotContain(audit.Entries, e => e.Action == "AgentWebsiteStartedAgain");
        Assert.Contains(audit.Entries, e => e.Action == "AgentWebsiteNotStartedAgain");

        // Typed as shown (spaces around it aside), it goes.
        Assert.IsType<RedirectToActionResult>(await Controller(db, audit, new RecordingLogger()).StartAgain(demo.AgentId, $" {demo.UserName} ", true, plan.Fingerprint));
    }

    [Fact]
    public async Task A_site_changed_after_the_screen_opened_is_not_written()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedStarterContentAsync(db);
        var demo = await SignUpAsync(db, Insurance, await PackageAsync(db));
        await SetBusinessTypeAsync(db, demo.AgentId, Mortgage);
        db.ChangeTracker.Clear();
        var plan = (await SiteStartAgainService.PlanAsync(db, demo.AgentId))!;

        // The adviser adds a page meanwhile: the screen no longer says everything that would go.
        db.WebsitePages.Add(new WebsitePage { AgentWebsiteId = demo.SiteId, Title = "Team", Slug = "team", NavigationLabel = "Team", SortOrder = 50 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var before = await PagesTextAsync(db, demo.SiteId);

        var result = await SiteStartAgainService.ApplyAsync(db, demo.AgentId, demo.UserName, true, plan.Fingerprint, "owner");
        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Contains("changed after this screen was opened", StringComparison.Ordinal));
        db.ChangeTracker.Clear();
        Assert.Equal(before, await PagesTextAsync(db, demo.SiteId));
        Assert.Contains("team", (await SiteStartAgainService.PlanAsync(db, demo.AgentId))!.PagesGoing.Select(p => p.Slug));
    }

    [Fact]
    public async Task With_no_starter_pages_to_write_the_site_is_left_as_it_is()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedStarterContentAsync(db);
        var demo = await SignUpAsync(db, Insurance, await PackageAsync(db));
        await SetBusinessTypeAsync(db, demo.AgentId, Mortgage);
        // Every starter page switched off under Starter Pages (the shared "All" set too): a start-again
        // would leave the website with no page, so it is refused before anything is written.
        await db.WebsiteStarterPages.ExecuteUpdateAsync(u => u.SetProperty(p => p.IsActive, false));
        db.ChangeTracker.Clear();
        var before = await PagesTextAsync(db, demo.SiteId);

        var plan = (await SiteStartAgainService.PlanAsync(db, demo.AgentId))!;
        Assert.False(plan.CanStart);
        Assert.Contains(plan.Problems, p => p.Contains("no starter pages for the business type \"Mortgage\"", StringComparison.Ordinal));
        var result = await SiteStartAgainService.ApplyAsync(db, demo.AgentId, demo.UserName, true, plan.Fingerprint, "owner");
        Assert.False(result.Ok);
        db.ChangeTracker.Clear();
        Assert.Equal(before, await PagesTextAsync(db, demo.SiteId));
        Assert.False(await db.WebsiteBuilderImports.AnyAsync(i => i.AgentWebsiteId == demo.SiteId));
    }

    // ---------------- articles of another pack ----------------

    [Fact]
    public async Task Another_packs_articles_are_unpublished_only_when_the_box_is_ticked()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedStarterContentAsync(db);
        var demo = await SignUpAsync(db, Insurance, await PackageAsync(db));
        // One of the adviser's own, never a starter article: never listed, never touched.
        db.Articles.Add(new Article { AgentUserId = demo.AgentId, Title = "Our office is moving", IsPublished = true, PublishedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await SetBusinessTypeAsync(db, demo.AgentId, Mortgage);
        db.ChangeTracker.Clear();

        var insuranceOnly = (await db.WebsiteStarterArticles.AsNoTracking().Where(a => a.BusinessType == Insurance).Select(a => a.Title).ToListAsync())
            .Except(await db.WebsiteStarterArticles.AsNoTracking().Where(a => a.BusinessType == Mortgage || a.BusinessType == "All").Select(a => a.Title).ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shared = (await db.WebsiteStarterArticles.AsNoTracking().Where(a => a.BusinessType == "All").Select(a => a.Title).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty(insuranceOnly);

        var plan = (await SiteStartAgainService.PlanAsync(db, demo.AgentId))!;
        Assert.Equal(insuranceOnly.OrderBy(t => t), plan.OtherPackArticles.Select(a => a.Title).OrderBy(t => t), StringComparer.OrdinalIgnoreCase);
        Assert.All(plan.OtherPackArticles, a => Assert.Equal(Insurance, a.FromPack));

        // Not ticked: every article keeps its state.
        Assert.True((await SiteStartAgainService.ApplyAsync(db, demo.AgentId, demo.UserName, false, plan.Fingerprint, "owner")).Ok);
        db.ChangeTracker.Clear();
        var articles = await db.Articles.AsNoTracking().Where(a => a.AgentUserId == demo.AgentId).ToListAsync();
        Assert.All(articles, a => Assert.True(a.IsPublished, a.Title));

        // Ticked: only the insurance pack's go off the website; shared, mortgage and the adviser's own stay on.
        plan = (await SiteStartAgainService.PlanAsync(db, demo.AgentId))!;
        var result = await SiteStartAgainService.ApplyAsync(db, demo.AgentId, demo.UserName, true, plan.Fingerprint, "owner");
        Assert.True(result.Ok);
        Assert.Equal(insuranceOnly.Count, result.ArticlesUnpublished);
        db.ChangeTracker.Clear();
        articles = await db.Articles.AsNoTracking().Where(a => a.AgentUserId == demo.AgentId).ToListAsync();
        Assert.All(articles, a => Assert.Equal(!insuranceOnly.Contains(a.Title), a.IsPublished));
        Assert.Contains(articles, a => a.Title == "Our office is moving" && a.IsPublished);
        Assert.Contains(articles, a => shared.Contains(a.Title) && a.IsPublished);
        Assert.Empty((await SiteStartAgainService.PlanAsync(db, demo.AgentId))!.OtherPackArticles);
    }

    // ---------------- the screens ----------------

    [Fact]
    public async Task The_confirm_screen_says_what_goes_what_comes_and_what_to_type()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedStarterContentAsync(db);
        var demo = await SignUpAsync(db, Insurance, await PackageAsync(db));
        await DressSiteAsync(db, demo);
        await SetBusinessTypeAsync(db, demo.AgentId, Mortgage);
        db.ChangeTracker.Clear();

        var shown = Assert.IsType<ViewResult>(await Controller(db, new RecordingAudit(), new RecordingLogger()).StartAgain(demo.AgentId));
        var model = Assert.IsType<AgentStartAgainViewModel>(shown.Model);
        Assert.True(model.UnpublishOtherPackArticles);
        var html = System.Net.WebUtility.HtmlDecode(await BuilderImportScreens571Tests.RenderAsync("~/Views/Agents/StartAgain.cshtml", model));

        Assert.Contains("What goes", html);
        Assert.Contains($"All {model.Plan.PageCountGoing} pages of the website", html);
        Assert.Contains("Our team (/our-team)", html);
        Assert.Contains("and 1 page under it", html);
        Assert.Contains("What comes instead", html);
        Assert.Contains("The Mortgage starter pages", html);
        Assert.All(model.Plan.StarterPages, title => Assert.Contains($"<li>{title}</li>", html));
        Assert.Contains("Resources: ", html);
        Assert.Contains("calculators", html);
        Assert.Contains("form for Mortgage, added to the agent's forms", html);
        Assert.Contains("What stays as it is", html);
        Assert.All(model.Plan.OtherPackArticles, a => Assert.Contains(a.Title, html));
        Assert.Contains("name=\"unpublishOtherPackArticles\" value=\"true\" checked=\"checked\"", html);
        Assert.Contains("Put the previous pages back", html);
        Assert.Contains($"type the agent's username: <code>{demo.UserName}</code>", html);
        Assert.Contains($"name=\"plan\" value=\"{model.Plan.Fingerprint}\"", html);
    }

    [Fact]
    public async Task The_template_builder_list_names_a_start_again_and_puts_it_back_the_same_way()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedStarterContentAsync(db);
        var demo = await SignUpAsync(db, Insurance, await PackageAsync(db));
        await DressSiteAsync(db, demo);
        await SetBusinessTypeAsync(db, demo.AgentId, Mortgage);
        db.ChangeTracker.Clear();
        var before = await PagesTextAsync(db, demo.SiteId);
        Assert.True((await StartAgainAsync(db, demo.AgentId)).Ok);
        db.ChangeTracker.Clear();

        var audit = new RecordingAudit();
        var builder = BuilderController(db, audit);
        var index = Assert.IsType<BuilderImportIndexViewModel>(Assert.IsType<ViewResult>(await builder.Index(demo.AgentId)).Model);
        var row = Assert.Single(index.Imports);
        Assert.Equal(row.Id, index.RestorableId);
        var html = System.Net.WebUtility.HtmlDecode(await BuilderImportScreens571Tests.RenderAsync("~/Views/BuilderImports/Index.cshtml", index));
        Assert.Contains("Started again from the Mortgage starter pack", html);
        Assert.Contains("Put the previous pages back", html);
        Assert.DoesNotContain("(built from scratch)", html);

        Assert.IsType<RedirectToActionResult>(await builder.Restore(row.Id));
        db.ChangeTracker.Clear();
        Assert.Equal(before, await PagesTextAsync(db, demo.SiteId));
        Assert.Equal("https://blob.example.test/agent-logos/logo.png", (await db.AgentWebsites.AsNoTracking().SingleAsync(w => w.Id == demo.SiteId)).LogoUrl);
        Assert.Contains(audit.Entries, e => e.Action == "AgentWebsiteStartAgainPutBack");
    }

    // ---------------- the review's cases (2026-10-16) ----------------

    [Fact]
    public async Task Going_back_to_a_pack_whose_articles_were_taken_off_gives_full_pages_again()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedStarterContentAsync(db);
        var rule = await PackageAsync(db);
        var demo = await SignUpAsync(db, Insurance, rule);
        var insurance = await SignUpAsync(db, Insurance, rule);
        var mortgage = await SignUpAsync(db, Mortgage, rule);

        // Insurance, then Mortgage (the insurance articles taken off), then Insurance again.
        await SetBusinessTypeAsync(db, demo.AgentId, Mortgage);
        Assert.True((await StartAgainAsync(db, demo.AgentId)).Ok);
        db.ChangeTracker.Clear();
        var afterMortgage = await PublishedArticlesAsync(db, demo.AgentId);
        await SetBusinessTypeAsync(db, demo.AgentId, Insurance);
        db.ChangeTracker.Clear();
        var plan = (await SiteStartAgainService.PlanAsync(db, demo.AgentId))!;
        Assert.NotEmpty(plan.PackArticlesToPublish);
        var html = System.Net.WebUtility.HtmlDecode(await BuilderImportScreens571Tests.RenderAsync("~/Views/Agents/StartAgain.cshtml", new AgentStartAgainViewModel { Plan = plan }));
        Assert.Contains("among the agent's articles but not published", html);
        Assert.All(plan.PackArticlesToPublish, a => Assert.Contains($"<li>{a.Title}</li>", html));
        var back = await SiteStartAgainService.ApplyAsync(db, demo.AgentId, demo.UserName, true, plan.Fingerprint, "owner");
        Assert.True(back.Ok);
        Assert.Equal(plan.PackArticlesToPublish.Count, back.ArticlesPublished);
        db.ChangeTracker.Clear();
        // ShapeAsync fails on any Resources page or Did You Know section that names an unpublished article.
        Assert.Equal(await ShapeAsync(db, insurance.AgentId, insurance.SiteId), await ShapeAsync(db, demo.AgentId, demo.SiteId));

        // Putting that one back gives the articles the state the Mortgage start-again left.
        var newest = (await SiteStartAgainService.PutBackableAsync(db, demo.SiteId))!;
        Assert.True((await BuilderImportService.RestoreAsync(db, await db.WebsiteBuilderImports.SingleAsync(i => i.Id == newest.Id))).Ok);
        db.ChangeTracker.Clear();
        Assert.Equal(afterMortgage, await PublishedArticlesAsync(db, demo.AgentId));

        // Try, put back, try again: the second Mortgage start-again is as full as the first.
        var first = (await SiteStartAgainService.PutBackableAsync(db, demo.SiteId))!;
        Assert.True((await BuilderImportService.RestoreAsync(db, await db.WebsiteBuilderImports.SingleAsync(i => i.Id == first.Id))).Ok);
        await SetBusinessTypeAsync(db, demo.AgentId, Mortgage);
        db.ChangeTracker.Clear();
        Assert.True((await StartAgainAsync(db, demo.AgentId)).Ok);
        db.ChangeTracker.Clear();
        Assert.Equal(await ShapeAsync(db, mortgage.AgentId, mortgage.SiteId), await ShapeAsync(db, demo.AgentId, demo.SiteId));
    }

    [Fact]
    public async Task A_second_press_of_the_same_screen_changes_nothing_and_says_so()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedStarterContentAsync(db);
        var demo = await SignUpAsync(db, Insurance, await PackageAsync(db));
        await SetBusinessTypeAsync(db, demo.AgentId, Mortgage);
        db.ChangeTracker.Clear();
        var plan = (await SiteStartAgainService.PlanAsync(db, demo.AgentId))!;
        var audit = new RecordingAudit();

        Assert.IsType<RedirectToActionResult>(await Controller(db, audit, new RecordingLogger()).StartAgain(demo.AgentId, demo.UserName, true, plan.Fingerprint));
        db.ChangeTracker.Clear();
        var after = await PagesTextAsync(db, demo.SiteId);
        var second = Controller(db, audit, new RecordingLogger());
        Assert.IsType<RedirectToActionResult>(await second.StartAgain(demo.AgentId, demo.UserName, true, plan.Fingerprint));
        Assert.Contains("already started again from this screen", second.TempData["Success"] as string);
        db.ChangeTracker.Clear();
        Assert.Equal(after, await PagesTextAsync(db, demo.SiteId));
        Assert.Equal(1, await db.WebsiteBuilderImports.CountAsync(i => i.AgentWebsiteId == demo.SiteId));
        Assert.Single(audit.Entries, e => e.Action == "AgentWebsiteStartedAgain");
        Assert.DoesNotContain(audit.Entries, e => e.Action == "AgentWebsiteNotStartedAgain");

        // A second press of the put-back says they are already back, in this screen's words.
        var started = (await SiteStartAgainService.PutBackableAsync(db, demo.SiteId))!;
        Assert.IsType<RedirectToActionResult>(await Controller(db, audit, new RecordingLogger()).PutBackStartAgain(demo.AgentId, started.Id));
        db.ChangeTracker.Clear();
        var again = Controller(db, audit, new RecordingLogger());
        Assert.IsType<RedirectToActionResult>(await again.PutBackStartAgain(demo.AgentId, started.Id));
        Assert.Contains("already back", again.TempData["Error"] as string);
        Assert.DoesNotContain("package", (again.TempData["Error"] as string)!);
    }

    [Fact]
    public async Task A_site_that_had_no_pages_is_not_put_back_to_none()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedStarterContentAsync(db);
        var demo = await SignUpAsync(db, Insurance, await PackageAsync(db));
        var pageIds = await db.WebsitePages.Where(p => p.AgentWebsiteId == demo.SiteId).Select(p => p.Id).ToListAsync();
        await db.WebsiteContentBlocks.Where(b => pageIds.Contains(b.WebsitePageId)).ExecuteDeleteAsync();
        await db.WebsitePages.Where(p => p.AgentWebsiteId == demo.SiteId).ExecuteUpdateAsync(u => u.SetProperty(p => p.ParentPageId, (int?)null));
        await db.WebsitePages.Where(p => p.AgentWebsiteId == demo.SiteId).ExecuteDeleteAsync();
        db.ChangeTracker.Clear();

        // Nothing to keep: the site is filled, and no record stands in front of an older package.
        var html = System.Net.WebUtility.HtmlDecode(await BuilderImportScreens571Tests.RenderAsync("~/Views/Agents/StartAgain.cshtml",
            new AgentStartAgainViewModel { Plan = (await SiteStartAgainService.PlanAsync(db, demo.AgentId))! }));
        Assert.Contains("nothing to keep and nothing to put back afterwards", html);
        var result = await StartAgainAsync(db, demo.AgentId);
        Assert.True(result.Ok);
        Assert.Equal(0, result.ImportId);
        db.ChangeTracker.Clear();
        var after = await PagesTextAsync(db, demo.SiteId);
        Assert.NotEmpty(after);
        Assert.False(await db.WebsiteBuilderImports.AnyAsync(i => i.AgentWebsiteId == demo.SiteId));
        Assert.Null(await SiteStartAgainService.PutBackableAsync(db, demo.SiteId));

        // And a kept copy with no page (none is made now) is never put back to an empty site.
        var empty = new WebsiteBuilderImport
        {
            AgentUserId = demo.AgentId, AgentWebsiteId = demo.SiteId, Status = WebsiteBuilderImportStatus.Applied, FileName = "Started again: Mortgage starter pack",
            PackageSha256 = SiteStartAgainService.StartAgainMarker, TemplateName = "Mortgage starter pack", AppliedAt = DateTime.UtcNow,
            SnapshotJson = new SiteSnapshot { StartedAgain = new SiteSnapshot.StartAgainRecord { BusinessType = Mortgage } }.ToJson()
        };
        db.WebsiteBuilderImports.Add(empty);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var controller = Controller(db, new RecordingAudit(), new RecordingLogger());
        Assert.IsType<RedirectToActionResult>(await controller.PutBackStartAgain(demo.AgentId, empty.Id));
        Assert.Contains("would leave it empty", controller.TempData["Error"] as string);
        db.ChangeTracker.Clear();
        Assert.Equal(after, await PagesTextAsync(db, demo.SiteId));
        Assert.Equal(WebsiteBuilderImportStatus.Applied, (await db.WebsiteBuilderImports.AsNoTracking().SingleAsync(i => i.Id == empty.Id)).Status);
    }

    [Fact]
    public async Task The_screen_names_links_that_will_find_nothing_and_template_builder_work_still_out()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedStarterContentAsync(db);
        var demo = await SignUpAsync(db, Insurance, await PackageAsync(db));
        await DressSiteAsync(db, demo);
        await SetBusinessTypeAsync(db, demo.AgentId, Mortgage);
        var header = new WebsiteHeaderSettings();
        header.ButtonText = "Join us";
        header.ButtonUrl = "/careers";
        header.CustomLinks.Add(new WebsiteCustomNavigationLink { Label = "Meet the team", Url = "/our-team", SortOrder = 0 });
        header.CustomLinks.Add(new WebsiteCustomNavigationLink { Label = "Book", Url = "/request-meeting", SortOrder = 1 });
        header.CustomLinks.Add(new WebsiteCustomNavigationLink { Label = "Elsewhere", Url = "https://example.test/our-team", SortOrder = 2 });
        var footer = new WebsiteFooterSettings();
        footer.LegalLinks.Add(new WebsiteFooterLink { Label = "Jobs", Url = "/careers?from=footer", SortOrder = 0 });
        await db.AgentWebsites.Where(w => w.Id == demo.SiteId).ExecuteUpdateAsync(u => u
            .SetProperty(w => w.HeaderSettingsJson, header.ToJson())
            .SetProperty(w => w.FooterSettingsJson, footer.ToJson()));
        db.WebsiteBuilderImports.Add(new WebsiteBuilderImport
        {
            AgentUserId = demo.AgentId, AgentWebsiteId = demo.SiteId, Status = WebsiteBuilderImportStatus.Pending, FileName = "redesign.zip",
            PackageSha256 = new string('a', 64), TemplateName = "Redesign", TemplateJson = "{}", UploadedBy = "owner"
        });
        db.WebsiteBuilderHandovers.Add(new WebsiteBuilderHandover
        {
            AgentUserId = demo.AgentId, AgentWebsiteId = demo.SiteId, HandoverId = "h_0123456789abcdef", IssuedAt = new DateTime(2026, 10, 12, 15, 0, 0, DateTimeKind.Utc),
            SessionExpiresAt = DateTime.UtcNow.AddDays(1), SessionTokenSha256 = new string('b', 64), IssuedBy = "owner", TemplateName = "Site", PackageSha256 = new string('c', 64)
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var plan = (await SiteStartAgainService.PlanAsync(db, demo.AgentId))!;
        Assert.Equal(new[] { "Header button link \"Join us\" (/careers)", "Header menu link \"Meet the team\" (/our-team)", "Footer link \"Jobs\" (/careers?from=footer)" }, plan.LinksToNowhere);
        Assert.Equal(1, plan.PackagesWaiting);
        Assert.NotNull(plan.OpenHandoverIssuedAt);
        var html = System.Net.WebUtility.HtmlDecode(await BuilderImportScreens571Tests.RenderAsync("~/Views/Agents/StartAgain.cshtml", new AgentStartAgainViewModel { Plan = plan }));
        Assert.Contains("added by hand will find no page afterwards", html);
        Assert.Contains("Header menu link \"Meet the team\" (/our-team)", html);
        Assert.Contains("1 package waits for review", html);
        Assert.Contains("handed to the Template Builder on Oct 12, 2026", html);
    }

    [Fact]
    public async Task Rebuild_request_meeting_after_a_start_again_keeps_the_new_business_types_form()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedStarterContentAsync(db);
        var demo = await SignUpAsync(db, Insurance, await PackageAsync(db));
        await SetBusinessTypeAsync(db, demo.AgentId, Mortgage);
        Assert.True((await StartAgainAsync(db, demo.AgentId)).Ok);
        db.ChangeTracker.Clear();
        async Task<int> MeetingFormAsync()
        {
            var page = await db.WebsitePages.AsNoTracking().Include(p => p.Blocks).SingleAsync(p => p.AgentWebsiteId == demo.SiteId && p.Slug == "request-meeting");
            return WebsiteFormSettings.FromJson(page.Blocks.Single(b => b.BlockType == WebsiteBlockTypes.Form).SettingsJson).WebsiteFormId;
        }
        var mortgageForm = await MeetingFormAsync();
        Assert.Equal(2, await db.WebsiteForms.CountAsync(f => f.AgentUserId == demo.AgentId && f.Title == WebsiteStarterFormSeeder.MeetingFormTitle));

        Assert.IsType<RedirectToActionResult>(await Controller(db, new RecordingAudit(), new RecordingLogger()).RebuildRequestMeeting(demo.AgentId));
        db.ChangeTracker.Clear();
        Assert.Equal(mortgageForm, await MeetingFormAsync());
    }

    [Fact]
    public async Task The_template_builder_screens_put_back_is_superadmin_only_too()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedStarterContentAsync(db);
        var demo = await SignUpAsync(db, Insurance, await PackageAsync(db));
        await SetBusinessTypeAsync(db, demo.AgentId, Mortgage);
        Assert.True((await StartAgainAsync(db, demo.AgentId)).Ok);
        db.ChangeTracker.Clear();
        var after = await PagesTextAsync(db, demo.SiteId);
        var started = (await SiteStartAgainService.PutBackableAsync(db, demo.SiteId))!;

        Assert.IsType<ForbidResult>(await BuilderController(db, new RecordingAudit(), AdminRoles.Support).Restore(started.Id));
        db.ChangeTracker.Clear();
        Assert.Equal(after, await PagesTextAsync(db, demo.SiteId));
    }

    // ---------------- sign-up's provisioning, moved ----------------

    [Fact]
    public void Sign_ups_provisioning_is_the_shared_code_the_admin_app_calls()
    {
        // Moved to IPRO.DataAccess so IPRO.Admin (which does not reference IPRO.Web) can start a site
        // again with the very code a sign-up uses; IPRO.Web calls it as before.
        Assert.Equal(typeof(IPRODbContext).Assembly, typeof(WebsiteStarterPagesHelper).Assembly);
        Assert.Equal(typeof(IPRODbContext).Assembly, typeof(WebsiteStarterResourcesHelper).Assembly);
        Assert.Equal(typeof(IPRODbContext).Assembly, typeof(VerticalCalculatorCatalog).Assembly);
        Assert.DoesNotContain(typeof(AgentsController).Assembly.GetReferencedAssemblies(), a => a.Name == "IPRO.Web");
        var provisioning = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Infrastructure\WebsiteProvisioning.cs"));
        Assert.Contains("await WebsiteStarterPagesHelper.EnsureStarterPagesAsync(db, site, agent.Id);", provisioning);
        Assert.Contains("await WebsiteStarterResourcesHelper.EnsureResourcesAsync(db, site, agent.Id);", provisioning);
        var service = File.ReadAllText(FindRepoFile(@"src\IPRO.Business\SiteImport\SiteStartAgainService.cs"));
        Assert.Contains("await WebsiteStarterPagesHelper.EnsureStarterPagesAsync(db, site, agentId);", service);
        Assert.Contains("await WebsiteStarterResourcesHelper.EnsureResourcesAsync(db, site, agentId);", service);
    }

    // ---------------- helpers ----------------

    private sealed record Demo(int AgentId, int SiteId, string UserName);

    private const string OwnPicture = "https://blob.example.test/website-media/584/own-photo.png";

    private static async Task SeedStarterContentAsync(IPRODbContext db)
    {
        await WebsiteTemplateSeeder.SeedAsync(db);
        await WebsiteStarterContentSeeder.SeedAsync(db);
        await WebsiteStarterContentSeeder.SeedNavV2AdditionsAsync(db);
        await WebsiteStarterContentSeeder.SeedGenericEditionAsync(db);
        await WebsiteStarterFormSeeder.SeedAsync(db);
        await WebsiteStarterArticleSeeder.SeedAsync(db);
        db.ChangeTracker.Clear();
    }

    private static async Task<int> PackageAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = $"T584-{Guid.NewGuid():N}"[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        return rule.Id;
    }

    // An adviser as sign-up makes one: the account, then the website through the portal's own provisioning.
    private static async Task<Demo> SignUpAsync(IPRODbContext db, string businessType, int packageId)
    {
        var agent = new AgentUser
        {
            UserName = $"t584-{Guid.NewGuid():N}"[..20], Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Michael", LastName = "Tran", CompanyName = "Tran Advisory",
            DomainName = $"t584-{Guid.NewGuid():N}"[..24], Country = "Canada", Province = "Ontario",
            PackageId = packageId, BusinessType = businessType
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        var site = await WebsiteProvisioning.EnsurePublishedAsync(db, new WebsiteService(new UnitOfWork(db)), agent);
        db.ChangeTracker.Clear();
        return new Demo(agent.Id, site.Id, agent.UserName);
    }

    private static Task SetBusinessTypeAsync(IPRODbContext db, int agentId, string businessType) =>
        db.AgentUsers.Where(a => a.Id == agentId).ExecuteUpdateAsync(u => u.SetProperty(a => a.BusinessType, businessType));

    // A site the adviser has worked on: its own look and settings, a page of their own with one under it,
    // a page in French, old addresses, a hidden section with a picture, a form, a contact, a lead, a domain.
    private static async Task DressSiteAsync(IPRODbContext db, Demo demo)
    {
        var site = await db.AgentWebsites.SingleAsync(w => w.Id == demo.SiteId);
        site.LogoUrl = "https://blob.example.test/agent-logos/logo.png";
        site.ThemeColor = "#aa3300";
        site.TagLine = "Insurance that fits";
        site.HeaderSettingsJson = "{\"Style\":\"centered\"}";
        site.FooterSettingsJson = "{\"Text\":\"Licensed in Ontario\"}";
        site.FontFamilyOverride = "Georgia";
        site.CustomDomain = $"t584-{Guid.NewGuid():N}"[..14] + ".example.test";
        db.AgentDomains.Add(new AgentDomain
        {
            AgentUserId = demo.AgentId, AgentWebsiteId = demo.SiteId, DomainName = site.CustomDomain, WwwDomain = site.CustomDomain, RootDomain = site.CustomDomain,
            AzureBindingStatus = AgentDomainStatus.Bound, SslStatus = AgentDomainStatus.Bound, IsPrimary = true
        });
        db.WebsiteMediaAssets.Add(new WebsiteMediaAsset { AgentWebsiteId = demo.SiteId, OriginalFileName = "own-photo.png", BlobUrl = OwnPicture, ContentType = "image/png", FileSize = 10 });
        var team = new WebsitePage
        {
            AgentWebsiteId = demo.SiteId, Title = "Our team", Slug = "our-team", NavigationLabel = "Team", MetaTitle = "The team", MetaDescription = "Who we are.",
            OldAddresses = "team.html,staff", Language = "fr", ShowInNavigation = true, IsPublished = true, SortOrder = 40,
            Blocks = new List<WebsiteContentBlock>
            {
                new() { BlockType = WebsiteBlockTypes.Text, Heading = "Meet us", Body = "Line one\r\nLine two", ImageUrl = OwnPicture, SortOrder = 0, IsVisible = false, LayoutVariant = "wide" }
            }
        };
        db.WebsitePages.Add(team);
        await db.SaveChangesAsync();
        db.WebsitePages.Add(new WebsitePage
        {
            AgentWebsiteId = demo.SiteId, ParentPageId = team.Id, Title = "Careers", Slug = "careers", NavigationLabel = "Careers", IsPublished = false, SortOrder = 0,
            Blocks = new List<WebsiteContentBlock> { new() { BlockType = WebsiteBlockTypes.Text, Heading = "Join", ButtonText = "Apply", ButtonUrl = "/contact", SortOrder = 0, IsVisible = true } }
        });
        var home = await db.WebsitePages.Include(p => p.Blocks).SingleAsync(p => p.AgentWebsiteId == demo.SiteId && p.IsHomePage);
        home.Blocks.First().Heading = "My own welcome";
        var client = new Client { AgentUserId = demo.AgentId, FirstName = "Cli", LastName = "Ent", Email = $"{Guid.NewGuid():N}@example.test" };
        db.Add(client);
        db.WebsiteForms.Add(new WebsiteForm { AgentUserId = demo.AgentId, Title = "Quote request", Description = "Mine", IsActive = true });
        await db.SaveChangesAsync();
        db.WebsiteLeads.Add(new WebsiteLead { AgentUserId = demo.AgentId, AgentWebsiteId = demo.SiteId, WebsitePageId = team.Id, Email = "lead@example.test", FirstName = "Lee", SourcePage = "/our-team" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static Task<SiteStartAgainService.Result> StartAgainAsync(IPRODbContext db, int agentId, bool unpublish = true) =>
        StartAgainWithPlanAsync(db, agentId, unpublish);

    private static async Task<SiteStartAgainService.Result> StartAgainWithPlanAsync(IPRODbContext db, int agentId, bool unpublish)
    {
        var plan = (await SiteStartAgainService.PlanAsync(db, agentId))!;
        var userName = await db.AgentUsers.AsNoTracking().Where(a => a.Id == agentId).Select(a => a.UserName).SingleAsync();
        return await SiteStartAgainService.ApplyAsync(db, agentId, userName, unpublish, plan.Fingerprint, "owner");
    }

    // Every page as a visitor or the adviser could tell it apart, with each section; the rows that
    // other rows name (forms, articles) by what they are, not by number.
    private static async Task<List<string>> ShapeAsync(IPRODbContext db, int agentId, int siteId)
    {
        var pages = await db.WebsitePages.AsNoTracking().Where(p => p.AgentWebsiteId == siteId).Include(p => p.Blocks).ToListAsync();
        var articles = await db.Articles.AsNoTracking().Where(a => a.AgentUserId == agentId).ToDictionaryAsync(a => a.Id);
        var forms = await db.WebsiteForms.AsNoTracking().Where(f => f.AgentUserId == agentId).ToDictionaryAsync(f => f.Id);
        string Settings(WebsiteContentBlock b)
        {
            if (b.BlockType == WebsiteBlockTypes.DidYouKnow)
            {
                var ids = WebsiteDidYouKnowSettings.FromJson(b.SettingsJson).ArticleIds;
                Assert.All(ids, id => Assert.True(articles.ContainsKey(id) && articles[id].IsPublished, $"Did You Know names article {id}, not a published article of the adviser's"));
                return "dyk:" + string.Join(";", ids.Select(id => articles[id].Title));
            }
            if (b.BlockType == WebsiteBlockTypes.ArticleContent)
            {
                var id = WebsiteArticleContentSettings.FromJson(b.SettingsJson).ArticleId;
                Assert.True(articles.ContainsKey(id) && articles[id].IsPublished, $"a Resources page names article {id}, not a published article of the adviser's");
                return "article:" + articles[id].Title;
            }
            if (b.BlockType == WebsiteBlockTypes.Form)
            {
                var id = WebsiteFormSettings.FromJson(b.SettingsJson).WebsiteFormId;
                Assert.True(forms.ContainsKey(id), $"a form section names form {id}, not the adviser's");
                return "form:" + forms[id].Title;
            }
            return b.SettingsJson;
        }
        return pages.OrderBy(p => p.ParentPageId.HasValue).ThenBy(p => p.SortOrder).ThenBy(p => p.Slug).Select(p =>
            $"{p.Slug}|{p.Title}|{p.NavigationLabel}|{p.MetaTitle}|{p.MetaDescription}|{p.IsHomePage}|{p.ShowInNavigation}|{p.IsPublished}|{p.SortOrder}|parent={pages.FirstOrDefault(x => x.Id == p.ParentPageId)?.Slug}\n" +
            string.Join("\n", p.Blocks.OrderBy(b => b.SortOrder).Select(b =>
                $"   {b.BlockType}|{b.Heading}|{b.Subheading}|{b.Body}|{b.ImageUrl}|{b.ButtonText}|{b.ButtonUrl}|{Settings(b)}|{b.LayoutVariant}|{b.SortOrder}|{b.IsVisible}"))).ToList();
    }

    // Everything a page holds, as the put-back must give it back.
    private static async Task<List<string>> PagesTextAsync(IPRODbContext db, int siteId)
    {
        var pages = await db.WebsitePages.AsNoTracking().Where(p => p.AgentWebsiteId == siteId).Include(p => p.Blocks).ToListAsync();
        var lines = new List<string>();
        foreach (var p in pages.OrderBy(p => p.Slug, StringComparer.Ordinal))
        {
            lines.Add($"{p.Slug}|{p.Title}|{p.NavigationLabel}|{p.MetaTitle}|{p.MetaDescription}|{p.OldAddresses}|{p.Language}|{p.IsHomePage}|{p.ShowInNavigation}|{p.IsPublished}|{p.SortOrder}|{p.CreatedAt:O}|parent={pages.FirstOrDefault(x => x.Id == p.ParentPageId)?.Slug}");
            lines.AddRange(p.Blocks.OrderBy(b => b.SortOrder).ThenBy(b => b.Id).Select(b =>
                $"   {b.BlockType}|{b.Heading}|{b.Subheading}|{b.Body}|{b.ImageUrl}|{b.ButtonText}|{b.ButtonUrl}|{b.SettingsJson}|{b.LayoutVariant}|{b.SortOrder}|{b.IsVisible}|{b.CreatedAt:O}"));
        }
        return lines;
    }

    private static Task<List<string>> PublishedArticlesAsync(IPRODbContext db, int agentId) =>
        db.Articles.AsNoTracking().Where(a => a.AgentUserId == agentId && a.IsPublished).OrderBy(a => a.Id).Select(a => a.Title).ToListAsync();

    private static async Task<List<string>> FormsTextAsync(IPRODbContext db, int agentId)
    {
        var forms = await db.WebsiteForms.AsNoTracking().Where(f => f.AgentUserId == agentId).OrderBy(f => f.Id).ToListAsync();
        var ids = forms.Select(f => f.Id).ToList();
        var fields = await db.WebsiteFormFields.AsNoTracking().Where(f => ids.Contains(f.WebsiteFormId)).OrderBy(f => f.SortOrder).ThenBy(f => f.Id).ToListAsync();
        return forms.Select(f => $"{f.Id}|{f.Title}|{f.Description}|{f.IsActive}|{string.Join(";", fields.Where(x => x.WebsiteFormId == f.Id).Select(x => x.Label))}").ToList();
    }

    private static string SiteText(AgentWebsite w) =>
        $"{w.TemplateId}|{w.LogoUrl}|{w.ThemeColor}|{w.SiteTitle}|{w.TagLine}|{w.SiteLanguage}|{w.FontFamilyOverride}|{w.HeaderSettingsJson}|{w.FooterSettingsJson}|{w.CustomDomain}|{w.IsPublished}";

    private static AgentsController Controller(IPRODbContext db, IAdminAuditLogService audit, ILogger<AgentsController> logger, string role = AdminRoles.SuperAdmin)
    {
        var uow = new UnitOfWork(db);
        var services = new ServiceCollection().BuildServiceProvider();
        var controller = new AgentsController(new AgentService(uow, new PasswordHasher<AgentUser>()), new WebsiteService(uow), uow, null!,
            new PasswordHasher<AgentUser>(), logger, audit, db, services);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Name, "owner"), new Claim("Role", role)
            }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(context, new NullTempData());
        return controller;
    }

    private static BuilderImportsController BuilderController(IPRODbContext db, IAdminAuditLogService audit, string role = AdminRoles.SuperAdmin)
    {
        var services = new ServiceCollection().AddSingleton<IBlobStorageService>(new BuilderImport571Tests.MemoryBlob()).BuildServiceProvider();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:PortalBaseUrl"] = "https://portal.example.test/" })
            .Build();
        var controller = new BuilderImportsController(db, new PackageEntitlementService(new UnitOfWork(db), db), audit, configuration, services);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Name, "owner"), new Claim("Role", role)
            }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(context, new NullTempData());
        return controller;
    }

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative.Replace('\\', Path.DirectorySeparatorChar));
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

    private sealed class RecordingLogger : ILogger<AgentsController>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class NullTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
