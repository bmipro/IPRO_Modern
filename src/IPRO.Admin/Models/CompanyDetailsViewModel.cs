using System.ComponentModel.DataAnnotations;
using IPRO.DataAccess;
using IPRO.Entities;

namespace IPRO.Admin.Models;

// 514: the Company Details form. Optional fields bind null when left empty; the profile stores them
// as empty strings, and BillingCompanyDetails falls back to the settings for any blank one.
public class CompanyDetailsViewModel
{
    [Required, StringLength(255)]
    [Display(Name = "Company name")]
    public string Name { get; set; } = string.Empty;

    [StringLength(255)]
    [Display(Name = "Address line 1")]
    public string? AddressLine1 { get; set; }

    [StringLength(255)]
    [Display(Name = "Address line 2")]
    public string? AddressLine2 { get; set; }

    [StringLength(255)]
    public string? City { get; set; }

    [StringLength(255)]
    public string? Province { get; set; }

    [StringLength(255)]
    [Display(Name = "Postal code")]
    public string? PostalCode { get; set; }

    [StringLength(255)]
    public string? Country { get; set; }

    [StringLength(255)]
    [Display(Name = "GST/HST registration number")]
    public string? TaxRegistrationNumber { get; set; }

    [StringLength(255), EmailAddress]
    [Display(Name = "Billing email")]
    public string? Email { get; set; }

    [StringLength(255)]
    public string? Website { get; set; }

    public static CompanyDetailsViewModel From(BillingCompanyProfile row) => new()
    {
        Name = row.Name,
        AddressLine1 = row.AddressLine1,
        AddressLine2 = row.AddressLine2,
        City = row.City,
        Province = row.Province,
        PostalCode = row.PostalCode,
        Country = row.Country,
        TaxRegistrationNumber = row.TaxRegistrationNumber,
        Email = row.Email,
        Website = row.Website
    };

    // Nothing saved yet: start from what the invoices print today (the settings), so the owner only
    // adds the address and the GST/HST number. The settings' one-line address is not split into
    // fields; it keeps serving until an address is saved here.
    public static CompanyDetailsViewModel FromSettings(Func<string, string?> setting)
    {
        var current = BillingCompanyDetails.From(null, setting);
        return new CompanyDetailsViewModel
        {
            Name = current.Name,
            TaxRegistrationNumber = current.TaxRegistrationNumber,
            Email = current.Email,
            Website = current.Website
        };
    }

    public BillingCompanyProfile ToProfile() => new()
    {
        Name = Name,
        AddressLine1 = AddressLine1 ?? string.Empty,
        AddressLine2 = AddressLine2 ?? string.Empty,
        City = City ?? string.Empty,
        Province = Province ?? string.Empty,
        PostalCode = PostalCode ?? string.Empty,
        Country = Country ?? string.Empty,
        TaxRegistrationNumber = TaxRegistrationNumber ?? string.Empty,
        Email = Email ?? string.Empty,
        Website = Website ?? string.Empty
    };
}
