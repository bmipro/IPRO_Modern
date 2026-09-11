using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

// 475 (2026-09-10): the business types SuperAdmin can file starter content under. "All" first, then
// the verticals the product ships with, then anything already in use anywhere (starter pages,
// articles, forms, agents) -- so a new vertical typed once is offered from then on, and a typo
// cannot quietly create a second spelling of an existing one.
public static class StarterBusinessTypes
{
    public const string All = "All";
    public static readonly string[] Known = { "Accountants", "Insurance / Financial", "Mortgage" };

    public static async Task<List<string>> ListAsync(IPRODbContext db)
    {
        var inUse = new List<string>();
        inUse.AddRange(await db.WebsiteStarterPages.AsNoTracking().Select(p => p.BusinessType).Distinct().ToListAsync());
        inUse.AddRange(await db.WebsiteStarterArticles.AsNoTracking().Select(a => a.BusinessType).Distinct().ToListAsync());
        inUse.AddRange(await db.WebsiteStarterForms.AsNoTracking().Select(f => f.BusinessType).Distinct().ToListAsync());
        inUse.AddRange(await db.AgentUsers.AsNoTracking().Select(a => a.BusinessType).Distinct().ToListAsync());

        var result = new List<string> { All };
        result.AddRange(Known);
        foreach (var value in inUse.Select(v => (v ?? string.Empty).Trim()).Where(v => v.Length > 0).OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
        {
            if (!result.Any(r => string.Equals(r, value, StringComparison.OrdinalIgnoreCase))) result.Add(value);
        }
        return result;
    }
}
