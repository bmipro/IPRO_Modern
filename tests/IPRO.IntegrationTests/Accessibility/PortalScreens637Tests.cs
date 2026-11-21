using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using IPRO.Web.Controllers;
using IPRO.Web.Infrastructure;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPRO.IntegrationTests.Accessibility;

// 637 (2026-11-21): accessibility phases 2 and 3 changed about 70 portal views (labels tied to their fields,
// names on icon buttons and unlabelled boxes, group captions, a skip link and one main region with one
// heading in both portal layouts, wrapped rows on phones). These checks render the screens where that work
// is densest from their real compiled views with small made-up models, open them in headless Edge at a
// desktop width and at 320 px, and ask axe-core about the rules the work is about. At 320 px the page must
// not scroll sideways (WCAG 1.4.10); a wide table may, inside its own .table-responsive box.
//
// A screen is rendered without its layout, inside a page that carries what the layout gives it in the
// product: the viewport line, Bootstrap, the portal's site.css and the layout's own style block (its accent
// colour on buttons and links, its field edges), and the main region the layout puts it in. The two layouts
// themselves are rendered once each, signed in, on a throwaway database (they read it for the menu and the
// footer), and checked with every WCAG A and AA rule: the first Tab reaches the skip link, which lands on the
// one main region; the page has one level-1 heading; no Tab stop is off screen (the phone menu, closed, kept
// them); and the adviser menu's text, on a gradient axe cannot judge, reads at 4.5:1.
public class PortalScreens637Tests : IClassFixture<EdgeFixture>
{
    private readonly EdgeFixture _edge;
    public PortalScreens637Tests(EdgeFixture edge) => _edge = edge;

    // The rules this work is about (names, labels, ids, ARIA values, contrast, nesting).
    private static readonly string[] Rules =
    {
        "label", "select-name", "button-name", "link-name", "input-button-name", "aria-input-field-name",
        "aria-toggle-field-name", "duplicate-id-aria", "duplicate-id-active", "aria-valid-attr-value", "aria-allowed-attr",
        "color-contrast", "nested-interactive", "form-field-multiple-labels"
    };

    public static IEnumerable<object[]> Screens() => ScreenNames.Select(s => new object[] { s });

    private static readonly string[] ScreenNames =
    {
        "WebsitePages/Navigation", "WebsitePages/Edit", "Website/Index", "WebsiteLeads/Index", "PortalRequests/Index",
        "Clients/Create", "Account/Profile", "ClientPortalProfile/Index", "ClientInvoices/Edit"
    };

    [Theory]
    [MemberData(nameof(Screens))]
    public async Task Every_field_and_button_is_named_and_readable_at_desktop_and_phone_widths(string screen)
    {
        var page = await RenderScreenPageAsync(screen);
        var findings = new List<string>();
        foreach (var width in new[] { 1280, 320 })
        {
            await _edge.OpenAsync(page, Slug(screen) + "-" + width, width);
            // What is folded away (a picture's description, the reviews to show, the review-link steps) is
            // checked opened, as the adviser sees it after a click.
            await _edge.Edge.EvaluateAsync<int>("document.querySelectorAll('details').forEach(function(d){d.open=true;}),0");
            findings.AddRange((await _edge.AxeOpenPageAsync(Rules)).Select(f => $"{width}px: {f}"));
            // axe only flags a repeated id that ARIA or a label points at as "needs review"; a repeated id
            // breaks a label's for="" the same way, so any repeat is a finding here.
            var repeated = await _edge.Edge.EvaluateAsync<string>(RepeatedIds);
            if (!string.IsNullOrEmpty(repeated)) findings.Add($"{width}px: ids used more than once: {repeated}");
        }
        Assert.True(findings.Count == 0, $"{screen}: {findings.Count} findings\n" + string.Join("\n", findings));
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public async Task Nothing_makes_the_page_scroll_sideways_on_a_320px_phone(string screen)
    {
        var page = await RenderScreenPageAsync(screen);
        await _edge.OpenAsync(page, Slug(screen) + "-reflow", 320);
        var overflow = await _edge.Edge.EvaluateAsync<string>(Overflow);
        Assert.True(string.IsNullOrEmpty(overflow), $"{screen}: {overflow}");
    }

    [Fact]
    public async Task The_adviser_portal_layout_starts_with_a_skip_link_into_its_one_main_region()
    {
        await using var db = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var adviser = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "7"), new Claim(ClaimTypes.Name, "dana"), new Claim("FullName", "Dana Rivers")
        }, "Cookies"));
        var requests = PortalRequestsModel();
        var html = await RenderAsync("~/Views/PortalRequests/Index.cshtml", "/portal/PortalRequests", requests,
            new Dictionary<string, object?> { ["Status"] = "all", ["AgentTimeZone"] = "America/Toronto", ["AppointmentTimes"] = AppointmentTimes(requests) },
            withLayout: true, user: adviser, database: db);
        Assert.Contains("class=\"agent-sidebar\"", html);
        await CheckLayoutAsync(html, "adviser-layout");

        await _edge.OpenAsync(WithPortalCss(html), "adviser-layout-menu", 1280);
        var faint = await _edge.Edge.EvaluateAsync<string>(MenuContrast);
        Assert.True(string.IsNullOrEmpty(faint), "the menu's text is too faint: " + faint);
    }

    [Fact]
    public async Task The_client_portal_layout_starts_with_a_skip_link_into_its_one_main_region()
    {
        await using var db = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var client = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "31"), new Claim(ClaimTypes.Name, "sam.okafor@example.test")
        }, "ClientPortal"));
        var html = await RenderAsync("~/Views/ClientPortalProfile/Index.cshtml", "/ClientPortalProfile", PortalProfileModel(),
            new Dictionary<string, object?>(), withLayout: true, user: client, database: db);
        Assert.Contains("client-portal-nav", html);
        await CheckLayoutAsync(html, "client-layout");
    }

    // ---------------------------------------------------------------- the layouts --

    private async Task CheckLayoutAsync(string html, string name)
    {
        html = WithPortalCss(html);
        var findings = new List<string>();
        foreach (var width in new[] { 1280, 320 })
        {
            await _edge.OpenAsync(html, $"{name}-{width}", width);
            await _edge.Edge.PressAsync("Tab");
            if (!await _edge.Edge.EvaluateAsync<bool>("document.activeElement.classList.contains('skip-link') && document.activeElement.getAttribute('href') === '#main-content'"))
                findings.Add($"{width}px: the first Tab went to {await _edge.Edge.EvaluateAsync<string>(ActiveDescription)}, not the skip link");
            else if (!await _edge.Edge.EvaluateAsync<bool>("(function(r){return r.top >= 0 && r.left >= 0 && r.right <= document.documentElement.clientWidth;})(document.activeElement.getBoundingClientRect())"))
                findings.Add($"{width}px: the focused skip link is not on screen");
            await _edge.Edge.PressAsync("Enter");
            if (!await _edge.Edge.EvaluateAsync<bool>("document.activeElement === document.getElementById('main-content')"))
                findings.Add($"{width}px: the skip link left focus on {await _edge.Edge.EvaluateAsync<string>(ActiveDescription)}, not #main-content");
            var mains = await _edge.Edge.EvaluateAsync<int>("document.querySelectorAll('main, [role=main]').length");
            var headings = await _edge.Edge.EvaluateAsync<int>("document.querySelectorAll('h1').length");
            if (mains != 1) findings.Add($"{width}px: {mains} main regions");
            if (headings != 1) findings.Add($"{width}px: {headings} level-1 headings");
            findings.AddRange((await _edge.AxeOpenPageAsync()).Select(f => $"{width}px: {f}"));
            var repeated = await _edge.Edge.EvaluateAsync<string>(RepeatedIds);
            if (!string.IsNullOrEmpty(repeated)) findings.Add($"{width}px: ids used more than once: {repeated}");
            if (width == 320)
            {
                var overflow = await _edge.Edge.EvaluateAsync<string>(Overflow);
                if (!string.IsNullOrEmpty(overflow)) findings.Add("320px: " + overflow);
            }

            // Every stop the keyboard makes from the top of the page is on screen (WCAG 2.4.7): a menu slid out of
            // view on a phone must not take the Tab key with it.
            await _edge.OpenAsync(html, $"{name}-{width}-tabs", width);
            for (var press = 1; press <= 40; press++)
            {
                await _edge.Edge.PressAsync("Tab");
                if (await _edge.Edge.EvaluateAsync<bool>(FocusOffScreen))
                {
                    findings.Add($"{width}px: Tab {press} went to {await _edge.Edge.EvaluateAsync<string>(ActiveDescription)}, which is off screen");
                    break;
                }
            }
        }
        Assert.True(findings.Count == 0, $"{name}: {findings.Count} findings\n" + string.Join("\n", findings));
    }

    private const string FocusOffScreen =
        "(function(e){if(!e||e===document.body)return false;var r=e.getBoundingClientRect();return r.width===0||r.right<=0||r.left>=document.documentElement.clientWidth;})(document.activeElement)";

    // axe cannot judge text on a gradient, so it leaves the adviser menu alone: each piece of the menu's text
    // (the open page's link has a background of its own, and axe judges it) is checked here against both ends
    // of the gradient, at 4.5:1 (WCAG 1.4.3; all of it is small text).
    private const string MenuContrast =
        "(function(){function p(s){var m=s.match(/[\\d.]+/g).map(Number);return [m[0],m[1],m[2],m.length>3?m[3]:1];}" +
        "function lum(c){var w=[0.2126,0.7152,0.0722];return c.slice(0,3).reduce(function(s,v,i){v/=255;return s+w[i]*(v<=0.03928?v/12.92:Math.pow((v+0.055)/1.055,2.4));},0);}" +
        "var bar=document.querySelector('.agent-sidebar');if(!bar)return 'no menu';" +
        "var ends=(getComputedStyle(bar).backgroundImage.match(/rgba?\\([^)]*\\)/g)||[]).map(p);if(ends.length<2)return 'the menu has no gradient';" +
        "var out=[];bar.querySelectorAll('.portal-label, .nav-section, .nav-link').forEach(function(e){var st=getComputedStyle(e);" +
        "if(!e.textContent.trim()||st.display==='none'||p(st.backgroundColor)[3]>0)return;var c=p(st.color);" +
        "ends.forEach(function(b){var t=[0,1,2].map(function(i){return c[3]*c[i]+(1-c[3])*b[i];});var l1=lum(t),l2=lum(b);" +
        "var ratio=(Math.max(l1,l2)+0.05)/(Math.min(l1,l2)+0.05);if(ratio<4.5)out.push('\"'+e.textContent.trim()+'\" '+ratio.toFixed(2)+':1 on rgb('+b.slice(0,3).join(',')+')');});});" +
        "return out.join('; ');})()";

    // ---------------------------------------------------------------- the screens --

    private static async Task<string> RenderScreenPageAsync(string screen)
    {
        var clientPortal = screen.StartsWith("ClientPortal", StringComparison.Ordinal);
        var fragment = screen switch
        {
            "WebsitePages/Navigation" => await RenderAsync("~/Views/WebsitePages/Navigation.cshtml", "/portal/WebsitePages/Navigation", NavigationModel(), new()),
            "WebsitePages/Edit" => await RenderAsync("~/Views/WebsitePages/Edit.cshtml", "/portal/WebsitePages/Edit/21", PageEditModel(), new()
            {
                ["StorageAccess"] = new PackageFeatureAccess { FeatureCode = PackageFeatureCodes.FileUploadCapacity, IsIncluded = true, LimitValue = 500 },
                ["StorageUsedMb"] = 42.5,
                ["ProfileGoogleReviewLink"] = "https://g.page/r/rivers-financial/review"
            }),
            "Website/Index" => await RenderAsync("~/Views/Website/Index.cshtml", "/portal/Website", WebsiteModel(), WebsiteViewData()),
            "WebsiteLeads/Index" => await RenderAsync("~/Views/WebsiteLeads/Index.cshtml", "/portal/WebsiteLeads", (IReadOnlyCollection<WebsiteLead>)LeadsModel(), new()
            {
                ["Status"] = "all", ["Sort"] = "newest", ["Page"] = 1, ["TotalPages"] = 2, ["TotalCount"] = 3, ["UnreadCount"] = 1, ["NewCount"] = 2,
                ["AgentTimeZone"] = "America/Toronto"
            }),
            "PortalRequests/Index" => await PortalRequestsFragmentAsync(),
            "Clients/Create" => await RenderAsync("~/Views/Clients/Create.cshtml", "/portal/Clients/Create", new Client { Country = "Canada" }, new()
            {
                ["AccountTypes"] = new List<ClientCategory>
                {
                    new() { Id = 1, Name = "Retirement", Description = "Clients planning to retire within ten years" },
                    new() { Id = 2, Name = "Insurance", Description = "Life and disability policies" },
                    new() { Id = 3, Name = "Small business" }
                },
                ["SelectedCategoryIds"] = new HashSet<int> { 2 },
                ["ContactLimit"] = new ContactLimitStatus { CurrentCount = 212, Limit = 500, PackageName = "Professional" }
            }),
            "Account/Profile" => await RenderAsync("~/Views/Account/Profile.cshtml", "/portal/Account/Profile", ProfileModel(), new()
            {
                ["EmailChangeTwoStepMethod"] = TwoStepMethods.Email,
                ["ReferralSummary"] = new IPRO.Billing.ReferralProgram.Summary(true, 50m, 50m, 2, 50m),
                ["GoogleCalendarAccess"] = new PackageFeatureAccess { FeatureCode = PackageFeatureCodes.CalendarScheduler, IsIncluded = true },
                ["GoogleCalendarConnection"] = new GoogleCalendarConnection { Id = 4, AgentUserId = 7, GoogleAccountEmail = "dana.rivers@example.test" }
            }),
            // This view names its layout itself, so it comes back inside it: the screen is what the layout's
            // main region holds. Signed out, the layout's footer has no client to look up.
            "ClientPortalProfile/Index" => MainRegion(await RenderAsync("~/Views/ClientPortalProfile/Index.cshtml", "/ClientPortalProfile", PortalProfileModel(), new(),
                user: new ClaimsPrincipal(new ClaimsIdentity()))),
            "ClientInvoices/Edit" => await RenderAsync("~/Views/ClientInvoices/Edit.cshtml", "/portal/ClientInvoices/Edit/12", InvoiceModel(), InvoiceViewData()),
            _ => throw new ArgumentOutOfRangeException(nameof(screen), screen, "not a screen these checks render")
        };
        var page = Page(fragment, screen, clientPortal);
        // For the eye only: the pages as checked, written when a folder is named (never in the gate).
        var folder = Environment.GetEnvironmentVariable("PORTAL_SCREENS_OUT");
        if (!string.IsNullOrEmpty(folder)) File.WriteAllText(Path.Combine(folder, Slug(screen) + ".html"), page);
        return page;
    }

    private static string MainRegion(string html)
    {
        var main = Regex.Match(html, "<main id=\"main-content\"[^>]*>(.*)</main>", RegexOptions.Singleline);
        Assert.True(main.Success, "the layout has no main region");
        return Regex.Replace(main.Groups[1].Value, "<h1 class=\"visually-hidden\">.*?</h1>", string.Empty, RegexOptions.Singleline);
    }

    private static async Task<string> PortalRequestsFragmentAsync()
    {
        var requests = PortalRequestsModel();
        return await RenderAsync("~/Views/PortalRequests/Index.cshtml", "/portal/PortalRequests", requests, new()
        {
            ["Status"] = "all", ["AgentTimeZone"] = "America/Toronto", ["AppointmentTimes"] = AppointmentTimes(requests)
        });
    }

    private static WebsiteNavigationViewModel NavigationModel()
    {
        var header = new WebsiteHeaderSettings
        {
            Style = "standard", ButtonText = "Book a call", ButtonUrl = "/contact", ShowPhone = true,
            Announcement = new WebsiteAnnouncementBar { Enabled = true, Message = "Our office is closed December 24 to January 2.", LinkText = "Book now", LinkUrl = "/contact" },
            Popup = new WebsitePopup { Enabled = true, Headline = "Join our mailing list", Text = "Tips a few times a year.", ImageAssetId = 501 }.Normalized()
        };
        header.CustomLinks.Add(new WebsiteCustomNavigationLink { Label = "Client Login", Url = "https://login.example.test", SortOrder = 1 });
        header.CustomLinks.Add(new WebsiteCustomNavigationLink { Label = "Book online", Url = "https://booking.example.test/rivers", SortOrder = 2 });
        return new WebsiteNavigationViewModel
        {
            Website = new AgentWebsite { Id = 70, SiteTitle = "Rivers Financial" },
            Header = header,
            Pages = new List<WebsitePage>
            {
                new() { Id = 1, Title = "Home", Slug = "home", NavigationLabel = "Home", IsHomePage = true, SortOrder = 1 },
                new() { Id = 2, Title = "Resources", Slug = "resources", NavigationLabel = "Resources", SortOrder = 2 },
                new() { Id = 3, Title = "Guides", Slug = "guides", NavigationLabel = "Guides", ParentPageId = 2, SortOrder = 3 },
                new() { Id = 4, Title = "Retirement guide", Slug = "retirement-guide", NavigationLabel = "Retirement guide", ParentPageId = 3, SortOrder = 4, ShowInNavigation = false }
            },
            AnnouncementAccess = new PackageFeatureAccess { IsIncluded = true },
            PopupAccess = new PackageFeatureAccess { IsIncluded = true },
            Pictures = MediaAssets()
        };
    }

    private static List<WebsiteMediaAsset> MediaAssets() => new()
    {
        new() { Id = 501, AgentWebsiteId = 70, OriginalFileName = "team-on-king-street.jpg", BlobUrl = "photo.png", Description = "Dana and Lee outside the King Street office" },
        new() { Id = 502, AgentWebsiteId = 70, OriginalFileName = "seminar-2026.png", BlobUrl = "photo.png?2" }
    };

    private static WebsitePageEditViewModel PageEditModel()
    {
        var nextBlock = 300;
        WebsiteContentBlock Block(string type, string heading, string settings = "{}", string image = "", string body = "") => new()
        {
            Id = ++nextBlock, BlockType = type, Heading = heading, Subheading = "Supporting line", Body = body, ImageUrl = image,
            SettingsJson = settings, SortOrder = nextBlock, IsVisible = true
        };
        var gallery = new WebsiteGallerySettings
        {
            Images =
            {
                new WebsiteGalleryImage { Url = "photo.png", ThumbUrl = "photo.png", Width = 800, Height = 600, Caption = "Our office on King Street" },
                new WebsiteGalleryImage { Url = "photo.png?b", ThumbUrl = "photo.png?b", Width = 800, Height = 600 }
            }
        };
        var reviews = new WebsiteReviewSettings { ReviewUrl = "https://maps.example.test/rivers", Rating = 4.8m, ReviewCount = 37 };
        reviews.Quotes.Add(new WebsiteReviewQuote { Name = "Sam", Stars = 5, Text = "Clear advice and quick answers." });
        var page = new WebsitePage
        {
            Id = 21, AgentWebsiteId = 70, Title = "Services", Slug = "services", NavigationLabel = "Services", IsPublished = true, SortOrder = 2,
            OldAddresses = "services.html",
            Blocks = new List<WebsiteContentBlock>
            {
                Block(WebsiteBlockTypes.Hero, "Plan with confidence", new WebsiteHeroSettings().ToJson(), "photo.png", "We help you save, invest and protect what matters."),
                Block(WebsiteBlockTypes.DidYouKnow, "Did you know?", new WebsiteDidYouKnowSettings { ArticleIds = { 11 } }.ToJson()),
                Block(WebsiteBlockTypes.AgentInfo, "Your adviser", new WebsiteAgentInfoSettings { ShowAddress = false }.ToJson()),
                Block(WebsiteBlockTypes.PollVote, "Quick poll", new WebsitePollResultsSettings { PollSurveyId = 81 }.ToJson()),
                Block(WebsiteBlockTypes.PollResults, "Poll results", new WebsitePollResultsSettings { PollSurveyId = 82 }.ToJson()),
                Block(WebsiteBlockTypes.Reviews, "What clients say", reviews.ToJson()),
                Block(WebsiteBlockTypes.Gallery, "Our office", gallery.ToJson()),
                Block(WebsiteBlockTypes.PriceList, "Fees", WebsitePriceListSettings.Starter().ToJson())
            }
        };
        return new WebsitePageEditViewModel
        {
            Page = page,
            SearchTitle = "Services | Rivers Financial",
            SearchDescription = "Rivers Financial. We help you save, invest and protect what matters.",
            AvailableParents = new List<WebsitePage> { new() { Id = 1, NavigationLabel = "Home" }, new() { Id = 2, NavigationLabel = "Resources" } },
            MediaAssets = MediaAssets(),
            AvailableVotePolls = new List<PollSurvey>
            {
                new() { Id = 81, Title = "When do you plan to retire?", TotalResponded = 4 },
                new() { Id = 83, Title = "Which topic next?", TotalResponded = 1 }
            },
            AvailableSentPolls = new List<PollSurvey> { new() { Id = 82, Title = "How did you hear about us?", TotalResponded = 14 } },
            AvailableArticles = new List<Article>
            {
                new() { Id = 11, Title = "RRSP or TFSA?", IsPublished = true },
                new() { Id = 12, Title = "Insurance at 40", IsPublished = true },
                new() { Id = 13, Title = "Budget 2026", IsPublished = false }
            }
        };
    }

    private const string DnsTarget = "ipro-prod-web.azurewebsites.net";

    private static AgentWebsite WebsiteModel()
    {
        var footer = new WebsiteFooterSettings();
        for (var day = 1; day <= 5; day++) footer.Hours.Days.Add(new WebsiteDayHours { Day = day, Opens = "09:00", Closes = "17:00" });
        footer.Hours.ShowOnContact = true;
        return new AgentWebsite
        {
            Id = 70, AgentUserId = 7, TemplateId = 1, SiteTitle = "Rivers Financial", TagLine = "Planning for every stage", IsPublished = true,
            CustomDomain = "www.riversfinancial.example", ThemeColor = "#1457d9", FooterSettingsJson = footer.ToJson()
        };
    }

    private static Dictionary<string, object?> WebsiteViewData()
    {
        var templates = new List<WebsiteTemplate>
        {
            WithId(WebsiteTemplateSeeder.CreateDefaultTemplate(), 1),
            WithId(WebsiteTemplateSeeder.CreateClassicSidebarTemplate(), 2),
            WithId(WebsiteTemplateSeeder.CreateEditorialVisualTemplate(), 3)
        };
        var domain = new AgentDomain
        {
            Id = 41, AgentUserId = 7, AgentWebsiteId = 70, DomainName = "www.riversfinancial.example", WwwDomain = "www.riversfinancial.example",
            RootDomain = "riversfinancial.example", DnsTarget = DnsTarget, IsPrimary = true,
            DnsStatus = AgentDomainStatus.Bound, AzureBindingStatus = AgentDomainStatus.Bound, SslStatus = AgentDomainStatus.Bound,
            RootDnsStatus = AgentDomainStatus.Bound, RootAzureBindingStatus = AgentDomainStatus.Bound, RootSslStatus = AgentDomainStatus.Bound,
            RootRedirectsToWww = true, OwnershipVerifiedAt = new DateTime(2026, 10, 7, 13, 0, 0, DateTimeKind.Utc),
            CreatedAt = new DateTime(2026, 10, 6, 20, 0, 0, DateTimeKind.Utc), LastCheckedAt = DateTime.UtcNow.AddMinutes(-3)
        };
        var settings = DomainSetupGuide.Settings.From(DnsTarget, "40.89.19.0", "C6D5FC2CBD50987D0FF7FE648BC710D4B2454FA211AE9094BB2816A6505E8572");
        return new Dictionary<string, object?>
        {
            ["Templates"] = templates,
            ["TemplateRetired"] = false,
            ["TemporaryDomain"] = "rivers.247advisers.com",
            ["TemporaryRootDomain"] = "247advisers.com",
            ["WebsiteDnsTarget"] = DnsTarget,
            ["WebsiteAddress"] = "40.89.19.0",
            ["DomainVerificationId"] = "C6D5FC2CBD50987D0FF7FE648BC710D4B2454FA211AE9094BB2816A6505E8572",
            ["PrimaryDomain"] = domain,
            ["AgentDomains"] = new List<AgentDomain> { domain },
            ["DomainAccess"] = new PackageFeatureAccess { FeatureCode = PackageFeatureCodes.MultiDomainSupport, IsIncluded = true, LimitValue = 3 },
            ["DomainGuides"] = new Dictionary<int, DomainSetupGuide.Guide> { [domain.Id] = DomainSetupGuide.Describe(domain, null, settings, DateTime.UtcNow) },
            ["AgentTimeZone"] = "America/Toronto"
        };
    }

    private static WebsiteTemplate WithId(WebsiteTemplate template, int id)
    {
        template.Id = id;
        template.IsActive = true;
        return template;
    }

    private static List<WebsiteLead> LeadsModel()
    {
        var page = new WebsitePage { Id = 2, Title = "Contact", Slug = "contact" };
        return new List<WebsiteLead>
        {
            new()
            {
                Id = 901, AgentUserId = 7, SubmissionType = WebsiteLeadTypes.CustomForm, FirstName = "Priya", LastName = "Shah", Email = "priya.shah@example.test",
                Phone = "416-555-0142", Message = "I would like to talk about my RRSP before March.", SourceDomain = "www.riversfinancial.example",
                WebsitePage = page, IsRead = false, Status = WebsiteLeadStatuses.New, NotificationSent = true, ClientId = 51,
                CreatedAt = new DateTime(2026, 11, 20, 15, 4, 0, DateTimeKind.Utc)
            },
            new()
            {
                Id = 902, AgentUserId = 7, SubmissionType = WebsiteLeadTypes.Newsletter, FirstName = "Tom", LastName = "Becker", Email = "tom.becker@example.test",
                IsRead = true, Status = WebsiteLeadStatuses.New, NotificationSent = false, NotificationError = "Mailbox unavailable",
                ProcessingNote = "Your package's contact limit was reached, so no CRM contact was made.",
                CreatedAt = new DateTime(2026, 11, 18, 9, 30, 0, DateTimeKind.Utc)
            },
            new()
            {
                Id = 903, AgentUserId = 7, SubmissionType = WebsiteLeadTypes.Contact, FirstName = "Ana", LastName = "Lopes", Email = "ana.lopes@example.test",
                Message = "Please call me back.", IsRead = true, Status = WebsiteLeadStatuses.Contacted, NotificationSent = true, ClientId = 52,
                CreatedAt = new DateTime(2026, 11, 12, 18, 0, 0, DateTimeKind.Utc)
            }
        };
    }

    private static List<PortalAppointmentRequest> PortalRequestsModel()
    {
        Client Person(int id, string first, string last) => new() { Id = id, FirstName = first, LastName = last, Email = $"{first.ToLowerInvariant()}@example.test" };
        return new List<PortalAppointmentRequest>
        {
            new() { Id = 61, ClientId = 31, Client = Person(31, "Sam", "Okafor"), Notes = "Annual review, mornings are best.", PreferredDate = new DateTime(2026, 12, 2), Status = PortalAppointmentRequestStatus.Pending, CreatedAt = new DateTime(2026, 11, 19, 14, 0, 0, DateTimeKind.Utc) },
            new() { Id = 62, ClientId = 32, Client = Person(32, "Mei", "Tanaka"), Notes = "Questions about my TFSA.", Status = PortalAppointmentRequestStatus.Scheduled, ClientFollowUpId = 700, CreatedAt = new DateTime(2026, 11, 10, 14, 0, 0, DateTimeKind.Utc) },
            new() { Id = 63, ClientId = 33, Client = Person(33, "Luc", "Girard"), Notes = "", Status = PortalAppointmentRequestStatus.Declined, CreatedAt = new DateTime(2026, 11, 2, 14, 0, 0, DateTimeKind.Utc) }
        };
    }

    private static Dictionary<int, DateTime> AppointmentTimes(List<PortalAppointmentRequest> requests) =>
        new() { [requests[1].Id] = new DateTime(2026, 11, 24, 10, 30, 0) };

    private static AgentProfileViewModel ProfileModel() => new()
    {
        Id = 7, UserName = "dana", DomainName = "rivers.247advisers.com", PackageName = "Professional", FirstName = "Dana", LastName = "Rivers",
        Designation = "CFP", Email = "dana@example.test", CompanyName = "Rivers Financial", CompanyAddress = "1 King St W", City = "Toronto",
        Province = "Ontario", PostalCode = "M5H 1A1", Country = "Canada", TimeZone = "America/Toronto", Phone = "416-555-0100",
        BusinessType = "Advisors", GoogleReviewUrl = "https://g.page/r/rivers-financial/review", GoogleReviewAsk = true, PhotoUrl = "photo.png"
    };

    private static PortalProfileViewModel PortalProfileModel() => new()
    {
        FirstName = "Sam", LastName = "Okafor", Email = "sam.okafor@example.test", Phone = "416-555-0177", City = "Toronto", Province = "ON",
        PostalCode = "M4C 1B5", Country = "Canada"
    };

    private static ClientInvoiceEditViewModel InvoiceModel() => new()
    {
        Id = 12, ClientId = 31, IssueDate = new DateTime(2026, 11, 21), DueDate = new DateTime(2026, 12, 6), Notes = "Thank you for your business.",
        DiscountMode = "lines",
        LineItems = new List<ClientInvoiceLineItemInputModel>
        {
            new() { Description = "Financial plan, first meeting", Quantity = 1, UnitPrice = 450m, DiscountValue = 10m },
            new() { Description = "Follow-up meeting", Quantity = 2, UnitPrice = 150m, IsTaxable = false }
        }
    };

    private static Dictionary<string, object?> InvoiceViewData() => new()
    {
        ["Clients"] = new List<Client>
        {
            new() { Id = 31, FirstName = "Sam", LastName = "Okafor" },
            new() { Id = 32, FirstName = "Mei", LastName = "Tanaka", CompanyName = "Tanaka Design" },
            new() { Id = 33, FirstName = "Luc", LastName = "Girard" }
        },
        ["TaxByClient"] = new Dictionary<int, ClientInvoiceTaxRule> { [31] = new(0.13m, "ON", "client", "HST for Ontario") },
        ["RememberedItems"] = new List<RememberedInvoiceItem> { new("Financial plan, first meeting", 450m, true, 3) },
        ["TaxNumberMissing"] = true
    };

    // ---------------------------------------------------------------- plumbing --

    private static string Slug(string screen) => screen.Replace('/', '-');

    // The page a fragment is opened in: what its layout gives it in the product, and nothing of the layout's own.
    private static string Page(string fragment, string title, bool clientPortal)
    {
        var css = SiteCss() + (clientPortal ? LayoutCss("_ClientPortalLayout.cshtml") : LayoutCss("_Layout.cshtml"));
        var main = clientPortal ? "<main id=\"main-content\" class=\"container-fluid py-4\">" : "<main id=\"main-content\" class=\"agent-content\">";
        return "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
               "<title>" + title + "</title><link rel=\"stylesheet\" href=\"https://cdn.jsdelivr.net/npm/bootstrap@5.3.0/dist/css/bootstrap.min.css\">" +
               "<style>" + css + "</style></head><body>" + main + "<h1 class=\"visually-hidden\">" + title + "</h1>" + fragment + "</main></body></html>";
    }

    // A rendered layout loads /css/site.css, which a page opened from disk cannot reach: it is put in the page.
    private static string WithPortalCss(string html) =>
        Regex.Replace(html, "<link[^>]+href=\"/css/site\\.css\"[^>]*>", "<style>" + SiteCss() + "</style>");

    private static string SiteCss() => File.ReadAllText(Path.Combine(PublicSitePages.RepoRoot(), "src", "IPRO.Web", "wwwroot", "css", "site.css"));

    // The layout's own style block, with the default accent its one Razor value stands for.
    private static string LayoutCss(string layout)
    {
        var source = File.ReadAllText(Path.Combine(PublicSitePages.RepoRoot(), "src", "IPRO.Web", "Views", "Shared", layout));
        var style = Regex.Match(source, "<style>(.*?)</style>", RegexOptions.Singleline);
        Assert.True(style.Success, $"{layout} has no style block");
        return style.Groups[1].Value.Replace("@effectiveAccent", "#1457d9").Replace("@@", "@");
    }

    private const string ActiveDescription =
        "(function(e){return e?(e.tagName+(e.id?'#'+e.id:'')+'.'+(e.className||'')+' '+(e.getAttribute('aria-label')||e.textContent||'').trim().slice(0,40)):'none';})(document.activeElement)";

    private const string RepeatedIds =
        "(function(){var seen={},twice=[];document.querySelectorAll('[id]').forEach(function(e){if(seen[e.id]&&twice.indexOf(e.id)<0)twice.push(e.id);seen[e.id]=1;});return twice.join(', ');})()";

    // WCAG 1.4.10: the page is no wider than the window. When it is, the elements reaching past the right edge
    // that no box of their own scrolls or clips, the deepest first: the first is usually the cause. The window
    // is the root's clientWidth: a phone (and Edge playing one) widens window.innerWidth to whatever the page
    // needs, so a page 402px wide reads as fitting a 402px window.
    private const string Overflow =
        "(function(){var w=document.documentElement.clientWidth,s=document.documentElement.scrollWidth;if(s<=w+1)return '';var out=[];" +
        "document.querySelectorAll('body *').forEach(function(e){var r=e.getBoundingClientRect();if(r.width===0||r.right<=w+1)return;" +
        "for(var p=e.parentElement;p&&p!==document.body;p=p.parentElement){var o=getComputedStyle(p).overflowX;if(o!=='visible')return;}" +
        "var c=typeof e.className==='string'&&e.className.trim()?'.'+e.className.trim().split(/\\s+/).join('.'):'';" +
        "var id=e.getAttribute('id');out.push({d:e.querySelectorAll('*').length,t:e.tagName.toLowerCase()+(id?'#'+id:'')+c+' (right edge '+Math.round(r.right)+')'});});" +
        "out.sort(function(a,b){return a.d-b.d;});" +
        "return 'the page is '+s+'px wide in a '+w+'px window; past the edge: '+out.slice(0,12).map(function(o){return o.t;}).join('; ');})()";

    internal static async Task<string> RenderAsync<TModel>(string viewPath, string path, TModel model, Dictionary<string, object?> viewData,
        bool withLayout = false, ClaimsPrincipal? user = null, TestDatabase? database = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(WebsitePagesController).Assembly.GetName().Name,
            ContentRootPath = Path.Combine(PublicSitePages.RepoRoot(), "src", "IPRO.Web"),
            EnvironmentName = "Production"
        });
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(WebsitePagesController).Assembly);
        if (database != null)
        {
            // What the layouts reach for: the database (the menu's "own website" switch, the client portal's
            // footer), the package and billing services (an adviser with everything included and no billing issue).
            builder.Services.AddScoped(_ => database.CreateContext());
            builder.Services.AddSingleton<IPackageEntitlementService, EverythingIncluded>();
            builder.Services.AddSingleton(DispatchProxy.Create<IPRO.Billing.IBillingService, NothingToSay>());
        }
        else
        {
            // A context that is never opened, for a view that names its layout itself (the client portal's).
            builder.Services.AddScoped(_ => new IPRODbContext(new DbContextOptionsBuilder<IPRODbContext>()
                .UseMySql("server=127.0.0.1;database=none_637", new MySqlServerVersion(new Version(8, 0, 36))).Options));
        }
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var http = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = user ?? new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "7") }, "Cookies"))
        };
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("app.iproadvisers.com");
        http.Request.Path = path;
        http.Items["CspNonce"] = "n637";
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "render"));
        var parts = viewPath.Split('/');
        var route = new Microsoft.AspNetCore.Routing.RouteData();
        route.Values["controller"] = parts[2];
        route.Values["action"] = Path.GetFileNameWithoutExtension(parts[3]);
        var actionContext = new ActionContext(http, route, new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor
        {
            ControllerName = parts[2], ActionName = Path.GetFileNameWithoutExtension(parts[3])
        });
        var engine = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Mvc.Razor.IRazorViewEngine>();
        var found = engine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: withLayout);
        Assert.True(found.Success, $"{viewPath} was not found among IPRO.Web's compiled views");
        var data = new ViewDataDictionary<TModel>(scope.ServiceProvider.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary()) { Model = model };
        foreach (var (key, value) in viewData) data[key] = value;
        await using var writer = new StringWriter();
        await found.View!.RenderAsync(new ViewContext(actionContext, found.View, data, new TempDataDictionary(http, new NoTempData()), writer, new HtmlHelperOptions()));
        return writer.ToString();
    }

    private sealed class NoTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class EverythingIncluded : IPackageEntitlementService
    {
        public Task<PackageFeatureAccess> GetAccessAsync(int agentId, string featureCode) =>
            Task.FromResult(new PackageFeatureAccess { FeatureCode = featureCode, FeatureName = featureCode, IsIncluded = true });
        public Task<bool> HasAccessAsync(int agentId, string featureCode) => Task.FromResult(true);
        public Task<Dictionary<int, bool>> HasAccessBulkAsync(IEnumerable<int> agentIds, string featureCode) =>
            Task.FromResult(agentIds.ToDictionary(id => id, _ => true));
        public Task<bool> IsAccessGatedAsync(int agentId) => Task.FromResult(false);
    }

    // The billing service the layout's BillingIssue box asks: every answer empty (no billing issue to show).
    public class NothingToSay : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var type = targetMethod!.ReturnType;
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = type.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner)
                    .Invoke(null, new[] { inner.IsValueType ? Activator.CreateInstance(inner) : null });
            }
            return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
        }
    }
}
