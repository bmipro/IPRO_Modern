using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Controllers;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPRO.IntegrationTests.Accessibility;

// 578: a public website as visitors get it, rendered by the real compiled views of IPRO.Web from a
// site built in memory: every design, a page of every section type, a three-level menu, the footer
// with everything switched on. Written to a folder with the stylesheet the pages load (Bootstrap, here
// 5.3.3 from the .NET web template; the live pages load 5.3.0 from a CDN) so headless Edge can open it.
internal static class PublicSitePages
{
    public const string Modern = "modern";
    public const string Classic = "classic";
    public const string Editorial = "editorial";
    public static readonly string[] Designs = { Modern, Classic, Editorial };

    // The 14 colours My Website offers (Views/Website/Index.cshtml): the three looks' and designs'
    // accents and the eight palettes. Each one must give readable text in every design.
    public static readonly (string Name, string Hex)[] OfferedColours =
    {
        ("Ocean Blue", "#1457d9"), ("Sunset Orange", "#d9541f"), ("Forest Green", "#1f7a4d"), ("Slate Gray", "#4b5563"),
        ("Burgundy", "#7a1f3d"), ("Royal Purple", "#5b2f9e"), ("Amber Gold", "#b8892b"), ("Teal", "#0f8c8c"),
        ("Heritage Green", "#1F6B4D"), ("Modern Minimal", "#0EA5E9"), ("Classic Navy", "#1B3A5C"),
        ("Classic Sidebar", "#315b46"), ("Editorial Visual", "#b42318"), ("Professional Adviser", "#1457d9")
    };

    private static readonly SemaphoreSlim AppGate = new(1, 1);
    private static WebApplication? _app;

    // A 1 x 1 grey PNG, written beside each page as the photo every picture points at.
    private static readonly byte[] Photo = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    public sealed class Site
    {
        public AgentWebsite Website { get; init; } = null!;
        public List<WebsitePage> Pages { get; init; } = new();
        public Dictionary<int, PublicFormBlockData> Forms { get; } = new();
        public Dictionary<int, PollVoteBlockData> PollVotes { get; } = new();
        public Dictionary<int, PollResultsBlockData> PollResults { get; } = new();
        public Dictionary<int, DidYouKnowBlockData> DidYouKnow { get; } = new();
        public Dictionary<int, Article> Articles { get; } = new();
        public Dictionary<int, BlogBlockData> Blogs { get; } = new();
        public List<TestimonialSubmission> Testimonials { get; } = new();

        public PublicWebsiteViewModel Model(string? slug)
        {
            var page = slug == null ? null : Pages.SingleOrDefault(p => p.Slug == slug);
            return new PublicWebsiteViewModel
            {
                Website = Website,
                Pages = Pages,
                CurrentPage = page,
                PageNotFound = slug != null && page == null,
                FormsByBlockId = Forms,
                PollVoteByBlockId = PollVotes,
                PollResultsByBlockId = PollResults,
                DidYouKnowByBlockId = DidYouKnow,
                ArticleContentByBlockId = Articles,
                BlogByBlockId = Blogs,
                ApprovedTestimonials = Testimonials
            };
        }
    }

    public static readonly string[] Slugs = { "home", "contact", "forms", "resources", "guides" };

    public static Site Build(string design, string? themeColor = null, Action<AgentWebsite>? tweak = null)
    {
        var template = design switch
        {
            Classic => WebsiteTemplateSeeder.CreateClassicSidebarTemplate(),
            Editorial => WebsiteTemplateSeeder.CreateEditorialVisualTemplate(),
            _ => WebsiteTemplateSeeder.CreateDefaultTemplate()
        };
        var agent = new AgentUser
        {
            Id = 7, FirstName = "Dana", LastName = "Rivers", CompanyName = "Rivers Financial", Email = "dana@example.test",
            Phone = "416-555-0100", City = "Toronto", Province = "ON", Country = "Canada", BusinessType = "Advisors",
            PhotoUrl = "photo.png"
        };
        var footer = new WebsiteFooterSettings
        {
            CopyrightText = "Rivers Financial", Phone = "416-555-0100", Email = "dana@example.test", Address = "1 King St W, Toronto",
            ShowDisclaimer = true,
            DisclaimerText = "Mutual funds are offered through a registered dealer. Commissions, fees and expenses may apply.",
            SocialLinks = { new WebsiteSocialLink { Platform = "facebook", Url = "https://facebook.example/rivers", SortOrder = 1 } },
            LegalLinks = { new WebsiteFooterLink { Label = "Privacy", Url = "/privacy", SortOrder = 1 } }
        };
        footer.Hours.ShowInFooter = true;
        for (var day = 1; day <= 5; day++) footer.Hours.Days.Add(new WebsiteDayHours { Day = day, Opens = "09:00", Closes = "17:00" });
        var website = new AgentWebsite
        {
            Id = 70, AgentUserId = agent.Id, SiteTitle = "Rivers Financial", TagLine = "Planning for every stage",
            ThemeColor = themeColor ?? WebsiteTemplateDesign.FromTemplate(template).AccentColor,
            HeaderSettingsJson = new WebsiteHeaderSettings { ButtonText = "Book a call", ButtonUrl = "/contact", ShowPhone = true }.ToJson(),
            FooterSettingsJson = footer.ToJson(),
            IsPublished = true, AgentUser = agent, Template = template
        };
        tweak?.Invoke(website);
        var site = new Site { Website = website };
        var nextBlock = 1000;
        WebsiteContentBlock Block(string type, string heading, string sub = "", string body = "", string image = "", string button = "", string settings = "{}", string variant = "")
            => new() { Id = ++nextBlock, BlockType = type, Heading = heading, Subheading = sub, Body = body, ImageUrl = image, ButtonText = button, ButtonUrl = button.Length > 0 ? "/contact" : "", SettingsJson = settings, LayoutVariant = variant, SortOrder = nextBlock, IsVisible = true };
        WebsitePage Page(int id, string slug, string title, int? parent, params WebsiteContentBlock[] blocks)
        {
            var page = new WebsitePage { Id = id, AgentWebsiteId = website.Id, ParentPageId = parent, Slug = slug, Title = title, NavigationLabel = title, IsHomePage = slug == "home", SortOrder = id, Blocks = blocks.ToList() };
            site.Pages.Add(page);
            return page;
        }

        var gallery = new WebsiteGallerySettings
        {
            Images =
            {
                new WebsiteGalleryImage { Url = "photo.png", ThumbUrl = "photo.png", Width = 800, Height = 600, Caption = "Our office on King Street" },
                new WebsiteGalleryImage { Url = "photo.png", ThumbUrl = "photo.png", Width = 800, Height = 600, Caption = "The team at the spring seminar" }
            }
        };
        var reviews = new WebsiteReviewSettings { Rating = 4.8m, ReviewCount = 37, ReviewUrl = "https://reviews.example/rivers", Quotes = { new WebsiteReviewQuote { Name = "Sam", Stars = 5, Text = "Clear advice and quick answers." } } };
        Page(1, "home", "Home", null,
            Block(WebsiteBlockTypes.Hero, "Plan with confidence", "Advice for families and small businesses", "We help you save, invest and protect what matters.", "photo.png", "Book a meeting", new WebsiteHeroSettings().ToJson()),
            Block(WebsiteBlockTypes.Services, "What we do", "Planning that fits", "Retirement planning\nInsurance\nEducation savings"),
            Block(WebsiteBlockTypes.Text, "About Dana", "Who we are", "Twenty years of helping clients plan.", "photo.png", "Meet the team"),
            Block(WebsiteBlockTypes.Reviews, "What clients say", "Reviews", "", "", "", reviews.ToJson()),
            Block(WebsiteBlockTypes.Gallery, "Our office", "Photos", "", "", "", gallery.ToJson()),
            Block(WebsiteBlockTypes.AgentInfo, "Your adviser", "Meet Dana", "", "", "", new WebsiteAgentInfoSettings().ToJson()),
            Block(WebsiteBlockTypes.CallToAction, "Ready to talk?", "Book a free first meeting", "", "", "Get in touch"),
            Block(WebsiteBlockTypes.CallToAction, "Still deciding?", "Read our guides first", "", "", "See the guides", "{}", "banner"),
            Block(WebsiteBlockTypes.Video, "A short welcome", "Video", "", "", "", new WebsiteVideoSettings { VideoUrl = "https://www.youtube.com/watch?v=dQw4w9WgXcQ" }.ToJson()));

        Page(2, "contact", "Contact", null,
            Block(WebsiteBlockTypes.ContactForm, "Contact us", "We reply within a day", "Send a message or call.", "", "", new WebsiteContactFormSettings().ToJson()),
            Block(WebsiteBlockTypes.NewsletterSignup, "Our newsletter", "One email a month"));

        var formBlock = Block(WebsiteBlockTypes.Form, "Request a meeting", "Meetings");
        site.Forms[formBlock.Id] = new PublicFormBlockData
        {
            WebsiteFormId = 501, Title = "Request a meeting", Description = "Tell us what you would like to discuss.", SubmitButtonText = "Request meeting",
            Fields =
            {
                new PublicFormField { FieldId = 1, FieldType = WebsiteFormFieldTypes.Section, Label = "About the meeting", HelpText = "We will confirm by email." },
                new PublicFormField { FieldId = 2, FieldType = WebsiteFormFieldTypes.Dropdown, Label = "Preferred office", IsRequired = true, Options = { "Toronto", "Ottawa" } },
                new PublicFormField { FieldId = 3, FieldType = WebsiteFormFieldTypes.CheckboxGroup, Label = "Topics", IsRequired = true, HelpText = "Pick one or more.", Options = { "Retirement", "Insurance" } },
                new PublicFormField { FieldId = 4, FieldType = WebsiteFormFieldTypes.DateTime, Label = "Preferred time" },
                new PublicFormField { FieldId = 5, FieldType = WebsiteFormFieldTypes.Text, Label = "Company", Placeholder = "Optional" },
                new PublicFormField { FieldId = 6, FieldType = WebsiteFormFieldTypes.Textarea, Label = "Anything else", IsRequired = true }
            }
        };
        var pollVote = Block(WebsiteBlockTypes.PollVote, "Quick poll", "Have your say");
        site.PollVotes[pollVote.Id] = new PollVoteBlockData
        {
            BlockId = pollVote.Id, SurveyId = 81, Title = "Quick poll",
            Questions = { new PollVoteQuestion { Id = 1, Text = "When do you plan to retire?", Options = { new PollVoteOption { Id = 1, Text = "Within 5 years" }, new PollVoteOption { Id = 2, Text = "Later" } } } }
        };
        var pollResults = Block(WebsiteBlockTypes.PollResults, "Poll results", "Results");
        site.PollResults[pollResults.Id] = new PollResultsBlockData
        {
            Title = "Poll results", TotalResponded = 12,
            Questions = { new PollResultsQuestion { Text = "When do you plan to retire?", Options = { new PollResultsOption { Text = "Within 5 years", Count = 5, Percent = 42 }, new PollResultsOption { Text = "Later", Count = 7, Percent = 58 } } } }
        };
        site.Testimonials.Add(new TestimonialSubmission { FirstName = "Lee", LastName = "Chan", Body = "Dana made it simple." });
        Page(3, "forms", "Forms", null,
            formBlock,
            Block(WebsiteBlockTypes.TestimonialForm, "Share your experience", "Testimonials"),
            pollVote,
            pollResults,
            Block(WebsiteBlockTypes.LeadMagnet, "Free retirement checklist", "Download"));

        var dyk = Block(WebsiteBlockTypes.DidYouKnow, "Did you know?", "Tips");
        site.DidYouKnow[dyk.Id] = new DidYouKnowBlockData
        {
            Teasers = { new DidYouKnowTeaser { ArticleId = 1, Title = "RRSP or TFSA?", Excerpt = "Which account suits you depends on your tax rate." },
                        new DidYouKnowTeaser { ArticleId = 2, Title = "Insurance at 40", Excerpt = "What changes when your family grows." } }
        };
        var article = Block(WebsiteBlockTypes.ArticleContent, "Article");
        site.Articles[article.Id] = new Article { Id = 11, Title = "Five ways to save on tax", Content = "<p>Use your RRSP room first.</p>", ImageUrl = "photo.png", IsPublished = true };
        var blog = Block(WebsiteBlockTypes.Blog, "News", "Blog");
        site.Blogs[blog.Id] = new BlogBlockData
        {
            Posts = { new Article { Id = 12, Title = "Budget 2026", Summary = "What the budget means for you.", ImageUrl = "photo.png", PublishedAt = new DateTime(2026, 3, 1), IsPublished = true } }
        };
        var priceList = WebsitePriceListSettings.Starter();
        if (priceList.Sections.Count > 0 && priceList.Sections[0].Items.Count > 0) priceList.Sections[0].Items[0].Available = false;
        Page(4, "resources", "Resources", null,
            dyk,
            article,
            blog,
            Block(WebsiteBlockTypes.SectionIndex, "Guides", "Browse"),
            Block(WebsiteBlockTypes.Calculator, "Mortgage payments", "Calculator", "", "", "", new WebsiteCalculatorSettings { CalculatorKind = CalculatorKinds.MortgagePayment }.ToJson()),
            Block(WebsiteBlockTypes.PriceList, "Fees", "Prices", "", "", "", priceList.ToJson()));

        // Two levels under Resources (a mega panel) and a plain dropdown under Contact.
        Page(5, "guides", "Guides", 4, Block(WebsiteBlockTypes.Text, "Guides", "Read", "Our plain-language guides."));
        Page(6, "retirement-guide", "Retirement guide", 5, Block(WebsiteBlockTypes.Text, "Retirement guide", "", "Start early."));
        Page(7, "tax-guide", "Tax guide", 5, Block(WebsiteBlockTypes.Text, "Tax guide", "", "Use your room."));
        Page(8, "faq", "FAQ", 4, Block(WebsiteBlockTypes.Text, "FAQ", "", "Answers."));
        Page(9, "directions", "Directions", 2, Block(WebsiteBlockTypes.Text, "Directions", "", "Near Union Station."));
        foreach (var page in site.Pages) page.AgentWebsite = website;
        return site;
    }

    public sealed class RenderOptions
    {
        /// <summary>A turned-down form to show again, kept as the controller keeps it.</summary>
        public IPRO.Web.Infrastructure.PublicFormRetry? Retry { get; set; }
        /// <summary>579: the address asked for, when it is not the page's own (/accessibility).</summary>
        public string? Path { get; set; }
        /// <summary>579: ViewBag values the controller sets (the preview flags).</summary>
        public Dictionary<string, object?> ViewBag { get; } = new();
    }

    public static async Task<string> RenderAsync(PublicWebsiteViewModel model, RenderOptions? options = null)
    {
        options ??= new RenderOptions();
        var path = options.Path ?? (model.CurrentPage == null || model.CurrentPage.IsHomePage ? "/" : "/" + model.CurrentPage.Slug);
        var viewData = new ViewDataDictionary<PublicWebsiteViewModel>(
            new Microsoft.AspNetCore.Mvc.ModelBinding.EmptyModelMetadataProvider(), new Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary()) { Model = model };
        foreach (var (key, value) in options.ViewBag) viewData[key] = value;
        // The controller's name is how a view's partials are found in its own folder.
        return await RenderCoreAsync("~/Views/PublicWebsite/Index.cshtml", "PublicWebsite", "rivers.example.test", path, viewData, tempData => options.Retry?.Save(tempData));
    }

    /// <summary>
    /// 579: another public view of IPRO.Web (iPro's own pages, which carry no website model), rendered
    /// the same way, its layout included.
    /// </summary>
    public static Task<string> RenderViewAsync(string viewPath, string controller, string path) =>
        RenderCoreAsync(viewPath, controller, "app.iproadvisers.com", path,
            new ViewDataDictionary(new Microsoft.AspNetCore.Mvc.ModelBinding.EmptyModelMetadataProvider(), new Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary()), null);

    private static async Task<string> RenderCoreAsync(string viewPath, string controller, string host, string path, ViewDataDictionary viewData, Action<TempDataDictionary>? fillTempData)
    {
        var app = await AppAsync();
        using var scope = app.Services.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider, User = new ClaimsPrincipal(new ClaimsIdentity()) };
        http.Request.Scheme = "https";
        http.Request.Host = new HostString(host);
        http.Request.Path = path;
        http.Items["IproPublicPath"] = http.Request.Path.Value;
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "render"));
        var routeData = new Microsoft.AspNetCore.Routing.RouteData();
        routeData.Values["controller"] = controller;
        routeData.Values["action"] = "Index";
        var actionContext = new ActionContext(http, routeData, new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor { ControllerName = controller, ActionName = "Index" });
        var engine = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Mvc.Razor.IRazorViewEngine>();
        var found = engine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: true);
        Assert.True(found.Success, $"{viewPath} was not found among IPRO.Web's compiled views");
        var tempData = new TempDataDictionary(http, new FixedTempData());
        fillTempData?.Invoke(tempData);
        await using var writer = new StringWriter();
        await found.View!.RenderAsync(new ViewContext(actionContext, found.View, viewData, tempData, writer, new HtmlHelperOptions()));
        return writer.ToString();
    }

    /// <summary>Writes the page and what it loads to a new folder; returns the page's file.</summary>
    public static string WriteToDisk(string html, string name)
    {
        var folder = Path.Combine(Path.GetTempPath(), "ipro-a11y-pages", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var vendor = Path.Combine(AppContext.BaseDirectory, "Accessibility", "vendor");
        File.Copy(Path.Combine(vendor, "bootstrap-5.3.3.min.css"), Path.Combine(folder, "bootstrap.min.css"));
        File.WriteAllBytes(Path.Combine(folder, "photo.png"), Photo);
        html = html.Replace("https://cdn.jsdelivr.net/npm/bootstrap@5.3.0/dist/css/bootstrap.min.css", "bootstrap.min.css", StringComparison.Ordinal);
        html = Regex.Replace(html, "<link[^>]+(cdnjs\\.cloudflare\\.com|fonts\\.googleapis\\.com)[^>]*>", string.Empty);
        var file = Path.Combine(folder, name + ".html");
        File.WriteAllText(file, html);
        return file;
    }

    public static string AxeSource() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Accessibility", "vendor", "axe.min.js"));

    private static async Task<WebApplication> AppAsync()
    {
        if (_app != null) return _app;
        await AppGate.WaitAsync();
        try
        {
            if (_app != null) return _app;
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(PublicWebsiteController).Assembly.GetName().Name,
                ContentRootPath = Path.Combine(RepoRoot(), "src", "IPRO.Web"),
                EnvironmentName = "Production"
            });
            builder.Services.AddControllersWithViews().AddApplicationPart(typeof(PublicWebsiteController).Assembly);
            _app = builder.Build();
            return _app;
        }
        finally
        {
            AppGate.Release();
        }
    }

    public static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private sealed class FixedTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
