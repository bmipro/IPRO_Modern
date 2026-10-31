using IPRO.Entities;

namespace IPRO.Business.Services;

// 563 (2026-10-11): which tax an invoice to a client carries, and why -- one answer for the invoice
// editor (which shows it while the invoice is being written), the save, and the recurring job.
//
// The bakery sent an invoice with no tax on it: its client had no province on file, the rule read
// the CLIENT's province alone, and the editor showed no subtotal, tax or total, so nothing said so
// until the invoice was out. Now a client with no province takes the rate of the business's own
// province (a counter sale is made where the business is), and every answer comes with its reason.
public sealed record ClientInvoiceTaxRule(decimal Rate, string Region, string Source, string Note)
{
    public bool Charges => Rate > 0;
}

public static class ClientInvoiceTaxRules
{
    public const string FromClient = "client";
    public const string FromBusiness = "business";
    public const string None = "none";

    public static ClientInvoiceTaxRule Resolve(Client client, AgentUser? business, IEnumerable<ProvinceTaxRate> rates)
    {
        var active = rates.Where(r => r.IsActive).ToList();
        ProvinceTaxRate? RateFor(string code) =>
            code.Length == 0 ? null : active.FirstOrDefault(r => string.Equals(r.ProvinceCode?.Trim(), code, StringComparison.OrdinalIgnoreCase));

        var country = (client.Country ?? string.Empty).Trim();
        if (IsUnitedStates(country))
        {
            return new ClientInvoiceTaxRule(0, "US", None, "Clients in the United States are not charged Canadian tax.");
        }
        if (country.Length > 0 && !IsCanada(country))
        {
            return new ClientInvoiceTaxRule(0, country, None, "Clients outside Canada are not charged tax.");
        }

        var province = NormalizeProvince(client.Province);
        if (province.Length > 0)
        {
            var rate = RateFor(province);
            return rate != null
                ? new ClientInvoiceTaxRule(rate.Rate, Region(rate), FromClient, string.Empty)
                : new ClientInvoiceTaxRule(0, province, None,
                    $"There is no tax rate for the province on this client's record (\"{client.Province.Trim()}\"), so no tax is charged. Use the two-letter code, for example ON.");
        }

        var businessCountry = (business?.Country ?? string.Empty).Trim();
        if (business != null && (businessCountry.Length == 0 || IsCanada(businessCountry)))
        {
            var own = RateFor(NormalizeProvince(business.Province));
            if (own != null)
            {
                return new ClientInvoiceTaxRule(own.Rate, Region(own), FromBusiness,
                    "This client has no province on file, so the rate for your own province is used.");
            }
        }

        return new ClientInvoiceTaxRule(0, country.Length == 0 ? "No tax" : "Canada", None,
            "This client has no province on file and neither does your business profile, so no tax is charged.");
    }

    // The tax on what is taxable, to the cent, rounded the one way every invoice in the product is.
    public static decimal TaxOn(decimal taxableAmount, decimal rate) =>
        taxableAmount <= 0 || rate <= 0 ? 0m : Math.Round(taxableAmount * rate, 2, MidpointRounding.AwayFromZero);

    private static string Region(ProvinceTaxRate rate) => $"{rate.ProvinceCode} {rate.TaxLabel}".Trim();

    private static bool IsCanada(string country) =>
        country.Equals("Canada", StringComparison.OrdinalIgnoreCase) || country.Equals("CA", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnitedStates(string country) =>
        country.Equals("US", StringComparison.OrdinalIgnoreCase) ||
        country.Equals("USA", StringComparison.OrdinalIgnoreCase) ||
        country.Equals("United States", StringComparison.OrdinalIgnoreCase) ||
        country.Equals("United States of America", StringComparison.OrdinalIgnoreCase);

    public static string NormalizeProvince(string? province)
    {
        var value = (province ?? string.Empty).Trim().ToUpperInvariant();
        return ProvinceAliases.TryGetValue(value, out var alias) ? alias : value;
    }

    private static readonly Dictionary<string, string> ProvinceAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ALBERTA"] = "AB",
        ["BRITISH COLUMBIA"] = "BC",
        ["MANITOBA"] = "MB",
        ["NEW BRUNSWICK"] = "NB",
        ["NEWFOUNDLAND"] = "NL",
        ["NEWFOUNDLAND AND LABRADOR"] = "NL",
        ["NORTHWEST TERRITORIES"] = "NT",
        ["NOVA SCOTIA"] = "NS",
        ["NUNAVUT"] = "NU",
        ["ONTARIO"] = "ON",
        ["PRINCE EDWARD ISLAND"] = "PE",
        ["QUEBEC"] = "QC",
        ["QU" + (char)0xC9 + "BEC"] = "QC",   // with the accent
        ["SASKATCHEWAN"] = "SK",
        ["YUKON"] = "YT",
        // 625: what the sign-up and profile forms actually store. iPro's own billing had this gap closed in
        // August (PayPalBillingService, F3); this list was written later from the bare names.
        ["YUKON TERRITORY"] = "YT"
    };
}
