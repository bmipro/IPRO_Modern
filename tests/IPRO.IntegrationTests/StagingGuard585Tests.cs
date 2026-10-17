using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Email;
using IPRO.Utility;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// 585 (the owner's decision of 2026-10-17, "Builder + staging together"): a staging copy of iPro runs the
// same code as production, so what keeps it from a real person or real money is its configuration, held
// to the rule by StagingGuard, and its email, which reaches only the addresses Email:OnlyTo names.
public class StagingGuard585Tests
{
    private static IConfiguration Settings(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void A_staging_copy_set_up_unsafely_does_not_start_and_says_why()
    {
        Assert.Empty(StagingGuard.Problems(Settings()));                     // not staging: nothing asked
        Assert.Empty(StagingGuard.Problems(Settings(("PayPal:IsSandbox", "false"))));

        var unsafeStaging = Settings(("App:Environment", "Staging"), ("PayPal:IsSandbox", "false"),
            ("AzureDomainAutomation:Enabled", "true"), ("APPLICATIONINSIGHTS_CONNECTION_STRING", "InstrumentationKey=x"));
        var problems = StagingGuard.Problems(unsafeStaging);
        Assert.Equal(4, problems.Count);
        Assert.Contains(problems, p => p.Contains("Email:OnlyTo"));
        Assert.Contains(problems, p => p.Contains("PayPal:IsSandbox"));
        Assert.Contains(problems, p => p.Contains("AzureDomainAutomation:Enabled"));
        Assert.Contains(problems, p => p.Contains("APPLICATIONINSIGHTS_CONNECTION_STRING"));
        Assert.Throws<InvalidOperationException>(() => StagingGuard.ThrowIfUnsafe(unsafeStaging));

        // PayPal must say sandbox outright: missing is not safe.
        Assert.Single(StagingGuard.Problems(Settings(("App:Environment", "staging"), ("Email:OnlyTo", "test@iproadvisers.com"))));

        var safe = Settings(("App:Environment", "Staging"), ("Email:OnlyTo", "test@iproadvisers.com, @example.test"),
            ("PayPal:IsSandbox", "true"), ("AzureDomainAutomation:Enabled", "false"));
        Assert.Empty(StagingGuard.Problems(safe));
        StagingGuard.ThrowIfUnsafe(safe);
    }

    [Fact]
    public void Only_the_named_addresses_and_endings_may_be_emailed()
    {
        var onlyTo = StagingGuard.OnlyTo(Settings(("Email:OnlyTo", "Test@iProAdvisers.com; @example.test, not-an-address")));
        Assert.Equal(new[] { "test@iproadvisers.com", "@example.test" }, onlyTo);
        Assert.True(StagingGuard.MaySend(onlyTo, " TEST@iproadvisers.com "));
        Assert.True(StagingGuard.MaySend(onlyTo, "anyone@example.test"));
        Assert.False(StagingGuard.MaySend(onlyTo, "client@gmail.com"));
        Assert.False(StagingGuard.MaySend(onlyTo, "test@iproadvisers.com.evil.test"));
        Assert.False(StagingGuard.MaySend(onlyTo, "someone@notexample.test.org"));
        Assert.False(StagingGuard.MaySend(onlyTo, ""));
    }

    [Fact]
    public async Task Email_to_anyone_else_is_logged_not_sent_and_the_sender_is_told_it_went()
    {
        var inner = new Recording();
        var email = new RestrictedEmailService(inner, new[] { "test@iproadvisers.com" }, NullLogger.Instance);

        Assert.True(await email.SendAsync("client@gmail.com", "A client", "Your newsletter", "<p>Hi</p>"));
        var held = await email.SendDetailedAsync("adviser@example.com", "An adviser", "Reminder", "<p>Hi</p>");
        Assert.True(held.Success);
        Assert.True(await email.SendTemplateAsync("x@y.test", "X", "t-1", new object()));
        Assert.True(await email.SendBulkAsync(new[] { new EmailRecipient("a@b.test", "A"), new EmailRecipient("test@iproadvisers.com", "Test") }, "Bulk", "<p/>"));
        Assert.True(await email.SendAsync("test@iproadvisers.com", "Test", "Reaches the test box", "<p>Hi</p>"));

        Assert.Equal(new[] { "bulk:test@iproadvisers.com", "test@iproadvisers.com" }, inner.Sent);
    }

    [Fact]
    public async Task Both_kinds_of_registration_are_wrapped_and_nothing_changes_without_OnlyTo()
    {
        var byType = new ServiceCollection().AddLogging().AddScoped<IEmailService, Recording>();
        byType.RestrictEmailTo(new[] { "test@iproadvisers.com" });
        var first = byType.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IEmailService>();
        Assert.IsType<RestrictedEmailService>(first);
        Assert.True(await first.SendAsync("client@gmail.com", "C", "S", "<p/>"));

        var recording = new Recording();
        var byFactory = new ServiceCollection().AddLogging().AddScoped<IEmailService>(_ => recording);
        byFactory.RestrictEmailTo(new[] { "test@iproadvisers.com" });
        var second = byFactory.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IEmailService>();
        await second.SendAsync("test@iproadvisers.com", "T", "S", "<p/>");
        await second.SendAsync("client@gmail.com", "C", "S", "<p/>");
        Assert.Equal(new[] { "test@iproadvisers.com" }, recording.Sent);

        var untouched = new ServiceCollection().AddLogging().AddScoped<IEmailService, Recording>();
        untouched.RestrictEmailTo(Array.Empty<string>());
        Assert.IsType<Recording>(untouched.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IEmailService>());
    }

    [Fact]
    public void Both_apps_check_staging_before_they_start_and_restrict_their_email()
    {
        foreach (var app in new[] { "IPRO.Web", "IPRO.Admin" })
        {
            var program = File.ReadAllText(Path.Combine(RepoRoot(), "src", app, "Program.cs"));
            var guard = program.IndexOf("StagingGuard.ThrowIfUnsafe(builder.Configuration);", StringComparison.Ordinal);
            var restrict = program.IndexOf("builder.Services.RestrictEmailTo(StagingGuard.OnlyTo(builder.Configuration));", StringComparison.Ordinal);
            var build = program.IndexOf("var app = builder.Build();", StringComparison.Ordinal);
            Assert.True(guard > 0 && restrict > guard && build > restrict, $"{app}: the staging guard and the email restriction come before the app is built");
            // ...and the restriction comes after the app's own IEmailService, which it wraps.
            var lastEmailRegistration = program.LastIndexOf("AddScoped<IEmailService", StringComparison.Ordinal);
            Assert.True(lastEmailRegistration > 0 && lastEmailRegistration < restrict, $"{app}: RestrictEmailTo wraps the email service registered before it");
        }
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private sealed class Recording : IEmailService
    {
        public List<string> Sent { get; } = new();
        public Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        { Sent.Add(toEmail); return Task.FromResult(true); }
        public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        { Sent.Add(toEmail); return Task.FromResult(EmailSendResult.Sent()); }
        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null)
        { Sent.AddRange(recipients.Select(r => "bulk:" + r.Email)); return Task.FromResult(true); }
        public Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData)
        { Sent.Add("template:" + toEmail); return Task.FromResult(true); }
    }
}
