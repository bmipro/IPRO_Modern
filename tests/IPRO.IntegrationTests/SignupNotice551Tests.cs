using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using IPRO.Billing;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IPRO.IntegrationTests;

// 551 (2026-10-05). L'Avenue Boulangerie signed up and nobody at iPro heard about it -- the owner: "as an admin
// to this system I did not get any email saying that someone has registered ... Lets do it so I/admin get an
// email". Two notices to iPro's own mailbox: one when an adviser registers (before PayPal), one when the
// subscription starts. Neither may ever fail a sign-up or a payment.
public class SignupNotice551Tests
{
    private static readonly DateTime When = new(2026, 10, 5, 14, 42, 0, DateTimeKind.Utc); // 10:42 a.m. Eastern

    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    [Fact]
    public void The_notice_goes_to_the_sign_up_setting_first_then_to_the_support_mailbox()
    {
        Assert.Equal("signups@example.test", SignupNotice.Recipient(Config(
            ("Signups:NotificationEmail", "signups@example.test"), ("Support:NotificationEmail", "support@example.test"))));
        Assert.Equal("support@example.test", SignupNotice.Recipient(Config(("Support:NotificationEmail", "support@example.test"))));
        Assert.Equal("support@example.test", SignupNotice.Recipient(Config(
            ("Signups:NotificationEmail", "CHANGE_THIS_EMAIL"), ("Support:NotificationEmail", "support@example.test"))));
        Assert.Null(SignupNotice.Recipient(Config(("Support:NotificationEmail", "CHANGE_THIS_SUPPORT_EMAIL"))));
        Assert.Null(SignupNotice.Recipient(Config(("Signups:NotificationEmail", "  "), ("Support:NotificationEmail", "not-an-address"))));
        Assert.Null(SignupNotice.Recipient(Config()));
    }

    [Fact]
    public void The_registration_notice_names_the_adviser_the_plan_how_to_reach_them_and_where_they_came_from()
    {
        var notice = SignupNotice.ForRegistration(Registration(), Config());

        Assert.Equal("New sign-up: L'Avenue Boulangerie (Jane Doe)", notice.Subject);
        foreach (var expected in new[]
                 {
                     "Jane Doe of L&#39;Avenue Boulangerie signed up on October 5, 2026 at 10:42 a.m.",
                     "Generic", "IPro Gold, monthly", "$60.00 a month", "setup fee $200.00", "WELCOME10",
                     "jane@example.test", "416-555-0100", "Toronto, Ontario", "Google (organic)",
                     "They are on their way to PayPal.", "a second email when their subscription starts",
                     "href=\"https://admin.iproadvisers.com/Agents/Details/42\"",
                 })
        {
            Assert.Contains(expected, notice.Html);
        }
        Assert.Contains("Jane Doe of L'Avenue Boulangerie signed up on October 5, 2026 at 10:42 a.m.", notice.Text);
        Assert.Contains("https://admin.iproadvisers.com/Agents/Details/42", notice.Text);
    }

    [Fact]
    public void What_an_adviser_typed_is_encoded_in_the_notice()
    {
        var notice = SignupNotice.ForRegistration(Registration() with { CompanyName = "<b>Evil</b> & Co" }, Config());
        Assert.DoesNotContain("<b>Evil</b>", notice.Html);
        Assert.Contains("&lt;b&gt;Evil&lt;/b&gt; &amp; Co", notice.Html);
    }

    [Fact]
    public void A_waived_setup_fee_no_code_and_a_trial_read_plainly()
    {
        var waived = SignupNotice.ForRegistration(Registration() with { SetupFee = 0m, Code = "" }, Config());
        Assert.Contains("no setup fee", waived.Html);
        Assert.Contains("(none)", waived.Html);

        var trial = SignupNotice.ForRegistration(Registration() with
        {
            PackageName = "Broker Package", Period = "", RecurringPrice = 0m, SetupFee = 0m,
            TrialCode = "BROKER2026", TrialEndsAt = new DateTime(2026, 10, 19, 14, 42, 0, DateTimeKind.Utc),
        }, Config());
        Assert.Contains("Broker Package, free trial", trial.Html);
        Assert.Contains("Free trial through invitation BROKER2026, until October 19, 2026. There is no payment step.", trial.Html);
        Assert.DoesNotContain("on their way to PayPal", trial.Html);
    }

    [Theory]
    [InlineData("google", "organic", "", "www.google.com", "Google (organic)")]
    [InlineData("linkedin", "", "fall2026", "", "LinkedIn (campaign fall2026)")]
    [InlineData("newsletter", "email", "oct", "", "Newsletter (email, campaign oct)")]
    [InlineData("", "", "", "www.facebook.com", "a link on www.facebook.com")]
    [InlineData("", "", "", "", "typed the address or used a bookmark (no referring site)")]
    public void Where_they_came_from_reads_as_words(string source, string medium, string campaign, string referrer, string expected)
    {
        var origin = new PlatformSignupOrigin { Source = source, Medium = medium, Campaign = campaign, ReferrerHost = referrer };
        Assert.Equal(expected, SignupNotice.CameFrom(origin));
        Assert.Equal("not recorded", SignupNotice.CameFrom(null));
    }

    [Fact]
    public void The_start_notice_says_the_plan_is_active_and_what_the_first_bill_was()
    {
        var notice = SignupNotice.ForStart(Started(), Config());
        Assert.Equal("Subscription started: L'Avenue Boulangerie (Jane Doe)", notice.Subject);
        Assert.Contains("IPro Gold, monthly", notice.Html);
        Assert.Contains("First bill: $67.80 (tax included)", notice.Html);
        Assert.Contains("PayPal approved it on October 5, 2026 at 10:42 a.m.", notice.Html);
        Assert.Contains("A new customer.", notice.Html);
        Assert.Contains("href=\"https://admin.iproadvisers.com/Agents/Details/42\"", notice.Html);

        var again = SignupNotice.ForStart(Started() with { Again = true }, Config());
        Assert.Equal("Subscription restarted: L'Avenue Boulangerie (Jane Doe)", again.Subject);
        Assert.Contains("They have had a subscription before", again.Html);

        var free = SignupNotice.ForStart(Started() with { NoCost = true, FirstBillTotal = 0m, Code = "FREE551" }, Config());
        Assert.Contains("No charge: code FREE551 covers it.", free.Html);
    }

    [Fact]
    public void The_SuperAdmin_link_follows_its_setting()
    {
        var notice = SignupNotice.ForRegistration(Registration(), Config(("Signups:AdminBaseUrl", "https://admin.example.test/")));
        Assert.Contains("href=\"https://admin.example.test/Agents/Details/42\"", notice.Html);
    }

    [Fact]
    public async Task A_failing_mailer_or_no_recipient_never_fails_the_caller()
    {
        var notice = SignupNotice.ForRegistration(Registration(), Config());

        var throwing = new RecordingEmail { Throw = true };
        Assert.False(await SignupNotice.SendAsync(throwing, Config(("Support:NotificationEmail", "support@example.test")), NullLogger.Instance, notice));

        var recording = new RecordingEmail();
        Assert.False(await SignupNotice.SendAsync(recording, Config(), NullLogger.Instance, notice));
        Assert.Empty(recording.Sent);

        Assert.True(await SignupNotice.SendAsync(recording, Config(("Support:NotificationEmail", "support@example.test")), NullLogger.Instance, notice));
        var sent = Assert.Single(recording.Sent);
        Assert.Equal("support@example.test", sent.To);
        Assert.Equal(notice.Subject, sent.Subject);
    }

    [Fact]
    public void Registration_and_the_start_of_a_subscription_both_send_the_notice()
    {
        var account = Read(@"src\IPRO.Web\Controllers\AccountController.cs").Replace("\r\n", "\n");
        var register = account[account.IndexOf("public async Task<IActionResult> Register(AgentRegistrationViewModel model", StringComparison.Ordinal)..];
        register = register[..register.IndexOf("private async Task<IActionResult> RerenderRegisterAsync", StringComparison.Ordinal)];
        // After the welcome email, before PayPal: a registrant who never pays is still news.
        Assert.True(register.IndexOf("await SendSignupNoticeAsync(agent, submittedPackage, model, trialInvite);", StringComparison.Ordinal)
                    > register.IndexOf("RegistrationWelcomeTemplate.BuildHtml(welcome)", StringComparison.Ordinal));
        Assert.True(register.IndexOf("await SendSignupNoticeAsync(agent, submittedPackage, model, trialInvite);", StringComparison.Ordinal)
                    < register.IndexOf("await _billing.CreateSubscriptionAsync(", StringComparison.Ordinal));
        Assert.Contains("await SignupNotice.SendAsync(_email, _configuration, _logger, SignupNotice.ForRegistration(", account);

        var billing = Read(@"src\IPRO.Billing\PayPalBillingService.cs");
        Assert.Contains("var startsSubscription551 = change?.ChangeType == SubscriptionChangeType.Subscribe;", billing);
        Assert.Contains("if (startsSubscription551)", billing);
        Assert.Contains("await NotifySubscriptionStartedAsync(userId, billing, invoice, paymentConfirmed, now);", billing);
    }

    [Fact]
    public async Task A_no_cost_sign_up_tells_the_owner_once_that_the_subscription_started()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var package = new BillingRule
        {
            PackageName = "IPro Gold", MonthlyPrice = 60m, AnnualPrice = 720m, SetupFee = 200m,
            PayPalMonthlyPlanId = "P-GOLD-MONTHLY", PayPalAnnualPlanId = "P-GOLD-ANNUAL", IsActive = true,
        };
        db.Add(package);
        db.Add(new PromotionCode
        {
            Code = "FREE551", IsActive = true,
            RecurringDiscountType = PromoDiscountType.PercentOff, RecurringDiscountValue = 100m,
            SetupFeeDiscountType = PromoDiscountType.PercentOff, SetupFeeDiscountValue = 100m,
        });
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = $"sn-{Guid.NewGuid():N}"[..20], DomainName = $"sn-{Guid.NewGuid():N}"[..24],
            FirstName = "Jane", LastName = "Doe", CompanyName = "L'Avenue Boulangerie", Email = "jane@example.test",
            PackageId = package.Id, PromotionCode = "FREE551",
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var email = new RecordingEmail();
        var configuration = Config(("Support:NotificationEmail", "support@example.test"));
        var service = new PayPalBillingService(new UnitOfWork(db), db, new NoHttp(), email,
            Options.Create(new PayPalSettings { ClientId = "test-client", ClientSecret = "test-secret" }),
            configuration, NullLogger<PayPalBillingService>.Instance);

        var result = await service.CreateSubscriptionAsync(agent.Id, package.Id, BillingPeriod.Monthly, "https://app.example.test/Billing/PayPalReturn", "https://app.example.test/Billing/Cancel");

        Assert.True(result.Success, result.Message);
        var notice = Assert.Single(email.Sent, s => s.To == "support@example.test");
        Assert.Equal("Subscription started: L'Avenue Boulangerie (Jane Doe)", notice.Subject);
        Assert.Contains("No charge: code FREE551 covers it.", notice.Html);
        Assert.Contains("A new customer.", notice.Html);
    }

    private static SignupNotice.Registration Registration() => new(
        AgentId: 42, FirstName: "Jane", LastName: "Doe", CompanyName: "L'Avenue Boulangerie", BusinessType: "Generic",
        Email: "jane@example.test", Phone: "416-555-0100", City: "Toronto", Province: "Ontario",
        PackageName: "IPro Gold", Period: "Monthly", RecurringPrice: 60m, SetupFee: 200m, Code: "WELCOME10",
        TrialCode: null, TrialEndsAt: null, CameFrom: "Google (organic)", RegisteredAtUtc: When);

    private static SignupNotice.Started Started() => new(
        AgentId: 42, FirstName: "Jane", LastName: "Doe", CompanyName: "L'Avenue Boulangerie", Email: "jane@example.test",
        PackageName: "IPro Gold", Period: "Monthly", FirstBillTotal: 67.80m, Code: "", Again: false, NoCost: false, StartedAtUtc: When);

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative));
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("A no-cost sign-up must not call PayPal.");
    }

    private sealed class RecordingEmail : IEmailService
    {
        public bool Throw { get; init; }
        public List<(string To, string Subject, string Html)> Sent { get; } = new();

        public Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        {
            if (Throw) throw new InvalidOperationException("mail is down");
            Sent.Add((toEmail, subject, htmlBody));
            return Task.FromResult(true);
        }

        public async Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
            await SendAsync(toEmail, toName, subject, htmlBody, textBody) ? EmailSendResult.Sent() : EmailSendResult.Failed("not sent");

        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData) => Task.FromResult(true);
    }
}
