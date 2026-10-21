using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

// 592: an adviser's proof value for one domain (DomainClaimToken). Made when first needed -- for a new row, or to
// show an adviser the record that would move a name another account holds unproven -- and kept until a row that
// used it is removed (ForgetAsync). Two requests of the same adviser for the same name may race to make it; the
// unique index keeps one and both read it back.
public static class DomainClaimTokens
{
    // Stages the removal of the adviser's value for this name; the caller saves it with the row's removal. A record
    // left in the domain's DNS after a removal must prove nothing afterwards -- not even for the same adviser, who
    // may have left the firm whose domain it is (592's review, round 3).
    // Kept while another of the adviser's rows still carries the value (www.sub.x.com and sub.x.com share a proven
    // name): that row is the adviser's still. `removedRowId` is the row going with this save.
    public static async Task ForgetAsync(IPRODbContext db, int agentUserId, string provenName, int removedRowId, CancellationToken cancellationToken = default)
    {
        var name = (provenName ?? string.Empty).Trim().ToLowerInvariant();
        var known = await db.DomainClaimTokens.Where(t => t.AgentUserId == agentUserId && t.ProvenName == name).ToListAsync(cancellationToken);
        foreach (var value in known)
        {
            var stillUsed = await db.AgentDomains.AnyAsync(d => d.AgentUserId == agentUserId && d.Id != removedRowId && d.VerificationToken == value.Token, cancellationToken);
            if (!stillUsed) db.DomainClaimTokens.Remove(value);
        }
    }

    // A value no row ever took (shown for a takeover that was refused) is good for this long; after that the next
    // request gets a new one, so a record published once and forgotten does not prove the name years later.
    public static readonly TimeSpan UnusedLifetime = TimeSpan.FromDays(30);

    public static async Task<string> GetOrCreateAsync(IPRODbContext db, int agentUserId, string provenName, string newToken, CancellationToken cancellationToken = default)
    {
        var name = (provenName ?? string.Empty).Trim().ToLowerInvariant();
        var known = await db.DomainClaimTokens.AsNoTracking()
            .Where(t => t.AgentUserId == agentUserId && t.ProvenName == name)
            .Select(t => new { t.Id, t.Token, t.CreatedAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (known != null)
        {
            var lapsed = known.CreatedAt < DateTime.UtcNow - UnusedLifetime &&
                         !await db.AgentDomains.AnyAsync(d => d.AgentUserId == agentUserId && d.VerificationToken == known.Token, cancellationToken);
            if (!lapsed) return known.Token;
            // Only while it is still that value: a concurrent request may have replaced it already.
            await db.DomainClaimTokens.Where(t => t.Id == known.Id && t.Token == known.Token)
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.Token, newToken).SetProperty(t => t.CreatedAt, DateTime.UtcNow), cancellationToken);
            return await db.DomainClaimTokens.AsNoTracking().Where(t => t.Id == known.Id).Select(t => t.Token).FirstAsync(cancellationToken);
        }

        // Written on its own (not through the caller's pending changes): only this row, and a race is settled
        // by the unique index.
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO `DomainClaimTokens` (`AgentUserId`, `ProvenName`, `Token`, `CreatedAt`) VALUES ({0}, {1}, {2}, {3})",
                new object[] { agentUserId, name, newToken, DateTime.UtcNow }, cancellationToken);
        }
        catch (Exception ex) when (ex is MySqlConnector.MySqlException { ErrorCode: MySqlConnector.MySqlErrorCode.DuplicateKeyEntry }
                                   || ex.InnerException is MySqlConnector.MySqlException { ErrorCode: MySqlConnector.MySqlErrorCode.DuplicateKeyEntry })
        {
            // Another request made it first; read theirs.
        }

        return await db.DomainClaimTokens.AsNoTracking()
            .Where(t => t.AgentUserId == agentUserId && t.ProvenName == name)
            .Select(t => t.Token)
            .FirstAsync(cancellationToken);
    }
}
