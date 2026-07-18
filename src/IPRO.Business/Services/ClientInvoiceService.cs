using IPRO.Business.Interfaces;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;

namespace IPRO.Business.Services;

public class ClientInvoiceService : IClientInvoiceService
{
    private readonly IUnitOfWork _uow;
    public ClientInvoiceService(IUnitOfWork uow) => _uow = uow;

    public async Task<ClientInvoiceTaxResult> CalculateTaxAsync(Client client, decimal taxableAmount)
    {
        if (taxableAmount <= 0)
        {
            return new ClientInvoiceTaxResult(0, 0, "No tax");
        }

        var country = (client.Country ?? string.Empty).Trim();
        if (country.Equals("US", StringComparison.OrdinalIgnoreCase) ||
            country.Equals("USA", StringComparison.OrdinalIgnoreCase) ||
            country.Equals("United States", StringComparison.OrdinalIgnoreCase) ||
            country.Equals("United States of America", StringComparison.OrdinalIgnoreCase))
        {
            return new ClientInvoiceTaxResult(0, 0, "US");
        }

        if (!country.Equals("Canada", StringComparison.OrdinalIgnoreCase) &&
            !country.Equals("CA", StringComparison.OrdinalIgnoreCase))
        {
            return new ClientInvoiceTaxResult(0, 0, country.Length == 0 ? "No tax" : country);
        }

        var province = NormalizeProvince(client.Province);
        var taxRate = await _uow.ProvinceTaxRates.FirstOrDefaultAsync(t => t.ProvinceCode == province && t.IsActive);
        if (taxRate == null)
        {
            return new ClientInvoiceTaxResult(0, 0, string.IsNullOrWhiteSpace(province) ? "Canada" : province);
        }

        var amount = Math.Round(taxableAmount * taxRate.Rate, 2, MidpointRounding.AwayFromZero);
        return new ClientInvoiceTaxResult(taxRate.Rate, amount, $"{taxRate.ProvinceCode} {taxRate.TaxLabel}".Trim());
    }

    public async Task<string> GenerateDocumentNumberAsync(int agentUserId, ClientInvoiceDocumentType documentType)
    {
        var prefix = documentType == ClientInvoiceDocumentType.Estimate ? "EST-" : "INV-";
        var existing = await _uow.ClientInvoices.FindAsync(i => i.AgentUserId == agentUserId && i.DocumentNumber.StartsWith(prefix));
        var nextNumber = existing
            .Select(i => int.TryParse(i.DocumentNumber[prefix.Length..], out var number) ? number : 0)
            .DefaultIfEmpty(1000)
            .Max() + 1;
        if (nextNumber < 1001) nextNumber = 1001;

        string documentNumber;
        do
        {
            documentNumber = $"{prefix}{nextNumber}";
            nextNumber++;
        }
        while (await _uow.ClientInvoices.FirstOrDefaultAsync(i => i.AgentUserId == agentUserId && i.DocumentNumber == documentNumber) != null);

        return documentNumber;
    }

    private static string NormalizeProvince(string? province)
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
        ["QUÉBEC"] = "QC",
        ["SASKATCHEWAN"] = "SK",
        ["YUKON"] = "YT"
    };
}
