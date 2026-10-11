using IPRO.Entities;

namespace IPRO.Business.Interfaces;

public record ClientInvoiceTaxResult(decimal Rate, decimal Amount, string Region);

public interface IClientInvoiceService
{
    Task<ClientInvoiceTaxResult> CalculateTaxAsync(Client client, decimal taxableAmount);
    // 563: with the business, whose own province is used when the client has none.
    Task<ClientInvoiceTaxResult> CalculateTaxAsync(Client client, AgentUser? business, decimal taxableAmount);
    Task<IReadOnlyList<ProvinceTaxRate>> ActiveTaxRatesAsync();
    Task<string> GenerateDocumentNumberAsync(int agentUserId, ClientInvoiceDocumentType documentType);
}
