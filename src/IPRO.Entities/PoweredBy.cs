namespace IPRO.Entities;

// 525 (2026-09-27): the platform's own line -- "Powered by iPro" under every adviser site and at the
// foot of every client document, "Sent with iPro" on the emails that carry a document. The link goes
// to the brand that matches the adviser's business: accountants to iProAccountants, mortgage
// advisers to iProMortgages, everyone else to iProAdvisers. The text people read keeps the owner's
// capitals; the address itself stays lower-case. Every adviser site linking to the brand names is
// worth real search ranking, so the link is an ordinary one, not a nofollow.
public static class PoweredBy
{
    public const string Label = "iPro";

    public static string BrandUrl(string? businessType) => Vertical(businessType) switch
    {
        "accountants" => "https://www.iproaccountants.com/",
        "mortgage" => "https://www.ipromortgages.com/",
        _ => "https://www.iproadvisers.com/"
    };

    public static string BrandName(string? businessType) => Vertical(businessType) switch
    {
        "accountants" => "www.iProAccountants.com",
        "mortgage" => "www.iProMortgages.com",
        _ => "www.iProAdvisers.com"
    };

    // StarterBusinessTypes.Known is "Accountants", "Insurance / Financial", "Mortgage" and "Generic";
    // SuperAdmin can file a new spelling, so the match is by the word's start, not the exact value.
    private static string Vertical(string? businessType)
    {
        var value = (businessType ?? string.Empty).Trim().ToLowerInvariant();
        if (value.StartsWith("account")) return "accountants";
        if (value.StartsWith("mortgage")) return "mortgage";
        return "advisers";
    }
}
