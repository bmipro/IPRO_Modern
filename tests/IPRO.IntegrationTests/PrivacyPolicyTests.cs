using System;
using System.IO;
using Xunit;

namespace IPRO.IntegrationTests;

// 489 (2026-09-14). Three facts the privacy policy had fallen behind on, found while preparing the
// Google OAuth verification (DOCS/27) and building the platform's own open/click tracking (488):
// clicks are recorded now, not just opens; Google's reviewer reads the policy for how the app uses
// calendar data, and one table row did not say; and production has sent through Azure Communication
// Services since 30 August while the table still named SendGrid as the deliverer. The policy has two
// copies -- the rendered partial and the Markdown source the reviewer works from -- and these tests
// keep both in step, along with the reviewer's change log.
public class PrivacyPolicyTests
{
    private const string Partial = @"src\IPRO.Web\Views\Shared\_LegalPrivacy.cshtml";
    private const string Markdown = @"DOCS\legal\privacy-policy.md";
    private const string ReviewNotes = @"DOCS\legal\README-review-notes.md";

    [Theory]
    [InlineData(Partial)]
    [InlineData(Markdown)]
    public void The_email_paragraph_names_clicks_and_says_the_platform_measures_them_itself(string file)
    {
        var text = Read(file);
        Assert.Contains("delivered, bounced, opened, clicked or reported as spam", text);
        // 488: a pixel and a redirect of our own, so the policy says so in plain words.
        Assert.Contains("small image", text);
        Assert.Contains("advertising", text);
    }

    [Theory]
    [InlineData(Partial)]
    [InlineData(Markdown)]
    public void Google_calendar_use_is_described_where_google_will_look(string file)
    {
        var text = Read(file);
        Assert.Contains("Your Google Calendar, if you connect it", text);
        // 493: there is no calendar chooser (GoogleCalendarId is always "primary"), so the policy says so.
        Assert.Contains("primary Google calendar", text);
        Assert.DoesNotContain("calendar you choose", text);
        Assert.Contains("email address", text);
        Assert.Contains("encrypted", text);
        Assert.Contains("Disconnect", text);
        Assert.Contains("Limited Use", text);
        Assert.Contains("developers.google.com/terms/api-services-user-data-policy", text);
    }

    [Theory]
    [InlineData(Partial)]
    [InlineData(Markdown)]
    public void The_processor_table_names_the_service_that_actually_sends_the_mail(string file)
    {
        var text = Read(file);
        Assert.Contains("Azure Communication Services", text);
        // ACS email data location for ipro-prod-email is Canada (read from the resource on 2026-09-14).
        Assert.Contains("and reporting on delivery", text);
        // SendGrid stays in the codebase as the rollback provider (Email__Provider), so it stays in
        // the table -- as standby, not as the deliverer.
        Assert.Contains("SendGrid (Twilio)", text);
        Assert.Contains("Standby", text);
    }

    [Theory]
    [InlineData(Partial)]
    [InlineData(Markdown)]
    public void Both_copies_carry_the_same_last_updated_date(string file)
    {
        var text = Read(file);
        Assert.Contains("Last updated: 17 September 2026", text);   // 493: the calendar wording
        Assert.DoesNotContain("Last updated: 15 August 2026", text);
    }

    [Fact]
    public void The_reviewers_change_log_records_the_edit()
    {
        var text = Read(ReviewNotes);
        Assert.Contains("17 September 2026", text);
        Assert.Contains("Azure Communication Services", text);
        Assert.Contains("Google Calendar", text);
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative));
    }
}
