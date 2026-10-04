using System;
using System.IO;
using IPRO.Entities;
using Xunit;

namespace IPRO.IntegrationTests;

// 550 (2026-10-04). Pilot test 7: the owner's newsletter to Amazon's complaint mailbox read "Unsubscribed --
// complaint: abuse" on the newsletter's own page while the client's page said "Reported spam", and Email
// Activity's detail page counted the complaint among the failures although the email arrived. Offered "I can
// make them match if you'd like"; he: "Yes please". One definition of a complaint (EmailIssue), read by both
// screens; a real unsubscribe still reads Unsubscribed.
public class ComplaintWording550Tests
{
    [Theory]
    [InlineData("complaint: abuse", true)]
    [InlineData("complaint", true)]
    [InlineData("Complaint: not-spam", true)]
    [InlineData("smtp; 550 5.1.1 As requested: user unknown", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_complaint_is_recognised_by_the_issue_Amazon_leaves(string? issue, bool complaint) =>
        Assert.Equal(complaint, EmailIssue.IsComplaint(issue));

    [Fact]
    public void The_issue_written_for_a_complaint_is_the_one_the_screens_read()
    {
        Assert.Contains("$\"complaint: {feedback}\" : \"complaint\"", Read(@"src\IPRO.Web\Controllers\SesEmailEventsController.cs"));
        Assert.Equal("complaint", EmailIssue.ComplaintPrefix);
    }

    [Fact]
    public void The_newsletter_page_and_Email_Activity_read_a_complaint_as_Reported_spam_and_not_as_a_failure()
    {
        var preview = Read(@"src\IPRO.Web\Views\Newsletter\Preview.cshtml");
        Assert.Contains("if (IPRO.Entities.EmailIssue.IsComplaint(recipient.FailureReason))", preview);
        Assert.Contains("statusLabel = \"Reported spam\";", preview);
        Assert.Contains("<td><span class=\"badge @statusClass\">@statusLabel</span></td>", preview);
        Assert.DoesNotContain("<span class=\"badge @statusClass\">@effectiveStatus</span>", preview);

        var details = Read(@"src\IPRO.Web\Views\EmailActivity\Details.cshtml");
        Assert.Contains("if (IPRO.Entities.EmailIssue.IsComplaint(r.Issue)) return (\"Reported spam\", \"bg-danger-subtle text-danger\");", details);
        Assert.Contains("var failed = Model.Count(r => !string.IsNullOrWhiteSpace(r.Issue) && !IPRO.Entities.EmailIssue.IsComplaint(r.Issue));", details);

        var newsletters = Read(@"DOCS\03_NEWSLETTERS_AND_CAMPAIGNS.md");
        Assert.Contains("reads **Reported spam**: it arrived (it counts as delivered, not failed)", newsletters);
        Assert.DoesNotContain("SendGrid event webhooks update these results", newsletters);
        Assert.Contains("A recipient who reported the email as spam is not counted as Failed", Read(@"DOCS\23_EMAIL_ACTIVITY.md"));
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
