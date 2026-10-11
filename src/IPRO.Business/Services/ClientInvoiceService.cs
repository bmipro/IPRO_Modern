using IPRO.Business.Interfaces;
using IPRO.DataAccess.Repositories;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Business.Services;

public class ClientInvoiceService : IClientInvoiceService
{
    private readonly IUnitOfWork _uow;
    public ClientInvoiceService(IUnitOfWork uow) => _uow = uow;

    public Task<ClientInvoiceTaxResult> CalculateTaxAsync(Client client, decimal taxableAmount) =>
        CalculateTaxAsync(client, null, taxableAmount);

    // 563: the business is asked too, so a client with no province takes the business's own rate
    // (see ClientInvoiceTaxRules). The amount is what is TAXABLE, which is no longer the subtotal:
    // each line says whether it is taxed.
    public async Task<ClientInvoiceTaxResult> CalculateTaxAsync(Client client, AgentUser? business, decimal taxableAmount)
    {
        if (taxableAmount <= 0)
        {
            return new ClientInvoiceTaxResult(0, 0, "No tax");
        }

        var rule = ClientInvoiceTaxRules.Resolve(client, business, await ActiveTaxRatesAsync());
        return new ClientInvoiceTaxResult(rule.Rate, ClientInvoiceTaxRules.TaxOn(taxableAmount, rule.Rate), rule.Region);
    }

    public async Task<IReadOnlyList<ProvinceTaxRate>> ActiveTaxRatesAsync() =>
        await _uow.Context.ProvinceTaxRates.AsNoTracking().Where(t => t.IsActive).ToListAsync();

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
}
