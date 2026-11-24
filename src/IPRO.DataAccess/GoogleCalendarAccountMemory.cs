using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

// 632 (2026-11-24, the 2026-09-17 audit's E-5): remembering which Google account an adviser's follow-ups were
// written to (AgentGoogleCalendarAccount says why). Used by the connect callback, which compares the account
// being connected with the remembered one, and by the sync job, which records it for a connection made before
// this existed.
public static class GoogleCalendarAccountMemory
{
    // The adviser's id in the hash, so the same address gives a different value for every adviser.
    public static string Hash(int agentUserId, string accountEmail)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{agentUserId}|{accountEmail.Trim().ToLowerInvariant()}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static async Task<string?> RememberedAsync(IPRODbContext db, int agentUserId) =>
        await db.AgentGoogleCalendarAccounts.AsNoTracking()
            .Where(a => a.AgentUserId == agentUserId)
            .Select(a => a.AccountHash)
            .FirstOrDefaultAsync();

    // Tracked, so it is written by the caller's own save, in the caller's transaction.
    public static async Task RememberAsync(IPRODbContext db, int agentUserId, string accountEmail)
    {
        var hash = Hash(agentUserId, accountEmail);
        var row = await db.AgentGoogleCalendarAccounts.FirstOrDefaultAsync(a => a.AgentUserId == agentUserId);
        if (row == null)
        {
            db.AgentGoogleCalendarAccounts.Add(new AgentGoogleCalendarAccount { AgentUserId = agentUserId, AccountHash = hash, UpdatedAt = DateTime.UtcNow });
        }
        else if (row.AccountHash != hash)
        {
            row.AccountHash = hash;
            row.UpdatedAt = DateTime.UtcNow;
        }
    }
}
