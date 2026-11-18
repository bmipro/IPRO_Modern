using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPRO.IntegrationTests;

// 661 (2026-11-18, the owner's decisions): "Ask for a Google review". The professional saves their Google review
// link on the Profile; a button on a client's page emails that client the link; the page after a client writes a
// testimonial offers it too. Financial and insurance advisers have it off until they turn it on; every other
// profession has it on. No automatic sending.
public class GoogleReview661Tests
{
    // ---- the rules, without a database ----------------------------------------------------------------

    [Theory]
    [InlineData("Insurance / Financial", false)]
    [InlineData("Financial Advisor", false)]
    [InlineData("Wealth Management", false)]
    [InlineData("Accountants", true)]
    [InlineData("Mortgage", true)]
    [InlineData("Mortgage Advisers", true)]
    [InlineData("Generic", true)]
    [InlineData("", true)]
    public void Financial_and_insurance_advisers_start_with_it_off_and_everyone_else_with_it_on(string businessType, bool on)
    {
        Assert.Equal(on, GoogleReviews.DefaultAskEnabled(businessType));
    }

    [Theory]
    [InlineData("https://g.page/r/CabcDEF/review", "https://g.page/r/CabcDEF/review")]
    [InlineData("g.page/r/CabcDEF/review", "https://g.page/r/CabcDEF/review")]
    [InlineData("https://search.google.com/local/writereview?placeid=ChIJ123", "https://search.google.com/local/writereview?placeid=ChIJ123")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("/contact", null)]
    [InlineData("ftp://example.com/x", null)]
    [InlineData("not a link", null)]
    [InlineData("", null)]
    public void Only_a_full_web_address_is_kept_as_the_review_link(string typed, string? kept)
    {
        Assert.Equal(kept, GoogleReviews.NormalizeUrl(typed));
    }

    // ---- the saved settings -----------------------------------------------------------------------------

    [Fact]
    public async Task The_schema_repair_makes_tables_the_model_can_use_repeatably_and_they_go_with_the_account()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, "Accountants");
        await db.Database.ExecuteSqlRawAsync("DROP TABLE `ClientGoogleReviewRequests`");   // production before this deploy
        await db.Database.ExecuteSqlRawAsync("DROP TABLE `AgentGoogleReviewSettings`");

        await StartupSchemaRepair.EnsureGoogleReviewSchemaAsync(db);
        await StartupSchemaRepair.EnsureGoogleReviewSchemaAsync(db);

        var before = await GoogleReviewPreference.GetAsync(db, agentId, "Accountants");
        Assert.False(before.Saved);
        Assert.True(before.AskEnabled);
        Assert.False(before.CanAsk);   // no link yet

        await GoogleReviewPreference.SetAsync(db, agentId, "g.page/r/abc/review", true);
        await GoogleReviewPreference.SetAsync(db, agentId, "g.page/r/abc/review", true);   // the same answer twice is not an error
        var after = await GoogleReviewPreference.GetAsync(db, agentId, "Accountants");
        Assert.True(after.Saved);
        Assert.True(after.CanAsk);
        Assert.Equal("https://g.page/r/abc/review", after.ReviewUrl);

        db.ClientGoogleReviewRequests.Add(new ClientGoogleReviewRequest { AgentUserId = agentId, ClientId = 1, SentAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        Assert.NotNull(await GoogleReviewPreference.LastAskedAsync(db, agentId, 1));

        await db.AgentUsers.Where(a => a.Id == agentId).ExecuteDeleteAsync();
        Assert.False(await db.AgentGoogleReviewSettings.AsNoTracking().AnyAsync());
    }

    [Fact]
    public async Task A_financial_adviser_who_saves_a_link_still_has_to_turn_it_on()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, "Insurance / Financial");

        var fresh = await GoogleReviewPreference.GetAsync(db, agentId, "Insurance / Financial");
        Assert.False(fresh.AskEnabled);

        await GoogleReviewPreference.SetAsync(db, agentId, "https://g.page/r/abc/review", false);
        Assert.False((await GoogleReviewPreference.GetAsync(db, agentId, "Insurance / Financial")).CanAsk);

        await GoogleReviewPreference.SetAsync(db, agentId, "https://g.page/r/abc/review", true);
        Assert.True((await GoogleReviewPreference.GetAsync(db, agentId, "Insurance / Financial")).CanAsk);
    }

    // ---- the button on a client's page ------------------------------------------------------------------

    [Fact]
    public async Task Without_a_saved_link_nothing_is_sent()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, "Accountants");
        var clientId = await SeedClientAsync(db, agentId);

        var email = new RecordingEmail();
        var controller = NewTestimonials(db, email, agentId, new NoConsent());
        await controller.AskForGoogleReview(clientId);

        Assert.Empty(email.Sent);
        Assert.Contains("Profile", (string)controller.TempData["Error"]!);
        Assert.False(await db.ClientGoogleReviewRequests.AsNoTracking().AnyAsync());
    }

    [Fact]
    public async Task The_client_gets_one_letter_with_the_link_and_a_second_press_the_same_day_sends_nothing()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, "Accountants");
        var clientId = await SeedClientAsync(db, agentId);
        await GoogleReviewPreference.SetAsync(db, agentId, "https://search.google.com/local/writereview?placeid=X&hl=en", true);

        var email = new RecordingEmail();
        var controller = NewTestimonials(db, email, agentId, new NoConsent());
        await controller.AskForGoogleReview(clientId);

        var sent = Assert.Single(email.Sent);
        Assert.Equal("Would you review Review Co on Google?", sent.Subject);
        Assert.Contains("Leave a Google review", sent.Body);
        Assert.Contains("href=\"https://search.google.com/local/writereview?placeid=X&amp;hl=en\"", sent.Body);
        Assert.Contains("Hi Cli,", sent.Body);
        Assert.Contains("Google review request sent", (string)controller.TempData["Success"]!);
        Assert.Single(await db.ClientGoogleReviewRequests.AsNoTracking().Where(r => r.ClientId == clientId).ToListAsync());

        var again = NewTestimonials(db, email, agentId, new NoConsent());
        await again.AskForGoogleReview(clientId);
        Assert.Single(email.Sent);
        Assert.Contains("less than a day ago", (string)again.TempData["Warning"]!);
    }

    [Fact]
    public async Task An_unsubscribed_client_is_not_sent_one()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, "Accountants");
        var clientId = await SeedClientAsync(db, agentId);
        await GoogleReviewPreference.SetAsync(db, agentId, "https://g.page/r/abc/review", true);

        var email = new RecordingEmail();
        var controller = NewTestimonials(db, email, agentId, new AllSuppressed());
        await controller.AskForGoogleReview(clientId);

        Assert.Empty(email.Sent);
        Assert.Contains("unsubscribed", (string)controller.TempData["Warning"]!);
    }

    [Fact]
    public async Task A_failed_send_is_said_and_not_recorded_as_asked()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, "Accountants");
        var clientId = await SeedClientAsync(db, agentId);
        await GoogleReviewPreference.SetAsync(db, agentId, "https://g.page/r/abc/review", true);

        var email = new RecordingEmail { Next = _ => EmailSendResult.Failed("Address rejected") };
        var controller = NewTestimonials(db, email, agentId, new NoConsent());
        await controller.AskForGoogleReview(clientId);

        Assert.Null(controller.TempData["Success"]);
        Assert.Contains("Address rejected", (string)controller.TempData["Error"]!);
        Assert.False(await db.ClientGoogleReviewRequests.AsNoTracking().AnyAsync());
    }

    // ---- the testimonial thank-you page -----------------------------------------------------------------

    [Fact]
    public async Task The_thank_you_page_offers_the_link_only_when_the_professional_turned_it_on()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, "Mortgage");
        var submission = new TestimonialSubmission
        {
            AgentUserId = agentId, FirstName = "Cli", LastName = "Ent", Email = "c@example.test",
            Body = "Great help with our renewal.", Status = TestimonialStatus.Pending, RequestToken = Guid.NewGuid().ToString("N")
        };
        db.TestimonialSubmissions.Add(submission);
        await db.SaveChangesAsync();

        var page = new IPRO.Web.Controllers.TestimonialRequestController(db);
        Assert.IsType<ViewResult>(await page.Show(submission.RequestToken!));
        Assert.Null(page.ViewBag.GoogleReviewUrl);   // no link saved

        await GoogleReviewPreference.SetAsync(db, agentId, "https://g.page/r/abc/review", true);
        var withLink = new IPRO.Web.Controllers.TestimonialRequestController(db);
        await withLink.Show(submission.RequestToken!);
        Assert.Equal("https://g.page/r/abc/review", (string)withLink.ViewBag.GoogleReviewUrl);

        await GoogleReviewPreference.SetAsync(db, agentId, "https://g.page/r/abc/review", false);
        var switchedOff = new IPRO.Web.Controllers.TestimonialRequestController(db);
        await switchedOff.Show(submission.RequestToken!);
        Assert.Null(switchedOff.ViewBag.GoogleReviewUrl);
    }

    // ---- the screens and the start-up step --------------------------------------------------------------

    [Fact]
    public void The_profile_card_the_client_button_the_thank_you_link_and_both_start_ups_are_in_place()
    {
        var profile = Read(@"src\IPRO.Web\Views\Account\Profile.cshtml");
        Assert.Contains("id=\"google-reviews\"", profile);
        Assert.Contains("<input type=\"hidden\" name=\"GoogleReviewPosted\" value=\"true\" />", profile);
        Assert.Contains("Let me ask clients for Google reviews", profile);
        Assert.Contains("your dealer's or regulator's rules on reviews and testimonials come first", profile);

        var details = Read(@"src\IPRO.Web\Views\Clients\Details.cshtml");
        Assert.Contains("asp-action=\"AskForGoogleReview\"", details);
        Assert.Contains("Ask for a Google Review", details);
        // After the testimonial card's heading, so the portal card's slice (ClientPortalAddressTests) is untouched.
        Assert.True(details.IndexOf("AskForGoogleReview", StringComparison.Ordinal) > details.IndexOf(">Testimonial</h6>", StringComparison.Ordinal));

        var thanks = Read(@"src\IPRO.Web\Views\TestimonialRequest\Show.cshtml");
        Assert.Contains("Leave a Google review", thanks);
        Assert.Contains("rel=\"noopener\"", thanks);

        const string step = "await StartupGuard.RunStepAsync(\"StartupSchemaRepair.EnsureGoogleReviewSchemaAsync\", () => StartupSchemaRepair.EnsureGoogleReviewSchemaAsync(db), db, app.Logger);";
        Assert.Contains(step, Read(@"src\IPRO.Web\Program.cs"));
        Assert.Contains(step, Read(@"src\IPRO.Admin\Program.cs"));

        var guide = Read(@"DOCS\15_TESTIMONIALS.md");
        Assert.Contains("## Ask a Client for a Google Review", guide);
    }

    // ---- harness ----------------------------------------------------------------------------------------

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = "https://app.example.test" })
        .Build();

    private static IPRO.Web.Controllers.TestimonialsController NewTestimonials(IPRODbContext db, IEmailService email, int agentId, IPRO.Business.Services.IEmailConsentService consent)
    {
        var controller = new IPRO.Web.Controllers.TestimonialsController(db, new GrantAll(), email, Config(), consent);
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(ctx, new NoTempData());
        return controller;
    }

    private static async Task<int> SeedAgentAsync(IPRODbContext db, string businessType)
    {
        var rule = new BillingRule { PackageName = ($"T661-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t661-{Guid.NewGuid():N}")[..20],
            Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Review", LastName = "Agent", CompanyName = "Review Co",
            DomainName = ($"t661-{Guid.NewGuid():N}")[..24],
            BusinessType = businessType,
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return agent.Id;
    }

    private static async Task<int> SeedClientAsync(IPRODbContext db, int agentId)
    {
        var client = new Client { AgentUserId = agentId, FirstName = "Cli", LastName = "Ent", Email = $"cli-{Guid.NewGuid():N}@example.test" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return client.Id;
    }

    private sealed class RecordingEmail : IEmailService
    {
        public Func<string, EmailSendResult> Next { get; set; } = _ => EmailSendResult.Sent();
        public List<(string To, string Subject, string Body)> Sent { get; } = new();
        public async Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
            (await SendDetailedAsync(toEmail, toName, subject, htmlBody, textBody, customArgs, replyToEmail, replyToName, listUnsubscribeUrl)).Success;
        public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        { Sent.Add((toEmail, subject, htmlBody)); return Task.FromResult(Next(toEmail)); }
        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> r, string s, string h, string? t = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string a, string b, string c, object d) => Task.FromResult(true);
    }

    private sealed class GrantAll : IPRO.Business.Interfaces.IPackageEntitlementService
    {
        public Task<IPRO.Business.Interfaces.PackageFeatureAccess> GetAccessAsync(int agentId, string featureCode) =>
            Task.FromResult(new IPRO.Business.Interfaces.PackageFeatureAccess { FeatureCode = featureCode, IsIncluded = true });
        public Task<bool> HasAccessAsync(int agentId, string featureCode) => Task.FromResult(true);
        public Task<Dictionary<int, bool>> HasAccessBulkAsync(IEnumerable<int> agentIds, string featureCode) => throw new NotSupportedException();
        public Task<bool> IsAccessGatedAsync(int agentId) => Task.FromResult(false);
    }

    private class NoConsent : IPRO.Business.Services.IEmailConsentService
    {
        public virtual bool IsSuppressed(Client client, IPRO.Business.Services.EmailChannel channel, bool designSurvivesOptOut = false) => false;
        public Task<IPRO.Business.Services.SuppressionResult> SuppressAllAsync(Client client, string source) => throw new NotSupportedException();
        public Task ResubscribeAsync(Client client) => throw new NotSupportedException();
        public bool LiftBounceSuppression(Client client) => throw new NotSupportedException();
        public Task<int> CancelSuppressedDripEnrollmentsAsync(int batchLimit = 500) => Task.FromResult(0);
        public Task<string> GetOrCreateTokenAsync(Client client) => Task.FromResult("tok");
        public string BuildPreferencesUrl(string token) => $"https://example.test/prefs/{token}";
    }

    private sealed class AllSuppressed : NoConsent
    {
        public override bool IsSuppressed(Client client, IPRO.Business.Services.EmailChannel channel, bool designSurvivesOptOut = false) => true;
    }

    private sealed class NoTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private static string Read(string relative) => File.ReadAllText(FindRepoFile(relative));

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }
}
