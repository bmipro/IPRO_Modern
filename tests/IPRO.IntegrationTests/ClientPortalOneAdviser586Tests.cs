using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Web.Controllers;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPRO.IntegrationTests;

// 586 (2026-10-17), from 583's security review (its item d): the client portal sign-in checked every
// adviser's client with the email typed, and an adviser can make a sign-in for any email -- give one of
// their own client records another adviser's client's email, and choose its password through the
// activation link the profile shows them. That one sign-in then reached across advisers: the planted
// password opened the planted record on the other adviser's own address, the planter's own sign-ins
// counted as wrong passwords at the other adviser's client (5 of them locked it and emailed it), and a
// name that is no client was counted once for all advisers.
//
// Now a client's sign-in belongs to the adviser whose client it is, and a password is only ever checked
// against one adviser's clients. The sign-in page decides whose: the adviser whose own address it is on
// (their domain or their free site); on iPro's shared address, the adviser the link names (?adviser=<id>,
// which every sign-in address we hand a client now carries); with no adviser named (links sent before
// this, bookmarks), the adviser whose client record with that email and a portal password is the oldest.
// These drive the real actions against a throwaway MySQL database. A refusal takes 1.5 seconds on purpose.
public class ClientPortalOneAdviser586Tests
{
    private const string Shared = "app.example.test";
    private const string RealHost = "www.real-adviser.test";       // the real adviser's own domain
    private const string PlanterHost = "planter.example.test";     // the planter's free site
    private const string Email = "jo@example.com";
    private const string Real = "the-real-one";
    private const string Planted = "planted-by-another-adviser";
    private const string Invalid = "Invalid email or password.";

    [Fact]
    public async Task On_the_real_advisers_own_address_the_planted_password_opens_nothing()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var s = await SeedPlantedAsync(testDb);

        Assert.Equal(Invalid, await RefusalAsync(testDb, RealHost, Email, Planted));
        Assert.Equal(0, await Lock583.CountAsync(testDb, "Clients", s.Planted));   // never checked there
        Assert.Equal(1, await Lock583.CountAsync(testDb, "Clients", s.Victim));    // a wrong password for the real client

        Assert.Equal(s.Victim, await SignedInAsync(testDb, RealHost, Email, Real));
        Assert.Equal(0, await Lock583.CountAsync(testDb, "Clients", s.Victim));
    }

    [Fact]
    public async Task The_planters_own_sign_ins_never_count_at_the_other_advisers_client()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var s = await SeedPlantedAsync(testDb);
        var notices = new RecordingLockNotices();

        // Five sign-ins with the planted password on the planter's own address: before 586 each was a
        // wrong password for the other adviser's client, and the fifth locked it and emailed it.
        for (var i = 0; i < 5; i++)
            Assert.Equal(s.Planted, await SignedInAsync(testDb, PlanterHost, Email, Planted, notices: notices));

        Assert.Equal(0, await Lock583.CountAsync(testDb, "Clients", s.Victim));
        Assert.Null(await Lock583.LockedUntilAsync(testDb, "Clients", s.Victim));
        Assert.Empty(notices.Queued);
    }

    [Fact]
    public async Task On_the_shared_address_the_adviser_named_in_the_link_decides()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var s = await SeedPlantedAsync(testDb);

        Assert.Equal(Invalid, await RefusalAsync(testDb, Shared, Email, Planted, adviser: s.Real.ToString()));
        Assert.Equal(0, await Lock583.CountAsync(testDb, "Clients", s.Planted));
        Assert.Equal(s.Victim, await SignedInAsync(testDb, Shared, Email, Real, adviser: s.Real.ToString()));
        Assert.Equal(s.Planted, await SignedInAsync(testDb, Shared, Email, Planted, adviser: s.Planter.ToString()));
        Assert.Equal(0, await Lock583.CountAsync(testDb, "Clients", s.Victim));   // the planter's sign-in did not count there

        // On an adviser's own address the address decides, whatever the link names.
        Assert.Equal(Invalid, await RefusalAsync(testDb, RealHost, Email, Planted, adviser: s.Planter.ToString()));
        Assert.Equal(0, await Lock583.CountAsync(testDb, "Clients", s.Planted));
    }

    [Fact]
    public async Task Without_an_adviser_in_the_link_the_oldest_client_record_with_a_portal_password_decides()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        // The oldest record of all has no portal password: it is no sign-in, and does not decide.
        var never = await Lock583.SeedAgentAsync(testDb, "never", "never@example.com");
        await SeedClientWithoutPortalAsync(testDb, never, Email);
        var s = await SeedPlantedAsync(testDb);

        Assert.Equal(Invalid, await RefusalAsync(testDb, Shared, Email, Planted));
        Assert.Equal(0, await Lock583.CountAsync(testDb, "Clients", s.Planted));
        Assert.Equal(1, await Lock583.CountAsync(testDb, "Clients", s.Victim));
        Assert.Equal(s.Victim, await SignedInAsync(testDb, Shared, Email, Real));

        // A host that is no adviser's own address answers as the shared address does.
        Assert.Equal(s.Victim, await SignedInAsync(testDb, "nobody-here.example.test", Email, Real));
    }

    [Fact]
    public async Task A_client_of_one_adviser_signs_in_from_every_address_of_theirs_and_from_no_other_advisers()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var solo = await Lock583.SeedAgentAsync(testDb, "solo", "solo@example.com");
        await SeedDomainAsync(testDb, solo, "www.solo-adviser.test");
        var other = await Lock583.SeedAgentAsync(testDb, "other", "other@example.com");
        var kim = await Lock583.SeedClientAsync(testDb, solo, "kim@example.com", Real);

        Assert.Equal(kim, await SignedInAsync(testDb, Shared, "kim@example.com", Real));                               // links sent before 586
        Assert.Equal(kim, await SignedInAsync(testDb, Shared, "kim@example.com", Real, adviser: solo.ToString()));   // the links we send now
        Assert.Equal(kim, await SignedInAsync(testDb, "www.solo-adviser.test", "kim@example.com", Real));
        Assert.Equal(kim, await SignedInAsync(testDb, "solo-adviser.test", "kim@example.com", Real));
        Assert.Equal(kim, await SignedInAsync(testDb, "solo.example.test", "kim@example.com", Real));                 // the free site
        Assert.Equal(kim, await SignedInAsync(testDb, Shared, "kim@example.com", Real, adviser: "not-a-number"));     // a mangled link

        Assert.Equal(Invalid, await RefusalAsync(testDb, "other.example.test", "kim@example.com", Real));
        Assert.Equal(Invalid, await RefusalAsync(testDb, Shared, "kim@example.com", Real, adviser: other.ToString()));
        Assert.Equal(0, await Lock583.CountAsync(testDb, "Clients", kim));
    }

    [Fact]
    public async Task A_name_that_is_no_client_is_counted_for_each_adviser_on_its_own()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await SeedPlantedAsync(testDb);
        const string nobody = "nobody@example.com";

        for (var i = 1; i <= 4; i++)
            Assert.Equal(Invalid, await RefusalAsync(testDb, PlanterHost, nobody, "x" + i));
        Assert.Equal(SignInMessages.ClientPortalLocked, await RefusalAsync(testDb, PlanterHost, nobody, "x5"));

        // The lock is the planter's address's own: another adviser's address and the shared one count afresh.
        Assert.Equal(Invalid, await RefusalAsync(testDb, RealHost, nobody, "y1"));
        Assert.Equal(Invalid, await RefusalAsync(testDb, Shared, nobody, "y2"));

        // Still a hash, never the name.
        await using var db = testDb.CreateContext();
        var keys = await db.SignInNameLocks.AsNoTracking().Select(n => n.NameKey).ToListAsync();
        Assert.Equal(3, keys.Distinct().Count());
        Assert.All(keys, k => Assert.Matches("^client:[0-9a-f]{64}$", k));
    }

    // 586's review: an "email" typed to spell out another page's count (line break and all) must not reach
    // it. If it did, five tries of it on the shared address would lock an adviser's count for a name that is
    // not their client and leave a client's own count alone -- the lock would then say, without anyone
    // being locked, whether an email is that adviser's client.
    [Fact]
    public async Task No_typed_email_reaches_another_pages_count()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var s = await SeedPlantedAsync(testDb);
        const string nobody = "nobody@example.com";

        foreach (var spelled in new[] { $"adviser {s.Planter}\n{nobody}", $"shared\n{nobody}" })
        {
            for (var i = 1; i <= 4; i++)
                Assert.Equal(Invalid, await RefusalAsync(testDb, Shared, spelled, "x" + i));
            Assert.Equal(SignInMessages.ClientPortalLocked, await RefusalAsync(testDb, Shared, spelled, "x5"));
        }

        // The planter's page and the shared address count the plain email afresh.
        Assert.Equal(Invalid, await RefusalAsync(testDb, PlanterHost, nobody, "y1"));
        Assert.Equal(Invalid, await RefusalAsync(testDb, Shared, nobody, "y2"));
    }

    [Fact]
    public async Task An_invitation_opens_on_its_advisers_own_address_and_the_shared_one_and_on_no_other()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var s = await SeedPlantedAsync(testDb);
        const string token = "0123456789abcdef0123456789abcdef";
        int invited;
        await using (var db = testDb.CreateContext())
        {
            var client = new Client { AgentUserId = s.Planter, FirstName = "Ivy", LastName = "New", Email = "ivy@example.com", PortalInviteToken = token, PortalInviteTokenExpiresAt = DateTime.UtcNow.AddDays(7) };
            db.Clients.Add(client);
            await db.SaveChangesAsync();
            invited = client.Id;
        }

        await using (var db = testDb.CreateContext())
        {
            Assert.IsType<NotFoundResult>(await Controller(db, RealHost).Activate(token));
            Assert.IsType<NotFoundResult>(await Controller(db, RealHost).Activate(token, "a-new-password", "a-new-password"));
        }
        await using (var db = testDb.CreateContext())
            Assert.Null((await db.Clients.AsNoTracking().SingleAsync(c => c.Id == invited)).PortalPasswordHash);

        await using (var db = testDb.CreateContext())
        {
            Assert.IsType<ViewResult>(await Controller(db, PlanterHost).Activate(token));
            Assert.IsType<ViewResult>(await Controller(db, Shared).Activate(token));
            var auth = new RecordingAuthentication();
            Assert.IsType<RedirectToActionResult>(await Controller(db, Shared, auth: auth).Activate(token, "a-new-password", "a-new-password"));
            Assert.Equal(invited.ToString(), auth.SignedIn?.FindFirstValue(ClaimTypes.NameIdentifier));
        }
    }

    [Fact]
    public async Task Two_records_of_one_adviser_with_one_email_and_password_offer_the_choice_of_that_advisers_records_only()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var s = await SeedPlantedAsync(testDb);
        var second = await Lock583.SeedClientAsync(testDb, s.Planter, Email, Planted);   // a couple sharing one email
        await Lock583.SetPortalPasswordAsync(testDb, s.Victim, Planted);                 // the same password at the other adviser, too

        await using var db = testDb.CreateContext();
        var controller = Controller(db, PlanterHost);
        var view = Assert.IsType<ViewResult>(await controller.Login(Email, Planted));
        Assert.Equal("ChooseAccount", view.ViewName);
        Assert.Equal(new[] { s.Planted, second }, Assert.IsAssignableFrom<IEnumerable<Client>>(view.Model).Select(c => c.Id).OrderBy(id => id));

        Assert.IsType<ForbidResult>(await controller.ChooseAccount(s.Victim));
        Assert.Equal(0, await Lock583.CountAsync(testDb, "Clients", s.Victim));
    }

    [Fact]
    public async Task Signing_out_on_the_shared_address_returns_to_the_advisers_own_sign_in()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var s = await SeedPlantedAsync(testDb);

        await using var db = testDb.CreateContext();
        var signedIn = Lock583.User(new Claim(ClaimTypes.NameIdentifier, s.Planted.ToString()), new Claim("AgentUserId", s.Planter.ToString()));
        var shared = Assert.IsType<RedirectToActionResult>(await Controller(db, Shared, signedIn).Logout());
        Assert.Equal("Login", shared.ActionName);
        Assert.Equal(s.Planter, shared.RouteValues?["adviser"]);

        // On the adviser's own address the address says it all.
        var own = Assert.IsType<RedirectToActionResult>(await Controller(db, PlanterHost, signedIn).Logout());
        Assert.Equal("Login", own.ActionName);
        Assert.False(own.RouteValues?.ContainsKey("adviser") ?? false);
    }

    [Fact]
    public async Task The_shared_sign_in_address_we_hand_a_client_names_their_adviser()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var config = Config;
        var plain = await Lock583.SeedAgentAsync(testDb, "plain", "plain@example.com");
        var withDomain = await Lock583.SeedAgentAsync(testDb, "domain", "domain@example.com");
        await SeedDomainAsync(testDb, withDomain, "www.has-domain.test");

        await using (var db = testDb.CreateContext())
        {
            Assert.Equal($"https://app.example.test/ClientPortalAccount/Login?adviser={plain}",
                ClientPortalUrls.LoginUrl(await ClientPortalUrls.GetBaseUrlAsync(db, plain, config), plain, config));
            Assert.Equal("https://www.has-domain.test/ClientPortalAccount/Login",
                ClientPortalUrls.LoginUrl(await ClientPortalUrls.GetBaseUrlAsync(db, withDomain, config), withDomain, config));
        }

        // The appointment email carries it (the shape of 460's test, on the shared address).
        int requestId;
        await using (var db = testDb.CreateContext())
        {
            var client = new Client { AgentUserId = plain, FirstName = "Ann", LastName = "Appt", Email = "ann@example.com" };
            db.Clients.Add(client);
            await db.SaveChangesAsync();
            var request = new PortalAppointmentRequest { ClientId = client.Id, PreferredDate = DateTime.UtcNow.AddDays(7) };
            db.PortalAppointmentRequests.Add(request);
            await db.SaveChangesAsync();
            requestId = request.Id;
        }
        var email = new RecordingEmail();
        await using (var db = testDb.CreateContext())
        {
            var controller = new PortalRequestsController(db, email, new Lock583.GrantAll(), config);
            var http = new DefaultHttpContext { User = Lock583.User(new Claim(ClaimTypes.NameIdentifier, plain.ToString())) };
            controller.ControllerContext = new ControllerContext { HttpContext = http };
            controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(http, new NoTempData());
            await controller.Schedule(requestId, new DateTime(2026, 11, 3, 10, 0, 0));
        }
        Assert.Contains($"https://app.example.test/ClientPortalAccount/Login?adviser={plain}", Assert.Single(email.Sent));

        // The client's profile card shows the same address (the line the card is built from).
        Assert.Contains("ViewBag.ClientPortalLoginUrl = ClientPortalUrls.LoginUrl(clientPortalBase, AgentId, _configuration);",
            Lock583.Read(@"src\IPRO.Web\Controllers\ClientsController.cs"));
        // And the sign-in form sends the adviser back with the email and password.
        Assert.Contains("<input type=\"hidden\" name=\"adviser\" value=\"@ViewData[\"Adviser\"]\" />",
            Lock583.Read(@"src\IPRO.Web\Views\ClientPortalAccount\Login.cshtml"));
    }

    // 586's review: My Website lets an adviser type any name, iPro's own included. A row naming the shared
    // address (or a brand name, or the App Service's own) must not make every sign-in there theirs.
    [Fact]
    public async Task A_domain_row_naming_one_of_ipros_own_addresses_never_makes_it_an_advisers()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var s = await SeedPlantedAsync(testDb);
        var squatter = await Lock583.SeedAgentAsync(testDb, "squatter", "squatter@example.com");
        foreach (var ours in new[] { Shared, "ipro-shared.test", "www.ipro-brand.test", "ipro-test-web.azurewebsites.net", "localhost" })
            await SeedDomainAsync(testDb, squatter, ours);   // bound, even

        foreach (var ours in new[] { Shared, "www.ipro-shared.test", "ipro-brand.test", "ipro-test-web.azurewebsites.net", "localhost" })
            Assert.Equal(s.Victim, await SignedInAsync(testDb, ours, Email, Real, adviser: s.Real.ToString()));
        Assert.Equal(s.Victim, await SignedInAsync(testDb, Shared, Email, Real));

        // An invitation sent on the shared address still opens there.
        const string token = "fedcba9876543210fedcba9876543210";
        await using (var db = testDb.CreateContext())
        {
            db.Clients.Add(new Client { AgentUserId = s.Real, FirstName = "Ivy", LastName = "New", Email = "ivy@example.com", PortalInviteToken = token, PortalInviteTokenExpiresAt = DateTime.UtcNow.AddDays(7) });
            await db.SaveChangesAsync();
        }
        await using (var db = testDb.CreateContext())
            Assert.IsType<ViewResult>(await Controller(db, Shared).Activate(token));
    }

    // A domain an adviser has typed in but Azure has not bound is not their address yet: it decides
    // nothing, and the page answers as the shared address does.
    [Fact]
    public async Task A_domain_that_is_not_bound_yet_decides_nothing()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var pending = await Lock583.SeedAgentAsync(testDb, "pending", "pending@example.com");
        var domainId = await SeedDomainAsync(testDb, pending, "www.pending-adviser.test", bound: false);
        var other = await Lock583.SeedAgentAsync(testDb, "other", "other@example.com");
        var kim = await Lock583.SeedClientAsync(testDb, other, "kim@example.com", Real);

        Assert.Equal(kim, await SignedInAsync(testDb, "www.pending-adviser.test", "kim@example.com", Real));

        await using (var db = testDb.CreateContext())
            await db.Database.ExecuteSqlRawAsync("UPDATE `AgentDomains` SET `AzureBindingStatus` = {0}, `OwnershipVerifiedAt` = UTC_TIMESTAMP(6) WHERE `Id` = {1}", AgentDomainStatus.Bound, domainId);   // 592: bound only once proven
        Assert.Equal(Invalid, await RefusalAsync(testDb, "www.pending-adviser.test", "kim@example.com", Real));
    }

    // The limit of "the oldest record decides" (said to the owner): a record made with the email BEFORE the
    // real client's takes links that name no adviser. It never takes the real client's own: their adviser's
    // address and the links that name their adviser.
    [Fact]
    public async Task An_older_planted_record_takes_links_without_an_adviser_but_never_the_clients_own_address()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var planter = await Lock583.SeedAgentAsync(testDb, "planter", "planter@example.com");
        var planted = await Lock583.SeedClientAsync(testDb, planter, Email, Planted);
        var real = await Lock583.SeedAgentAsync(testDb, "real", "real@example.com");
        await SeedDomainAsync(testDb, real, RealHost);
        var victim = await Lock583.SeedClientAsync(testDb, real, Email, Real);

        Assert.Equal(Invalid, await RefusalAsync(testDb, Shared, Email, Real));
        Assert.Equal(victim, await SignedInAsync(testDb, Shared, Email, Real, adviser: real.ToString()));
        Assert.Equal(victim, await SignedInAsync(testDb, RealHost, Email, Real));
        Assert.Equal(planted, await SignedInAsync(testDb, Shared, Email, Planted));
    }

    // ---- harness ------------------------------------------------------------------------------------

    private sealed record Planting(int Real, int Planter, int Victim, int Planted);

    // The real adviser (own domain) and their client, then the planter (free site) and the record they made
    // with that client's email and a password of their choosing. The real client's record is the older.
    private static async Task<Planting> SeedPlantedAsync(TestDatabase testDb)
    {
        var real = await Lock583.SeedAgentAsync(testDb, "real", "real@example.com");
        await SeedDomainAsync(testDb, real, RealHost);
        var victim = await Lock583.SeedClientAsync(testDb, real, Email, Real);
        var planter = await Lock583.SeedAgentAsync(testDb, "planter", "planter@example.com");
        var planted = await Lock583.SeedClientAsync(testDb, planter, Email, Planted);
        return new Planting(real, planter, victim, planted);
    }

    private static async Task SeedClientWithoutPortalAsync(TestDatabase testDb, int agentId, string email)
    {
        await using var db = testDb.CreateContext();
        db.Clients.Add(new Client { AgentUserId = agentId, FirstName = "No", LastName = "Portal", Email = email });
        await db.SaveChangesAsync();
    }

    private static async Task<int> SeedDomainAsync(TestDatabase testDb, int agentId, string host, bool bound = true)
    {
        await using var db = testDb.CreateContext();
        var template = new WebsiteTemplate { TemplateKey = ($"t586-{Guid.NewGuid():N}")[..16], Name = "T586", BusinessType = "Insurance" };
        db.Add(template);
        await db.SaveChangesAsync();
        var website = new AgentWebsite { AgentUserId = agentId, TemplateId = template.Id, SiteTitle = "Site", CustomDomain = host };
        db.AgentWebsites.Add(website);
        await db.SaveChangesAsync();
        db.AgentDomains.Add(new AgentDomain
        {
            AgentUserId = agentId, AgentWebsiteId = website.Id,
            DomainName = host, WwwDomain = host, RootDomain = host.Replace("www.", ""),
            AzureBindingStatus = bound ? AgentDomainStatus.Bound : AgentDomainStatus.BindingPending,
            SslStatus = bound ? AgentDomainStatus.Bound : AgentDomainStatus.BindingPending, IsPrimary = true,
            OwnershipVerifiedAt = bound ? DateTime.UtcNow : null   // 592: a live domain is a proven one
        });
        await db.SaveChangesAsync();
        return (await db.AgentDomains.AsNoTracking().SingleAsync(d => d.AgentWebsiteId == website.Id)).Id;
    }

    // iPro's own names, as production names them (App:BaseUrl, App:PlatformDomains, App:AliasHosts).
    private static readonly IConfiguration Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["App:BaseUrl"] = "https://" + Shared,
        ["App:PlatformDomains"] = "ipro-test-web.azurewebsites.net," + Shared + ",www.ipro-shared.test,ipro-shared.test",
        ["App:AliasHosts"] = "www.ipro-brand.test=/accountants,ipro-brand.test=/accountants",
        ["App:TemporarySiteRootDomain"] = "example.test"
    }).Build();

    private static ClientPortalAccountController Controller(IPRODbContext db, string host, ClaimsPrincipal? user = null,
        RecordingLockNotices? notices = null, RecordingAuthentication? auth = null)
    {
        var controller = Lock583.Prepare(new ClientPortalAccountController(db, new PasswordHasher<Client>(), notices ?? new RecordingLockNotices(),
            new RecordingLogger().For<ClientPortalAccountController>(), Config), user);
        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationService>(auth ?? new RecordingAuthentication());
        controller.HttpContext.RequestServices = services.BuildServiceProvider();
        controller.HttpContext.Request.Host = new HostString(host);
        return controller;
    }

    private static async Task<string?> RefusalAsync(TestDatabase testDb, string host, string email, string password, string? adviser = null)
    {
        await using var db = testDb.CreateContext();
        var result = await Controller(db, host).Login(email, password, null, adviser);
        Assert.IsType<ViewResult>(result);
        return Lock583.Error(result);
    }

    // The client record the sign-in signed in to.
    private static async Task<int> SignedInAsync(TestDatabase testDb, string host, string email, string password,
        string? adviser = null, RecordingLockNotices? notices = null)
    {
        await using var db = testDb.CreateContext();
        var auth = new RecordingAuthentication();
        var result = await Controller(db, host, notices: notices, auth: auth).Login(email, password, null, adviser);
        Assert.IsType<RedirectToActionResult>(result);
        Assert.NotNull(auth.SignedIn);
        return int.Parse(auth.SignedIn!.FindFirstValue(ClaimTypes.NameIdentifier)!);
    }

    private sealed class RecordingAuthentication : IAuthenticationService
    {
        public ClaimsPrincipal? SignedIn { get; private set; }
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(AuthenticateResult.NoResult());
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            SignedIn = principal;
            return Task.CompletedTask;
        }
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }

    private sealed class RecordingEmail : IEmailService
    {
        public List<string> Sent { get; } = new();
        public async Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
            (await SendDetailedAsync(toEmail, toName, subject, htmlBody, textBody, customArgs, replyToEmail, replyToName, listUnsubscribeUrl)).Success;
        public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        { Sent.Add(htmlBody); return Task.FromResult(EmailSendResult.Sent("msg-1")); }
        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> r, string s, string h, string? t = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string a, string b, string c, object d) => Task.FromResult(true);
    }

    private sealed class NoTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
