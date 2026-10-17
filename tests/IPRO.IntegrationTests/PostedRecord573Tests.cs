using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Utility;
using IPRO.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace IPRO.IntegrationTests;

// 573 (2026-10-13). Found by the security audit of the Template Builder work, in code that was
// already live: My Website's save took the whole website record from the form, the logo's address
// included. A signed-in customer could post another customer's picture as "their logo" (with a
// harmless-looking ?z on the end), post once more, and the address being replaced was deleted from
// storage by its path: the "is this file still used?" check compares addresses as text and found
// nothing under that spelling. Two more forms added the posted record whole (a new contact, a new
// newsletter), which lets a request write related rows it names.
//
// Three things now hold, each tested here: a posted record is only its plain fields; the logo's
// address comes from an upload and nowhere else; and storage deletes a file only under the exact
// address it gave that file.
public class PostedRecord573Tests
{
    private const string OwnLogo = "https://acct.example.test/agent-logos/0001aaaa_own-logo.png";
    private const string SomeoneElses = "https://acct.example.test/agent-photos/0002bbbb_their-photo.jpg";

    // ---------------------------------------------------------------- what the binder really does

    [Fact]
    public async Task The_form_binder_fills_whatever_the_request_names_and_Clean_leaves_the_plain_fields()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        // This is the request a browser never sends and a person with a signed-in session can.
        var client = await BindAsync<Client>(new Dictionary<string, StringValues>
        {
            ["FirstName"] = "Ada",
            ["LastName"] = "Lovelace",
            ["Email"] = "ada@example.test",
            ["Id"] = "987654",
            ["PortalDocuments[0].FileName"] = "x.pdf",
            ["PortalDocuments[0].BlobUrl"] = SomeoneElses,
            ["AgentUser.Email"] = "made-up@example.test",
            ["AgentUser.UserName"] = "made-up",
        });

        Assert.Equal(987654, client.Id);
        Assert.Equal(SomeoneElses, Assert.Single(client.PortalDocuments).BlobUrl);
        Assert.Equal("made-up@example.test", client.AgentUser.Email);

        PostedRecord.Clean(db, client);

        Assert.Equal(0, client.Id);
        Assert.Empty(client.PortalDocuments);
        Assert.Null(client.AgentUser);
        Assert.Empty(client.Messages);
        Assert.Empty(client.Categories);
        Assert.Equal("Ada", client.FirstName);
        Assert.Equal("Lovelace", client.LastName);
        Assert.Equal("ada@example.test", client.Email);

        // The same for the website record, whose logo address was the way in.
        var site = await BindAsync<AgentWebsite>(new Dictionary<string, StringValues>
        {
            ["SiteTitle"] = "Mine",
            ["LogoUrl"] = SomeoneElses + "?z",
            ["Pages[0].Title"] = "A page",
            ["Pages[0].Slug"] = "a-page",
        });
        Assert.Equal(SomeoneElses + "?z", site.LogoUrl);
        Assert.Single(site.Pages);
        PostedRecord.Clean(db, site);
        Assert.Empty(site.Pages);
        Assert.Empty(site.Domains);
        Assert.Empty(site.MediaAssets);
        Assert.Equal("Mine", site.SiteTitle);
    }

    [Fact]
    public void Every_form_that_adds_the_posted_record_itself_cleans_it_first()
    {
        var folder = Path.Combine(RepoRoot(), "src", "IPRO.Web", "Controllers");
        var adds = new Regex(@"(CreateAsync|AddAsync|\.Add|\.Update|UpdateAsync|Attach)\(model\)");
        var found = 0;
        foreach (var file in Directory.GetFiles(folder, "*.cs"))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in adds.Matches(text))
            {
                found++;
                var method = text.LastIndexOf("public async Task<IActionResult>", match.Index, StringComparison.Ordinal);
                Assert.True(method >= 0, $"{Path.GetFileName(file)}: {match.Value} is outside an action");
                Assert.True(text[method..match.Index].Contains("PostedRecord.Clean(_db, model)", StringComparison.Ordinal),
                    $"{Path.GetFileName(file)} adds the posted record whole ({match.Value}) without PostedRecord.Clean before it.");
            }
        }
        // None since 588: the contact and the newsletter build their own record from the form's fields, as
        // My Website does (PostedFields588Tests holds every action that takes a stored record).
        Assert.Equal(0, found);
        var website = File.ReadAllText(Path.Combine(folder, "WebsiteController.cs"));
        Assert.DoesNotContain("CreateAsync(model)", website);
        Assert.Contains("model.LogoUrl = string.Empty;", website);
    }

    // ---------------------------------------------------------------- My Website

    [Fact]
    public async Task A_logo_address_in_the_request_is_not_stored_and_deletes_nothing()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, templateId) = await AgentAsync(db, withSite: true);
        var blob = new RecordingBlob();
        var controller = Controller(db, agentId, blob);

        // First post: "my logo is somebody else's picture, spelled so that nothing seems to use it".
        var first = await controller.Save(Posted(templateId, SomeoneElses + "?z"), logo: null);
        Assert.Equal("Index", Assert.IsType<RedirectToActionResult>(first).ActionName);
        Assert.Null(((RedirectToActionResult)first).ControllerName);
        // Second post: anything else. Before 573 this deleted the first address's file from storage.
        await controller.Save(Posted(templateId, "https://elsewhere.example.test/x.png"), logo: null);

        Assert.Empty(blob.Deleted);
        Assert.Empty(blob.Uploaded);
        db.ChangeTracker.Clear();
        Assert.Equal(OwnLogo, (await db.AgentWebsites.SingleAsync(w => w.AgentUserId == agentId)).LogoUrl);
    }

    [Fact]
    public async Task An_uploaded_logo_still_replaces_the_old_one_and_the_old_file_goes()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, templateId) = await AgentAsync(db, withSite: true);
        var blob = new RecordingBlob();

        await Controller(db, agentId, blob).Save(Posted(templateId, SomeoneElses + "?z"), logo: Png("new logo.png"));

        db.ChangeTracker.Clear();
        var uploaded = Assert.Single(blob.Uploaded);
        Assert.Equal(uploaded, (await db.AgentWebsites.SingleAsync(w => w.AgentUserId == agentId)).LogoUrl);
        // The address replaced is the one that was on the row, never one from the request.
        Assert.Equal(OwnLogo, Assert.Single(blob.Deleted));
    }

    [Fact]
    public async Task A_first_save_writes_the_form_s_fields_and_nothing_else_the_request_named()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, templateId) = await AgentAsync(db, withSite: false);
        var accounts = await db.AgentUsers.CountAsync();

        var posted = Posted(templateId, SomeoneElses);
        posted.Id = 424242;
        posted.AgentUserId = agentId + 1000;
        posted.IsPublished = true;
        posted.HeaderSettingsJson = "{\"injected\":true}";
        posted.AgentUser = new AgentUser { UserName = "made-up", Email = "made-up@example.test", FirstName = "M", LastName = "U", DomainName = "made-up", Country = "Canada", Province = "Ontario" };
        posted.Pages.Add(new WebsitePage { Title = "Injected", Slug = "injected" });
        posted.MediaAssets.Add(new WebsiteMediaAsset { OriginalFileName = "x.jpg", BlobUrl = SomeoneElses });
        var blob = new RecordingBlob();

        var result = await Controller(db, agentId, blob).Save(posted, logo: null);

        Assert.Equal("Index", Assert.IsType<RedirectToActionResult>(result).ActionName);
        db.ChangeTracker.Clear();
        var site = await db.AgentWebsites.SingleAsync(w => w.AgentUserId == agentId);
        Assert.NotEqual(424242, site.Id);
        Assert.Equal("Mine", site.SiteTitle);
        Assert.False(site.IsPublished);
        Assert.Equal("{}", site.HeaderSettingsJson);
        Assert.Equal(string.Empty, site.LogoUrl);
        Assert.Equal(0, await db.WebsitePages.CountAsync(p => p.AgentWebsiteId == site.Id));
        Assert.Equal(0, await db.WebsiteMediaAssets.CountAsync(m => m.AgentWebsiteId == site.Id));
        Assert.Equal(accounts, await db.AgentUsers.CountAsync());
        Assert.Empty(blob.Deleted);
    }

    // ---------------------------------------------------------------- storage

    public static IEnumerable<object[]> Accounts() => new[]
    {
        // The local emulator (the account is part of the path) and a real account (part of the host).
        new object[] { "UseDevelopmentStorage=true" },
        new object[] { "DefaultEndpointsProtocol=https;AccountName=iprotest573;AccountKey=" + Convert.ToBase64String(new byte[32]) + ";EndpointSuffix=core.windows.net" },
    };

    [Theory]
    [MemberData(nameof(Accounts))]
    public void Storage_knows_its_own_address_for_a_file_whatever_the_file_is_called(string connection)
    {
        var storage = Storage(connection, out _);
        var names = new[]
        {
            "0123abcd_logo.png", "0123abcd_logo-mobile copy.png", "0123abcd_" + (char)0xC9 + "quipe.jpg", "0123abcd_Bob's office.png",
            "0123abcd_100% growth.png", "0123abcd_a#b.png", "0123abcd_a+b&c.png", "0123abcd_why?.png",
        };
        foreach (var name in names)
        {
            // What an upload returns and every row stores.
            var own = new BlobServiceClient(connection).GetBlobContainerClient("agent-logos").GetBlobClient(name).Uri.ToString();
            Assert.True(storage.IsOwnAddress(own), own);
        }
    }

    [Theory]
    [MemberData(nameof(Accounts))]
    public async Task Storage_refuses_to_delete_under_any_other_spelling_of_a_file(string connection)
    {
        var storage = Storage(connection, out var log);
        var own = new BlobServiceClient(connection).GetBlobContainerClient("agent-photos").GetBlobClient("0002bbbb_their-photo.jpg").Uri.ToString();
        var uri = new Uri(own);
        var others = new List<string>
        {
            own + "?z",
            own + "?",
            own + "#x",
            " " + own,
            own + " ",
            own.Replace("0002bbbb", "%30002bbbb"),
            own.Replace("their-photo", "their%2Dphoto"),
            own.Replace("/agent-photos/", "/x/../agent-photos/"),
            own.Replace("/agent-photos/", "//agent-photos/"),
            own.Replace(uri.Host, "elsewhere.example.test"),
            own.Replace(uri.Scheme + "://", uri.Scheme + "://user@"),
            own.Replace("/agent-photos/", "\\agent-photos\\"),
            own[..own.LastIndexOf('/')],
            "agent-photos/0002bbbb_their-photo.jpg",
            "/agent-photos/0002bbbb_their-photo.jpg",
            "",
        };
        if (uri.Host.Contains("iprotest573", StringComparison.Ordinal))
        {
            others.Add(own.Replace("iprotest573", "IPROTEST573"));
            others.Add(own.Replace(uri.Host, uri.Host + ":443"));
            others.Add(own.Replace(uri.Host, uri.Host + "."));
            others.Add(own.Replace("https://", "http://"));
        }

        foreach (var other in others)
        {
            Assert.False(storage.IsOwnAddress(other), other);
            Assert.False(await storage.DeleteAsync(other), other);
        }

        // Each one was turned away before storage was asked anything: a refusal, never a failed try.
        Assert.Equal(others.Count, log.Entries.Count(e => e.Level == LogLevel.Warning && e.Text.StartsWith("Refused to delete", StringComparison.Ordinal)));
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Error);
    }

    // ---------------------------------------------------------------- helpers

    private static AgentWebsite Posted(int templateId, string logoUrl) => new()
    {
        TemplateId = templateId, SiteTitle = "Mine", TagLine = "A line", ThemeColor = "#224466", LogoUrl = logoUrl,
        CustomDomain = string.Empty, CustomDomainDisplay = string.Empty,
    };

    private static async Task<(int AgentId, int TemplateId)> AgentAsync(IPRODbContext db, bool withSite)
    {
        var rule = new BillingRule { PackageName = ($"T573-{Guid.NewGuid():N}")[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        var template = new WebsiteTemplate { Name = "Modern", TemplateKey = ($"t573-{Guid.NewGuid():N}")[..16], BusinessType = "All", IsActive = true };
        db.AddRange(rule, template);
        await db.SaveChangesAsync();
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.InstantWebsite, FeatureName = "Website", IsIncluded = true });
        var agent = new AgentUser
        {
            UserName = ($"t573-{Guid.NewGuid():N}")[..20], Email = ($"t573-{Guid.NewGuid():N}")[..14] + "@example.test",
            FirstName = "Lena", LastName = "Baker", CompanyName = "Baker Advisory",
            DomainName = ($"t573-{Guid.NewGuid():N}")[..24], Country = "Canada", Province = "Ontario",
            PackageId = rule.Id, TrialEndsAt = null,
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        db.Add(new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id, BillingRuleId = rule.Id, Amount = 60m, Status = BillingStatus.Active,
            Period = BillingPeriod.Monthly, StartDate = DateTime.UtcNow.AddDays(-40), NextBillingDate = DateTime.UtcNow.AddDays(20),
        });
        if (withSite) db.Add(new AgentWebsite { AgentUserId = agent.Id, TemplateId = template.Id, SiteTitle = "Mine", LogoUrl = OwnLogo });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (agent.Id, template.Id);
    }

    private static WebsiteController Controller(IPRODbContext db, int agentId, IBlobStorageService blob)
    {
        var controller = new WebsiteController(
            new WebsiteService(new UnitOfWork(db)), blob, new PackageEntitlementService(new UnitOfWork(db), db),
            new AgentService(new UnitOfWork(db), new PasswordHasher<AgentUser>()), new ConfigurationBuilder().Build(), db,
            null!, null!, NullLogger<WebsiteController>.Instance);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString(CultureInfo.InvariantCulture)) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(context, new NoTempData());
        return controller;
    }

    private static IFormFile Png(string name)
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0 };
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "logo", name) { Headers = new HeaderDictionary(), ContentType = "image/png" };
    }

    // The request binder exactly as an action's parameter gets it.
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

    private static AzureBlobStorageService Storage(string connection, out ListLogger log)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Azure:StorageConnectionString"] = connection,
            ["Azure:StorageAccountName"] = "iprotest573",
        }).Build();
        log = new ListLogger();
        return new AzureBlobStorageService(configuration, log);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "IPRO.sln"))) directory = directory.Parent;
        return directory!.FullName;
    }

    private sealed class RecordingBlob : IBlobStorageService
    {
        public List<string> Deleted { get; } = new();
        public List<string> Uploaded { get; } = new();
        public Task<string> UploadAsync(Stream fileStream, string fileName, string containerName, string contentType, bool isPrivate)
        {
            var url = $"https://acct.example.test/{containerName}/{Guid.NewGuid():N}_{fileName}";
            Uploaded.Add(url);
            return Task.FromResult(url);
        }
        public Task<bool> DeleteAsync(string blobUrl) { Deleted.Add(blobUrl); return Task.FromResult(true); }
        public Task<Stream?> DownloadAsync(string blobUrl) => Task.FromResult<Stream?>(null);
        public Task<List<string>> ListAsync(string containerName) => Task.FromResult(new List<string>());
        public string GetPublicUrl(string containerName, string fileName) => $"https://acct.example.test/{containerName}/{fileName}";
        public Task EnsureContainerAccessAsync(string containerName, bool isPrivate) => Task.CompletedTask;
    }

    private sealed class NoTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class ListLogger : ILogger<AzureBlobStorageService>
    {
        public List<(LogLevel Level, string Text)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
