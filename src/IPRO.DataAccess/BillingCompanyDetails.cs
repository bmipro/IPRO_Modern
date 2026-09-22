using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

// 514: the supplier's details as every invoice (page, email, SuperAdmin's print view) shows them.
// The SuperAdmin-edited row first (BillingCompanyProfile), field by field; a blank field falls back
// to the configuration value that served before it existed (BillingCompany:* ; the address to
// Legal:RegisteredAddress), so the invoices never lose a line while the page is being filled in.
// `setting` is the configuration read, passed in so this project needs no configuration package.
public sealed record BillingCompanyDetails(
    string Name,
    string Email,
    string Website,
    string TaxRegistrationNumber,
    IReadOnlyList<string> AddressLines)
{
    public static async Task<BillingCompanyDetails> LoadAsync(IPRODbContext db, Func<string, string?> setting)
    {
        var row = await db.BillingCompanyProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == 1);
        return From(row, setting);
    }

    public static BillingCompanyDetails From(BillingCompanyProfile? row, Func<string, string?> setting)
    {
        string Pick(string? fromRow, string key, string fallback) =>
            !string.IsNullOrWhiteSpace(fromRow) ? fromRow.Trim() : (setting(key) ?? fallback).Trim();

        var lines = AddressLinesOf(row);
        if (lines.Count == 0)
        {
            var configured = (setting("BillingCompany:Address") ?? setting("Legal:RegisteredAddress") ?? string.Empty).Trim();
            if (configured.Length > 0) lines = new[] { configured };
        }

        return new BillingCompanyDetails(
            Pick(row?.Name, "BillingCompany:Name", "IPRO Advisers"),
            Pick(row?.Email, "BillingCompany:Email", "billing@iproadvisers.com"),
            Pick(row?.Website, "BillingCompany:Website", "www.iProAdvisers.com"),
            Pick(row?.TaxRegistrationNumber, "BillingCompany:TaxRegistrationNumber", string.Empty),
            lines);
    }

    // Street, second line, "City, Province  PostalCode", country -- each only when given.
    public static IReadOnlyList<string> AddressLinesOf(BillingCompanyProfile? row)
    {
        if (row == null) return Array.Empty<string>();
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(row.AddressLine1)) lines.Add(row.AddressLine1.Trim());
        if (!string.IsNullOrWhiteSpace(row.AddressLine2)) lines.Add(row.AddressLine2.Trim());
        var city = string.Join(", ", new[] { row.City, row.Province }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
        var cityLine = string.Join("  ", new[] { city, row.PostalCode?.Trim() ?? string.Empty }.Where(s => s.Length > 0));
        if (cityLine.Length > 0) lines.Add(cityLine);
        if (!string.IsNullOrWhiteSpace(row.Country)) lines.Add(row.Country.Trim());
        return lines;
    }

    // Set-based upsert of the one row, like 498 and 508: nothing tracked, saving twice is not an error.
    // (The values are trimmed into locals first: an expression tree may not call a local function.)
    public static async Task SaveAsync(IPRODbContext db, BillingCompanyProfile profile, DateTime nowUtc)
    {
        var name = (profile.Name ?? string.Empty).Trim();
        var line1 = (profile.AddressLine1 ?? string.Empty).Trim();
        var line2 = (profile.AddressLine2 ?? string.Empty).Trim();
        var city = (profile.City ?? string.Empty).Trim();
        var province = (profile.Province ?? string.Empty).Trim();
        var postalCode = (profile.PostalCode ?? string.Empty).Trim();
        var country = (profile.Country ?? string.Empty).Trim();
        var taxNumber = (profile.TaxRegistrationNumber ?? string.Empty).Trim();
        var email = (profile.Email ?? string.Empty).Trim();
        var website = (profile.Website ?? string.Empty).Trim();

        var updated = await db.BillingCompanyProfiles
            .Where(p => p.Id == 1)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Name, name)
                .SetProperty(p => p.AddressLine1, line1)
                .SetProperty(p => p.AddressLine2, line2)
                .SetProperty(p => p.City, city)
                .SetProperty(p => p.Province, province)
                .SetProperty(p => p.PostalCode, postalCode)
                .SetProperty(p => p.Country, country)
                .SetProperty(p => p.TaxRegistrationNumber, taxNumber)
                .SetProperty(p => p.Email, email)
                .SetProperty(p => p.Website, website)
                .SetProperty(p => p.UpdatedAt, nowUtc));
        if (updated == 0)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO `BillingCompanyProfiles` (`Id`, `Name`, `AddressLine1`, `AddressLine2`, `City`, `Province`, `PostalCode`, `Country`, `TaxRegistrationNumber`, `Email`, `Website`, `UpdatedAt`)
VALUES (1, {name}, {line1}, {line2}, {city}, {province}, {postalCode}, {country}, {taxNumber}, {email}, {website}, {nowUtc})
ON DUPLICATE KEY UPDATE `Name` = VALUES(`Name`), `AddressLine1` = VALUES(`AddressLine1`), `AddressLine2` = VALUES(`AddressLine2`), `City` = VALUES(`City`),
    `Province` = VALUES(`Province`), `PostalCode` = VALUES(`PostalCode`), `Country` = VALUES(`Country`), `TaxRegistrationNumber` = VALUES(`TaxRegistrationNumber`),
    `Email` = VALUES(`Email`), `Website` = VALUES(`Website`), `UpdatedAt` = VALUES(`UpdatedAt`)");
        }
    }
}
