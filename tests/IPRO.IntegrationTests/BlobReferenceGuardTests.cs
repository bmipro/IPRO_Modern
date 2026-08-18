using System;
using System.Linq;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using Xunit;

namespace IPRO.IntegrationTests;

// The blob family's safe subset (A5-H11/H12/H14). The property every case here defends: the new
// checks can only ever KEEP MORE FILES than the unconditional deletes they replaced — a file is
// deletable only when nothing in the database references it, directly or inside stored HTML.
public class BlobReferenceGuardTests
{
    private const string Url = "https://iprostorageprod.blob.core.windows.net/article-media/photo-1.jpg";

    [Fact]
    public async Task A_url_referenced_by_a_direct_column_is_referenced()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agent = await SeedAgentAsync(db, "direct");
        db.Add(new Article { AgentUserId = agent.Id, Title = "T", Content = "body", ImageUrl = Url });
        await db.SaveChangesAsync();

        Assert.True(await BlobReferences.IsReferencedAsync(db, Url));
    }

    [Fact]
    public async Task A_url_embedded_only_in_newsletter_html_is_still_referenced()
    {
        // THE A5-H14 case: the image's own row is gone, but a composed newsletter copied the URL
        // into its HTML. The old checks looked at two tables and missed this entirely.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agent = await SeedAgentAsync(db, "html");
        db.Add(new NewsLetter
        {
            AgentUserId = agent.Id,
            Subject = "August update",
            HtmlBody = $"<p>Hello</p><img src=\"{Url}\" alt=\"\">"
        });
        await db.SaveChangesAsync();

        Assert.True(await BlobReferences.IsReferencedAsync(db, Url));
    }

    [Fact]
    public async Task An_unreferenced_url_is_not_referenced()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        Assert.False(await BlobReferences.IsReferencedAsync(db, Url));
        Assert.False(await BlobReferences.IsReferencedAsync(db, null));
        Assert.False(await BlobReferences.IsReferencedAsync(db, "  "));
    }

    [Fact]
    public async Task Like_wildcards_in_a_url_do_not_cause_false_positives()
    {
        // A URL containing % or _ must match itself literally, not act as a wildcard — otherwise
        // "photo_1" would count "photoX1.jpg" in someone's HTML as a reference (harmless direction)
        // and, worse, a crafted URL could match everything.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agent = await SeedAgentAsync(db, "like");
        db.Add(new NewsLetter { AgentUserId = agent.Id, Subject = "N", HtmlBody = "<img src=\"https://x/photoX1.jpg\">" });
        await db.SaveChangesAsync();

        Assert.False(await BlobReferences.IsReferencedAsync(db, "https://x/photo_1.jpg"));
        Assert.False(await BlobReferences.IsReferencedAsync(db, "https://x/%1.jpg"));
    }

    [Fact]
    public async Task Erasing_one_agent_keeps_a_file_another_agent_still_uses()
    {
        // THE A5-H12 case: two agents point at the same file (starter provisioning copies shared
        // artwork into every agent's own Article row). Erasing one must not destroy the other's
        // image. Before this fix the "is it shared?" check consulted only the three library tables.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var erased = await SeedAgentAsync(db, "erased");
        var survivor = await SeedAgentAsync(db, "survivor");
        db.Add(new Article { AgentUserId = erased.Id, Title = "A", Content = "c", ImageUrl = Url });
        db.Add(new Article { AgentUserId = survivor.Id, Title = "B", Content = "c", ImageUrl = Url });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var report = await AgentDataEraser.EraseAsync(db, erased.Id);

        // The file moved from the delete list to the kept list, because the survivor's row —
        // found only after the erased agent's own rows were gone — still references it.
        Assert.DoesNotContain(Url, report.Blobs, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Url, report.SharedBlobsKept, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Erasing_an_agent_still_deletes_files_nobody_else_references()
    {
        // The guard must not swing the other way: a file only the erased agent ever used still
        // goes, or erasure stops actually erasing.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var erased = await SeedAgentAsync(db, "solo");
        var soloUrl = "https://iprostorageprod.blob.core.windows.net/article-media/only-mine.jpg";
        db.Add(new Article { AgentUserId = erased.Id, Title = "A", Content = "c", ImageUrl = soloUrl });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var report = await AgentDataEraser.EraseAsync(db, erased.Id);

        Assert.Contains(soloUrl, report.Blobs, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<AgentUser> SeedAgentAsync(IPRODbContext db, string tag)
    {
        var rule = new BillingRule { PackageName = $"Pkg-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = $"{tag}-{Guid.NewGuid():N}"[..20],
            Email = $"{tag}@example.com",
            FirstName = tag,
            LastName = "Blob",
            DomainName = $"{tag}-{Guid.NewGuid():N}"[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent;
    }
}
