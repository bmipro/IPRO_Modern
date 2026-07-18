using IPRO.Entities;

namespace IPRO.Business.Interfaces;

public record ClientInvoiceTaxResult(decimal Rate, decimal Amount, string Region);

public interface IClientInvoiceService
{
    Task<ClientInvoiceTaxResult> CalculateTaxAsync(Client client, decimal taxableAmount);
    Task<string> GenerateDocumentNumberAsync(int agentUserId, ClientInvoiceDocumentType documentType);
}
