using System;

namespace IPRO.Entities;

// 514 (2026-09-22): the supplier as it appears on invoices -- name, address, GST/HST registration
// number, billing email and website -- kept in the database and edited in SuperAdmin (Company
// Details), at the owner's word: "can u not hardcode the elements needed i.e. GST/HST and addresses
// so we could populate it from superadmin". One row, Id = 1. A field left blank falls back to the
// configuration value that served before (BillingCompanyDetails), so nothing on an invoice goes
// missing while the page is being filled in.
public class BillingCompanyProfile
{
    public int Id { get; set; } = 1;
    public string Name { get; set; } = string.Empty;
    public string AddressLine1 { get; set; } = string.Empty;
    public string AddressLine2 { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Province { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string TaxRegistrationNumber { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Website { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
