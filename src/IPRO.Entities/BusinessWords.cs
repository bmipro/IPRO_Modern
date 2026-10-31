namespace IPRO.Entities;

// 626 (truth sweep item 2): the examples the portal shows in its forms, and who the AI drafts for, by the
// adviser's business type. "Call about policy renewal" and "a financial/insurance advisor" read the same to
// an accountant, a mortgage broker and a Generic business. Examples only: nothing here is stored or sent.
public sealed record BusinessWords(
    string FollowUpExample,
    string RenewalName,
    string RenewalExample,
    string RenewalsPhrase,
    string GroupsExample,
    string SiteTitleExample,
    string AiWriter)
{
    private static readonly BusinessWords Insurance = new(
        "Call about policy renewal", "Policy Renewal", "Auto Policy Renewal", "policy renewals",
        "seminar attendees, life insurance prospects, or mailing groups", "John Smith Insurance Adviser",
        "a financial/insurance advisor");

    private static readonly BusinessWords Accountants = new(
        "Call about year-end documents", "Renewal", "Year-End Filing", "year-end filings",
        "seminar attendees, small-business clients, or mailing groups", "John Smith Accounting",
        "an accountant");

    private static readonly BusinessWords Mortgage = new(
        "Call about mortgage renewal", "Renewal", "Mortgage Renewal", "mortgage renewals",
        "seminar attendees, first-time buyers, or mailing groups", "John Smith Mortgages",
        "a mortgage broker");

    private static readonly BusinessWords Generic = new(
        "Call about next steps", "Renewal", "Annual Contract Renewal", "renewals",
        "seminar attendees, past clients, or mailing groups", "John Smith Consulting",
        "a small professional-services business");

    public static BusinessWords For(string? businessType) => businessType switch
    {
        "Insurance / Financial" => Insurance,
        "Accountants" => Accountants,
        "Mortgage" => Mortgage,
        _ => Generic
    };
}
