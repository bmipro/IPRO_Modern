using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.Business.Interfaces;
using IPRO.Entities;
using IPRO.Utility;
using IPRO.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPRO.IntegrationTests;

// 595 (2026-10-21): My Website, rendered from its real view with a real model -- the owner's own case of that day.
// The adviser's first domain is live; the one just added (www.maison1850.com) needs its proof. Before 595 the
// registrar steps were folded away behind "Show the setup steps (for adding another domain)" because the FIRST
// domain was done, and only the proof record showed beside the new one. Writes the markup to a folder when
// DOMAIN_GUIDE_SCREENS_OUT is set (never in the gate), for the pictures.
public class DomainGuideScreens595Tests
{
    internal const string Target = "ipro-prod-web.azurewebsites.net";
    internal const string Token = "ipro-ed01ef7bc81c8d709e6809e268aa3d2a";

    internal static AgentDomain LiveBakery() => new()
    {
        Id = 41, AgentUserId = 7, AgentWebsiteId = 3, DomainName = "www.lavenuebakery.com", WwwDomain = "www.lavenuebakery.com",
        RootDomain = "lavenuebakery.com", DnsTarget = Target, IsPrimary = true,
        DnsStatus = AgentDomainStatus.Bound, AzureBindingStatus = AgentDomainStatus.Bound, SslStatus = AgentDomainStatus.Bound,
        RootDnsStatus = AgentDomainStatus.Bound, RootAzureBindingStatus = AgentDomainStatus.Bound, RootSslStatus = AgentDomainStatus.Bound,
        RootRedirectsToWww = true, OwnershipVerifiedAt = new DateTime(2026, 10, 7, 13, 0, 0, DateTimeKind.Utc),
        CreatedAt = new DateTime(2026, 10, 6, 20, 0, 0, DateTimeKind.Utc), LastCheckedAt = DateTime.UtcNow.AddMinutes(-3),
    };

    internal static AgentDomain NewMaison() => new()
    {
        Id = 58, AgentUserId = 7, AgentWebsiteId = 3, DomainName = "www.maison1850.com", WwwDomain = "www.maison1850.com",
        RootDomain = "maison1850.com", DnsTarget = Target, IsPrimary = false, VerificationToken = Token,
        DnsStatus = AgentDomainStatus.PendingDns, AzureBindingStatus = AgentDomainStatus.BindingPending, SslStatus = AgentDomainStatus.BindingPending,
        RootDnsStatus = AgentDomainStatus.PendingDns, RootAzureBindingStatus = AgentDomainStatus.NotConfigured, RootSslStatus = AgentDomainStatus.NotConfigured,
        CreatedAt = DateTime.UtcNow.AddMinutes(-1), LastCheckedAt = DateTime.UtcNow.AddMinutes(-1),
        LastError = "Before maison1850.com can be connected, prove it is yours: add a TXT record named _ipro-verify with the value " + Token +
                    " where the domain's DNS is managed, then click Check now. Nothing is connected until we see it.",
    };

    [Fact]
    public async Task The_Domain_page_opens_a_numbered_guide_for_the_domain_just_added_whatever_else_is_connected()
    {
        var bakery = LiveBakery();
        var maison = NewMaison();
        var html = await RenderMyWebsiteAsync(new[] { bakery, maison }, bakery, openGuideFor: maison.Id, page: "Domains");
        Write("domains.html", html);

        // The guide is the new domain's own panel, open, from step 1.
        Assert.Contains("data-domain-guide=\"58\"", html);
        Assert.Contains("Step 1 of 4", html);
        Assert.Contains("Prove it", html);
        Assert.Contains("aria-current=\"step\"", html);
        // The registrar steps are no longer folded away because ANOTHER domain is connected.
        Assert.DoesNotContain("Show the setup steps (for adding another domain)", html);
        // The CNAME, the A record and the asuid record are in the new domain's guide.
        var guide = html[html.IndexOf("data-domain-guide=\"58\"", StringComparison.Ordinal)..];
        Assert.Contains(Token, guide);
        Assert.Contains(Target, guide);
        Assert.Contains("40.89.19.0", guide);
        // Where the email goes is checked before anything is said about it (the first look is the page's own).
        Assert.Contains("Checking where your email goes.", guide);

        // 595 (owner, 2026-10-22): My Website keeps only a card saying where the domain stands, and the way there.
        var myWebsite = await RenderMyWebsiteAsync(new[] { bakery, maison }, bakery, openGuideFor: null);
        Write("my-website.html", myWebsite);
        Assert.DoesNotContain("data-domain-guide", myWebsite);
        Assert.DoesNotContain("Custom Domain Setup", myWebsite);
        var card = myWebsite[myWebsite.IndexOf("data-domain-summary", StringComparison.Ordinal)..];
        Assert.Contains("www.lavenuebakery.com", card);
        Assert.Contains("Live and secured", card);
        Assert.Contains("and 1 more", card);
        Assert.Contains("Manage domain", card);
        var none = await RenderMyWebsiteAsync(Array.Empty<AgentDomain>(), null, openGuideFor: null);
        Assert.Contains("Connect your own domain", none);
    }

    [Fact]
    public async Task Each_step_says_what_iPro_sees_in_the_advisers_words()
    {
        var bakery = LiveBakery();
        var godaddy = new[] { "ns25.domaincontrol.com", "ns26.domaincontrol.com" };

        // Just added, first look: GoDaddy; www already a CNAME to iPro (the owner's case); the proof not there yet.
        var maison = NewMaison();
        var html = await RenderMyWebsiteAsync(new[] { bakery, maison }, bakery, openGuideFor: maison.Id, page: "Domains", looks: new Dictionary<int, DomainSetupLook>
        {
            [maison.Id] = Look(godaddy, proof: Array.Empty<string>(), cname: Target, root: new[] { "15.197.225.128", "3.33.251.168" }, asuid: Array.Empty<string>()),
        });
        Write("guide-step1.html", html);
        var guide = html[html.IndexOf("data-domain-guide=\"58\"", StringComparison.Ordinal)..];
        Assert.Contains("Your domain's DNS is managed at <strong data-guide-host-name>GoDaddy</strong>", guide);   // the view's own words are not encoded
        Assert.Contains("We don&#x27;t see this record yet.", guide);
        Assert.Contains("www.maison1850.com already points to iPro, so this step is done.", guide);
        Assert.Contains("GoDaddy&#x27;s domain forwarding is on: open the Forwarding tab for maison1850.com and delete the forwarding", guide);
        Assert.Contains("https://www.godaddy.com/help/manage-dns-records-680", guide);
        Assert.Contains("aria-current=\"step\"", guide);
        Assert.Contains("aria-expanded=\"true\" aria-controls=\"guide-58-step-1\"", guide);
        // The live domain's guide is folded, one click away.
        Assert.Contains("data-domain-guide=\"41\"", html);
        Assert.Matches("data-domain-guide=\"41\"[^>]*hidden", html);

        // Proven; www still at the old shop.
        var proven = NewMaison();
        proven.OwnershipVerifiedAt = DateTime.UtcNow;
        html = await RenderMyWebsiteAsync(new[] { bakery, proven }, bakery, openGuideFor: proven.Id, page: "Domains", looks: new Dictionary<int, DomainSetupLook>
        {
            [proven.Id] = Look(godaddy, proof: new[] { Token }, cname: "lavenuebakery.myshopify.com", root: new[] { "23.227.38.65" }, asuid: Array.Empty<string>()),
        });
        Write("guide-step2.html", html);
        Assert.Contains("Right now www.maison1850.com points to lavenuebakery.myshopify.com. Edit that record and change its value to " + Target + ".", html);
        Assert.Contains("GoDaddy already has a www record", html);

        // Connected; the padlock on its way; the short address being connected.
        var finishing = NewMaison();
        finishing.OwnershipVerifiedAt = DateTime.UtcNow;
        finishing.DnsStatus = finishing.AzureBindingStatus = AgentDomainStatus.Bound;
        finishing.RootDnsStatus = finishing.RootAzureBindingStatus = AgentDomainStatus.Bound;
        finishing.RootSslStatus = AgentDomainStatus.BindingPending;
        finishing.RootLastError = "We are securing maison1850.com. This usually takes a few minutes and happens automatically.";
        html = await RenderMyWebsiteAsync(new[] { bakery, finishing }, bakery, openGuideFor: finishing.Id, page: "Domains", looks: new Dictionary<int, DomainSetupLook>
        {
            [finishing.Id] = Look(godaddy, proof: new[] { Token }, cname: Target, root: new[] { "40.89.19.0" }, asuid: new[] { Asuid }),
        });
        Write("guide-step4.html", html);

        // For the eye: the endpoint's answers for the same three states, as the page receives them.
        var settings = DomainSetupGuide.Settings.From(Target, "40.89.19.0", Asuid);
        WriteJson("state1.json", DomainSetupGuide.Describe(maison, Look(godaddy, Array.Empty<string>(), Target, new[] { "15.197.225.128", "3.33.251.168" }, Array.Empty<string>()), settings, DateTime.UtcNow));
        WriteJson("state2.json", DomainSetupGuide.Describe(proven, Look(godaddy, new[] { Token }, "lavenuebakery.myshopify.com", new[] { "23.227.38.65" }, Array.Empty<string>()), settings, DateTime.UtcNow));
        WriteJson("state4.json", DomainSetupGuide.Describe(finishing, Look(godaddy, new[] { Token }, Target, new[] { "40.89.19.0" }, new[] { Asuid }), settings, DateTime.UtcNow));
        Assert.Contains("Step 4 of 4: iPro finishes", html);
        Assert.Contains("Ordered. It usually arrives within a few minutes, and it renews itself.", html);
        Assert.Contains("We are securing maison1850.com.", html);
    }

    private const string Asuid = "C6D5FC2CBD50987D0FF7FE648BC710D4B2454FA211AE9094BB2816A6505E8572";

    private static DomainSetupLook Look(string[] ns, string[] proof, string cname, string[] root, string[] asuid) => new()
    {
        CheckedAtUtc = DateTime.UtcNow, FromZoneServers = true, Zone = "maison1850.com", NameServers = ns,
        ProofValues = proof, WwwCname = cname, WwwAddresses = new[] { cname == Target ? "40.89.19.0" : "23.227.38.65" },
        RootAddresses = root, AsuidValues = asuid, PlatformAddresses = new[] { "40.89.19.0" },
        MailServers = new[] { "maison1850-com.mail.protection.outlook.com" }, MailOnShortAddress = false,
    };

    // ---------------------------------------------------------------- plumbing --

    internal static async Task<string> RenderMyWebsiteAsync(IReadOnlyList<AgentDomain> domains, AgentDomain? primary, int? openGuideFor,
        string page = "Index", IDictionary<int, DomainSetupLook>? looks = null)
    {
        var template = new WebsiteTemplate { Id = 1, Name = "Modern", TemplateKey = "modern", IsActive = true, IsDefault = true };
        var website = new AgentWebsite
        {
            Id = 3, AgentUserId = 7, TemplateId = 1, SiteTitle = "L'Avenue Boulangerie", TagLine = "Since 1850", IsPublished = true,
            CustomDomain = primary?.DomainName ?? string.Empty, ThemeColor = "#1457d9",
        };
        var data = new Dictionary<string, object?>
        {
            ["Templates"] = new List<WebsiteTemplate> { template },
            ["TemplateRetired"] = false,
            ["TemporaryDomain"] = "lavenue.247advisers.com",
            ["TemporaryRootDomain"] = "247advisers.com",
            ["WebsiteDnsTarget"] = Target,
            ["WebsiteAddress"] = "40.89.19.0",
            ["DomainVerificationId"] = "C6D5FC2CBD50987D0FF7FE648BC710D4B2454FA211AE9094BB2816A6505E8572",
            ["PrimaryDomain"] = primary,
            ["AgentDomains"] = domains.ToList(),
            ["DomainAccess"] = new PackageFeatureAccess { FeatureCode = PackageFeatureCodes.MultiDomainSupport, IsIncluded = true, LimitValue = 3 },
            ["AgentTimeZone"] = "America/Toronto",
        };
        var settings = DomainSetupGuide.Settings.From(Target, "40.89.19.0", "C6D5FC2CBD50987D0FF7FE648BC710D4B2454FA211AE9094BB2816A6505E8572");
        data["DomainGuides"] = domains.ToDictionary(d => d.Id, d => DomainSetupGuide.Describe(d, looks != null && looks.TryGetValue(d.Id, out var look) ? look : null, settings, DateTime.UtcNow));
        data["OpenDomainGuide"] = openGuideFor;
        return await RenderWebAsync($"~/Views/Website/{page}.cshtml", website, data);
    }

    internal static async Task<string> RenderWebAsync<TModel>(string viewPath, TModel model, IDictionary<string, object?> data)
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(new Microsoft.AspNetCore.Builder.WebApplicationOptions
        {
            ApplicationName = typeof(WebsiteController).Assembly.GetName().Name,
            ContentRootPath = Path.Combine(RepoRoot(), "src", "IPRO.Web"),
            EnvironmentName = "Production"
        });
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(WebsiteController).Assembly);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var http = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "7") }, "test"))
        };
        http.Items["CspNonce"] = "render595";
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "render"));
        // The controller's name is where a partial named on its own is looked for (Views/Website/...).
        var route = new Microsoft.AspNetCore.Routing.RouteData();
        route.Values["controller"] = viewPath.Split('/')[2];
        route.Values["action"] = "Index";
        var actionContext = new ActionContext(http, route, new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var engine = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Mvc.Razor.IRazorViewEngine>();
        var found = engine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: false);
        Assert.True(found.Success, $"{viewPath} was not found among the web app's compiled views");
        var viewData = new Microsoft.AspNetCore.Mvc.ViewFeatures.ViewDataDictionary<TModel>(
            new Microsoft.AspNetCore.Mvc.ModelBinding.EmptyModelMetadataProvider(), new Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary()) { Model = model };
        foreach (var (key, value) in data) viewData[key] = value;
        var tempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(http, new NullTempData());
        await using var writer = new StringWriter();
        await found.View!.RenderAsync(new Microsoft.AspNetCore.Mvc.Rendering.ViewContext(
            actionContext, found.View, viewData, tempData, writer, new Microsoft.AspNetCore.Mvc.ViewFeatures.HtmlHelperOptions()));
        return writer.ToString();
    }

    // For the eye only: the markup inside the portal's stylesheets, written when a folder is named.
    internal static void Write(string name, string body)
    {
        var folder = Environment.GetEnvironmentVariable("DOMAIN_GUIDE_SCREENS_OUT");
        if (string.IsNullOrEmpty(folder)) return;
        File.WriteAllText(Path.Combine(folder, name),
            "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
            "<link href=\"https://cdn.jsdelivr.net/npm/bootstrap@5.3.0/dist/css/bootstrap.min.css\" rel=\"stylesheet\">" +
            "<link href=\"https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.5.0/css/all.min.css\" rel=\"stylesheet\">" +
            "<link href=\"site.css\" rel=\"stylesheet\">" +
            "</head><body style=\"background:#f4f6fb\"><main class=\"container-fluid py-4\" style=\"max-width:1400px\">" + body +
            "</main><script src=\"https://cdn.jsdelivr.net/npm/bootstrap@5.3.0/dist/js/bootstrap.bundle.min.js\"></script>" +
            "<script src=\"/js/domain-guide.js\"></script></body></html>");
    }

    private static void WriteJson(string name, DomainSetupGuide.Guide guide)
    {
        var folder = Environment.GetEnvironmentVariable("DOMAIN_GUIDE_SCREENS_OUT");
        if (string.IsNullOrEmpty(folder)) return;
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) },
        };
        File.WriteAllText(Path.Combine(folder, name), System.Text.Json.JsonSerializer.Serialize(guide, options));
    }

    internal static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private sealed class NullTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
