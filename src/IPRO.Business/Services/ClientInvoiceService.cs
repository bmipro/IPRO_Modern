using IPRO.Business.Interfaces;
using IPRO.DataAccess.Repositories;
using IPRO.DataAccess;
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

    // 418 (2026-09-09): per agent and per document type, a counter that only goes up (see
    // PayPalBillingService.GenerateInvoiceNumberAsync for the incident). Seeded from the agent's
    // existing maximum the first time, floor 1000 so numbering starts at 1001 as before.
    public async Task<string> GenerateDocumentNumberAsync(int agentUserId, ClientInvoiceDocumentType documentType)
    {
        var prefix = documentType == ClientInvoiceDocumentType.Estimate ? "EST-" : "INV-";
        var key = InvoiceNumbering.ClientKey(agentUserId, documentType);
        var db = _uow.Context;

        string documentNumber;
        do
        {
            var next = await NumberSequences.NextAsync(db, key, () => InvoiceNumbering.SeedClientAsync(db, agentUserId, prefix));
            documentNumber = $"{prefix}{next}";
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
