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
    // 494 (2026-09-18): Generic is the fourth. The home page has sold a "Generic edition -- the flexible
    // starting point for any professional-service business" since the redesign, and both landing
    // footers link to it, but no form offered it. 495 gave it a pack of its own (six pages, eight
    // articles, two forms); the shared "All" forms and articles still reach a Generic adviser too.
    public const string Generic = "Generic";
    public static readonly string[] Known = { "Accountants", "Insurance / Financial", "Mortgage", Generic };

    // What the four forms that ask for a business type render (sign-up, the profile, the 30-second
    // preview, SuperAdmin's agent editor). ONE list, so they cannot drift from each other or from the
    // marketing page again -- each used to carry its own hard-coded copy of three. The stored value is
    // the plain name; only Generic needs a label that says what it is.
    public static readonly (string Value, string Label)[] Offered =
        Known.Select(k => (k, k == Generic ? "Generic (any other business)" : k)).ToArray();

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
