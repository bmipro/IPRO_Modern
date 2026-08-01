namespace IPRO.Web.Models;

// Shared prospect identity across the 3 Preview actions (Index posts here via a GET form, Show
// composes the AI card, Site renders the fabricated public site). Deliberately never persisted --
// this only ever exists as request-scoped data materialized from the querystring.
public class ProspectPreviewInput
{
    public static readonly string[] ValidBusinessTypes = { "Accountants", "Insurance / Financial", "Mortgage" };

    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string BusinessType { get; set; } = string.Empty;
    public string? Page { get; set; }

    public ProspectPreviewInput Normalized() => new()
    {
        FirstName = Clamp(FirstName, 80, "Alex"),
        LastName = Clamp(LastName, 80, "Morgan"),
        CompanyName = Clamp(CompanyName, 150, "Your Company"),
        BusinessType = ValidBusinessTypes.Contains(BusinessType) ? BusinessType : ValidBusinessTypes[0],
        Page = string.IsNullOrWhiteSpace(Page) ? null : Page.Trim()
    };

    // Identity only -- deliberately never includes Page, so this is safe to use as a stable base
    // route that callers append their own "&page=slug" onto without ever risking a duplicate param.
    public string ToIdentityQueryString() =>
        $"firstName={Uri.EscapeDataString(FirstName)}&lastName={Uri.EscapeDataString(LastName)}&companyName={Uri.EscapeDataString(CompanyName)}&businessType={Uri.EscapeDataString(BusinessType)}";

    private static string Clamp(string? value, int maxLength, string fallback)
    {
        value = (value ?? string.Empty).Trim();
        if (value.Length == 0) return fallback;
        return value.Length > maxLength ? value[..maxLength] : value;
    }
}
