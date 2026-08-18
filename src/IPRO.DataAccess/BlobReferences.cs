using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace IPRO.DataAccess;

// The blob family's SAFE subset (A5-H11/H12/H14). The original design — a reference-index table
// plus an orphan sweep that deletes — was rejected after an adversarial pass: the sweep would have
// destroyed images still live in ALREADY-DELIVERED mail, because an index over database rows
// cannot see a newsletter photo sitting in someone's inbox. What ships instead can only ever keep
// MORE files than today:
//
//   - IsReferencedAsync / CountReferencesAsync: a LIVE query over every column known to hold a
//     blob URL (directly, or embedded in stored HTML/JSON). No index to maintain, no write hooks
//     to forget — the database itself is asked at the moment of the decision.
//   - The image-delete sites that used to delete UNCONDITIONALLY now ask first, and keep the file
//     when anything still references it. A kept file is at worst a few hundred kilobytes of
//     storage (measured: the whole account holds 17 MB); a destroyed shared file is another
//     agent's broken page or a blank image in delivered mail, with no undo.
//   - The orphan sweep exists ONLY as a report (Admin), because "referenced by no row" still does
//     not prove "referenced by no delivered email". A human reads the report; nothing deletes.
//
// Deliberately NOT here: per-record documents (AgentDocuments/PortalDocuments blobs deleted with
// their own record) — the blob IS the record's file, never shared imagery. Erasure keeps its own
// ownership-anchored collection and uses this class only as the final cross-agent keep check.
public static class BlobReferences
{
    // Every column that stores a blob URL directly. Adding an image-URL column to the schema means
    // adding it here, or the new column's references are invisible to the keep check and the report.
    public static readonly IReadOnlyList<(string Table, string Column)> UrlColumns = new[]
    {
        ("AgentUsers",             "PhotoUrl"),
        ("AgentWebsites",          "LogoUrl"),
        ("WebsiteMediaAssets",     "BlobUrl"),
        ("AgentDocuments",         "BlobUrl"),
        ("PortalDocuments",        "BlobUrl"),
        ("Articles",               "ImageUrl"),
        ("WebsiteStarterArticles", "ImageUrl"),
        ("WebsiteStarterBlocks",   "ImageUrl"),
        ("ECardDesigns",           "ImageUrl"),
        ("NewsLetters",            "BannerUrl")
    };

    // Stored HTML/JSON that can EMBED a blob URL: newsletter and drip bodies (image tags), article
    // content, e-letter bodies, and block settings (gallery images live inside SettingsJson).
    public static readonly IReadOnlyList<(string Table, string Column)> ContentColumns = new[]
    {
        ("NewsLetters",          "HtmlBody"),
        ("DripCampaignSteps",    "HtmlBody"),
        ("Articles",             "Content"),
        ("ELetters",             "Body"),
        ("WebsiteContentBlocks", "SettingsJson")
    };

    // The containers this application owns, and what each holds. The report enumerates exactly
    // these — an unknown container is a finding in itself, not something to scan silently.
    public static readonly IReadOnlyList<(string Container, string Holds)> Containers = new[]
    {
        ("agent-photos",     "Agent profile photos (also embedded in newsletter footers)"),
        ("agent-logos",      "Website logos"),
        ("agent-documents",  "Agent document library"),
        ("portal-documents", "Client portal documents and invoices"),
        ("article-media",    "Article images (copied by reference into newsletters)"),
        ("website-media",    "Website page media assets"),
        ("website-gallery",  "Gallery block images")
    };

    public static async Task<bool> IsReferencedAsync(IPRODbContext db, string? blobUrl, CancellationToken ct = default) =>
        await CountReferencesAsync(db, blobUrl, ct) > 0;

    // How many rows still point at this URL — exact matches on the URL columns plus substring
    // matches inside the content columns. Missing tables are tolerated (fresh databases; same rule
    // as StartupSchemaRepair). A null/empty URL is never "referenced".
    public static async Task<int> CountReferencesAsync(IPRODbContext db, string? blobUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(blobUrl)) return 0;
        var url = blobUrl.Trim();
        var total = 0;

        foreach (var (table, column) in UrlColumns)
        {
            if (!await TableExistsAsync(db, table, ct)) continue;
            total += await ScalarCountAsync(db,
                $"SELECT COUNT(*) FROM `{table}` WHERE `{column}` = @p0",
                new MySqlParameter("@p0", url), ct);
        }

        // LIKE with explicit escaping: a URL containing % or _ (rare but legal in query strings)
        // must match itself, not act as a wildcard.
        var escaped = url.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        foreach (var (table, column) in ContentColumns)
        {
            if (!await TableExistsAsync(db, table, ct)) continue;
            total += await ScalarCountAsync(db,
                $"SELECT COUNT(*) FROM `{table}` WHERE `{column}` LIKE @p0 ESCAPE '\\\\'",
                new MySqlParameter("@p0", "%" + escaped + "%"), ct);
        }

        return total;
    }

    private static async Task<int> ScalarCountAsync(IPRODbContext db, string sql, MySqlParameter p, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        if (wasClosed) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.Add(p);
            var result = await command.ExecuteScalarAsync(ct);
            return Convert.ToInt32(result);
        }
        finally
        {
            if (wasClosed) await connection.CloseAsync();
        }
    }

    private static async Task<bool> TableExistsAsync(IPRODbContext db, string table, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        if (wasClosed) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t";
            command.Parameters.Add(new MySqlParameter("@t", table));
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) > 0;
        }
        finally
        {
            if (wasClosed) await connection.CloseAsync();
        }
    }
}
