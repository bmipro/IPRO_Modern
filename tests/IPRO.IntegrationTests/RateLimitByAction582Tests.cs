using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AspNetCoreRateLimit;
using IPRO.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using WebLimits = IPRO.Web.Infrastructure.EndpointRateLimits;
using AdminLimits = IPRO.Admin.Infrastructure.EndpointRateLimits;

namespace IPRO.IntegrationTests;

// 582 (2026-10-16). The limits on sign-in, registration, password reset and the public forms were rules
// naming exact addresses ("POST:/PublicWebsite/SubmitLead" in appsettings.json). Routing reaches the
// same action from other spellings -- /PublicWebsite/SubmitLead/x (the default route's optional id),
// /portal/..., a long s for an s -- and each spelling had an allowance of its own, or none: the
// 3-in-5-minutes brake on the contact forms (and on guessing their security sum) and the 10-in-5-minutes
// brake on sign-in (no lockout behind it) were a path suffix away from nothing. Found by the 578
// security review.
//
// The servers below are the real routing tables (both apps' controllers, the routes Program.cs maps,
// pinned at the end), the real limiter registrations and each app's own appsettings.json, on a loopback
// port. The actions themselves never run: the last middleware answers with the name of the action the
// request reached. X-Test-Visitor stands in for UseForwardedHeaders (the address Azure reported).
public class RateLimitByAction582Tests : IClassFixture<RateLimitByAction582Tests.Servers>
{
    private const string Lead = "PublicWebsiteController.SubmitLead (";
    private static int _visitors;
    private readonly Servers _servers;

    public RateLimitByAction582Tests(Servers servers) => _servers = servers;

    private static string NewVisitor()
    {
        var n = Interlocked.Increment(ref _visitors);
        return $"198.18.{n / 250}.{n % 250 + 1}";
    }

    // ---- the public forms ------------------------------------------------------------------------------

    [Fact]
    public async Task An_honest_visitor_gets_through_and_the_fourth_post_in_five_minutes_does_not()
    {
        var visitor = NewVisitor();
        for (var i = 0; i < 3; i++)
            Assert.Contains(Lead, await ReachedAsync(_servers.Web, HttpMethod.Post, "/PublicWebsite/SubmitLead", visitor));

        var refused = await SendAsync(_servers.Web, HttpMethod.Post, "/PublicWebsite/SubmitLead", visitor);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.Status);
        Assert.True(refused.Response.Headers.Contains("Retry-After"), "a refused visitor is told when to come back");
        Assert.DoesNotContain(Lead, refused.Body);                         // the action never ran

        // Somebody else is not held up by it.
        Assert.Contains(Lead, await ReachedAsync(_servers.Web, HttpMethod.Post, "/PublicWebsite/SubmitLead", NewVisitor()));
    }

    // Every spelling here reaches SubmitLead ({0} is a different suffix on each request).
    public static TheoryData<string> LeadSpellings => new()
    {
        "/PublicWebsite/SubmitLead/{0}",                // the one the review found
        "/PublicWebsite/SubmitLead/x{0}",
        "/portal/PublicWebsite/SubmitLead",
        "/portal/PublicWebsite/SubmitLead/{0}",
        "/PUBLICWEBSITE/submitlead",
        "/PublicWebsite/SubmitLead/",
        "/PublicWebsite/Submit%4Cead",                  // an encoded letter
        "/PublicWebsite/SubmitLead/%2F{0}",             // an encoded slash in the id
    };

    // And these reach no action at all, so there is nothing to count.
    [Theory]
    [InlineData("/PublicWeb%C5%BFite/SubmitLead")]      // long s: routing compares letters ASCII-only
    [InlineData("/PublicWebsite/Subm%C4%B1tLead")]      // dotless i
    [InlineData("/PublicWebsite/SubmitLead/x/y")]
    [InlineData("/PublicWebsite/SubmitLead.x")]
    [InlineData("/PublicWebsite%2FSubmitLead")]
    [InlineData("//PublicWebsite/SubmitLead")]
    [InlineData("/PublicWebsite//SubmitLead")]
    public async Task Spellings_routing_does_not_take_reach_no_action(string spelling)
    {
        var answer = await SendAsync(_servers.Web, HttpMethod.Post, spelling, NewVisitor());
        Assert.Equal(HttpStatusCode.NotFound, answer.Status);
        Assert.Equal("no action", answer.Body);
    }

    [Theory]
    [MemberData(nameof(LeadSpellings))]
    public async Task Every_spelling_of_the_contact_form_shares_one_allowance(string spelling)
    {
        // The spelling really reaches the action (otherwise there would be nothing to limit).
        Assert.Contains(Lead, await ReachedAsync(_servers.Web, HttpMethod.Post, Spell(spelling, 0), NewVisitor()));

        // On its own: three, then refused.
        var visitor = NewVisitor();
        for (var i = 1; i <= 3; i++)
            Assert.Contains(Lead, await ReachedAsync(_servers.Web, HttpMethod.Post, Spell(spelling, i), visitor));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SendAsync(_servers.Web, HttpMethod.Post, Spell(spelling, 4), visitor)).Status);

        // After three on the usual address it is no second allowance either.
        var other = NewVisitor();
        for (var i = 0; i < 3; i++)
            Assert.Contains(Lead, await ReachedAsync(_servers.Web, HttpMethod.Post, "/PublicWebsite/SubmitLead", other));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SendAsync(_servers.Web, HttpMethod.Post, Spell(spelling, 5), other)).Status);
    }

    [Fact]
    public async Task Each_form_keeps_its_own_allowance_as_before()
    {
        var visitor = NewVisitor();
        for (var i = 0; i < 3; i++) await ReachedAsync(_servers.Web, HttpMethod.Post, "/PublicWebsite/SubmitLead", visitor);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SendAsync(_servers.Web, HttpMethod.Post, "/PublicWebsite/SubmitLead", visitor)).Status);

        Assert.Contains("PublicWebsiteController.SubmitTestimonial (", await ReachedAsync(_servers.Web, HttpMethod.Post, "/PublicWebsite/SubmitTestimonial/x", visitor));
        Assert.Contains("PublicWebsiteController.SubmitCustomForm (", await ReachedAsync(_servers.Web, HttpMethod.Post, "/portal/PublicWebsite/SubmitCustomForm/1", visitor));
    }

    [Fact]
    public async Task The_website_poll_is_limited_like_the_emailed_one()
    {
        // WebsiteVote never had a rule of its own (only the general 120 a minute).
        var visitor = NewVisitor();
        for (var i = 0; i < 20; i++)
            Assert.Contains("PollVoteController.WebsiteVote (", await ReachedAsync(_servers.Web, HttpMethod.Post, "/Poll/WebsiteVote", visitor));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SendAsync(_servers.Web, HttpMethod.Post, "/poll/websitevote/", visitor)).Status);
    }

    // ---- sign-in, registration, the admin sign-in ------------------------------------------------------

    public static TheoryData<string, string, string, int> SignInSpellings => new()
    {
        // usual address, another spelling, the action, its allowance
        { "/Account/Login", "/Account/Login/{0}", "AccountController.Login (", 10 },
        { "/Account/Login", "/portal/Account/Login/{0}", "AccountController.Login (", 10 },
        { "/Account/Login", "/ACCOUNT/LOGIN", "AccountController.Login (", 10 },
        { "/ClientPortalAccount/Login", "/ClientPortalAccount/Login/{0}", "ClientPortalAccountController.Login (", 10 },
        { "/ClientPortalAccount/Login", "/portal/ClientPortalAccount/Login/{0}", "ClientPortalAccountController.Login (", 10 },
        { "/Account/Register", "/Account/Register/{0}", "AccountController.Register (", 5 },
        { "/Account/Register", "/pub/register.aspx", "AccountController.Register (", 5 },
        { "/Account/ForgotPassword", "/Account/ForgotPassword/{0}", "AccountController.ForgotPassword (", 5 },
        { "/Account/ResetPassword", "/portal/Account/ResetPassword/{0}", "AccountController.ResetPassword (", 5 },
        { "/Account/ValidatePromoCode", "/Account/ValidatePromoCode/{0}", "AccountController.ValidatePromoCode (", 5 },
    };

    [Theory]
    [MemberData(nameof(SignInSpellings))]
    public async Task Sign_in_and_account_steps_keep_one_allowance_however_written(string usual, string spelling, string action, int allowance)
        => await OneAllowanceAsync(_servers.Web, HttpMethod.Post, usual, spelling, action, allowance);

    [Theory]
    [InlineData("/Admin/Login/{0}")]
    [InlineData("/admin/LOGIN/")]
    public async Task The_super_admin_sign_in_keeps_one_allowance_however_written(string spelling)
        => await OneAllowanceAsync(_servers.Admin, HttpMethod.Post, "/Admin/Login", spelling, "AdminController.Login (", 10);

    [Fact]
    public async Task The_prospect_preview_keeps_its_allowance_however_written()
        => await OneAllowanceAsync(_servers.Web, HttpMethod.Get, "/Preview/Show", "/Preview/Show/{0}", "PreviewController.Show (", 30);

    [Fact]
    public async Task Retrying_a_domain_is_limited_at_the_address_the_page_really_posts_to()
    {
        // Website/Index.cshtml posts to /portal/Website/RetryDomain/<id>; the old rule named the address
        // without the id, so it never applied to the button.
        await OneAllowanceAsync(_servers.Web, HttpMethod.Post, "/portal/Website/RetryDomain/7", "/Website/RetryDomain/{0}", "WebsiteController.RetryDomain (", 5);
    }

    private static async Task OneAllowanceAsync(HttpClient server, HttpMethod method, string usual, string spelling, string action, int allowance)
    {
        Assert.Contains(action, await ReachedAsync(server, method, Spell(spelling, 0), NewVisitor()));

        var visitor = NewVisitor();
        for (var i = 1; i <= allowance; i++)
            Assert.Contains(action, await ReachedAsync(server, method, Spell(spelling, i), visitor));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SendAsync(server, method, Spell(spelling, allowance + 1), visitor)).Status);

        var other = NewVisitor();
        for (var i = 0; i < allowance; i++)
            Assert.Contains(action, await ReachedAsync(server, method, usual, other));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SendAsync(server, method, Spell(spelling, allowance + 2), other)).Status);
    }

    // ---- who the visitor is ----------------------------------------------------------------------------

    [Fact]
    public void A_visitor_is_their_address_an_IPv6_visitor_their_64()
    {
        Assert.Equal("203.0.113.9", RateLimitVisitor.Key(IPAddress.Parse("203.0.113.9")));
        Assert.Equal("203.0.113.9", RateLimitVisitor.Key(IPAddress.Parse("::ffff:203.0.113.9")));   // IPv4 written as IPv6
        Assert.Equal(RateLimitVisitor.Key(IPAddress.Parse("2001:db8:582:1::a")), RateLimitVisitor.Key(IPAddress.Parse("2001:db8:582:1:ffff:ffff:ffff:ffff")));
        Assert.NotEqual(RateLimitVisitor.Key(IPAddress.Parse("2001:db8:582:1::a")), RateLimitVisitor.Key(IPAddress.Parse("2001:db8:582:2::a")));
        Assert.NotEqual(RateLimitVisitor.Key(IPAddress.Parse("203.0.113.9")), RateLimitVisitor.Key(IPAddress.Parse("203.0.113.10")));
        // No address at all is one shared visitor, limited, never a free pass.
        Assert.Equal(RateLimitVisitor.Key((IPAddress?)null), RateLimitVisitor.Key(new DefaultHttpContext()));
        Assert.False(string.IsNullOrEmpty(RateLimitVisitor.Key((IPAddress?)null)));
    }

    [Fact]
    public async Task Changing_addresses_inside_one_IPv6_block_gives_no_new_allowance()
    {
        for (var i = 1; i <= 3; i++)
            Assert.Contains(Lead, await ReachedAsync(_servers.Web, HttpMethod.Post, "/PublicWebsite/SubmitLead", $"2001:db8:582:7::{i:x}"));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SendAsync(_servers.Web, HttpMethod.Post, "/PublicWebsite/SubmitLead", "2001:db8:582:7:abcd::4")).Status);
        Assert.Contains(Lead, await ReachedAsync(_servers.Web, HttpMethod.Post, "/PublicWebsite/SubmitLead", "2001:db8:582:8::1"));
    }

    [Fact]
    public async Task The_address_counted_is_the_one_Azure_reports_and_not_one_the_visitor_writes()
    {
        // Both apps configure forwarding exactly so (pinned below), and run it before either limiter.
        var options = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
        {
            ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
        };
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var cidr in new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16" })
        {
            var parts = cidr.Split('/');
            var prefix = IPAddress.Parse(parts[0]);
            var length = int.Parse(parts[1]);
            options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, length));
            options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix.MapToIPv6(), length + 96));
        }
        async Task<string> Counted(string connection, params string[] forwardedFor)
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Parse(connection);
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
            context.Request.Headers["X-Forwarded-Proto"] = "https";
            var middleware = new Microsoft.AspNetCore.HttpOverrides.ForwardedHeadersMiddleware(
                _ => Task.CompletedTask, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, Microsoft.Extensions.Options.Options.Create(options));
            await middleware.Invoke(context);
            return RateLimitVisitor.Key(context);
        }

        // Azure's front end (it connects from 169.254.x, written the IPv6 way) appends the address it saw.
        Assert.Equal("203.0.113.5", await Counted("::ffff:169.254.130.1", "203.0.113.5:51234"));
        // Whatever the visitor wrote into the header comes before it and is not read, one header or several.
        Assert.Equal("203.0.113.5", await Counted("::ffff:169.254.130.1", "198.51.100.1, 198.51.100.2, 203.0.113.5:51234"));
        Assert.Equal("203.0.113.5", await Counted("::ffff:169.254.130.1", "not-an-ip", "203.0.113.5:51234"));
        // Anything not connecting from Azure's own networks is not believed at all.
        Assert.Equal("198.51.100.7", await Counted("198.51.100.7", "203.0.113.99"));

        foreach (var app in new[] { "IPRO.Web", "IPRO.Admin" })
        {
            var program = Read($@"src\{app}\Program.cs");
            Assert.Contains("options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;", program);
            Assert.Contains("options.KnownNetworks.Clear();\n    options.KnownProxies.Clear();", program);
            Assert.Contains("new[] { \"10.0.0.0/8\", \"172.16.0.0/12\", \"192.168.0.0/16\", \"169.254.0.0/16\" }", program);
            Assert.Contains("options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix.MapToIPv6(), length + 96));", program);
            Assert.DoesNotContain("ForwardLimit", program);                   // only the last entry, Azure's
            Assert.DoesNotContain("KnownProxies.Add", program);
            var forwarded = program.IndexOf("app.UseForwardedHeaders();", StringComparison.Ordinal);
            Assert.True(forwarded >= 0 && forwarded < program.IndexOf("app.UseRateLimiter();", StringComparison.Ordinal));
        }
    }

    // ---- what carries a limit ----------------------------------------------------------------------------

    // Anonymous actions a post can reach that carry no limit, each with the reason.
    private static readonly Dictionary<string, string> WebUnlimited = new()
    {
        ["AzureEmailEventsController.Index"] = "Azure's delivery reports: a shared secret decides, and they come in bursts from Azure's own servers",
        ["SesEmailEventsController.Index"] = "Amazon's delivery reports: Amazon's signature decides, in bursts from Amazon's servers",
        ["PaymentsWebhookController.Stripe"] = "Stripe's signed notices",
        // Checking a notice costs two calls to PayPal (TODO 582 names the follow-up); a limit per address
        // would refuse PayPal's own bursts.
        ["BillingController.Webhook"] = "PayPal's signed notices",
        ["NewsletterController.SendGridEvents"] = "SendGrid's signed notices",
        ["EmailPreferencesController.OneClick"] = "the one-click unsubscribe mail providers send from their own servers; the random token in the address decides",
        ["EmailPreferencesController.Save"] = "the random token in the address decides; nothing to guess",
        ["ClientDocumentController.Approve"] = "the random token in the address decides; nothing to guess",
        ["ClientDocumentController.Decline"] = "the random token in the address decides; nothing to guess",
        ["TestimonialRequestController.Submit"] = "the random token in the address decides; nothing to guess",
        ["ClientPortalAccountController.Activate"] = "the random token from the invitation decides; nothing to guess",
        ["ClientPortalAccountController.ChooseAccount"] = "acts only on the accounts this browser has just signed in to (kept in its session)",
        ["ClientPortalAccountController.Logout"] = "signs the visitor out; nothing to guess",
        // No method named, so a post reaches them too.
        ["NewsletterController.Unsubscribe"] = "the random token in the link decides; nothing to guess",
        ["AccountController.Logout"] = "signs the visitor out; nothing to guess",
        ["AccountController.AccessDenied"] = "a page",
        ["ErrorController.Status"] = "the error page",
        ["HomeController.Index"] = "a page; a post shows it and records a visit, as a GET does",
        ["HomeController.Error"] = "the error page",
        ["PublicWebsiteController.Index"] = "a customer's public page; a post shows it and records a view, as a GET does",
        ["PublicWebsiteController.Page"] = "a customer's public page; a post shows it and records a view, as a GET does",
    };

    private static readonly Dictionary<string, string> AdminUnlimited = new()
    {
        ["AdminController.Logout"] = "signs the visitor out; nothing to guess",
        ["AdminController.AccessDenied"] = "a page",
        ["AdminController.Error"] = "the error page",
        ["ErrorController.Status"] = "the error page",
    };

    [Fact]
    public void Every_anonymous_post_in_both_apps_carries_a_limit_or_a_stated_reason()
    {
        var missing = new List<string>();
        var stale = new List<string>();
        foreach (var (assembly, unlimited, policies) in new[]
        {
            (typeof(IPRO.Web.Controllers.PublicWebsiteController).Assembly, WebUnlimited, WebLimits.Policies),
            (typeof(IPRO.Admin.Controllers.AdminController).Assembly, AdminUnlimited, AdminLimits.Policies),
        })
        {
            var seen = new HashSet<string>();
            foreach (var (name, method) in Actions(assembly))
            {
                var verbs = method.GetCustomAttributes(true).OfType<IActionHttpMethodProvider>().SelectMany(p => p.HttpMethods).ToList();
                // Pages are skipped; an action with no method named answers a post too, so it is not.
                if (verbs.Count > 0 && verbs.All(v => v is "GET" or "HEAD")) continue;
                var limit = PolicyOf(method);
                if (limit != null)
                {
                    Assert.True(policies.ContainsKey(limit), $"{name} names a limit that does not exist: {limit}");
                    Assert.False(unlimited.ContainsKey(name), $"{name} is limited; take it off the list of reasons");
                    continue;
                }
                if (!IsAnonymous(method)) continue;
                seen.Add(name);
                if (!unlimited.ContainsKey(name)) missing.Add(name);
            }
            stale.AddRange(unlimited.Keys.Where(k => !seen.Contains(k)));
        }
        Assert.True(missing.Count == 0, "anonymous and unlimited: " + string.Join(", ", missing));
        Assert.True(stale.Count == 0, "a reason for an action that is not an anonymous post: " + string.Join(", ", stale));
    }

    [Fact]
    public void The_limits_are_the_rules_they_replace_and_sit_on_the_actions_those_rules_named()
    {
        var minutes = TimeSpan.FromMinutes;
        Assert.Equal(new Dictionary<string, (int, TimeSpan)>
        {
            [WebLimits.SignIn] = (10, minutes(5)),
            [WebLimits.Registration] = (5, TimeSpan.FromHours(1)),
            [WebLimits.PasswordReset] = (5, minutes(5)),
            [WebLimits.PromoCode] = (5, minutes(5)),
            [WebLimits.PublicForm] = (3, minutes(5)),
            [WebLimits.PollVote] = (20, minutes(5)),
            [WebLimits.DomainRetry] = (5, minutes(1)),
            [WebLimits.Preview] = (30, minutes(1)),
        }, WebLimits.Policies.ToDictionary(p => p.Key, p => (p.Value.Limit, p.Value.Window)));
        Assert.Equal(new Dictionary<string, (int, TimeSpan)> { [AdminLimits.SignIn] = (10, minutes(5)) },
            AdminLimits.Policies.ToDictionary(p => p.Key, p => (p.Value.Limit, p.Value.Window)));

        var expected = new Dictionary<string, string>
        {
            ["AccountController.Login"] = WebLimits.SignIn,
            ["ClientPortalAccountController.Login"] = WebLimits.SignIn,
            ["AccountController.Register"] = WebLimits.Registration,
            ["AccountController.ForgotPassword"] = WebLimits.PasswordReset,
            ["AccountController.ResetPassword"] = WebLimits.PasswordReset,
            ["AccountController.ValidatePromoCode"] = WebLimits.PromoCode,
            ["PublicWebsiteController.SubmitLead"] = WebLimits.PublicForm,
            ["PublicWebsiteController.SubmitCustomForm"] = WebLimits.PublicForm,
            ["PublicWebsiteController.SubmitTestimonial"] = WebLimits.PublicForm,
            ["PollVoteController.Vote"] = WebLimits.PollVote,
            ["PollVoteController.WebsiteVote"] = WebLimits.PollVote,
            ["WebsiteController.RetryDomain"] = WebLimits.DomainRetry,
            ["PreviewController.Show"] = WebLimits.Preview,
            ["PreviewController.Site"] = WebLimits.Preview,
            ["AdminController.Login"] = AdminLimits.SignIn,
        };
        var actual = Actions(typeof(IPRO.Web.Controllers.PublicWebsiteController).Assembly)
            .Concat(Actions(typeof(IPRO.Admin.Controllers.AdminController).Assembly))
            .Select(a => (a.Name, Policy: PolicyOf(a.Method), a.Method))
            .Where(a => a.Policy != null)
            .ToList();
        Assert.Equal(expected.OrderBy(e => e.Key), actual.Select(a => new KeyValuePair<string, string>(a.Name, a.Policy!)).OrderBy(e => e.Key));
        // On the page-changing half only: the sign-in page itself is not counted.
        Assert.All(actual.Where(a => a.Name != "PreviewController.Show" && a.Name != "PreviewController.Site"),
            a => Assert.Contains("POST", a.Method.GetCustomAttributes(true).OfType<IActionHttpMethodProvider>().SelectMany(p => p.HttpMethods)));
    }

    [Fact]
    public void Both_apps_count_after_routing_and_the_library_keeps_only_the_general_rule()
    {
        foreach (var app in new[] { "IPRO.Web", "IPRO.Admin" })
        {
            var program = Read($@"src\{app}\Program.cs");
            Assert.Contains("builder.Services.AddEndpointRateLimits();", program);
            Assert.Contains("app.UseRouting();\napp.UseRateLimiter();\n", program);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(program, @"app\.UseRateLimiter\(\);"));

            // An address named in a rule is the weakness this closed: the library keeps the general
            // per-address rule and nothing else.
            using var settings = JsonDocument.Parse(Read($@"src\{app}\appsettings.json"));
            var rule = Assert.Single(settings.RootElement.GetProperty("IpRateLimiting").GetProperty("GeneralRules").EnumerateArray());
            Assert.Equal("*", rule.GetProperty("Endpoint").GetString());
            Assert.Equal("1m", rule.GetProperty("Period").GetString());
            Assert.Equal(120, rule.GetProperty("Limit").GetInt32());
        }

        // The servers above route as the apps do.
        var web = Read(@"src\IPRO.Web\Program.cs");
        Assert.Contains("app.MapControllerRoute(\n    \"legacy-register\",\n    \"pub/register.aspx\",\n    new { controller = \"Account\", action = \"Register\" });", web);
        Assert.Contains("app.MapControllerRoute(\"portal\", \"portal/{controller=Dashboard}/{action=Index}/{id?}\");", web);
        Assert.Contains("app.MapControllerRoute(\"default\", \"{controller=Home}/{action=Index}/{id?}\");", web);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(web, @"app\.MapControllerRoute\(").Count);
        var admin = Read(@"src\IPRO.Admin\Program.cs");
        Assert.Contains("app.MapControllerRoute(\"admin\", \"{controller=AdminDashboard}/{action=Index}/{id?}\");", admin);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(admin, @"app\.MapControllerRoute\("));
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static IEnumerable<(string Name, MethodInfo Method)> Actions(Assembly assembly) =>
        from type in assembly.GetTypes()
        where typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract && type.IsPublic
        from method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        where !method.IsSpecialName && method.GetCustomAttribute<NonActionAttribute>() == null
        select (type.Name + "." + method.Name, method);

    private static string? PolicyOf(MethodInfo method) =>
        (method.GetCustomAttribute<EnableRateLimitingAttribute>(true)
         ?? method.DeclaringType!.GetCustomAttribute<EnableRateLimitingAttribute>(true))?.PolicyName;

    private static bool IsAnonymous(MethodInfo method)
    {
        var type = method.DeclaringType!;
        if (method.GetCustomAttributes(true).OfType<IAllowAnonymous>().Any() || type.GetCustomAttributes(true).OfType<IAllowAnonymous>().Any())
            return true;
        return !method.GetCustomAttributes(true).OfType<IAuthorizeData>().Any() && !type.GetCustomAttributes(true).OfType<IAuthorizeData>().Any();
    }

    private static string Spell(string spelling, int n) => spelling.Replace("{0}", n.ToString(), StringComparison.Ordinal);

    private static async Task<string> ReachedAsync(HttpClient server, HttpMethod method, string path, string visitor)
    {
        var answer = await SendAsync(server, method, path, visitor);
        Assert.True(answer.Status == HttpStatusCode.OK, $"{method} {path} from {visitor}: {(int)answer.Status} {answer.Body}");
        return answer.Body;
    }

    private static async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> SendAsync(HttpClient server, HttpMethod method, string path, string visitor)
    {
        // Sent exactly as written: no client-side tidying of the path.
        var uri = new Uri(server.BaseAddress!.GetLeftPart(UriPartial.Authority) + path, new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("X-Test-Visitor", visitor);
        if (method != HttpMethod.Get) request.Content = new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>());
        var response = await server.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(), response);
    }

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relative)).Replace("\r\n", "\n");

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    public sealed class Servers : IAsyncLifetime
    {
        private WebApplication? _web, _admin;
        public HttpClient Web { get; private set; } = null!;
        public HttpClient Admin { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            (_web, Web) = await StartAsync("IPRO.Web", typeof(IPRO.Web.Controllers.PublicWebsiteController).Assembly,
                services =>
                {
                    services.AddSingleton<IRateLimitConfiguration, IPRO.Web.Infrastructure.ConnectionOnlyRateLimitConfiguration>();
                    WebLimits.AddEndpointRateLimits(services);
                },
                app =>
                {
                    app.MapControllerRoute(
                        "legacy-register",
                        "pub/register.aspx",
                        new { controller = "Account", action = "Register" });
                    app.MapControllerRoute("portal", "portal/{controller=Dashboard}/{action=Index}/{id?}");
                    app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
                });
            (_admin, Admin) = await StartAsync("IPRO.Admin", typeof(IPRO.Admin.Controllers.AdminController).Assembly,
                services =>
                {
                    services.AddSingleton<IRateLimitConfiguration, IPRO.Admin.Infrastructure.ConnectionOnlyRateLimitConfiguration>();
                    AdminLimits.AddEndpointRateLimits(services);
                },
                app => app.MapControllerRoute("admin", "{controller=AdminDashboard}/{action=Index}/{id?}"));
        }

        public async Task DisposeAsync()
        {
            Web?.Dispose();
            Admin?.Dispose();
            if (_web != null) await _web.DisposeAsync();
            if (_admin != null) await _admin.DisposeAsync();
        }

        private static async Task<(WebApplication, HttpClient)> StartAsync(string name, Assembly controllers, Action<IServiceCollection> limits, Action<WebApplication> routes)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = name,
                ContentRootPath = Path.Combine(RepoRoot(), "src", name),
                EnvironmentName = "Production"
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();

            // The library, registered as both Program.cs files do (pinned by RateLimitIdentity561Tests),
            // reading the app's own appsettings.json.
            builder.Services.AddMemoryCache();
            builder.Services.Configure<IpRateLimitOptions>(builder.Configuration.GetSection("IpRateLimiting"));
            builder.Services.PostConfigure<IpRateLimitOptions>(o => o.RealIpHeader = null);
            builder.Services.PostConfigure<ClientRateLimitOptions>(o => { o.RealIpHeader = null; o.ClientIdHeader = null; });
            builder.Services.AddSingleton<IIpPolicyStore, MemoryCacheIpPolicyStore>();
            builder.Services.AddSingleton<IRateLimitCounterStore, MemoryCacheRateLimitCounterStore>();
            builder.Services.AddSingleton<IProcessingStrategy, AsyncKeyLockProcessingStrategy>();
            builder.Services.AddInMemoryRateLimiting();
            limits(builder.Services);
            builder.Services.AddControllersWithViews().AddApplicationPart(controllers);

            var app = builder.Build();
            app.Use((context, next) =>
            {
                if (IPAddress.TryParse(context.Request.Headers["X-Test-Visitor"].ToString(), out var visitor))
                    context.Connection.RemoteIpAddress = visitor;
                return next(context);
            });
            app.UseIpRateLimiting();
            app.UseRouting();
            app.UseRateLimiter();
            app.Use((HttpContext context, RequestDelegate _) =>
            {
                var endpoint = context.GetEndpoint();
                context.Response.StatusCode = endpoint == null ? StatusCodes.Status404NotFound : StatusCodes.Status200OK;
                return context.Response.WriteAsync(endpoint?.DisplayName ?? "no action");
            });
            routes(app);
            await app.StartAsync();
            return (app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
        }
    }
}
