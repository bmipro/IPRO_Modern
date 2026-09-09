using System;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

// 418 (2026-09-09): the one way to take the next number for a key. The row is locked (SELECT ...
// FOR UPDATE) for the duration of the increment, so concurrent callers queue and never share a
// value. The first time a key is used the row is seeded from the existing maximum (what the old
// MAX+1 would have seen), so production continues its current numbering; after that the rows can
// come and go and the counter only moves forward. Works inside a caller's transaction (the lock is
// then held until the caller commits) or opens its own.
public static class NumberSequences
{
    private const int MaxAttempts = 5;

    // The increment is a single UPDATE ... SET LastValue = LastValue + 1, which InnoDB serialises on
    // the row lock; the value is then read back inside the same transaction. (A SELECT ... FOR UPDATE
    // composed through EF lands inside a derived table, where MySQL does not honour the lock -- the
    // first version of this method handed 4 of 20 concurrent callers the same number. The test that
    // caught it stays.)
    public static async Task<long> NextAsync(IPRODbContext db, string key, Func<Task<long>> seedFromExisting)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var ownsTransaction = db.Database.CurrentTransaction == null;
            var transaction = ownsTransaction ? await db.Database.BeginTransactionAsync() : null;
            try
            {
                var now = DateTime.UtcNow;
                var updated = await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE `NumberSequences` SET `LastValue` = `LastValue` + 1, `UpdatedAt` = {now} WHERE `Key` = {key}");

                if (updated == 0)
                {
                    if (transaction != null) await transaction.RollbackAsync();
                    await SeedAsync(db, key, await seedFromExisting());
                    continue; // and take the number
                }

                var value = await db.NumberSequences.AsNoTracking()
                    .Where(n => n.Key == key)
                    .Select(n => n.LastValue)
                    .FirstAsync();
                if (transaction != null) await transaction.CommitAsync();
                return value;
            }
            finally
            {
                if (transaction != null) await transaction.DisposeAsync();
            }
        }
        throw new InvalidOperationException($"Could not take the next number for '{key}' after {MaxAttempts} attempts.");
    }

    // Two first-time callers can race to seed; the primary key lets exactly one win and the other
    // simply retries and finds the row. Only the failed entry is detached -- never the caller's
    // other tracked changes (the billing service mints numbers mid-flow).
    private static async Task SeedAsync(IPRODbContext db, string key, long seed)
    {
        var row = new NumberSequence { Key = key, LastValue = seed, UpdatedAt = DateTime.UtcNow };
        db.NumberSequences.Add(row);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            db.Entry(row).State = EntityState.Detached;
        }
    }
}

public static class InvoiceNumbering
{
    public static string PlatformKey(int year) => $"invoice:{year}";

    public static string ClientKey(int agentUserId, ClientInvoiceDocumentType documentType) =>
        $"client-invoice:{agentUserId}:{(documentType == ClientInvoiceDocumentType.Estimate ? "EST" : "INV")}";

    // The highest numeric suffix among existing rows with the prefix -- what the old MAX+1 saw.
    public static async Task<long> SeedPlatformAsync(IPRODbContext db, string prefix)
    {
        var numbers = await db.Invoices.AsNoTracking().Where(i => i.InvoiceNumber.StartsWith(prefix)).Select(i => i.InvoiceNumber).ToListAsync();
        return numbers.Select(n => long.TryParse(n[prefix.Length..], out var v) ? v : 0L).DefaultIfEmpty(0L).Max();
    }

    public static async Task<long> SeedClientAsync(IPRODbContext db, int agentUserId, string prefix)
    {
        var numbers = await db.ClientInvoices.AsNoTracking().Where(i => i.AgentUserId == agentUserId && i.DocumentNumber.StartsWith(prefix)).Select(i => i.DocumentNumber).ToListAsync();
        var max = numbers.Select(n => long.TryParse(n[prefix.Length..], out var v) ? v : 0L).DefaultIfEmpty(0L).Max();
        return Math.Max(max, 1000L); // client numbering starts at 1001
    }
}
