using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

// Complete removal of one agent's data.
//
// Why raw SQL rather than EF: deleting an AgentUser through EF only cascades entities it has loaded,
// so a declarative table map that deletes by predicate is the only way to guarantee nothing is
// orphaned. A 2026-08-04 note here claimed the schema had NO foreign keys -- that was WRONG: both
// apps run MigrateAsync at startup and the EF migrations create ~48 FK constraints, several with
// ON DELETE CASCADE. On 2026-08-14 that false assumption destroyed a retained invoice: deleting the
// AgentUsers row cascaded Billings -> Invoices -> InvoiceLineItems at the DATABASE level, behind
// this class's back, after it had carefully skipped those tables (see the Bob2Mot post-mortem in
// DOCS/TODO.md). FinancialLedgerSchemaGuard now drops every CASCADE path into the financial ledger
// at startup, and RunAsync below counts retained rows BEFORE deleting and re-counts after, so any
// future cascade shows up as a loud RetentionShortfall instead of a silent "0 retained".
//
// The same map drives PreviewAsync, so "what would be deleted" and "what was deleted" can never drift
// apart: they are the same list of predicates, counted instead of executed.
//
// ADDING A NEW AGENT-OWNED TABLE? Add it here. AssertMapCoversAllTables (see AgentDataEraserTests
// guidance in DOCS/07_SUPER_ADMIN.md) is the reminder; the cost of forgetting is a silent orphan.
public static class AgentDataEraser
{
    // Child rows first. Ordering is not required for correctness (no FKs exist) but keeps the report
    // readable and stays correct if constraints are ever added.
    private static readonly (string Table, string Where)[] Map =
    {
        // -- Client-owned children (scoped through the agent's own client list) --
        ("ClientComments",              "ClientId IN (SELECT Id FROM Clients WHERE AgentUserId = @agentId)"),
        ("ClientFollowUps",             "ClientId IN (SELECT Id FROM Clients WHERE AgentUserId = @agentId)"),
        ("ClientLifeEvents",            "ClientId IN (SELECT Id FROM Clients WHERE AgentUserId = @agentId)"),
        ("PortalMessages",              "ClientId IN (SELECT Id FROM Clients WHERE AgentUserId = @agentId)"),
        ("PortalDocuments",             "ClientId IN (SELECT Id FROM Clients WHERE AgentUserId = @agentId)"),
        ("PortalAppointmentRequests",   "ClientId IN (SELECT Id FROM Clients WHERE AgentUserId = @agentId)"),
        // Queued but unsent "Did You Know" article emails -- scheduled mail that must not outlive the agent.
        ("DidYouKnowEmailQueueItems",   "ClientId IN (SELECT Id FROM Clients WHERE AgentUserId = @agentId)"),

        // -- Newsletters --
        ("NewsLetterArticles",          "NewsLetterId IN (SELECT Id FROM NewsLetters WHERE AgentUserId = @agentId)"),
        ("NewsLetterRecipients",        "NewsLetterId IN (SELECT Id FROM NewsLetters WHERE AgentUserId = @agentId)"),
        ("NewsLetterSends",             "AgentUserId = @agentId"),
        ("NewsLetters",                 "AgentUserId = @agentId"),

        // -- Drip campaigns --
        ("DripCampaignStepSends",       "DripCampaignEnrollmentId IN (SELECT Id FROM DripCampaignEnrollments WHERE AgentUserId = @agentId)"),
        ("DripCampaignEnrollments",     "AgentUserId = @agentId"),
        ("DripCampaignSteps",           "DripCampaignId IN (SELECT Id FROM DripCampaigns WHERE AgentUserId = @agentId)"),
        ("DripCampaigns",               "AgentUserId = @agentId"),

        // -- Polls and surveys --
        ("PollAnswers",                 "PollRecipientId IN (SELECT Id FROM PollRecipients WHERE PollSurveyId IN (SELECT Id FROM PollSurveys WHERE AgentUserId = @agentId))"),
        ("PollRecipients",              "PollSurveyId IN (SELECT Id FROM PollSurveys WHERE AgentUserId = @agentId)"),
        ("PollOptions",                 "PollQuestionId IN (SELECT Id FROM PollQuestions WHERE PollSurveyId IN (SELECT Id FROM PollSurveys WHERE AgentUserId = @agentId))"),
        ("PollQuestions",               "PollSurveyId IN (SELECT Id FROM PollSurveys WHERE AgentUserId = @agentId)"),
        ("PollSends",                   "AgentUserId = @agentId"),
        ("PollSurveys",                 "AgentUserId = @agentId"),

        // -- Custom forms (submission answers hang off the lead, not the form owner) --
        ("WebsiteFormSubmissionAnswers","WebsiteFormId IN (SELECT Id FROM WebsiteForms WHERE AgentUserId = @agentId)"),
        ("WebsiteFormFieldOptions",     "WebsiteFormFieldId IN (SELECT Id FROM WebsiteFormFields WHERE WebsiteFormId IN (SELECT Id FROM WebsiteForms WHERE AgentUserId = @agentId))"),
        ("WebsiteFormFields",           "WebsiteFormId IN (SELECT Id FROM WebsiteForms WHERE AgentUserId = @agentId)"),
        ("WebsiteForms",                "AgentUserId = @agentId"),

        // -- Website --
        ("WebsiteContentBlocks",        "WebsitePageId IN (SELECT Id FROM WebsitePages WHERE AgentWebsiteId IN (SELECT Id FROM AgentWebsites WHERE AgentUserId = @agentId))"),
        ("WebsitePages",                "AgentWebsiteId IN (SELECT Id FROM AgentWebsites WHERE AgentUserId = @agentId)"),
        ("WebsiteMediaAssets",          "AgentWebsiteId IN (SELECT Id FROM AgentWebsites WHERE AgentUserId = @agentId)"),
        ("WebsitePageViews",            "AgentWebsiteId IN (SELECT Id FROM AgentWebsites WHERE AgentUserId = @agentId)"),
        ("WebsiteLeads",                "AgentUserId = @agentId"),
        ("AgentWebsites",               "AgentUserId = @agentId"),
        ("AgentDomains",                "AgentUserId = @agentId"),

        // -- Agent billing housekeeping (the financial LEDGER lives in FinancialMap below) --
        ("PromotionCodeRedemptions",    "AgentUserId = @agentId"),
        ("TrialInviteCodeRedemptions",  "AgentUserId = @agentId"),

        // -- Client invoicing (the agent charging their own clients) --
        ("ClientInvoiceLineItems",      "ClientInvoiceId IN (SELECT Id FROM ClientInvoices WHERE AgentUserId = @agentId)"),
        ("ClientInvoices",              "AgentUserId = @agentId"),
        ("RecurringInvoiceLineItems",   "RecurringInvoiceScheduleId IN (SELECT Id FROM RecurringInvoiceSchedules WHERE AgentUserId = @agentId)"),
        ("RecurringInvoiceSchedules",   "AgentUserId = @agentId"),

        // -- E-Cards and E-Letters (recipients carry scheduled sends) --
        ("ECardRecipients",             "ECardId IN (SELECT Id FROM ECards WHERE AgentUserId = @agentId)"),
        ("ECards",                      "AgentUserId = @agentId"),
        ("ELetterRecipients",           "ELetterId IN (SELECT Id FROM ELetters WHERE AgentUserId = @agentId)"),
        ("ELetters",                    "AgentUserId = @agentId"),

        // -- Support --
        ("SupportTicketMessages",       "SupportTicketId IN (SELECT Id FROM SupportTickets WHERE AgentUserId = @agentId)"),
        ("SupportTickets",              "AgentUserId = @agentId"),

        // -- Everything else keyed directly on the agent --
        ("TeamMembers",                 "AgentUserId = @agentId"),
        ("Clients",                     "AgentUserId = @agentId"),
        ("ClientCategories",            "AgentUserId = @agentId"),
        ("Articles",                    "AgentUserId = @agentId"),
        ("BannerSlides",                "AgentUserId = @agentId"),
        ("Schedulers",                  "AgentUserId = @agentId"),
        ("Coupons",                     "AgentUserId = @agentId"),
        ("CalendarEvents",              "AgentUserId = @agentId"),
        ("ExternalCalendarEvents",      "AgentUserId = @agentId"),
        ("GoogleCalendarConnections",   "AgentUserId = @agentId"),
        ("Testimonials",                "AgentUserId = @agentId"),
        ("TestimonialSubmissions",      "AgentUserId = @agentId"),
        ("SocialPostDrafts",            "AgentUserId = @agentId"),
        ("AgentDailyInsights",          "AgentUserId = @agentId"),
        ("AgentDocuments",              "AgentUserId = @agentId"),
        ("OperateLogs",                 "AgentUserId = @agentId"),

        // The agent row itself, last, and by raw SQL like everything else. Deleting it through EF
        // instead (_uow.AgentUsers.Remove) throws DbUpdateConcurrencyException "expected to affect 1
        // row(s), but actually affected 0": loading the agent also tracks its related entities, whose
        // rows the statements above have already removed, so EF then issues DELETEs for children that
        // are already gone. Never mix a tracking Remove with raw-SQL deletes over the same data.
        ("AgentUsers",                  "Id = @agentId")
    };

    // The financial ledger: what IPRO charged this agent and what they paid. Retained by default when
    // an agent is deleted (2026-08-12, owner decision): the business practice is to delete an agent
    // about a month after they cancel, but CRA expects sales and tax records kept for six years, a
    // returning ex-customer may ask for invoice copies, and the Revenue Report reads these very rows --
    // deleting them was silently rewriting the books (bob3test3's $335 vanished from the August bar
    // the moment the agent was deleted). Privacy law permits retaining financial records through an
    // erasure request; the bill-to snapshot ON the invoice keeps them printable with no AgentUser row.
    // eraseFinancialRecords: true is for QA/test agents only, where full shredding is the point.
    private static readonly (string Table, string Where)[] FinancialMap =
    {
        ("InvoiceLineItems",            "InvoiceId IN (SELECT Id FROM Invoices WHERE AgentUserId = @agentId)"),
        ("Invoices",                    "AgentUserId = @agentId"),
        ("Billings",                    "AgentUserId = @agentId"),
        ("SubscriptionChanges",         "AgentUserId = @agentId")
    };

    // Blobs the agent genuinely uploaded. Deliberately sourced from ownership records rather than from
    // every column that happens to hold an image URL: starter provisioning copies shared library URLs
    // (WebsiteStarterArticle.ImageUrl) straight into the agent's own Article.ImageUrl, so deleting by
    // "any ImageUrl on an agent row" would destroy shared starter artwork for every future agent.
    // SharedAssetUrlsAsync re-checks that invariant at run time instead of trusting this comment.
    private static readonly (string Table, string Column, string Where)[] BlobSources =
    {
        ("AgentUsers",        "PhotoUrl", "Id = @agentId"),
        ("AgentWebsites",     "LogoUrl",  "AgentUserId = @agentId"),
        ("WebsiteMediaAssets","BlobUrl",  "AgentWebsiteId IN (SELECT Id FROM AgentWebsites WHERE AgentUserId = @agentId)"),
        ("AgentDocuments",    "BlobUrl",  "AgentUserId = @agentId"),
        ("PortalDocuments",   "BlobUrl",  "ClientId IN (SELECT Id FROM Clients WHERE AgentUserId = @agentId)"),
        // Article images are the mixed case this whole shared-asset mechanism exists for: an agent can
        // upload their own (ArticlesController -> the article-media container), but starter
        // provisioning also copies WebsiteStarterArticle.ImageUrl verbatim into Article.ImageUrl. Both
        // land in the same column, so the URL alone doesn't say who owns it -- SharedAssetUrlsAsync
        // decides, keeping starter artwork and deleting only what this agent actually uploaded.
        ("Articles",          "ImageUrl", "AgentUserId = @agentId")
    };

    // URLs that belong to the shared library and must survive any single agent's deletion.
    private static readonly (string Table, string Column)[] SharedAssetSources =
    {
        ("WebsiteStarterArticles", "ImageUrl"),
        ("WebsiteStarterBlocks",   "ImageUrl"),
        ("ECardDesigns",           "ImageUrl")
    };

    public static Task<AgentErasureReport> PreviewAsync(IPRODbContext db, int agentId, bool eraseFinancialRecords = false, CancellationToken ct = default) =>
        RunAsync(db, agentId, execute: false, eraseFinancialRecords, ct);

    public static Task<AgentErasureReport> EraseAsync(IPRODbContext db, int agentId, bool eraseFinancialRecords = false, CancellationToken ct = default) =>
        RunAsync(db, agentId, execute: true, eraseFinancialRecords, ct);

    private static async Task<AgentErasureReport> RunAsync(IPRODbContext db, int agentId, bool execute, bool eraseFinancialRecords, CancellationToken ct)
    {
        var lines = new List<AgentErasureLine>();
        var retained = new List<AgentErasureLine>();

        // Blob URLs must be collected BEFORE the rows are deleted -- afterwards there is nothing left
        // to read them from, and the files would be stranded in blob storage forever.
        var (blobs, skipped) = await CollectBlobsAsync(db, agentId, ct);

        // Retained financial rows are counted BEFORE any deletion, deliberately. On 2026-08-14 an FK
        // cascade (Billings ON DELETE CASCADE from AgentUsers) destroyed the rows this class had
        // skipped, and the after-the-fact count reported "0 retained" as if nothing had ever existed.
        // Counting first means the report states what SHOULD survive; the re-count after deletion
        // (below) turns any discrepancy into an explicit shortfall instead of silence.
        if (!eraseFinancialRecords)
        {
            foreach (var (table, where) in FinancialMap)
            {
                if (!await TableExistsAsync(db, table, ct)) continue;
                var rows = await ScalarAsync(db, $"SELECT COUNT(*) FROM `{table}` WHERE {where}", agentId, ct);
                if (rows > 0) retained.Add(new AgentErasureLine(table, rows));
            }
        }

        var toDelete = eraseFinancialRecords ? Map.Concat(FinancialMap) : Map.AsEnumerable();

        foreach (var (table, where) in toDelete)
        {
            if (!await TableExistsAsync(db, table, ct)) continue;

            // ExecuteSqlRawAsync takes positional {0} placeholders, not named ones; the predicate uses
            // @agentId (repeated across subqueries), so swap every occurrence onto the same parameter.
            //
            // EF1002 suppressed deliberately: `table` and `where` are compile-time constants from Map
            // above and never reachable from a request. The one runtime value, agentId, goes through
            // the {0} placeholder as a real parameter -- it is not interpolated into the SQL text.
#pragma warning disable EF1002
            var rows = execute
                ? await db.Database.ExecuteSqlRawAsync(
                    $"DELETE FROM `{table}` WHERE {where.Replace("@agentId", "{0}")}",
                    new object[] { agentId }, ct)
                : await ScalarAsync(db, $"SELECT COUNT(*) FROM `{table}` WHERE {where}", agentId, ct);
#pragma warning restore EF1002

            if (rows > 0) lines.Add(new AgentErasureLine(table, rows));
        }

        // Re-count what should have been retained. If the numbers no longer match the pre-delete
        // counts, something outside this class (an FK cascade, a trigger, anything) destroyed
        // retained rows during the deletion -- exactly the failure mode that silently ate invoice
        // IPRO-2026-000008 on 2026-08-14. Surface it; never let it read as "there was nothing".
        var shortfall = 0;
        if (execute && !eraseFinancialRecords)
        {
            foreach (var line in retained)
            {
                var where = FinancialMap.First(f => f.Table == line.Table).Where;
                var now = await ScalarAsync(db, $"SELECT COUNT(*) FROM `{line.Table}` WHERE {where}", agentId, ct);
                if (now < line.Rows) shortfall += line.Rows - now;
            }
        }

        return new AgentErasureReport(agentId, lines, blobs, skipped, retained, shortfall);
    }

    private static async Task<(List<string> Blobs, List<string> Skipped)> CollectBlobsAsync(
        IPRODbContext db, int agentId, CancellationToken ct)
    {
        var shared = await SharedAssetUrlsAsync(db, ct);
        var found = new List<string>();

        foreach (var (table, column, where) in BlobSources)
        {
            if (!await TableExistsAsync(db, table, ct)) continue;
            found.AddRange(await StringsAsync(db,
                $"SELECT `{column}` FROM `{table}` WHERE ({where}) AND `{column}` IS NOT NULL AND `{column}` <> ''",
                agentId, ct));
        }

        // Gallery photos live inside a block's SettingsJson rather than a column of their own, so they
        // are the one source that needs parsing rather than selecting.
        found.AddRange(await GalleryUrlsAsync(db, agentId, ct));

        var distinct = found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return (distinct.Where(u => !shared.Contains(u)).ToList(),
                distinct.Where(u => shared.Contains(u)).ToList());
    }

    private static async Task<HashSet<string>> SharedAssetUrlsAsync(IPRODbContext db, CancellationToken ct)
    {
        var shared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (table, column) in SharedAssetSources)
        {
            if (!await TableExistsAsync(db, table, ct)) continue;
            foreach (var url in await StringsAsync(db,
                         $"SELECT `{column}` FROM `{table}` WHERE `{column}` IS NOT NULL AND `{column}` <> ''",
                         agentId: null, ct))
            {
                shared.Add(url);
            }
        }
        return shared;
    }

    private static async Task<List<string>> GalleryUrlsAsync(IPRODbContext db, int agentId, CancellationToken ct)
    {
        var urls = new List<string>();
        if (!await TableExistsAsync(db, "WebsiteContentBlocks", ct)) return urls;

        var settings = await StringsAsync(db,
            "SELECT SettingsJson FROM `WebsiteContentBlocks` " +
            "WHERE BlockType = 'Gallery' AND SettingsJson IS NOT NULL AND SettingsJson <> '' " +
            "AND WebsitePageId IN (SELECT Id FROM WebsitePages WHERE AgentWebsiteId IN " +
            "(SELECT Id FROM AgentWebsites WHERE AgentUserId = @agentId))",
            agentId, ct);

        foreach (var json in settings)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("Images", out var images)) continue;
                foreach (var image in images.EnumerateArray())
                {
                    if (image.TryGetProperty("Url", out var url) && url.GetString() is { Length: > 0 } value)
                    {
                        urls.Add(value);
                    }
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // A malformed settings blob must not abort the whole erasure -- the row is being
                // deleted regardless; worst case a gallery file is left behind and reported as such.
            }
        }
        return urls;
    }

    // The two apps' schema-repair routines run independently, so a table this map knows about may not
    // exist yet on a freshly migrated database. Skipping is correct: no table means no rows to orphan.
    private static async Task<bool> TableExistsAsync(IPRODbContext db, string table, CancellationToken ct) =>
        await ScalarAsync(db,
            "SELECT COUNT(*) FROM information_schema.TABLES " +
            $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}'", agentId: null, ct) > 0;

    private static async Task<int> ScalarAsync(IPRODbContext db, string sql, int? agentId, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        if (agentId.HasValue) command.Parameters.Add(AgentParam(agentId.Value, command));
        await EnsureOpenAsync(db, ct);
        var result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }

    private static async Task<List<string>> StringsAsync(IPRODbContext db, string sql, int? agentId, CancellationToken ct)
    {
        var values = new List<string>();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        if (agentId.HasValue) command.Parameters.Add(AgentParam(agentId.Value, command));
        await EnsureOpenAsync(db, ct);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!reader.IsDBNull(0)) values.Add(reader.GetString(0));
        }
        return values;
    }

    // Opening the connection explicitly rather than relying on EF to do it: this has bitten the project
    // three times as "Connection must be Open" (see DOCS/09_TROUBLESHOOTING.md) whenever raw ADO
    // commands are mixed with EF on the same context.
    private static async Task EnsureOpenAsync(IPRODbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct);
    }

    private static System.Data.Common.DbParameter AgentParam(int agentId, System.Data.Common.DbCommand command)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@agentId";
        parameter.Value = agentId;
        return parameter;
    }
}

public sealed record AgentErasureLine(string Table, int Rows);

public sealed record AgentErasureReport(
    int AgentId,
    List<AgentErasureLine> Tables,
    List<string> Blobs,
    List<string> SharedBlobsKept,
    List<AgentErasureLine> RetainedFinancial,
    int RetentionShortfallRows = 0)
{
    public int TotalRows => Tables.Sum(line => line.Rows);
    public int TableCount => Tables.Count;
    public int RetainedRows => RetainedFinancial.Sum(line => line.Rows);
    public int RetainedInvoices => RetainedFinancial.FirstOrDefault(l => l.Table == "Invoices")?.Rows ?? 0;
    // > 0 means retained financial rows vanished DURING the deletion (e.g. an FK cascade). This is
    // always a bug worth an immediate investigation; the banner and audit log surface it loudly.
    public bool RetentionViolated => RetentionShortfallRows > 0;
}
