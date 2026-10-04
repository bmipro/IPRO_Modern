namespace IPRO.Entities;

// 550: what a recipient's issue text looks like when it is a spam complaint. Amazon's complaint reports are
// stored as "complaint" or "complaint: <feedback type>" (SesEmailEventsController). A complaint is the
// recipient's act on an email that ARRIVED (538), so every screen shows it as "Reported spam" and none counts
// it as a failed send -- the owner's pilot test 7 showed a newsletter reading "Unsubscribed -- complaint:
// abuse" beside the client's "Reported spam", and Email Activity counting the complaint among the failures.
public static class EmailIssue
{
    public const string ComplaintPrefix = "complaint";

    public static bool IsComplaint(string? issue) =>
        (issue ?? string.Empty).StartsWith(ComplaintPrefix, StringComparison.OrdinalIgnoreCase);
}
