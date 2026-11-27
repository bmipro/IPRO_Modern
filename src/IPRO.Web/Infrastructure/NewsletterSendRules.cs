using IPRO.Entities;

namespace IPRO.Web.Infrastructure;

// 683 (the whole-code audit of 2026-11-26, W05-01 and W05-02). Two things the newsletter's Send page got wrong,
// both about who is mailed, and an email cannot be taken back.
//
// W05-01. The page has three choices (everyone, a group, one client), each a radio button, and the group and the
// client are picked in lists under theirs. Nothing tied a list to its button, and "All newsletter subscribers" is
// the one selected at first: an adviser who picked "Seminar attendees" (or one client, for the one-to-one note the
// page suggests) and pressed Confirm mailed EVERY subscriber, and the history said so too. The server accepted any
// posted list value under "everyone" and ignored it. Now what is posted must say one thing: a list value under
// another choice is refused, with nothing sent.
//
// W05-02. After "send now" the page always said "Newsletter sent.", also when nobody was mailed (a client who is
// not on the newsletter list is skipped by the dispatcher, and the send ends Cancelled with no recipients) and when
// the send had only started. What it says now is read from the send's own record.
public static class NewsletterSendRules
{
    public const string AudienceField = "AudienceType";

    public const string Nobody =
        "Nobody would be emailed: no one in that audience is on the newsletter list. A client is on it when " +
        "\"Newsletter\" is ticked on their page and they have not unsubscribed.";

    // What is wrong with the posted audience, as (field, message); empty when it says one thing.
    public static IEnumerable<(string Field, string Message)> AudienceProblems(NewsLetterAudienceType audience, int? categoryId, int? clientId)
    {
        switch (audience)
        {
            case NewsLetterAudienceType.AllSubscribers:
                if (categoryId.HasValue || clientId.HasValue)
                    yield return (AudienceField,
                        (categoryId.HasValue ? "A group" : "A client") + " is chosen in the list, but \"All newsletter subscribers\" is selected. " +
                        "Choose who this goes to and confirm again. Nothing was sent.");
                break;
            case NewsLetterAudienceType.AccountType:
                if (!categoryId.HasValue) yield return ("ClientCategoryId", "Choose an account type.");
                else if (clientId.HasValue)
                    yield return (AudienceField, "Both a group and one client are chosen. Choose one of them and confirm again. Nothing was sent.");
                break;
            case NewsLetterAudienceType.IndividualClient:
                if (!clientId.HasValue) yield return ("ClientId", "Choose a client.");
                else if (categoryId.HasValue)
                    yield return (AudienceField, "Both a group and one client are chosen. Choose one of them and confirm again. Nothing was sent.");
                break;
            default:
                // "Selected clients" has no screen: the dispatcher would have read it as everyone.
                yield return (AudienceField, "Choose all subscribers, a group or one client. Nothing was sent.");
                break;
        }
    }

    // What happened to a send that was dispatched in the request, from its own record.
    public static (bool Ok, string Text) Outcome(NewsLetterSend? send)
    {
        if (send == null) return (false, "The newsletter could not be sent: its send record was not found. Nobody was emailed.");
        var total = send.TotalRecipients;
        var sent = send.TotalSent;
        return send.Status switch
        {
            NewsLetterSendStatus.Sent when sent >= total => (true, $"Newsletter sent to {People(sent)}."),
            NewsLetterSendStatus.Sent => (true, $"Newsletter sent to {sent} of {People(total)}; the rest could not be delivered. The send's history shows which."),
            NewsLetterSendStatus.Cancelled when total == 0 => (false, Nobody.Replace("would be", "was")),
            NewsLetterSendStatus.Cancelled => (false, $"Nobody was emailed: {(total == 1 ? "the email" : $"all {total} emails")} failed. Email Activity shows why."),
            NewsLetterSendStatus.Failed => (false, "The newsletter could not be sent and nobody was emailed. The send's history shows why."),
            _ => (true, total > 0
                ? $"Sending has started: {sent} of {total} sent so far. The rest go out by themselves in the next few minutes."
                : "The newsletter is queued and goes out in the next few minutes.")
        };
    }

    // The same honesty for an e-card or a letter sent in the request (their status is a word, not an enum).
    public static (bool Ok, string Text) Outcome(string what, string? status, int sent, int total)
    {
        var done = string.Equals(status, "Sent", StringComparison.Ordinal);
        var failed = string.Equals(status, "Failed", StringComparison.Ordinal) || string.Equals(status, "Cancelled", StringComparison.Ordinal);
        if (failed && total == 0)
            return (false, $"Nobody was emailed: the {what.ToLowerInvariant()} had no one it could go to in that audience.");
        if (failed || (done && sent == 0 && total > 0))
            return (false, $"The {what.ToLowerInvariant()} could not be sent and nobody was emailed. Its page shows why.");
        if (done)
            return sent >= total
                ? (true, $"{what} sent to {sent} client(s).")
                : (true, $"{what} sent to {sent} of {total} client(s); the rest could not be delivered. Its page shows which.");
        return (true, $"Sending has started: {sent} of {total} sent so far. The rest go out by themselves in the next few minutes.");
    }

    private static string People(int count) => count == 1 ? "1 subscriber" : $"{count} subscribers";
}
