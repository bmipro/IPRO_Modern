using System;
using System.Threading.Tasks;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

public static class TaxRateSeeder
{
    public static async Task SeedAsync(IPRODbContext db)
    {
        var defaults = new[]
        {
            new TaxRateDefinition("AB", "Alberta", "5% GST", 0.05m),
            new TaxRateDefinition("BC", "British Columbia", "12% GST+PST", 0.12m),
            new TaxRateDefinition("MB", "Manitoba", "12% GST+RST", 0.12m),
            new TaxRateDefinition("NB", "New Brunswick", "15% HST", 0.15m),
            new TaxRateDefinition("NL", "Newfoundland and Labrador", "15% HST", 0.15m),
            new TaxRateDefinition("NS", "Nova Scotia", "14% HST", 0.14m),
            new TaxRateDefinition("ON", "Ontario", "13% HST", 0.13m),
            new TaxRateDefinition("PE", "Prince Edward Island", "15% HST", 0.15m),
            new TaxRateDefinition("QC", "Quebec", "14.975% GST+QST", 0.14975m),
            new TaxRateDefinition("SK", "Saskatchewan", "11% GST+PST", 0.11m),
            new TaxRateDefinition("YT", "Yukon", "5% GST", 0.05m),
            new TaxRateDefinition("NT", "Northwest Territories", "5% GST", 0.05m),
            new TaxRateDefinition("NU", "Nunavut", "5% GST", 0.05m)
        };

        foreach (var item in defaults)
        {
            var existing = await db.ProvinceTaxRates.FirstOrDefaultAsync(t => t.ProvinceCode == item.Code);
            if (existing == null)
            {
                await db.ProvinceTaxRates.AddAsync(new ProvinceTaxRate
                {
                    ProvinceCode = item.Code,
                    ProvinceName = item.Name,
                    TaxLabel = item.Label,
                    Rate = item.Rate,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                });
            }
            else
            {
                existing.ProvinceName = string.IsNullOrWhiteSpace(existing.ProvinceName) ? item.Name : existing.ProvinceName;
                existing.TaxLabel = string.IsNullOrWhiteSpace(existing.TaxLabel) ? item.Label : existing.TaxLabel;
            }
        }

        await db.SaveChangesAsync();
    }

    private sealed record TaxRateDefinition(string Code, string Name, string Label, decimal Rate);
}
