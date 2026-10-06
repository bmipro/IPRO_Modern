using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Scheduler;
using IPRO.Utility;
using IPRO.Web.Controllers;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// 553 (2026-10-06). The first customer with an EXISTING website moved in: L'Avenue Boulangerie, whose
// site had lived for years on the short address (lavenuebakery.com, no www) with pages at
// /bread-%26-pastries, /drinks-and-meals, /gallery and /about-us. The setup steps knew one way to
// make a short address work -- the registrar's forwarding -- and GoDaddy's forwarder answers 404 for
// everything but the home address (measured that morning on three domains already on the platform:
// 4ipro.com/about -> 404). So the day the domain moved, every link to the bakery's menus died. The
// owner: "build it".
//
//   - A short address pointed straight at the platform (an A record plus the asuid TXT record) is
//     bound and secured like the www name, and the app sends every address on it to www with its
//     path and query.
//   - An address of the previous website finds its page: listed on the page (Old addresses), or the
//     same words spelled the old site's way ("bread-&-pastries", "services.html").
//   - Forwarding still counts as a working short address; the screens say which way it is connected.
public class ShortAddress553Tests : IDisposable
{
    private const string Platform = "40.89.19.0";
    private const string Target = "ipro-prod-web.azurewebsites.net";

    public ShortAddress553Tests() => DomainCheckService.ResolveHook = (host, ct) => Dns.GetHostAddressesAsync(host, ct);
    public void Dispose() => DomainCheckService.ResolveHook = (host, ct) => Dns.GetHostAddressesAsync(host, ct);

    // ---------------------------------------------------------------- the redirect to www --

    [Fact]
    public async Task The_short_address_goes_to_its_www_name_with_the_path_and_the_query()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var domain = await ConnectedDomainAsync(db, "bakery553.example.test");

        // The path arrives decoded ("%26" is "&" by the time the pipeline sees it).
        var context = Request(db, "bakery553.example.test", "/bread-&-pastries", "?utm_source=maps");
        Assert.True(await ShortAddressRedirect.TryHandleAsync(context, PlatformConfig()));

        Assert.Equal(StatusCodes.Status301MovedPermanently, context.Response.StatusCode);
        Assert.Equal("https://www.bakery553.example.test/bread-&-pastries?utm_source=maps", context.Response.Headers.Location.ToString());
        // Permanent, but not for ever (507): a 301 with no lifetime can sit in a browser indefinitely.
        Assert.Equal(PlatformAliasHosts.RedirectLifetime, context.Response.Headers.CacheControl.ToString());

        // HEAD is what link checkers and uptime monitors send.
        var head = Request(db, "bakery553.example.test", "/", "", method: "HEAD");
        Assert.True(await ShortAddressRedirect.TryHandleAsync(head, PlatformConfig()));
        Assert.Equal("https://www.bakery553.example.test/", head.Response.Headers.Location.ToString());
        Assert.Equal("www.bakery553.example.test", domain.DomainName);
    }

    [Fact]
    public async Task Not_before_the_www_name_is_secured()
    {
        // Redirecting earlier would send visitors into a certificate warning: the site is served on
        // the short address meanwhile.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var domain = await ConnectedDomainAsync(db, "early553.example.test");
        domain.SslStatus = AgentDomainStatus.BindingPending;
        await db.SaveChangesAsync();

        var context = Request(db, "early553.example.test", "/gallery", "");
        Assert.False(await ShortAddressRedirect.TryHandleAsync(context, PlatformConfig()));
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task Never_for_certificate_validation_a_form_post_or_any_other_host()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await ConnectedDomainAsync(db, "rules553.example.test");
        // A sub-domain added as the address itself has no separate short address.
        await ConnectedDomainAsync(db, "clients.firm553.example.test", www: false);
        var config = PlatformConfig();

        // The certificate authority proves a bare name by fetching a token over the A record.
        Assert.False(await ShortAddressRedirect.TryHandleAsync(
            Request(db, "rules553.example.test", "/.well-known/pki-validation/fileauth.txt", ""), config));
        Assert.False(await ShortAddressRedirect.TryHandleAsync(
            Request(db, "rules553.example.test", "/PublicWebsite/SubmitLead", "", method: "POST"), config));
        Assert.False(await ShortAddressRedirect.TryHandleAsync(Request(db, "www.rules553.example.test", "/gallery", ""), config));
        Assert.False(await ShortAddressRedirect.TryHandleAsync(Request(db, "clients.firm553.example.test", "/gallery", ""), config));
        Assert.False(await ShortAddressRedirect.TryHandleAsync(Request(db, "unknown553.example.test", "/gallery", ""), config));

        // Names that cannot be a customer's short address never reach the lookup at all.
        foreach (var host in new[] { "", "localhost", "40.89.19.0", "www.rules553.example.test", "ipro-prod-web.azurewebsites.net",
                     "app.iproadvisers.com", "someone.247advisers.com", "247advisers.com", "admin.iproadvisers.com", "old.example.test" })
        {
            Assert.False(ShortAddressRedirect.CouldBeShortAddress(host, config), host);
        }
        Assert.True(ShortAddressRedirect.CouldBeShortAddress("rules553.example.test", config));
    }

    [Fact]
    public void The_pipeline_asks_right_after_the_platforms_own_names_and_before_anything_serves()
    {
        var program = Read(@"src\IPRO.Web\Program.cs");
        var alias = program.IndexOf("PlatformAliasHosts.TryHandle(app.Configuration, context)", StringComparison.Ordinal);
        var shortAddress = program.IndexOf("ShortAddressRedirect.TryHandleAsync(context, app.Configuration)", StringComparison.Ordinal);
        var staticFiles = program.IndexOf("app.UseStaticFiles();", StringComparison.Ordinal);
        Assert.True(alias > 0 && shortAddress > alias && staticFiles > shortAddress,
            "ShortAddressRedirect must run after the alias names and before static files and routing");
    }

    // ------------------------------------------------- addresses of the previous website --

    [Theory]
    [InlineData("https://lavenuebakery.com/bread-%26-pastries", "bread-&-pastries")]
    [InlineData("lavenuebakery.com/About-Us/", "about-us")]
    [InlineData("/about-us?ref=maps#hours", "about-us")]
    [InlineData("about-us", "about-us")]
    [InlineData("about.html", "about.html")]
    [InlineData("Services//Tax.html", "services/tax.html")]
    [InlineData("https://lavenuebakery.com", "")]
    [InlineData("   ", "")]
    public void An_old_address_is_kept_as_its_path_however_it_was_pasted(string pasted, string kept) =>
        Assert.Equal(kept, OldAddresses.Normalize(pasted));

    [Fact]
    public void The_editors_field_is_one_address_a_line_without_repeats_or_runaways()
    {
        var parsed = OldAddresses.Parse("https://old.example/About-Us/\r\nabout-us\n\n /team , " + new string('x', OldAddresses.MaxLength + 1));
        Assert.Equal(new[] { "about-us", "team" }, parsed);

        var many = string.Join("\n", Enumerable.Range(1, 40).Select(i => "page-" + i));
        Assert.Equal(OldAddresses.MaxEntries, OldAddresses.Parse(many).Count);
        // What can be kept always fits the column.
        Assert.True(OldAddresses.MaxEntries * (OldAddresses.MaxLength + 1) <= 2000);
        Assert.Equal("a\nb", OldAddresses.Store(new[] { "a", "b" }));
    }

    [Fact]
    public void An_old_address_finds_its_page_listed_or_spelled_the_old_sites_way()
    {
        var pages = BakeryPages();

        // Listed on the page: the only way "/about-us" can become "/about".
        Assert.Equal("about", OldAddresses.Find(pages, "about-us")?.Slug);
        Assert.Equal("about", OldAddresses.Find(pages, "/About-Us/")?.Slug);
        // The same words, the old builder's spelling: GoDaddy kept the ampersand.
        Assert.Equal("bread-pastries", OldAddresses.Find(pages, "bread-&-pastries")?.Slug);
        Assert.Equal("drinks-and-meals", OldAddresses.Find(pages, "Drinks_and_Meals.php")?.Slug);
        Assert.Equal("gallery", OldAddresses.Find(pages, "gallery/index.html")?.Slug);
        // The old site's front page.
        Assert.True(OldAddresses.Find(pages, "index.html")?.IsHomePage);
        Assert.True(OldAddresses.Find(pages, "default.aspx")?.IsHomePage);

        // Nothing to say about an address that is simply not one of the site's.
        Assert.Null(OldAddresses.Find(pages, "no-such-page"));
        Assert.Null(OldAddresses.Find(pages, "gallery/photo-1"));
        Assert.Null(OldAddresses.Find(pages, ""));
    }

    [Fact]
    public async Task A_visitor_on_an_old_address_is_sent_to_the_page_for_good()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var host = await LiveSiteAsync(db, BakeryPages());

        async Task<LocalRedirectResult> Moved(string slug)
        {
            var controller = NewPublicController(db, host);
            var redirect = Assert.IsType<LocalRedirectResult>(await controller.Page(slug));
            Assert.True(redirect.Permanent, slug);
            Assert.Equal(PlatformAliasHosts.RedirectLifetime, controller.Response.Headers.CacheControl.ToString());
            return redirect;
        }

        // The bakery's four old page addresses, as the pipeline hands them over (decoded).
        Assert.Equal("/bread-pastries", (await Moved("bread-&-pastries")).Url);
        Assert.Equal("/about", (await Moved("about-us")).Url);
        Assert.Equal("/", (await Moved("index.html")).Url);
        Assert.Equal("/gallery", (await Moved("gallery/index.html")).Url);

        // No visit is counted for the old address: it is counted where it lands.
        Assert.Equal(0, await db.WebsitePageViews.CountAsync());
    }

    [Fact]
    public async Task A_page_that_lives_at_the_address_always_wins_and_an_unknown_address_is_still_a_404()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var pages = BakeryPages();
        // Written straight to the table: the editor refuses this, the visitor's side must not depend on it.
        pages.Single(p => p.Slug == "about").OldAddresses = "about-us\ngallery";
        var host = await LiveSiteAsync(db, pages);

        var live = NewPublicController(db, host);
        var view = Assert.IsType<ViewResult>(await live.Page("gallery"));
        Assert.Equal("gallery", Assert.IsType<IPRO.Web.Models.PublicWebsiteViewModel>(view.Model).CurrentPage?.Slug);

        // A longer path whose first part matches a page keeps rendering that page, as it always did.
        var deeper = NewPublicController(db, host);
        var deeperView = Assert.IsType<ViewResult>(await deeper.Page("gallery/photo-1"));
        Assert.Equal("gallery", Assert.IsType<IPRO.Web.Models.PublicWebsiteViewModel>(deeperView.Model).CurrentPage?.Slug);

        var missing = NewPublicController(db, host);
        var notFound = Assert.IsType<ViewResult>(await missing.Page("no-such-page"));
        Assert.Equal(StatusCodes.Status404NotFound, missing.Response.StatusCode);
        Assert.True(Assert.IsType<IPRO.Web.Models.PublicWebsiteViewModel>(notFound.Model).PageNotFound);
    }

    [Fact]
    public async Task An_unknown_old_style_address_gets_the_bare_404_it_always_got()
    {
        // /wp-login.php, /xmlrpc.php: what scanners probe all day, on every host. Reaching the public
        // site must not make each one a full page render -- three columns are read, and the answer
        // is the status-only 404 such a path got before.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var host = await LiveSiteAsync(db, BakeryPages());

        Assert.IsType<NotFoundResult>(await NewPublicController(db, host).Page("wp-login.php"));
        Assert.IsType<NotFoundResult>(await NewPublicController(db, host).Page("no-such-page.html"));
        Assert.Equal(0, await db.WebsitePageViews.CountAsync());

        // ...while one that IS an address of the previous website is answered.
        var known = Assert.IsType<LocalRedirectResult>(await NewPublicController(db, host).Page("Drinks_and_Meals.php"));
        Assert.Equal("/drinks-and-meals", known.Url);
        Assert.True(known.Permanent);

        var source = Read(@"src\IPRO.Web\Controllers\PublicWebsiteController.cs");
        var legacy = source.Substring(source.IndexOf("// 553: an old-style page address (/about.html, /index.php).", StringComparison.Ordinal), 1500);
        Assert.Contains("return replacement == null ? NotFound() : MovedForGood(replacement);", legacy);
        Assert.DoesNotContain(".Include(", legacy);
    }

    [Fact]
    public void An_old_style_page_address_reaches_the_public_site_on_an_advisers_domain()
    {
        // "/about.html" has an extension, and paths with extensions never reached the slug lookup:
        // the visitor got the platform's 404, not the adviser's site. Old-style page addresses are
        // the one exception; files of every other kind still bypass the public site.
        var program = Read(@"src\IPRO.Web\Program.cs");
        var rule = program.Substring(program.IndexOf("static bool ShouldRouteToPublicWebsite", StringComparison.Ordinal), 1400);
        Assert.Contains("Path.HasExtension(context.Request.Path.Value) &&", rule);
        Assert.Contains("!IPRO.Web.Infrastructure.PlatformAliasHosts.IsLegacyPage(context.Request.Path)) return false;", rule);
        Assert.True(PlatformAliasHosts.IsLegacyPage(new PathString("/about.html")));
        Assert.False(PlatformAliasHosts.IsLegacyPage(new PathString("/favicon.ico")));
        Assert.False(PlatformAliasHosts.IsLegacyPage(new PathString("/sitemap.xml")));
    }

    [Fact]
    public async Task The_page_editor_keeps_old_addresses_and_says_which_it_left_out()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var pages = BakeryPages();
        pages.Single(p => p.Slug == "about").OldAddresses = string.Empty;
        var (agentId, _) = await SiteAsync(db, pages, "editor553.example.test", paid: true);
        var about = await db.WebsitePages.AsNoTracking().SingleAsync(p => p.Slug == "about");
        var gallery = await db.WebsitePages.AsNoTracking().SingleAsync(p => p.Slug == "gallery");

        var controller = NewPagesController(db, agentId);
        await controller.SavePage(about.Id, "About", "about", "About", "", "", null, true, true, false,
            oldAddresses: "https://lavenuebakery.com/About-Us/\nabout\ngallery\nabout-us");

        db.ChangeTracker.Clear();
        // Its own address and a live page's address are not old addresses; the paste and the retyped
        // line were the same one.
        Assert.Equal("about-us", (await db.WebsitePages.AsNoTracking().SingleAsync(p => p.Id == about.Id)).OldAddresses);
        Assert.Contains("/gallery is where your Gallery page lives now", controller.TempData["Warning"] as string);

        // The first page to list an address keeps it.
        var second = NewPagesController(db, agentId);
        await second.SavePage(gallery.Id, "Gallery", "gallery", "Gallery", "", "", null, true, true, false,
            oldAddresses: "photos\nabout-us");
        db.ChangeTracker.Clear();
        Assert.Equal("photos", (await db.WebsitePages.AsNoTracking().SingleAsync(p => p.Id == gallery.Id)).OldAddresses);
        Assert.Contains("/about-us is already listed on your About page", second.TempData["Warning"] as string);

        // Saving the page again without the field clears nothing by accident: the form always sends it.
        var view = Read(@"src\IPRO.Web\Views\WebsitePages\Edit.cshtml");
        Assert.Contains("<textarea name=\"oldAddresses\"", view);
        Assert.Contains("@sitePage.OldAddresses</textarea>", view);
        Assert.Contains("Old addresses for this page", view);
    }

    // --------------------------------------- binding the short address (fake DNS and Azure) --

    [Fact]
    public async Task A_short_address_pointed_at_the_platform_is_bound_then_secured()
    {
        var azure = new ScriptedAzure();
        azure.RootAnswers.Enqueue(new AzureDomainAutomationResult { Success = true, BindingCreated = true, CertificateCreated = true, Message = "issuing" });
        azure.RootAnswers.Enqueue(new AzureDomainAutomationResult { Success = true, BindingCreated = true, CertificateCreated = true, SslBound = true });
        Dns553(("www.shop553.example.test", Platform), ("shop553.example.test", Platform), (Target, Platform));
        var service = new DomainCheckService(new NoHttp(), azure, NullLogger<DomainCheckService>.Instance);
        var domain = NewDomain("shop553.example.test");

        // First pass: bound; the certificate is being issued (the same two steps as a www name).
        await service.CheckAsync(domain);
        Assert.Equal(new[] { "shop553.example.test" }, azure.RootCalls);
        Assert.Equal(AgentDomainStatus.Bound, domain.RootDnsStatus);
        Assert.Equal(AgentDomainStatus.Bound, domain.RootAzureBindingStatus);
        Assert.Equal(AgentDomainStatus.BindingPending, domain.RootSslStatus);
        Assert.NotNull(domain.RootBoundAt);
        Assert.False(domain.RootRedirectsToWww);     // https on the short address still warns: not "working" yet
        Assert.Equal("Securing", ShortAddressState.Describe(domain).Label);
        Assert.Contains("securing shop553.example.test", domain.RootLastError);
        var boundAt = domain.RootBoundAt;

        // Second pass: the certificate is attached.
        await service.CheckAsync(domain);
        Assert.Equal(AgentDomainStatus.Bound, domain.RootSslStatus);
        Assert.True(domain.RootRedirectsToWww);
        Assert.Equal(string.Empty, domain.RootLastError);
        Assert.Equal(boundAt, domain.RootBoundAt);
        var state = ShortAddressState.Describe(domain);
        Assert.Equal(("Connected", true, true), (state.Label, state.Direct, state.Working));

        // Finished: Azure is not asked again.
        await service.CheckAsync(domain);
        Assert.Equal(2, azure.RootCalls.Count);
    }

    [Fact]
    public async Task A_missing_TXT_record_is_said_in_plain_words_and_fixed_by_the_next_check()
    {
        var azure = new ScriptedAzure();
        // Azure's own sentence for the usual mistake.
        azure.RootAnswers.Enqueue(AzureDomainAutomationResult.Failed(
            "Azure automation failed: 400 Bad Request: A TXT record pointing from asuid.txt553.example.test to C6D5 was not found."));
        Dns553(("www.txt553.example.test", Platform), ("txt553.example.test", Platform), (Target, Platform));
        var service = new DomainCheckService(new NoHttp(), azure, NullLogger<DomainCheckService>.Instance);
        var domain = NewDomain("txt553.example.test");

        await service.CheckAsync(domain);
        Assert.Equal(AgentDomainStatus.Failed, domain.RootAzureBindingStatus);
        Assert.False(domain.RootRedirectsToWww);
        Assert.Contains("TXT record", domain.RootLastError);
        Assert.Contains("name asuid", domain.RootLastError);
        Assert.DoesNotContain("400", domain.RootLastError);          // the adviser never sees Azure's wording
        Assert.Equal("Needs attention", ShortAddressState.Describe(domain).Label);

        // The record is added; the next check binds and secures in one go.
        await service.CheckAsync(domain);
        Assert.Equal(AgentDomainStatus.Bound, domain.RootAzureBindingStatus);
        Assert.True(domain.RootRedirectsToWww);
        Assert.Equal("Connected", ShortAddressState.Describe(domain).Label);
    }

    [Fact]
    public async Task A_certificate_order_refused_for_now_is_still_securing_and_is_asked_again()
    {
        // Seen on the platform's own names (2026-09-20): Azure binds the name, then refuses the
        // certificate order while its own DNS check still sees the previous record. Nothing for the
        // adviser to do, so it must not read as a fault -- and the job comes back in five minutes.
        var azure = new ScriptedAzure();
        azure.RootAnswers.Enqueue(new AzureDomainAutomationResult
        {
            Success = false, BindingCreated = true,
            Message = "Azure automation failed: 400 Bad Request: Missing one DNS record for hostname cert553.example.test"
        });
        Dns553(("www.cert553.example.test", Platform), ("cert553.example.test", Platform), (Target, Platform));
        var service = new DomainCheckService(new NoHttp(), azure, NullLogger<DomainCheckService>.Instance);
        var domain = NewDomain("cert553.example.test");

        await service.CheckAsync(domain);
        Assert.Equal(AgentDomainStatus.Bound, domain.RootAzureBindingStatus);
        Assert.Equal(AgentDomainStatus.BindingPending, domain.RootSslStatus);
        Assert.NotNull(domain.RootBoundAt);
        Assert.Equal("Securing", ShortAddressState.Describe(domain).Label);
        Assert.Contains("securing cert553.example.test", domain.RootLastError);
        Assert.DoesNotContain("400", domain.RootLastError);
        // On the job's every-run path, not the half-hourly one.
        Assert.Equal((true, (string?)null), DomainAutomationJob.CertificateWait(domain, DateTime.UtcNow));

        await service.CheckAsync(domain);
        Assert.Equal("Connected", ShortAddressState.Describe(domain).Label);
    }

    [Fact]
    public async Task A_try_that_simply_did_not_go_through_is_not_the_advisers_problem()
    {
        var azure = new ScriptedAzure();
        azure.RootAnswers.Enqueue(AzureDomainAutomationResult.Failed("Azure automation failed: 503 Service Unavailable: No response body was returned."));
        Dns553(("www.retry553.example.test", Platform), ("retry553.example.test", Platform), (Target, Platform));
        var service = new DomainCheckService(new NoHttp(), azure, NullLogger<DomainCheckService>.Instance);
        var domain = NewDomain("retry553.example.test");

        await service.CheckAsync(domain);
        Assert.Equal(AgentDomainStatus.NotConfigured, domain.RootAzureBindingStatus);
        Assert.Equal("Connecting", ShortAddressState.Describe(domain).Label);
        Assert.Contains("nothing for you to do", domain.RootLastError);
        Assert.DoesNotContain("503", domain.RootLastError);
    }

    [Fact]
    public async Task A_leftover_address_beside_ours_is_named_and_nothing_is_bound()
    {
        // The old host's A record left in place: the name answers from both in turn.
        var azure = new ScriptedAzure();
        DomainCheckService.ResolveHook = (host, ct) => Task.FromResult(host == "two553.example.test"
            ? new[] { IPAddress.Parse(Platform), IPAddress.Parse("76.223.105.230") }
            : new[] { IPAddress.Parse(Platform) });
        var service = new DomainCheckService(new NoHttp(), azure, NullLogger<DomainCheckService>.Instance);
        var domain = NewDomain("two553.example.test");

        await service.CheckAsync(domain);
        Assert.Empty(azure.RootCalls);
        Assert.False(domain.RootRedirectsToWww);
        Assert.Contains("more than one address", domain.RootLastError);
        Assert.Equal("Needs attention", ShortAddressState.Describe(domain).Label);

        // The same mistake made later, on a short address that was working: still theirs to fix,
        // and the binding stays on record.
        domain.RootAzureBindingStatus = AgentDomainStatus.Bound;
        domain.RootSslStatus = AgentDomainStatus.Bound;
        await service.CheckAsync(domain);
        Assert.Equal("Needs attention", ShortAddressState.Describe(domain).Label);
        Assert.Equal(AgentDomainStatus.Bound, domain.RootAzureBindingStatus);
        Assert.Empty(azure.RootCalls);
    }

    [Fact]
    public void Forwarding_still_counts_and_every_screen_words_the_state_the_same_way()
    {
        var forwarded = NewDomain("fwd553.example.test");
        forwarded.RootDnsStatus = AgentDomainStatus.DnsReady;
        forwarded.RootRedirectsToWww = true;
        var state = ShortAddressState.Describe(forwarded);
        Assert.Equal(("Forwarding OK", false, true), (state.Label, state.Direct, state.Working));

        forwarded.RootRedirectsToWww = false;
        Assert.Equal("Not set up", ShortAddressState.Describe(forwarded).Label);

        // A domain added as a sub-domain has no short address to talk about.
        Assert.False(ShortAddressState.HasShortAddress(new AgentDomain { DomainName = "clients.firm.example", RootDomain = "clients.firm.example" }));
        Assert.True(ShortAddressState.HasShortAddress(forwarded));

        // The agent's two panels and SuperAdmin's list all read the one description.
        var portal = Read(@"src\IPRO.Web\Views\Website\Index.cshtml");
        Assert.Equal(2, CountOf(portal, "IPRO.Utility.ShortAddressState.Describe("));
        Assert.Contains("IPRO.Utility.ShortAddressState.Describe(domain)", Read(@"src\IPRO.Admin\Views\Domains\Index.cshtml"));
        Assert.DoesNotContain("\"Not forwarding\"", portal);
    }

    [Fact]
    public void The_setup_steps_show_the_two_records_first_and_forwarding_one_click_down()
    {
        var view = Read(@"src\IPRO.Web\Views\Website\Index.cshtml");
        var step = view.Substring(view.IndexOf("Send the short address to it", StringComparison.Ordinal));
        var records = step.IndexOf("@websiteAddress", StringComparison.Ordinal);
        var txt = step.IndexOf("@domainVerificationId", StringComparison.Ordinal);
        var forwarding = step.IndexOf("Or use your registrar's forwarding instead", StringComparison.Ordinal);
        Assert.True(records > 0 && txt > records && forwarding > txt, "the A record, the TXT record, then forwarding");
        Assert.Contains("<span class=\"text-muted\">Name&nbsp;&nbsp;</span>asuid", step);
        Assert.Contains("a registrar forwards your home", step);       // the limit is said, not hidden
        // Without the two values the steps fall back to forwarding, open.
        Assert.Contains("@(canPointShortAddress ? \"\" : \"show\")", step);

        // The values ship with the app: the address the platform answers on and its verification id
        // (both are published in every connected domain's DNS).
        using var settings = System.Text.Json.JsonDocument.Parse(Read(@"src\IPRO.Web\appsettings.json"));
        var app = settings.RootElement.GetProperty("App");
        Assert.True(IPAddress.TryParse(app.GetProperty("WebsiteAddress").GetString(), out _));
        var id = app.GetProperty("DomainVerificationId").GetString()!;
        Assert.True(id.Length == 64 && id.All(Uri.IsHexDigit));
        // ...and the same two the operations check has always used.
        var script = Read(@"ops\domain-switch\dns-check.sh");
        Assert.Contains(app.GetProperty("WebsiteAddress").GetString()!, script);
        Assert.Contains(id, script);
    }

    // ------------------------------------------------------------- the five-minute job --

    [Fact]
    public async Task While_its_certificate_is_being_issued_the_domain_is_checked_every_run()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var now = DateTime.UtcNow;

        AgentDomain Finished(AgentDomain d) { d.LastCheckedAt = now.AddMinutes(-6); return d; }
        var waiting = Finished(await ConnectedDomainAsync(db, "wait553.example.test"));
        waiting.RootDnsStatus = AgentDomainStatus.Bound;
        waiting.RootAzureBindingStatus = AgentDomainStatus.Bound;
        waiting.RootSslStatus = AgentDomainStatus.BindingPending;
        waiting.RootBoundAt = now.AddMinutes(-10);

        var done = Finished(await ConnectedDomainAsync(db, "done553.example.test"));
        done.RootDnsStatus = AgentDomainStatus.Bound;
        done.RootAzureBindingStatus = AgentDomainStatus.Bound;
        done.RootSslStatus = AgentDomainStatus.Bound;
        done.RootBoundAt = now.AddDays(-2);

        // Past the grace period it is an alert, not something a five-minute retry fixes.
        var stuck = Finished(await ConnectedDomainAsync(db, "stuck553.example.test"));
        stuck.RootDnsStatus = AgentDomainStatus.Bound;
        stuck.RootAzureBindingStatus = AgentDomainStatus.Bound;
        stuck.RootSslStatus = AgentDomainStatus.BindingPending;
        stuck.RootBoundAt = now.AddHours(-5);
        await db.SaveChangesAsync();

        var check = new RecordingCheck();
        await new DomainAutomationJob(db, check, new NullEmail(), new ConfigurationBuilder().Build(), NullLogger<DomainAutomationJob>.Instance).RunAsync();

        Assert.Equal(new[] { "www.wait553.example.test" }, check.Checked);
    }

    [Fact]
    public void The_certificate_alert_names_whichever_address_is_overdue()
    {
        var now = DateTime.UtcNow;
        var domain = NewDomain("alert553.example.test");
        domain.CreatedAt = now.AddDays(-30);
        domain.DnsStatus = domain.AzureBindingStatus = domain.SslStatus = AgentDomainStatus.Bound;
        Assert.Equal((false, (string?)null), DomainAutomationJob.CertificateWait(domain, now));

        // The short address was connected long after the domain was added: its wait is counted from
        // its own binding, or the alert would fire the moment it was bound.
        domain.RootDnsStatus = AgentDomainStatus.Bound;
        domain.RootAzureBindingStatus = AgentDomainStatus.Bound;
        domain.RootSslStatus = AgentDomainStatus.BindingPending;
        domain.RootBoundAt = now.AddMinutes(-10);
        Assert.Equal((true, (string?)null), DomainAutomationJob.CertificateWait(domain, now));

        domain.RootBoundAt = now.AddHours(-4);
        Assert.Equal((true, (string?)"alert553.example.test"), DomainAutomationJob.CertificateWait(domain, now));

        // The www name's own wait is unchanged.
        var www = NewDomain("www553.example.test");
        www.CreatedAt = now.AddHours(-4);
        www.AzureBindingStatus = AgentDomainStatus.Bound;
        www.SslStatus = AgentDomainStatus.BindingPending;
        Assert.Equal((true, (string?)"www.www553.example.test"), DomainAutomationJob.CertificateWait(www, now));
    }

    // ------------------------------------------------------------------ taking it away --

    [Fact]
    public async Task Removing_the_domain_removes_the_short_addresss_binding_too()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var direct = await ConnectedDomainAsync(db, "gone553.example.test");
        direct.RootDnsStatus = AgentDomainStatus.Bound;
        direct.RootAzureBindingStatus = AgentDomainStatus.Bound;
        direct.RootSslStatus = AgentDomainStatus.Bound;
        await db.SaveChangesAsync();
        var azure = new ScriptedAzure();

        await NewWebsiteController(db, direct.AgentUserId, azure).RemoveDomain(direct.Id, "www.gone553.example.test");
        Assert.Equal(new[] { "www.gone553.example.test", "gone553.example.test" }, azure.Removed);
        Assert.False(await db.AgentDomains.AsNoTracking().AnyAsync(d => d.Id == direct.Id));

        // A forwarded short address never had anything in Azure: nothing to delete, nothing asked.
        var forwarded = await ConnectedDomainAsync(db, "fwdgone553.example.test");
        forwarded.RootRedirectsToWww = true;
        await db.SaveChangesAsync();
        var second = new ScriptedAzure();
        await NewWebsiteController(db, forwarded.AgentUserId, second).RemoveDomain(forwarded.Id, "www.fwdgone553.example.test");
        Assert.Equal(new[] { "www.fwdgone553.example.test" }, second.Removed);
    }

    // ------------------------------------------------------------------------ the guides --

    [Fact]
    public void The_guides_say_both_ways_and_where_old_addresses_go()
    {
        var domains = Read(@"DOCS\05_DOMAINS_AND_LEADS.md");
        Assert.Contains("### Point the short address straight at IPRO", domains);
        Assert.Contains("| TXT | `asuid` |", domains);
        Assert.Contains("forwards only your home address", domains);
        Assert.Contains("### Moving an existing website here", domains);

        var builder = Read(@"DOCS\04_WEBSITE_BUILDER.md");
        Assert.Contains("## Old Addresses (Moving From Another Website)", builder);

        var trouble = Read(@"DOCS\09_TROUBLESHOOTING.md");
        Assert.Contains("## Trap: A Registrar's Forwarding Moves Only The Home Address (2026-10-06)", trouble);

        Assert.Contains("pointed straight at the platform with an A record", Read(@"DOCS\07_SUPER_ADMIN.md"));
    }

    // ------------------------------------------------------------------------ plumbing --

    private static void Dns553(params (string Host, string Address)[] records)
    {
        var table = records.ToDictionary(r => r.Host, r => new[] { IPAddress.Parse(r.Address) }, StringComparer.OrdinalIgnoreCase);
        DomainCheckService.ResolveHook = (host, ct) =>
            Task.FromResult(table.TryGetValue(host, out var found) ? found : Array.Empty<IPAddress>());
    }

    private static AgentDomain NewDomain(string root) => new()
    {
        DomainName = "www." + root,
        WwwDomain = "www." + root,
        RootDomain = root,
        DnsTarget = Target,
    };

    private static List<WebsitePage> BakeryPages() => new()
    {
        new WebsitePage { Title = "Home", Slug = "home", NavigationLabel = "Home", IsHomePage = true, SortOrder = 0 },
        new WebsitePage { Title = "Bread & Pastries", Slug = "bread-pastries", NavigationLabel = "Bread & Pastries", SortOrder = 1 },
        new WebsitePage { Title = "Drinks and Meals", Slug = "drinks-and-meals", NavigationLabel = "Drinks and Meals", SortOrder = 2 },
        new WebsitePage { Title = "Gallery", Slug = "gallery", NavigationLabel = "Gallery", SortOrder = 3 },
        new WebsitePage { Title = "About", Slug = "about", NavigationLabel = "About", OldAddresses = "about-us", SortOrder = 4 },
    };

    private static IConfiguration PlatformConfig() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["App:BaseUrl"] = "https://app.iproadvisers.com",
        ["App:PlatformDomains"] = "ipro-prod-web.azurewebsites.net,app.iproadvisers.com",
        ["App:TemporarySiteRootDomain"] = "247advisers.com",
        ["App:AliasHosts"] = "old.example.test",
    }).Build();

    private static DefaultHttpContext Request(IPRODbContext db, string host, string path, string query, string method = "GET")
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddMemoryCache();
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.Method = method;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(host);
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        return context;
    }

    private static async Task<(int AgentId, int WebsiteId)> SiteAsync(IPRODbContext db, List<WebsitePage> pages, string host, bool paid = false)
    {
        var rule = new BillingRule { PackageName = ($"T553-{Guid.NewGuid():N}")[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        var template = new WebsiteTemplate { Name = "Modern", TemplateKey = ($"t553-{Guid.NewGuid():N}")[..16], BusinessType = "All" };
        db.AddRange(rule, template);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t553-{Guid.NewGuid():N}")[..20], Email = ($"t553-{Guid.NewGuid():N}")[..14] + "@example.test",
            FirstName = "Lena", LastName = "Baker", CompanyName = "L'Avenue Boulangerie",
            DomainName = ($"t553-{Guid.NewGuid():N}")[..24], Country = "Canada", Province = "Ontario",
            PackageId = rule.Id, TrialEndsAt = null,
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        if (paid)
        {
            db.Add(new IPRO.Entities.Billing
            {
                AgentUserId = agent.Id, BillingRuleId = rule.Id, Amount = 60m, Status = BillingStatus.Active,
                Period = BillingPeriod.Monthly, StartDate = DateTime.UtcNow.AddDays(-40), NextBillingDate = DateTime.UtcNow.AddDays(20),
            });
        }
        var website = new AgentWebsite { AgentUserId = agent.Id, TemplateId = template.Id, IsPublished = true, CustomDomain = host };
        db.Add(website);
        await db.SaveChangesAsync();
        foreach (var page in pages)
        {
            page.AgentWebsiteId = website.Id;
            page.IsPublished = true;
            db.Add(page);
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (agent.Id, website.Id);
    }

    private static async Task<string> LiveSiteAsync(IPRODbContext db, List<WebsitePage> pages)
    {
        var host = ($"www.live553-{Guid.NewGuid():N}")[..24] + ".example.test";
        await SiteAsync(db, pages, host, paid: true);
        return host;
    }

    // A custom domain as the portal stores it: the www name connected and secured, the short address
    // not yet dealt with. www: false is a sub-domain added as the address itself.
    private static async Task<AgentDomain> ConnectedDomainAsync(IPRODbContext db, string root, bool www = true)
    {
        var name = www ? "www." + root : root;
        var (agentId, websiteId) = await SiteAsync(db, new List<WebsitePage>(), name);
        var domain = new AgentDomain
        {
            AgentUserId = agentId, AgentWebsiteId = websiteId,
            DomainName = name, WwwDomain = name, RootDomain = root, DnsTarget = Target,
            DnsStatus = AgentDomainStatus.Bound, AzureBindingStatus = AgentDomainStatus.Bound, SslStatus = AgentDomainStatus.Bound,
            IsPrimary = true,
        };
        db.Add(domain);
        await db.SaveChangesAsync();
        return domain;
    }

    private static PublicWebsiteController NewPublicController(IPRODbContext db, string host)
    {
        var controller = new PublicWebsiteController(
            db, new PackageEntitlementService(new UnitOfWork(db), db), new NullEmail(),
            NullLogger<PublicWebsiteController>.Instance, new ConfigurationBuilder().Build(),
            DataProtectionProvider.Create($"t553-{Guid.NewGuid():N}"), new NullBlob(),
            new MemoryCache(new MemoryCacheOptions()), new NullConsent());
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(host);
        // A browser, so a counted visit WOULD be recorded if the controller got that far.
        context.Request.Headers.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(context, new NullTempData());
        return controller;
    }

    private static WebsitePagesController NewPagesController(IPRODbContext db, int agentId)
    {
        var controller = new WebsitePagesController(db, new PackageEntitlementService(new UnitOfWork(db), db), new NullBlob());
        Sign(controller, agentId);
        return controller;
    }

    private static WebsiteController NewWebsiteController(IPRODbContext db, int agentId, IAzureDomainAutomationService azure)
    {
        var controller = new WebsiteController(
            new WebsiteService(new UnitOfWork(db)), new NullBlob(), new PackageEntitlementService(new UnitOfWork(db), db),
            new AgentService(new UnitOfWork(db), new PasswordHasher<AgentUser>()), new ConfigurationBuilder().Build(), db,
            new RecordingCheck(), azure, NullLogger<WebsiteController>.Instance);
        Sign(controller, agentId);
        return controller;
    }

    private static void Sign(Controller controller, int agentId)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(context, new NullTempData());
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative)).Replace("\r\n", "\n");
    }

    private sealed class ScriptedAzure : IAzureDomainAutomationService
    {
        public bool IsConfigured => true;
        public List<string> RootCalls { get; } = new();
        public List<string> Removed { get; } = new();
        public Queue<AzureDomainAutomationResult> RootAnswers { get; } = new();

        // The www name stays "bound, certificate on its way": the check then never needs its HTTP probe.
        public Task<AzureDomainAutomationResult> EnsureDomainAsync(string hostName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AzureDomainAutomationResult { Success = true, BindingCreated = true, Message = "issuing" });

        public Task<AzureDomainAutomationResult> EnsureRootDomainAsync(string hostName, CancellationToken cancellationToken = default)
        {
            RootCalls.Add(hostName);
            return Task.FromResult(RootAnswers.Count > 0
                ? RootAnswers.Dequeue()
                : new AzureDomainAutomationResult { Success = true, BindingCreated = true, CertificateCreated = true, SslBound = true });
        }

        public Task<AzureDomainAutomationResult> RemoveDomainAsync(string hostName, CancellationToken cancellationToken = default)
        {
            Removed.Add(hostName);
            return Task.FromResult(new AzureDomainAutomationResult { Success = true, Message = "removed" });
        }
    }

    private sealed class RecordingCheck : IDomainCheckService
    {
        public List<string> Checked { get; } = new();
        public Task<bool> CheckAsync(AgentDomain domain, CancellationToken cancellationToken = default)
        {
            Checked.Add(domain.DomainName);
            return Task.FromResult(true);
        }
    }

    private sealed class NoHttp : System.Net.Http.IHttpClientFactory
    {
        public System.Net.Http.HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("a short address pointed at the platform is settled by DNS and Azure, never by fetching it");
    }

    private sealed class NullEmail : IPRO.Email.IEmailService
    {
        public Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) => Task.FromResult(true);
        public Task<IPRO.Email.EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) => Task.FromResult(IPRO.Email.EmailSendResult.Sent());
        public Task<bool> SendBulkAsync(IEnumerable<IPRO.Email.EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData) => Task.FromResult(true);
    }

    private sealed class NullBlob : IBlobStorageService
    {
        public Task<string> UploadAsync(Stream fileStream, string fileName, string containerName, string contentType, bool isPrivate) => Task.FromResult("https://blob.example.test/x");
        public Task<bool> DeleteAsync(string blobUrl) => Task.FromResult(true);
        public Task<Stream?> DownloadAsync(string blobUrl) => Task.FromResult<Stream?>(null);
        public Task<List<string>> ListAsync(string containerName) => Task.FromResult(new List<string>());
        public string GetPublicUrl(string containerName, string fileName) => $"https://blob.example.test/{containerName}/{fileName}";
        public Task EnsureContainerAccessAsync(string containerName, bool isPrivate) => Task.CompletedTask;
    }

    private sealed class NullConsent : IEmailConsentService
    {
        public bool IsSuppressed(Client client, EmailChannel channel, bool designSurvivesOptOut = false) => false;
        public Task<SuppressionResult> SuppressAllAsync(Client client, string source) => throw new NotSupportedException();
        public Task ResubscribeAsync(Client client) => throw new NotSupportedException();
        public bool LiftBounceSuppression(Client client) => throw new NotSupportedException();
        public Task<int> CancelSuppressedDripEnrollmentsAsync(int batchLimit = 500) => Task.FromResult(0);
        public Task<string> GetOrCreateTokenAsync(Client client) => Task.FromResult("tok");
        public string BuildPreferencesUrl(string token) => $"https://example.test/prefs/{token}";
    }

    private sealed class NullTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
