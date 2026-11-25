namespace IPRO.Billing;

// 678 (audit 2026-11-24 C-6): iPro's own sales tax comes from the professional's country and province. Neither was
// checked on the server, and the tax came out at 0% for any country or province it did not recognise: an Ontario
// adviser who picked "Other" as the country, or posted a province of "XX", was sold every renewal tax-free, and iPro
// owed the tax. Now a province typed on a Canadian address must be a real one, and checkout refuses until the Profile says so (the
// Profile and sign-up forms refuse it too). Both forms offer only Canadian provinces, so a professional outside Canada
// necessarily carries one: that pair is not refused (it would lock out every customer abroad) but logged at Error at
// checkout (Suspicious), for the owner to check the address.
public static class TaxAddress
{
    public static readonly string[] CanadianProvinceCodes = { "AB", "BC", "MB", "NB", "NL", "NT", "NS", "NU", "ON", "PE", "QC", "SK", "YT" };

    public static bool IsCanada(string? country)
    {
        var c = (country ?? string.Empty).Trim();
        return c.Equals("Canada", StringComparison.OrdinalIgnoreCase) || c.Equals("CA", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCanadianProvince(string? province) =>
        CanadianProvinceCodes.Contains(PayPalBillingService.NormalizeProvince(province));

    // What is wrong with the address for iPro's own tax, or null.
    public static string? Problem(string? country, string? province)
    {
        if (IsCanada(country) && !string.IsNullOrWhiteSpace(province) && !IsCanadianProvince(province))
            return "Choose your province on your Profile first: iPro charges its sales tax by province.";
        return null;
    }

    // Sold without Canadian tax, so worth a look: no country; a Canadian address with no province (an account made
    // without the sign-up form, which always asks); a country outside Canada with a Canadian province.
    public static bool Suspicious(string? country, string? province) =>
        string.IsNullOrWhiteSpace(country)
        || (IsCanada(country) && string.IsNullOrWhiteSpace(province))
        || (!IsCanada(country) && IsCanadianProvince(province));
}
