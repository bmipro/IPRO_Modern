using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Business.Services;

// 624 (2026-10-31; 588's follow-up): an opt-out outlives the contact. It lived on the contact's row, so
// deleting someone who had unsubscribed and adding them again, by hand or by import, started them over.
// Now the word is kept when a contact is deleted (ContactEmailSuppression, hashed: it holds no address)
// and put back on anyone the adviser adds with that address.
//
// Only the adviser's own additions are covered. A website form is the person's own act, and their
// preferences page or portal is how they come back: those are their word, the later one.
public static class ContactOptOuts
{
    public sealed record Kept(DateTime OptedOutAt, string Source);

    public static string Hash(int agentUserId, string email) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{agentUserId}:{CanonicalEmail.Canonical(email)}"))).ToLowerInvariant();

    // As a contact is deleted. Someone who opted out and came back since is no longer owed a stop.
    public static async Task RememberAsync(IPRODbContext db, int clientId)
    {
        var client = await db.Clients.AsNoTracking()
            .Where(c => c.Id == clientId)
            .Select(c => new { c.AgentUserId, c.Email, c.EmailOptOutAt, c.EmailOptOutSource })
            .FirstOrDefaultAsync();
        if (client == null || string.IsNullOrWhiteSpace(client.Email)) return;

        var hash = Hash(client.AgentUserId, client.Email);
        var row = await db.ContactEmailSuppressions.FirstOrDefaultAsync(s => s.AgentUserId == client.AgentUserId && s.EmailHash == hash);
        if (client.EmailOptOutAt is not { } optedOutAt)
        {
            if (row == null) return;
            db.ContactEmailSuppressions.Remove(row);
        }
        else
        {
            row ??= db.ContactEmailSuppressions.Add(new ContactEmailSuppression { AgentUserId = client.AgentUserId, EmailHash = hash }).Entity;
            row.OptedOutAt = optedOutAt;
            row.Source = client.EmailOptOutSource ?? string.Empty;
        }
        await db.SaveChangesAsync();
    }

    public static async Task<IReadOnlyDictionary<string, Kept>> ForAgentAsync(IPRODbContext db, int agentUserId)
    {
        var kept = (await db.ContactEmailSuppressions.AsNoTracking()
                .Where(s => s.AgentUserId == agentUserId)
                .Select(s => new { s.EmailHash, s.OptedOutAt, s.Source })
                .ToListAsync())
            .ToDictionary(s => s.EmailHash, s => new Kept(s.OptedOutAt, s.Source));

        // A contact deleted before this was built has no row, but its 30-day snapshot still says.
        var binned = await db.ClientRecycleBinItems.AsNoTracking()
            .Where(i => i.AgentUserId == agentUserId && EF.Functions.Like(i.PayloadJson, "%\"EmailOptOutAt\":\"%"))
            .Select(i => new { i.Email, i.PayloadJson })
            .ToListAsync();
        foreach (var item in binned)
        {
            if (string.IsNullOrWhiteSpace(item.Email) || FromSnapshot(item.PayloadJson) is not { } optOut) continue;
            kept.TryAdd(Hash(agentUserId, item.Email), optOut);
        }
        return kept;
    }

    // True when the new contact was opted out by this. Their AgentUserId and Email must be set.
    public static bool Apply(Client client, IReadOnlyDictionary<string, Kept> kept)
    {
        if (string.IsNullOrWhiteSpace(client.Email) || !kept.TryGetValue(Hash(client.AgentUserId, client.Email), out var optOut)) return false;
        EmailConsentService.CarryOptOut(client, optOut.OptedOutAt, optOut.Source);
        return true;
    }

    private static Kept? FromSnapshot(string payloadJson)
    {
        try
        {
            using var snapshot = JsonDocument.Parse(payloadJson);
            var client = snapshot.RootElement.GetProperty("Client");
            if (!client.TryGetProperty("EmailOptOutAt", out var at) || !at.TryGetDateTime(out var optedOutAt)) return null;
            var source = client.TryGetProperty("EmailOptOutSource", out var from) && from.ValueKind == JsonValueKind.String ? from.GetString() : null;
            return new Kept(optedOutAt, source ?? string.Empty);
        }
        catch (JsonException) { return null; }
        catch (KeyNotFoundException) { return null; }
    }
}
