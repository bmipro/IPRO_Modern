using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Web.Controllers;
using IPRO.Web.Infrastructure;
using IPRO.Web.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests.Accessibility;

// 578 (2026-10-15), audit finding 6 (WCAG 3.3.1, 3.3.3, 3.3.7 and 1.3.1): a public form the server
// turned down came back EMPTY, with one general message shown on every form of the page and keyboard
// focus left at the top. Now that form alone comes back as the visitor left it (never the security
// answer), its message says what to fix, each field in trouble is marked and described, and focus
// goes to the message. Checkbox questions are a group named by their question.
public class PublicForms578Tests : IClassFixture<EdgeFixture>
{
    private readonly EdgeFixture _edge;
    public PublicForms578Tests(EdgeFixture edge) => _edge = edge;

    // ------------------------------------------------------------------ the server keeps it --

    [Fact]
    public async Task A_wrong_security_answer_keeps_what_was_typed_but_never_the_answer()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var host = await SeedSiteAsync(db);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var controller = NewController(db, host, cache);

        var result = await controller.SubmitLead(new WebsiteLeadFormViewModel
        {
            SubmissionType = WebsiteLeadTypes.Contact, ReturnPath = "/contact",
            FirstName = "  Dana ", LastName = "Rivers", Email = "dana@example.test", Phone = "416-555-0100",
            Message = "Please call me about my RRSP.", ConsentGiven = true,
            FormStartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30,
            CaptchaToken = "not-a-real-token", CaptchaAnswer = "42"
        });

        Assert.Equal("/contact", Assert.IsType<LocalRedirectResult>(result).Url);
        var retry = Kept(controller, PublicFormRetry.LeadForm(WebsiteLeadTypes.Contact, null));
        Assert.Equal("Dana", retry.Value("FirstName"));
        Assert.Equal("dana@example.test", retry.Value("Email"));
        Assert.Equal("Please call me about my RRSP.", retry.Value("Message"));
        Assert.True(retry.Consent);
        Assert.Null(retry.Value("CaptchaAnswer"));
        Assert.DoesNotContain(retry.Values.Values, v => v == "42");
        Assert.Equal("CaptchaAnswer", Assert.Single(retry.Problems).Field);
        Assert.Contains("security question", retry.Message);
        // The visitor's details stay on the server; the redirect carries a token, not the details.
        Assert.DoesNotContain("Dana", (string)controller.TempData[PublicFormRetry.TempDataKey]!);
    }

    [Fact]
    public async Task A_turned_down_form_names_each_field_to_fix()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var host = await SeedSiteAsync(db);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var controller = NewController(db, host, cache);
        controller.ModelState.AddModelError("Email", "The Email field is not a valid e-mail address.");
        controller.ModelState.AddModelError("FirstName", "The FirstName field is required.");

        await controller.SubmitTestimonial(new TestimonialFormViewModel
        {
            ReturnPath = "/forms", FirstName = "", Email = "dana-at-example", Body = "Dana made it simple.", ConsentGiven = false,
            FormStartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30,
            CaptchaToken = ValidCaptcha(controller, 7), CaptchaAnswer = "7"
        });

        var retry = Kept(controller, PublicFormRetry.TestimonialForm);
        Assert.Equal(new[] { "FirstName", "Email", "ConsentGiven" }, retry.Problems.Select(p => p.Field));
        Assert.Equal("Enter your first name.", retry.ProblemWith("FirstName")!.Text);
        Assert.Equal("Enter an email address like name@example.com.", retry.ProblemWith("Email")!.Text);
        Assert.Equal("dana-at-example", retry.Value("Email"));
        Assert.Equal("Dana made it simple.", retry.Value("Body"));
    }

    [Fact]
    public async Task A_missing_answer_on_a_custom_form_is_named_by_its_question()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var host = await SeedSiteAsync(db);
        var agentId = await db.AgentWebsites.Where(w => w.CustomDomain == host).Select(w => w.AgentUserId).SingleAsync();
        var form = new WebsiteForm { AgentUserId = agentId, Title = "Request a meeting" };
        db.Add(form);
        await db.SaveChangesAsync();
        var topics = new WebsiteFormField { WebsiteFormId = form.Id, FieldType = WebsiteFormFieldTypes.CheckboxGroup, Label = "Topics", IsRequired = true, SortOrder = 1 };
        var company = new WebsiteFormField { WebsiteFormId = form.Id, FieldType = WebsiteFormFieldTypes.Text, Label = "Company", SortOrder = 2 };
        db.AddRange(topics, company);
        await db.SaveChangesAsync();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var controller = NewController(db, host, cache);

        await controller.SubmitCustomForm(new WebsiteCustomFormViewModel
        {
            WebsiteFormId = form.Id, ReturnPath = "/forms", FirstName = "Dana", Email = "dana@example.test", ConsentGiven = true,
            FormStartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30,
            CaptchaToken = ValidCaptcha(controller, 9), CaptchaAnswer = "9",
            Answers = new List<CustomAnswerInput> { new() { FieldId = company.Id, Values = new List<string> { "Rivers Ltd" } } }
        });

        var retry = Kept(controller, PublicFormRetry.CustomForm(form.Id));
        var problem = Assert.Single(retry.Problems);
        Assert.Equal(PublicFormRetry.AnswerField(topics.Id), problem.Field);
        Assert.Equal("Topics", problem.Label);
        Assert.Equal("Rivers Ltd", retry.Answer(company.Id));
        Assert.Equal("Dana", retry.Value("FirstName"));
    }

    [Fact]
    public void A_full_store_still_keeps_the_message_and_never_grows_past_its_size()
    {
        // The security review's flood: posts turned down before any security answer is checked, each
        // with as much text as a form takes. The store keeps what fits and no more.
        using var store = new MemoryCache(new MemoryCacheOptions { SizeLimit = 30_000, TrackStatistics = true });
        PublicFormRetry Big(int n) => new PublicFormRetry { Form = PublicFormRetry.TestimonialForm, Message = "Nothing was sent yet." }
            .Keep("FirstName", "Dana" + n, 80).Keep("Body", new string('x', 2000), 2000).Problem("Body", "Your testimonial", "Use 2,000 characters or fewer.");
        var kept = new List<TempDataDictionary>();
        for (var n = 0; n < 40; n++)
        {
            var tempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempData());
            Big(n).Save(tempData, store);
            kept.Add(tempData);
        }
        Assert.InRange(store.GetCurrentStatistics()!.CurrentEstimatedSize!.Value, 1, 30_000);
        // 40 forms of 2,000 characters did not all fit; the latest visitor still gets the message and the
        // field to fix, kept whole or without the typed text.
        Assert.True(kept.Count(k => PublicFormRetry.Find(k, PublicFormRetry.TestimonialForm, store: store)?.Value("Body") != null) < 40);
        var last = PublicFormRetry.Find(kept[^1], PublicFormRetry.TestimonialForm, store: store);
        Assert.NotNull(last);
        Assert.Equal("Nothing was sent yet.", last!.Message);
        Assert.Equal("Body", Assert.Single(last.Problems).Field);
    }

    [Fact]
    public void The_message_and_the_fields_to_fix_survive_a_restart()
    {
        // A deploy between the post and the page: the store is new and empty, TempData is not.
        var tempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempData());
        using (var before = new MemoryCache(new MemoryCacheOptions { SizeLimit = 100_000 }))
        {
            new PublicFormRetry { Form = PublicFormRetry.TestimonialForm, Message = "Nothing was sent yet." }
                .Keep("FirstName", "Dana", 80).Problem("Email", "Email", "Enter your email address.").Save(tempData, before);
        }
        using var after = new MemoryCache(new MemoryCacheOptions { SizeLimit = 100_000 });
        var retry = PublicFormRetry.Find(tempData, PublicFormRetry.TestimonialForm, store: after);
        Assert.NotNull(retry);
        Assert.Equal("Nothing was sent yet.", retry!.Message);
        Assert.Equal("Enter your email address.", retry.ProblemWith("Email")!.Text);
        Assert.Null(retry.Value("FirstName"));
        Assert.DoesNotContain("Dana", (string)tempData[PublicFormRetry.NoteKey]!);
    }

    [Fact]
    public void Once_the_page_has_looked_nothing_of_the_kept_form_rides_on_to_the_next_page()
    {
        // The re-check's case: the form came back from the store, the visitor sent it, and the message
        // must not reappear on the next visit from a TempData entry nobody read.
        var context = new DefaultHttpContext();
        var saved = new RecordingTempData();
        var tempData = new TempDataDictionary(context, saved);
        new PublicFormRetry { Form = PublicFormRetry.TestimonialForm, Message = "Nothing was sent yet." }.Save(tempData);
        tempData.Save();

        var next = new RecordingTempData(saved.Values);
        var nextPage = new TempDataDictionary(context, next);
        Assert.NotNull(PublicFormRetry.Find(nextPage, PublicFormRetry.TestimonialForm));
        nextPage.Save();
        Assert.Empty(next.Values);
    }

    private sealed class RecordingTempData : ITempDataProvider
    {
        public Dictionary<string, object> Values { get; private set; }
        public RecordingTempData(Dictionary<string, object>? values = null) => Values = values ?? new Dictionary<string, object>();
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>(Values);
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) => Values = new Dictionary<string, object>(values);
    }

    // ------------------------------------------------------------------ the page shows it --

    [Fact]
    public async Task The_kept_form_comes_back_filled_in_with_each_problem_beside_its_field()
    {
        var site = PublicSitePages.Build(PublicSitePages.Modern);
        var retry = new PublicFormRetry { Form = PublicFormRetry.LeadForm(WebsiteLeadTypes.Contact, ContactBlock(site)), Message = "Nothing was sent yet. Please fix the following and send again:", Consent = true }
            .Keep("FirstName", "Dana", 80).Keep("Email", "dana-at-example", 200).Keep("Message", "Call me <soon>", 4000)
            .Problem("Email", "Email", "Enter an email address like name@example.com.");
        var html = await RenderWithRetryAsync(site.Model("contact"), retry);

        var contact = FormHtml(html, "/PublicWebsite/SubmitLead", "value=\"Contact\"");
        Assert.Contains("value=\"Dana\"", contact);
        Assert.Contains("value=\"dana-at-example\"", contact);
        Assert.Contains("Call me &lt;soon&gt;</textarea>", contact);
        Assert.Matches("name=\"ConsentGiven\"[^>]*checked=\"checked\"", contact);
        Assert.Matches("name=\"Email\" id=\"(?<f>website-lead-[0-9a-f]+)-Email\"[^>]*aria-invalid=\"true\" aria-describedby=\"\\k<f>-Email-error\"", contact);
        Assert.DoesNotMatch("name=\"FirstName\"[^>]*aria-invalid", contact);
        Assert.Contains("-Email-error\" aria-hidden=\"true\">Enter an email address like name@example.com.</span>", contact);
        // The security answer field is empty, with a new question.
        Assert.Matches("name=\"CaptchaAnswer\"(?![^>]*value=)", contact);

        // Only that form: the newsletter form on the same page shows no message and no values.
        var newsletter = FormHtml(html, "/PublicWebsite/SubmitLead", "value=\"Newsletter\"");
        Assert.DoesNotContain("Dana", newsletter);
        Assert.Equal(1, Regex.Matches(html, "class=\"website-lead-error\"").Count);
    }

    [Fact]
    public async Task Of_two_forms_of_a_kind_on_a_page_only_the_one_turned_down_comes_back()
    {
        var site = PublicSitePages.Build(PublicSitePages.Modern);
        var contact = site.Pages.Single(p => p.Slug == "contact");
        var first = contact.Blocks.Single(b => b.BlockType == WebsiteBlockTypes.NewsletterSignup);
        contact.Blocks.Add(new WebsiteContentBlock { Id = 9001, BlockType = WebsiteBlockTypes.NewsletterSignup, Heading = "Another newsletter", IsVisible = true, SortOrder = 9001, SettingsJson = "{}" });
        var retry = new PublicFormRetry { Form = PublicFormRetry.LeadForm(WebsiteLeadTypes.Newsletter, first.Id), Message = "Nothing was sent yet." }
            .Keep("FirstName", "Dana", 80).Problem("CaptchaAnswer", "Security check", "Answer the new question.");
        var html = await RenderWithRetryAsync(site.Model("contact"), retry);

        Assert.Contains("Dana", FormHtml(html, "/PublicWebsite/SubmitLead", $"name=\"BlockId\" value=\"{first.Id}\""));
        Assert.DoesNotContain("Dana", FormHtml(html, "/PublicWebsite/SubmitLead", "name=\"BlockId\" value=\"9001\""));
        Assert.Single(Regex.Matches(html, "class=\"website-lead-error\""));
    }

    [Fact]
    public async Task Focus_goes_to_the_message_and_its_links_lead_to_the_fields()
    {
        var site = PublicSitePages.Build(PublicSitePages.Classic);
        var retry = new PublicFormRetry { Form = PublicFormRetry.LeadForm(WebsiteLeadTypes.Contact, ContactBlock(site)), Message = "Nothing was sent yet. Please fix the following and send again:" }
            .Keep("FirstName", "Dana", 80)
            .Problem("Email", "Email", "Enter your email address.")
            .Problem("ConsentGiven", "Agreement", "Tick the box to agree so we can reply.");
        await _edge.OpenAsync(await RenderWithRetryAsync(site.Model("contact"), retry), "form-retry", 1280);

        Assert.True(await _edge.Edge.EvaluateAsync<bool>("document.activeElement.classList.contains('website-lead-error') && document.activeElement.getAttribute('role') === 'alert'"),
            "focus did not go to the form's message");
        await _edge.Edge.PressAsync("Tab");
        Assert.Equal("Email: Enter your email address.", await _edge.Edge.EvaluateAsync<string>("document.activeElement.textContent.trim()"));
        await _edge.Edge.PressAsync("Enter");
        Assert.Equal("Email", await _edge.Edge.EvaluateAsync<string>("document.activeElement.name"));
        // A screen reader hears the field's own problem as its description.
        Assert.Equal("Enter your email address.", await _edge.Edge.EvaluateAsync<string>(
            "document.getElementById(document.activeElement.getAttribute('aria-describedby')).textContent.trim()"));
        Assert.Empty(await _edge.AxeOpenPageAsync());
    }

    [Fact]
    public async Task A_kept_Did_you_know_form_is_shown_instead_of_its_cards()
    {
        var site = PublicSitePages.Build(PublicSitePages.Editorial);
        var dykBlock = site.DidYouKnow.Keys.Single();
        var retry = new PublicFormRetry { Form = PublicFormRetry.LeadForm(WebsiteLeadTypes.DidYouKnow, dykBlock), Message = "Nothing was sent yet." }
            .Keep("FirstName", "Dana", 80).Problem("CaptchaAnswer", "Security check", "Answer the new question.");
        await _edge.OpenAsync(await RenderWithRetryAsync(site.Model("resources"), retry), "dyk-retry", 1280);

        Assert.False(await _edge.Edge.EvaluateAsync<bool>($"document.getElementById('dyk-form-{dykBlock}').hidden"), "the form with the message stayed hidden");
        Assert.True(await _edge.Edge.EvaluateAsync<bool>($"document.getElementById('dyk-cards-{dykBlock}').hidden"));
        Assert.Equal("Dana", await _edge.Edge.EvaluateAsync<string>($"document.querySelector('#dyk-form-{dykBlock} input[name=FirstName]').value"));
    }

    [Fact]
    public async Task A_checkbox_question_is_a_group_named_by_the_question_with_its_help_tied_to_it()
    {
        var site = PublicSitePages.Build(PublicSitePages.Modern);
        var html = await PublicSitePages.RenderAsync(site.Model("forms"));
        Assert.Matches("<fieldset class=\"website-form-group\" aria-describedby=\"website-form-[0-9a-f]+-answer-3-help\">\\s*<legend><span>Topics</span><span aria-hidden=\"true\"> \\*</span><span class=\"visually-hidden\"> \\(required\\)</span></legend>", html);
        Assert.Matches("<div class=\"website-form-help\" id=\"website-form-[0-9a-f]+-answer-3-help\">Pick one or more.</div>", html);
        // No label wraps another label any more.
        Assert.DoesNotMatch("<label>\\s*<span>Topics", html);
    }

    // ------------------------------------------------------------------ plumbing --

    private static PublicFormRetry Kept(PublicWebsiteController controller, string form)
    {
        var retry = PublicFormRetry.Find(controller.TempData, form);
        Assert.NotNull(retry);
        return retry!;
    }

    private static int ContactBlock(PublicSitePages.Site site) =>
        site.Pages.Single(p => p.Slug == "contact").Blocks.Single(b => b.BlockType == WebsiteBlockTypes.ContactForm).Id;

    private static Task<string> RenderWithRetryAsync(PublicWebsiteViewModel model, PublicFormRetry retry) =>
        PublicSitePages.RenderAsync(model, new PublicSitePages.RenderOptions { Retry = retry });

    /// <summary>The whole &lt;form&gt; with this action that contains the marker.</summary>
    private static string FormHtml(string html, string action, string marker)
    {
        foreach (Match form in Regex.Matches(html, $"<form [^>]*action=\"{Regex.Escape(action)}\".*?</form>", RegexOptions.Singleline))
        {
            if (form.Value.Contains(marker, StringComparison.Ordinal)) return form.Value;
        }
        throw new Xunit.Sdk.XunitException($"no form posting to {action} with {marker}");
    }

    private static string ValidCaptcha(PublicWebsiteController controller, int answer) =>
        ((IDataProtectionProvider)controller.HttpContext.Items["test-protection"]!)
            .CreateProtector("IPRO.Web.PublicWebsite.Captcha.v1")
            .Protect($"{answer}|{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}|{Guid.NewGuid():N}");

    private static async Task<string> SeedSiteAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = $"F-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        db.Add(rule);
        var host = $"f578-{Guid.NewGuid():N}"[..14] + ".example.test";
        var agent = new AgentUser
        {
            UserName = $"f578-{Guid.NewGuid():N}"[..20], Email = $"f578-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Dana", LastName = "Rivers", DomainName = $"f578-{Guid.NewGuid():N}"[..24], Country = "Canada", Province = "Ontario"
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        agent.PackageId = rule.Id;
        db.Add(new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id, BillingRuleId = rule.Id, Amount = 60m, Status = BillingStatus.Active, Period = BillingPeriod.Monthly,
            StartDate = DateTime.UtcNow.AddDays(-40), NextBillingDate = DateTime.UtcNow.AddDays(20)
        });
        var template = new WebsiteTemplate { TemplateKey = $"tk-{Guid.NewGuid():N}"[..16], Name = "Test", BusinessType = "All" };
        db.Add(template);
        await db.SaveChangesAsync();
        var website = new AgentWebsite { AgentUserId = agent.Id, TemplateId = template.Id, IsPublished = true, CustomDomain = host };
        db.Add(website);
        await db.SaveChangesAsync();
        // 592: a custom domain is served through its proven domain row, not the website's own field.
        db.Add(new AgentDomain { AgentUserId = agent.Id, AgentWebsiteId = website.Id, DomainName = host, WwwDomain = host, RootDomain = host, IsPrimary = true, OwnershipVerifiedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return host;
    }

    private static PublicWebsiteController NewController(IPRODbContext db, string host, IMemoryCache cache)
    {
        var protection = DataProtectionProvider.Create($"f578-{Guid.NewGuid():N}");
        var controller = new PublicWebsiteController(
            db, new PackageEntitlementService(new UnitOfWork(db), db), new NullEmail(), NullLogger<PublicWebsiteController>.Instance,
            new ConfigurationBuilder().Build(), protection, new NullBlob(), cache, new NullConsent());
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(host);
        context.Items["test-protection"] = protection;
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new TempDataDictionary(context, new NullTempData());
        return controller;
    }

    private sealed class NullEmail : IPRO.Email.IEmailService
    {
        public Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) => Task.FromResult(true);
        public Task<IPRO.Email.EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) => Task.FromResult(IPRO.Email.EmailSendResult.Sent());
        public Task<bool> SendBulkAsync(IEnumerable<IPRO.Email.EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData) => Task.FromResult(true);
    }

    private sealed class NullBlob : IPRO.Utility.IBlobStorageService
    {
        public Task<string> UploadAsync(System.IO.Stream fileStream, string fileName, string containerName, string contentType, bool isPrivate) => Task.FromResult("https://blob.example.test/x");
        public Task<bool> DeleteAsync(string blobUrl) => Task.FromResult(true);
        public Task<System.IO.Stream?> DownloadAsync(string blobUrl) => Task.FromResult<System.IO.Stream?>(null);
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

    private sealed class NullTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
