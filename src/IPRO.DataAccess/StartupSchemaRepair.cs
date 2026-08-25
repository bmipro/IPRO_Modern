using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

// THE startup schema repair, shared by both apps (A2-H8 / TODO 419).
//
// Until 2026-08-18 these ~30 functions existed as two hand-maintained copies, one per Program.cs
// (~1,200 duplicated lines). The copies had already drifted in 9 functions -- comments and one
// call ordering only, never the SQL, but nothing guaranteed that, and the whole FK-cascade
// invoice-loss incident began with exactly this kind of silent divergence. One copy, used by both
// apps, ends the class of bug.
//
// Rules that carried over unchanged:
//   - INVARIANTS rule 4 still holds, now cheaper: a new column/table is ONE call added here plus
//     one invocation in each Program.cs (or inside an existing Ensure function here).
//   - Everything is idempotent and safe under concurrent startup of both apps (H-4): ADD COLUMN
//     races resolve via the 1060 catch in EnsureTableColumnAsync, index races via 1061 in
//     EnsureUniqueIndexAsync, and missing tables are tolerated because repair functions are
//     ordered by dependency and MigrateAsync may create them later.
//   - Schema changes go through these repair functions / the sibling shared classes
//     (BillingRuleSchema, EmailDeliverySchema, WebsiteContentSchema) -- never fresh dotnet-ef
//     scaffolds; the EF snapshot is stale (28 of 85 tables).
//   - Admin-only pieces (EnsureAdminUserSchemaAsync, the recovery reset) stay in Admin's
//     Program.cs: they need IConfiguration and exist for exactly one app on purpose.
public static class StartupSchemaRepair
{

    public static async Task EnsureWebsiteTemplateSchemaAsync(IPRODbContext db)
    {
        await db.Database.OpenConnectionAsync();
        try
        {
            await EnsureAgentDomainSchemaAsync(db);
            await EnsureTeamMemberSchemaAsync(db);
            // Shared with IPRO.Admin (auditor 5, F4): the BillingRules/Invoices money columns live in
            // IPRO.DataAccess.BillingRuleSchema so the two apps cannot drift -- Admin had silently
            // fallen 10 columns behind this file's local copy.
            await IPRO.DataAccess.BillingRuleSchema.EnsureAsync(db);
            await EnsureWebsiteTemplateColumnAsync(db, "BusinessType", "ALTER TABLE `WebsiteTemplates` ADD COLUMN `BusinessType` longtext CHARACTER SET utf8mb4 NULL");
            await EnsureWebsiteTemplateColumnAsync(db, "IsDefault", "ALTER TABLE `WebsiteTemplates` ADD COLUMN `IsDefault` tinyint(1) NOT NULL DEFAULT FALSE");
            await EnsureWebsiteTemplateColumnAsync(db, "TemplateKey", "ALTER TABLE `WebsiteTemplates` ADD COLUMN `TemplateKey` varchar(80) CHARACTER SET utf8mb4 NULL");
            await EnsureTableColumnAsync(db, "AgentWebsites", "HeaderSettingsJson", "ALTER TABLE `AgentWebsites` ADD COLUMN `HeaderSettingsJson` longtext CHARACTER SET utf8mb4 NULL");
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE `AgentWebsites` SET `HeaderSettingsJson` = {0} WHERE `HeaderSettingsJson` IS NULL OR `HeaderSettingsJson` = ''",
                "{}");
            await EnsureTableColumnAsync(db, "AgentWebsites", "FooterSettingsJson", "ALTER TABLE `AgentWebsites` ADD COLUMN `FooterSettingsJson` longtext CHARACTER SET utf8mb4 NULL");
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE `AgentWebsites` SET `FooterSettingsJson` = {0} WHERE `FooterSettingsJson` IS NULL OR `FooterSettingsJson` = ''",
                "{}");
            await EnsureTableColumnAsync(db, "AgentWebsites", "FontFamilyOverride", "ALTER TABLE `AgentWebsites` ADD COLUMN `FontFamilyOverride` longtext CHARACTER SET utf8mb4 NULL");
            await EnsureTableColumnAsync(db, "AgentWebsites", "HeadingFontSizeOverride", "ALTER TABLE `AgentWebsites` ADD COLUMN `HeadingFontSizeOverride` int NOT NULL DEFAULT 0");
            await EnsureTableColumnAsync(db, "AgentWebsites", "BodyFontSizeOverride", "ALTER TABLE `AgentWebsites` ADD COLUMN `BodyFontSizeOverride` int NOT NULL DEFAULT 0");
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE `AgentWebsites` SET `FontFamilyOverride` = {0} WHERE `FontFamilyOverride` IS NULL",
                "");
            await EnsureTableColumnAsync(db, "AgentWebsites", "BackgroundColorOverride", "ALTER TABLE `AgentWebsites` ADD COLUMN `BackgroundColorOverride` longtext CHARACTER SET utf8mb4 NULL");
            await EnsureTableColumnAsync(db, "AgentWebsites", "ButtonStyleOverride", "ALTER TABLE `AgentWebsites` ADD COLUMN `ButtonStyleOverride` longtext CHARACTER SET utf8mb4 NULL");
            await EnsureTableColumnAsync(db, "AgentWebsites", "SectionSpacingOverride", "ALTER TABLE `AgentWebsites` ADD COLUMN `SectionSpacingOverride` longtext CHARACTER SET utf8mb4 NULL");
            await EnsureTableColumnAsync(db, "AgentWebsites", "HeroStyleOverride", "ALTER TABLE `AgentWebsites` ADD COLUMN `HeroStyleOverride` longtext CHARACTER SET utf8mb4 NULL");
            await EnsureTableColumnAsync(db, "AgentWebsites", "SidebarPositionOverride", "ALTER TABLE `AgentWebsites` ADD COLUMN `SidebarPositionOverride` longtext CHARACTER SET utf8mb4 NULL");
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE `AgentWebsites` SET `BackgroundColorOverride` = {0} WHERE `BackgroundColorOverride` IS NULL",
                "");
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE `AgentWebsites` SET `ButtonStyleOverride` = {0} WHERE `ButtonStyleOverride` IS NULL",
                "");
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE `AgentWebsites` SET `SectionSpacingOverride` = {0} WHERE `SectionSpacingOverride` IS NULL",
                "");
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE `AgentWebsites` SET `HeroStyleOverride` = {0} WHERE `HeroStyleOverride` IS NULL",
                "");
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE `AgentWebsites` SET `SidebarPositionOverride` = {0} WHERE `SidebarPositionOverride` IS NULL",
                "");
            await db.Database.ExecuteSqlRawAsync("UPDATE `WebsiteTemplates` SET `BusinessType` = '' WHERE `BusinessType` IS NULL");
            await db.Database.ExecuteSqlRawAsync("UPDATE `WebsiteTemplates` SET `TemplateKey` = CONCAT('template-', `Id`) WHERE `TemplateKey` IS NULL OR `TemplateKey` = ''");
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    // Widens a decimal column whose scale proved too small. Only ever widens: if the live column's
    // NUMERIC_SCALE is already at or above the requested scale, nothing runs, so this is idempotent
    // and safe under the same concurrent-startup rules as EnsureTableColumnAsync.
    public static async Task EnsureDecimalColumnScaleAsync(IPRODbContext db, string tableName, string columnName, int minScale, string alterSql)
    {
        var ownsConnection = db.Database.GetDbConnection().State != System.Data.ConnectionState.Open;
        if (ownsConnection) await db.Database.OpenConnectionAsync();
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = @"
    SELECT COALESCE(MAX(NUMERIC_SCALE), -1)
    FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = @tableName
      AND COLUMN_NAME = @columnName";

            var tableParameter = command.CreateParameter();
            tableParameter.ParameterName = "@tableName";
            tableParameter.Value = tableName;
            command.Parameters.Add(tableParameter);

            var columnParameter = command.CreateParameter();
            columnParameter.ParameterName = "@columnName";
            columnParameter.Value = columnName;
            command.Parameters.Add(columnParameter);

            var scale = Convert.ToInt32(await command.ExecuteScalarAsync());
            if (scale >= 0 && scale < minScale)
            {
                // No 1060-style race catch here on purpose: unlike ADD COLUMN, a MODIFY that loses the
                // web/admin startup race simply re-applies the same definition and succeeds. Anything
                // that throws is a real problem and should stay loud.
                await db.Database.ExecuteSqlRawAsync(alterSql);
            }
        }
        finally
        {
            if (ownsConnection) await db.Database.CloseConnectionAsync();
        }
    }

    public static async Task EnsureTeamMemberSchemaAsync(IPRODbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `TeamMembers` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `AgentUserId` int NOT NULL,
        `FullName` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
        `Email` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
        `PasswordHash` varchar(500) CHARACTER SET utf8mb4 NOT NULL,
        `IsActive` tinyint(1) NOT NULL DEFAULT TRUE,
        `MustChangePassword` tinyint(1) NOT NULL DEFAULT TRUE,
        `CreatedAt` datetime(6) NOT NULL,
        `LastLoginAt` datetime(6) NULL,
        PRIMARY KEY (`Id`),
        UNIQUE KEY `IX_TeamMembers_Email` (`Email`),
        KEY `IX_TeamMembers_AgentUserId` (`AgentUserId`)
    ) CHARACTER SET utf8mb4;");
    }

    public static async Task EnsureAgentDomainSchemaAsync(IPRODbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `AgentDomains` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `AgentUserId` int NOT NULL,
        `AgentWebsiteId` int NOT NULL,
        `DomainName` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
        `RootDomain` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
        `WwwDomain` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
        `DnsTarget` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
        `DnsStatus` varchar(40) CHARACTER SET utf8mb4 NOT NULL,
        `AzureBindingStatus` varchar(40) CHARACTER SET utf8mb4 NOT NULL,
        `SslStatus` varchar(40) CHARACTER SET utf8mb4 NOT NULL,
        `IsPrimary` tinyint(1) NOT NULL,
        `LastCheckedAt` datetime(6) NULL,
        `LastError` varchar(1000) CHARACTER SET utf8mb4 NOT NULL,
        `RetryCount` int NOT NULL DEFAULT 0,
        `LastFailedAt` datetime(6) NULL,
        `NextRetryAt` datetime(6) NULL,
        `AutoRetryExhausted` tinyint(1) NOT NULL DEFAULT FALSE,
        `RootDnsStatus` varchar(40) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'PendingDns',
        `RootRedirectsToWww` tinyint(1) NOT NULL DEFAULT FALSE,
        `RootLastCheckedAt` datetime(6) NULL,
        `RootLastError` varchar(1000) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `CreatedAt` datetime(6) NOT NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;");

        await EnsureTableColumnAsync(db, "AgentDomains", "AgentUserId", "ALTER TABLE `AgentDomains` ADD COLUMN `AgentUserId` int NOT NULL DEFAULT 0");
        await EnsureTableColumnAsync(db, "AgentDomains", "AgentWebsiteId", "ALTER TABLE `AgentDomains` ADD COLUMN `AgentWebsiteId` int NOT NULL DEFAULT 0");
        await EnsureTableColumnAsync(db, "AgentDomains", "DomainName", "ALTER TABLE `AgentDomains` ADD COLUMN `DomainName` varchar(255) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''");
        await EnsureTableColumnAsync(db, "AgentDomains", "RootDomain", "ALTER TABLE `AgentDomains` ADD COLUMN `RootDomain` varchar(255) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''");
        await EnsureTableColumnAsync(db, "AgentDomains", "WwwDomain", "ALTER TABLE `AgentDomains` ADD COLUMN `WwwDomain` varchar(255) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''");
        await EnsureTableColumnAsync(db, "AgentDomains", "DnsTarget", "ALTER TABLE `AgentDomains` ADD COLUMN `DnsTarget` varchar(255) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''");
        await EnsureTableColumnAsync(db, "AgentDomains", "DnsStatus", "ALTER TABLE `AgentDomains` ADD COLUMN `DnsStatus` varchar(40) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'PendingDns'");
        await EnsureTableColumnAsync(db, "AgentDomains", "AzureBindingStatus", "ALTER TABLE `AgentDomains` ADD COLUMN `AzureBindingStatus` varchar(40) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'BindingPending'");
        await EnsureTableColumnAsync(db, "AgentDomains", "SslStatus", "ALTER TABLE `AgentDomains` ADD COLUMN `SslStatus` varchar(40) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'BindingPending'");
        await EnsureTableColumnAsync(db, "AgentDomains", "IsPrimary", "ALTER TABLE `AgentDomains` ADD COLUMN `IsPrimary` tinyint(1) NOT NULL DEFAULT TRUE");
        await EnsureTableColumnAsync(db, "AgentDomains", "LastCheckedAt", "ALTER TABLE `AgentDomains` ADD COLUMN `LastCheckedAt` datetime(6) NULL");
        await EnsureTableColumnAsync(db, "AgentDomains", "LastError", "ALTER TABLE `AgentDomains` ADD COLUMN `LastError` varchar(1000) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''");
        await EnsureTableColumnAsync(db, "AgentDomains", "CreatedAt", "ALTER TABLE `AgentDomains` ADD COLUMN `CreatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)");
        await EnsureTableColumnAsync(db, "AgentDomains", "UpdatedAt", "ALTER TABLE `AgentDomains` ADD COLUMN `UpdatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)");
        await EnsureTableColumnAsync(db, "AgentDomains", "RetryCount", "ALTER TABLE `AgentDomains` ADD COLUMN `RetryCount` int NOT NULL DEFAULT 0");
        await EnsureTableColumnAsync(db, "AgentDomains", "LastFailedAt", "ALTER TABLE `AgentDomains` ADD COLUMN `LastFailedAt` datetime(6) NULL");
        await EnsureTableColumnAsync(db, "AgentDomains", "NextRetryAt", "ALTER TABLE `AgentDomains` ADD COLUMN `NextRetryAt` datetime(6) NULL");
        await EnsureTableColumnAsync(db, "AgentDomains", "AutoRetryExhausted", "ALTER TABLE `AgentDomains` ADD COLUMN `AutoRetryExhausted` tinyint(1) NOT NULL DEFAULT FALSE");
        await EnsureTableColumnAsync(db, "AgentDomains", "RootDnsStatus", "ALTER TABLE `AgentDomains` ADD COLUMN `RootDnsStatus` varchar(40) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'PendingDns'");
        await EnsureTableColumnAsync(db, "AgentDomains", "RootRedirectsToWww", "ALTER TABLE `AgentDomains` ADD COLUMN `RootRedirectsToWww` tinyint(1) NOT NULL DEFAULT FALSE");
        await EnsureTableColumnAsync(db, "AgentDomains", "RootLastCheckedAt", "ALTER TABLE `AgentDomains` ADD COLUMN `RootLastCheckedAt` datetime(6) NULL");
        await EnsureTableColumnAsync(db, "AgentDomains", "RootLastError", "ALTER TABLE `AgentDomains` ADD COLUMN `RootLastError` varchar(1000) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''");
        await EnsureTableColumnAsync(db, "AgentDomains", "CertificateAlertSentAt", "ALTER TABLE `AgentDomains` ADD COLUMN `CertificateAlertSentAt` datetime(6) NULL");
    }

    public static async Task EnsureWebsiteTemplateColumnAsync(IPRODbContext db, string columnName, string alterSql)
    {
        await EnsureTableColumnAsync(db, "WebsiteTemplates", columnName, alterSql);
    }

    public static async Task EnsureWebsiteLeadSchemaAsync(IPRODbContext db)
    {
        await db.Database.OpenConnectionAsync();
        try
        {
            await EnsureTableColumnAsync(db, "WebsiteLeads", "NotificationSent", "ALTER TABLE `WebsiteLeads` ADD COLUMN `NotificationSent` tinyint(1) NOT NULL DEFAULT TRUE");
            await EnsureTableColumnAsync(db, "WebsiteLeads", "NotificationError", "ALTER TABLE `WebsiteLeads` ADD COLUMN `NotificationError` varchar(500) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''");
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public static async Task EnsureWebsiteContentBlockSchemaAsync(IPRODbContext db)
    {
        await db.Database.OpenConnectionAsync();
        try
        {
            await EnsureTableColumnAsync(db, "WebsiteContentBlocks", "LayoutVariant", "ALTER TABLE `WebsiteContentBlocks` ADD COLUMN `LayoutVariant` varchar(30) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''");
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public static async Task EnsureDripCampaignEnrollmentSchemaAsync(IPRODbContext db)
    {
        await db.Database.OpenConnectionAsync();
        try
        {
            await EnsureTableColumnAsync(db, "DripCampaignEnrollments", "UnsubscribeToken", "ALTER TABLE `DripCampaignEnrollments` ADD COLUMN `UnsubscribeToken` varchar(80) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''");
            await EnsureTableColumnAsync(db, "DripCampaignEnrollments", "SendAttempts", "ALTER TABLE `DripCampaignEnrollments` ADD COLUMN `SendAttempts` int NOT NULL DEFAULT 0");
            await EnsureTableColumnAsync(db, "Articles", "ImageSizeBytes", "ALTER TABLE `Articles` ADD COLUMN `ImageSizeBytes` bigint NOT NULL DEFAULT 0");
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    // DOCS/22 (prepaid-value honesty, 2026-08-20): cancelled-but-paid-through on Billings, and
    // refund bookkeeping on SubscriptionChanges for the SuperAdmin manual-refund queue.
    public static async Task EnsurePrepaidValueSchemaAsync(IPRODbContext db)
    {
        await db.Database.OpenConnectionAsync();
        try
        {
            await EnsureTableColumnAsync(db, "Billings", "PaidThroughAt", "ALTER TABLE `Billings` ADD COLUMN `PaidThroughAt` datetime(6) NULL");
            await EnsureTableColumnAsync(db, "SubscriptionChanges", "RefundNetAmount", "ALTER TABLE `SubscriptionChanges` ADD COLUMN `RefundNetAmount` decimal(10,2) NOT NULL DEFAULT 0");
            await EnsureTableColumnAsync(db, "SubscriptionChanges", "RefundTaxAmount", "ALTER TABLE `SubscriptionChanges` ADD COLUMN `RefundTaxAmount` decimal(10,2) NOT NULL DEFAULT 0");
            await EnsureTableColumnAsync(db, "SubscriptionChanges", "RefundGrossAmount", "ALTER TABLE `SubscriptionChanges` ADD COLUMN `RefundGrossAmount` decimal(10,2) NOT NULL DEFAULT 0");
            await EnsureTableColumnAsync(db, "SubscriptionChanges", "RefundStatus", "ALTER TABLE `SubscriptionChanges` ADD COLUMN `RefundStatus` int NOT NULL DEFAULT 0");
            await EnsureTableColumnAsync(db, "SubscriptionChanges", "RefundPayPalTransactionId", "ALTER TABLE `SubscriptionChanges` ADD COLUMN `RefundPayPalTransactionId` varchar(64) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''");
            await EnsureTableColumnAsync(db, "SubscriptionChanges", "RefundWindowEndsAt", "ALTER TABLE `SubscriptionChanges` ADD COLUMN `RefundWindowEndsAt` datetime(6) NULL");
            await EnsureTableColumnAsync(db, "SubscriptionChanges", "RefundResolvedAt", "ALTER TABLE `SubscriptionChanges` ADD COLUMN `RefundResolvedAt` datetime(6) NULL");
            await EnsureTableColumnAsync(db, "SubscriptionChanges", "RefundResolutionNote", "ALTER TABLE `SubscriptionChanges` ADD COLUMN `RefundResolutionNote` varchar(500) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''");
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public static async Task EnsureNewsLetterTemplateSchemaAsync(IPRODbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `NewsLetterTemplates` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `Name` varchar(160) CHARACTER SET utf8mb4 NOT NULL,
        `Description` varchar(500) CHARACTER SET utf8mb4 NOT NULL,
        `Subject` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
        `HtmlBody` longtext CHARACTER SET utf8mb4 NOT NULL,
        `TextBody` longtext CHARACTER SET utf8mb4 NOT NULL,
        `IsActive` tinyint(1) NOT NULL DEFAULT TRUE,
        `SortOrder` int NOT NULL DEFAULT 0,
        `CreatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;");

    }

    public static async Task EnsureDripCampaignStepSendSchemaAsync(IPRODbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `DripCampaignStepSends` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `DripCampaignEnrollmentId` int NOT NULL,
        `DripCampaignStepId` int NOT NULL,
        `StepIndex` int NOT NULL,
        `Email` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
        `RecipientName` varchar(160) CHARACTER SET utf8mb4 NOT NULL,
        `Status` int NOT NULL,
        `SendGridMessageId` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
        `FailureReason` varchar(1000) CHARACTER SET utf8mb4 NOT NULL,
        `SentAt` datetime(6) NULL,
        `DeliveredAt` datetime(6) NULL,
        `OpenedAt` datetime(6) NULL,
        `ClickedAt` datetime(6) NULL,
        `BouncedAt` datetime(6) NULL,
        `FailedAt` datetime(6) NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;");
    }

    public static async Task EnsureDidYouKnowEmailQueueSchemaAsync(IPRODbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `DidYouKnowEmailQueueItems` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `ArticleId` int NOT NULL,
        `ClientId` int NOT NULL,
        `ScheduledForUtc` datetime(6) NOT NULL,
        `ClaimedAtUtc` datetime(6) NULL,
        `SentAtUtc` datetime(6) NULL,
        `CreatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`),
        KEY `IX_DidYouKnowEmailQueueItems_Dispatch` (`SentAtUtc`, `ScheduledForUtc`)
    ) CHARACTER SET=utf8mb4;");

        // Existing installs already have the table, so CREATE TABLE IF NOT EXISTS is a no-op for them --
        // the column has to be added separately. ClaimedAtUtc separates "a run owns this" from "this was
        // delivered", so an item claimed by a process that then died is recoverable instead of silently
        // lost. See DidYouKnowEmailDispatchJob.
        await EnsureTableColumnAsync(db, "DidYouKnowEmailQueueItems", "ClaimedAtUtc",
            "ALTER TABLE `DidYouKnowEmailQueueItems` ADD COLUMN `ClaimedAtUtc` datetime(6) NULL");

        // H14 (billing wave 2026-08-25): the attempt counter that bounds the stale-claim retry loop.
        await EnsureTableColumnAsync(db, "DidYouKnowEmailQueueItems", "SendAttempts",
            "ALTER TABLE `DidYouKnowEmailQueueItems` ADD COLUMN `SendAttempts` int NOT NULL DEFAULT 0");
    }

    public static async Task EnsureNewsLetterClickTrackingSchemaAsync(IPRODbContext db)
    {
        await db.Database.OpenConnectionAsync();
        try
        {
            await EnsureTableColumnAsync(db, "NewsLetterSends", "TotalClicked", "ALTER TABLE `NewsLetterSends` ADD COLUMN `TotalClicked` int NOT NULL DEFAULT 0");
            await EnsureTableColumnAsync(db, "NewsLetters", "TotalClicked", "ALTER TABLE `NewsLetters` ADD COLUMN `TotalClicked` int NOT NULL DEFAULT 0");
            await EnsureTableColumnAsync(db, "NewsLetters", "BannerUrl", "ALTER TABLE `NewsLetters` ADD COLUMN `BannerUrl` varchar(500) CHARACTER SET utf8mb4 NULL");
            await EnsureTableColumnAsync(db, "NewsLetters", "Edition", "ALTER TABLE `NewsLetters` ADD COLUMN `Edition` varchar(200) CHARACTER SET utf8mb4 NULL");
            await EnsureTableColumnAsync(db, "NewsLetters", "SidebarCtasJson", "ALTER TABLE `NewsLetters` ADD COLUMN `SidebarCtasJson` longtext CHARACTER SET utf8mb4 NULL");
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public static async Task EnsureSupportTicketSchemaAsync(IPRODbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `SupportTickets` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `AgentUserId` int NOT NULL,
        `Subject` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
        `Status` int NOT NULL,
        `HasUnreadForAgent` tinyint(1) NOT NULL DEFAULT FALSE,
        `HasUnreadForAdmin` tinyint(1) NOT NULL DEFAULT TRUE,
        `CreatedAt` datetime(6) NOT NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        `LastMessageAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;");

        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `SupportTicketMessages` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `SupportTicketId` int NOT NULL,
        `IsFromAdmin` tinyint(1) NOT NULL DEFAULT FALSE,
        `AuthorName` varchar(160) CHARACTER SET utf8mb4 NOT NULL,
        `Body` longtext CHARACTER SET utf8mb4 NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;");
    }

    public static async Task EnsurePromotionCodeSchemaAsync(IPRODbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `PromotionCodes` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `Code` varchar(60) CHARACTER SET utf8mb4 NOT NULL,
        `Description` varchar(300) CHARACTER SET utf8mb4 NULL,
        `IsActive` tinyint(1) NOT NULL DEFAULT TRUE,
        `ExpiresAt` datetime(6) NULL,
        `MaxRedemptions` int NULL,
        `RedemptionCount` int NOT NULL DEFAULT 0,
        `RestrictedBillingRuleId` int NULL,
        `RecurringDiscountType` int NOT NULL DEFAULT 0,
        `RecurringDiscountValue` decimal(10,2) NOT NULL DEFAULT 0,
        `RecurringDurationCycles` int NULL,
        `SetupFeeDiscountType` int NOT NULL DEFAULT 0,
        `SetupFeeDiscountValue` decimal(10,2) NOT NULL DEFAULT 0,
        `PayPalPromoPlanIdMonthly` varchar(80) CHARACTER SET utf8mb4 NULL,
        `PayPalPromoPlanIdAnnual` varchar(80) CHARACTER SET utf8mb4 NULL,
        `CreatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`),
        UNIQUE KEY `IX_PromotionCodes_Code` (`Code`)
    ) CHARACTER SET=utf8mb4;");

        await db.Database.ExecuteSqlRawAsync(@"
    ALTER TABLE `PromotionCodes`
        MODIFY COLUMN `Description` varchar(300) CHARACTER SET utf8mb4 NULL,
        MODIFY COLUMN `PayPalPromoPlanIdMonthly` varchar(80) CHARACTER SET utf8mb4 NULL,
        MODIFY COLUMN `PayPalPromoPlanIdAnnual` varchar(80) CHARACTER SET utf8mb4 NULL;");

        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `PromotionCodeRedemptions` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `PromotionCodeId` int NOT NULL,
        `AgentUserId` int NOT NULL,
        `BillingRuleId` int NOT NULL,
        `Period` int NOT NULL,
        `OriginalRecurringAmount` decimal(10,2) NOT NULL DEFAULT 0,
        `DiscountedRecurringAmount` decimal(10,2) NOT NULL DEFAULT 0,
        `OriginalSetupFee` decimal(10,2) NOT NULL DEFAULT 0,
        `DiscountedSetupFee` decimal(10,2) NOT NULL DEFAULT 0,
        `RedeemedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;");

        await db.Database.OpenConnectionAsync();
        try
        {
            await EnsureTableColumnAsync(db, "SubscriptionChanges", "PromotionCodeId", "ALTER TABLE `SubscriptionChanges` ADD COLUMN `PromotionCodeId` int NULL");
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public static async Task EnsureClientInvoiceSchemaAsync(IPRODbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `ClientInvoices` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `AgentUserId` int NOT NULL,
        `ClientId` int NOT NULL,
        `DocumentType` int NOT NULL,
        `Status` int NOT NULL,
        `DocumentNumber` varchar(40) CHARACTER SET utf8mb4 NOT NULL,
        `IssueDate` datetime(6) NOT NULL,
        `DueDate` datetime(6) NULL,
        `Currency` varchar(10) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'CAD',
        `SubTotal` decimal(10,2) NOT NULL DEFAULT 0,
        `TaxRegion` varchar(100) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `TaxRate` decimal(6,4) NOT NULL DEFAULT 0,
        `TaxAmount` decimal(10,2) NOT NULL DEFAULT 0,
        `Total` decimal(10,2) NOT NULL DEFAULT 0,
        `Notes` varchar(2000) CHARACTER SET utf8mb4 NULL,
        `PaidAt` datetime(6) NULL,
        `PaidMethod` int NULL,
        `ViewToken` varchar(80) CHARACTER SET utf8mb4 NOT NULL,
        `SentAt` datetime(6) NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`),
        UNIQUE KEY `IX_ClientInvoices_ViewToken` (`ViewToken`)
    ) CHARACTER SET=utf8mb4;");

        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `ClientInvoiceLineItems` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `ClientInvoiceId` int NOT NULL,
        `Description` varchar(500) CHARACTER SET utf8mb4 NOT NULL,
        `Quantity` decimal(10,2) NOT NULL DEFAULT 1,
        `UnitPrice` decimal(10,2) NOT NULL DEFAULT 0,
        `Amount` decimal(10,2) NOT NULL DEFAULT 0,
        `SortOrder` int NOT NULL DEFAULT 0,
        PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;");

        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `RecurringInvoiceSchedules` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `AgentUserId` int NOT NULL,
        `ClientId` int NOT NULL,
        `Frequency` int NOT NULL,
        `NextRunDate` datetime(6) NOT NULL,
        `DueInDays` int NOT NULL DEFAULT 15,
        `IsActive` tinyint(1) NOT NULL DEFAULT TRUE,
        `Notes` varchar(2000) CHARACTER SET utf8mb4 NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;");

        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `RecurringInvoiceLineItems` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `RecurringInvoiceScheduleId` int NOT NULL,
        `Description` varchar(500) CHARACTER SET utf8mb4 NOT NULL,
        `Quantity` decimal(10,2) NOT NULL DEFAULT 1,
        `UnitPrice` decimal(10,2) NOT NULL DEFAULT 0,
        `SortOrder` int NOT NULL DEFAULT 0,
        PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;");

        await db.Database.OpenConnectionAsync();
        try
        {
            await EnsureTableColumnAsync(db, "AgentUsers", "DefaultPaymentLink", "ALTER TABLE `AgentUsers` ADD COLUMN `DefaultPaymentLink` varchar(500) CHARACTER SET utf8mb4 NULL");
            await EnsureTableColumnAsync(db, "AgentUsers", "PortalAccentColor", "ALTER TABLE `AgentUsers` ADD COLUMN `PortalAccentColor` varchar(20) CHARACTER SET utf8mb4 NULL");
            await EnsureTableColumnAsync(db, "AgentUsers", "PhotoUrl", "ALTER TABLE `AgentUsers` ADD COLUMN `PhotoUrl` varchar(500) CHARACTER SET utf8mb4 NULL");
            await EnsureTableColumnAsync(db, "AgentUsers", "PasswordResetToken", "ALTER TABLE `AgentUsers` ADD COLUMN `PasswordResetToken` varchar(80) CHARACTER SET utf8mb4 NULL");
            await EnsureTableColumnAsync(db, "AgentUsers", "PasswordResetTokenExpiresAt", "ALTER TABLE `AgentUsers` ADD COLUMN `PasswordResetTokenExpiresAt` datetime(6) NULL");
            await EnsureTableColumnAsync(db, "AgentUsers", "TrialEndsAt", "ALTER TABLE `AgentUsers` ADD COLUMN `TrialEndsAt` datetime(6) NULL");
            await EnsureTableColumnAsync(db, "AgentUsers", "TrialRemindersSentCount", "ALTER TABLE `AgentUsers` ADD COLUMN `TrialRemindersSentCount` int NOT NULL DEFAULT 0");
            await EnsureTableColumnAsync(db, "ClientInvoices", "LastReminderSentAt", "ALTER TABLE `ClientInvoices` ADD COLUMN `LastReminderSentAt` datetime(6) NULL");
            await EnsureUniqueIndexAsync(db, "ClientInvoices", "UX_ClientInvoices_Agent_DocumentNumber",
                "ALTER TABLE `ClientInvoices` ADD UNIQUE INDEX `UX_ClientInvoices_Agent_DocumentNumber` (`AgentUserId`, `DocumentNumber`)");
            // Backstop for the 2026-08-05 domain-takeover fix. DescribeDomainClaimAsync is a read followed
            // by a write, so two simultaneous requests can both pass it; this makes the database the final
            // arbiter of who owns a hostname. EnsureUniqueIndexAsync tolerates pre-existing duplicate data
            // (it catches 1062 and leaves the index uncreated), so a dirty table cannot block startup.
            await EnsureUniqueIndexAsync(db, "AgentDomains", "UX_AgentDomains_DomainName",
                "ALTER TABLE `AgentDomains` ADD UNIQUE INDEX `UX_AgentDomains_DomainName` (`DomainName`)");
            // Auditor 5, F9: the model declares TemplateKey unique (IPRODbContext) but the two migrations
            // that create the index are among the 28 EF cannot see (TODO 425), and nothing else created
            // it. TemplateKey is the natural key template resolution joins on; a duplicate makes the
            // template an agent gets non-deterministic.
            await EnsureUniqueIndexAsync(db, "WebsiteTemplates", "IX_WebsiteTemplates_TemplateKey",
                "ALTER TABLE `WebsiteTemplates` ADD UNIQUE INDEX `IX_WebsiteTemplates_TemplateKey` (`TemplateKey`)");
            // Auditor 5, F10: PackageName is resolved by SEVEN call sites -- including
            // PackageEntitlementSeeder, which runs at every startup of both apps, and the legacy
            // package-number mapping in PackageEntitlementService. A second "IPro Gold" means agents
            // resolve to whichever row MySQL returns first: wrong features, wrong price, wrong PayPal
            // plan. PackagesController.Create also refuses duplicates now; this is the backstop.
            // (191) because PackageName is longtext (the migration-created type) and MySQL requires a
            // key length to index BLOB/TEXT -- found when the un-prefixed ALTER crashed a local boot.
            // 191 utf8mb4 chars fits the 767-byte index limit and is effectively full uniqueness here.
            await EnsureUniqueIndexAsync(db, "BillingRules", "UX_BillingRules_PackageName",
                "ALTER TABLE `BillingRules` ADD UNIQUE INDEX `UX_BillingRules_PackageName` (`PackageName`(191))");
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public static async Task EnsureClientPortalSchemaAsync(IPRODbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `PortalMessages` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `ClientId` int NOT NULL,
        `IsFromClient` tinyint(1) NOT NULL DEFAULT FALSE,
        `AuthorName` varchar(160) CHARACTER SET utf8mb4 NOT NULL,
        `Body` longtext CHARACTER SET utf8mb4 NOT NULL,
        `IsReadByAgent` tinyint(1) NOT NULL DEFAULT FALSE,
        `IsReadByClient` tinyint(1) NOT NULL DEFAULT TRUE,
        `CreatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;");

        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `PortalDocuments` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `ClientId` int NOT NULL,
        `UploadedByClient` tinyint(1) NOT NULL DEFAULT FALSE,
        `FileName` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
        `BlobUrl` varchar(1000) CHARACTER SET utf8mb4 NOT NULL,
        `ContentType` varchar(150) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `FileSizeBytes` bigint NOT NULL DEFAULT 0,
        `UploadedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;");

        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `PortalAppointmentRequests` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `ClientId` int NOT NULL,
        `Notes` varchar(2000) CHARACTER SET utf8mb4 NULL,
        `PreferredDate` datetime(6) NULL,
        `Status` int NOT NULL DEFAULT 0,
        `CreatedAt` datetime(6) NOT NULL,
        `RespondedAt` datetime(6) NULL,
        `ScheduledAt` datetime(6) NULL,
        `ClientFollowUpId` int NULL,
        PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;");

        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `GoogleCalendarConnections` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `AgentUserId` int NOT NULL,
        `GoogleAccountEmail` varchar(255) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `EncryptedAccessToken` longtext CHARACTER SET utf8mb4 NOT NULL,
        `EncryptedRefreshToken` longtext CHARACTER SET utf8mb4 NOT NULL,
        `AccessTokenExpiresAt` datetime(6) NOT NULL,
        `GoogleCalendarId` varchar(255) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'primary',
        `SyncToken` longtext CHARACTER SET utf8mb4 NULL,
        `IsActive` tinyint(1) NOT NULL DEFAULT TRUE,
        `ConnectedAt` datetime(6) NOT NULL,
        `LastSyncedAt` datetime(6) NULL,
        PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;");

        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `ExternalCalendarEvents` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `AgentUserId` int NOT NULL,
        `GoogleEventId` varchar(255) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `Title` varchar(500) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `StartAt` datetime(6) NOT NULL,
        `EndAt` datetime(6) NULL,
        `LastSyncedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;");

        await db.Database.OpenConnectionAsync();
        try
        {
            await EnsureTableColumnAsync(db, "Clients", "PortalPasswordHash", "ALTER TABLE `Clients` ADD COLUMN `PortalPasswordHash` varchar(500) CHARACTER SET utf8mb4 NULL");
            await EnsureTableColumnAsync(db, "Clients", "PortalInviteToken", "ALTER TABLE `Clients` ADD COLUMN `PortalInviteToken` varchar(80) CHARACTER SET utf8mb4 NULL");
            await EnsureTableColumnAsync(db, "Clients", "PortalInviteTokenExpiresAt", "ALTER TABLE `Clients` ADD COLUMN `PortalInviteTokenExpiresAt` datetime(6) NULL");
            await EnsureTableColumnAsync(db, "Clients", "PortalActivatedAt", "ALTER TABLE `Clients` ADD COLUMN `PortalActivatedAt` datetime(6) NULL");
            await EnsureTableColumnAsync(db, "PortalAppointmentRequests", "ScheduledAt", "ALTER TABLE `PortalAppointmentRequests` ADD COLUMN `ScheduledAt` datetime(6) NULL");
            await EnsureTableColumnAsync(db, "PortalAppointmentRequests", "ClientFollowUpId", "ALTER TABLE `PortalAppointmentRequests` ADD COLUMN `ClientFollowUpId` int NULL");
            await EnsureTableColumnAsync(db, "ClientFollowUps", "GoogleEventId", "ALTER TABLE `ClientFollowUps` ADD COLUMN `GoogleEventId` varchar(255) CHARACTER SET utf8mb4 NULL");
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public static async Task EnsureClientLifeEventSchemaAsync(IPRODbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `ClientLifeEvents` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `ClientId` int NOT NULL,
        `EventType` int NOT NULL,
        `Label` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `EventDate` datetime(6) NOT NULL,
        `ReminderDaysBefore` int NOT NULL DEFAULT 7,
        `IsActive` tinyint(1) NOT NULL DEFAULT TRUE,
        `LastReminderYear` int NULL,
        `CreatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;");

        await db.Database.OpenConnectionAsync();
        try
        {
            await EnsureTableColumnAsync(db, "Clients", "LastBirthdayReminderYear", "ALTER TABLE `Clients` ADD COLUMN `LastBirthdayReminderYear` int NULL");
            await EnsureTableColumnAsync(db, "ClientLifeEvents", "LastCheckedAt", "ALTER TABLE `ClientLifeEvents` ADD COLUMN `LastCheckedAt` datetime(6) NULL");
            await EnsureTableColumnAsync(db, "Clients", "BirthdayReminderLastCheckedAt", "ALTER TABLE `Clients` ADD COLUMN `BirthdayReminderLastCheckedAt` datetime(6) NULL");
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public static async Task EnsureAgentDocumentSchemaAsync(IPRODbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `AgentDocuments` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `AgentUserId` int NOT NULL,
        `FileName` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
        `BlobUrl` varchar(1000) CHARACTER SET utf8mb4 NOT NULL,
        `ContentType` varchar(150) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `FileSizeBytes` bigint NOT NULL DEFAULT 0,
        `Category` varchar(100) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `UploadedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;");
    }

    public static async Task EnsureSocialPostSchemaAsync(IPRODbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `SocialPostDrafts` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `AgentUserId` int NOT NULL,
        `Topic` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `Body` longtext CHARACTER SET utf8mb4 NOT NULL,
        `Status` int NOT NULL DEFAULT 0,
        `PostedAt` datetime(6) NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;");

        await db.Database.OpenConnectionAsync();
        try
        {
            await EnsureTableColumnAsync(db, "SocialPostDrafts", "ScheduledAt", "ALTER TABLE `SocialPostDrafts` ADD COLUMN `ScheduledAt` datetime(6) NULL");
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public static async Task EnsureECardDesignSchemaAsync(IPRODbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `ECardDesigns` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `Key` varchar(80) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `Occasion` varchar(80) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `Name` varchar(120) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `Kind` varchar(20) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'image',
        `DefaultHeaderText` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `DefaultMessage` varchar(600) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `ImageUrl` varchar(500) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `Width` int NOT NULL DEFAULT 0,
        `Height` int NOT NULL DEFAULT 0,
        `Accent` varchar(20) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `Emoji` varchar(20) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `IsDark` tinyint(1) NOT NULL DEFAULT 1,
        `IsActive` tinyint(1) NOT NULL DEFAULT 1,
        `SortOrder` int NOT NULL DEFAULT 0,
        `CreatedAt` datetime(6) NOT NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`),
        UNIQUE INDEX `IX_ECardDesigns_Key` (`Key`)
    ) CHARACTER SET=utf8mb4;

    CREATE TABLE IF NOT EXISTS `ELetterTemplates` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `Key` varchar(80) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `Name` varchar(120) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `Description` varchar(400) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `Subject` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `Body` longtext CHARACTER SET utf8mb4 NOT NULL,
        `IsActive` tinyint(1) NOT NULL DEFAULT 1,
        `SortOrder` int NOT NULL DEFAULT 0,
        `CreatedAt` datetime(6) NOT NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`),
        UNIQUE INDEX `IX_ELetterTemplates_Key` (`Key`)
    ) CHARACTER SET=utf8mb4;");
    }

    public static async Task EnsureECardSchemaAsync(IPRODbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `ECards` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `AgentUserId` int NOT NULL,
        `Occasion` varchar(40) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Birthday',
        `Subject` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `Message` varchar(2000) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `Status` varchar(20) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Draft',
        `ScheduledAt` datetime(6) NOT NULL,
        `SentAt` datetime(6) NULL,
        `TotalRecipients` int NOT NULL DEFAULT 0,
        `TotalSent` int NOT NULL DEFAULT 0,
        `CreatedAt` datetime(6) NOT NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;

    CREATE TABLE IF NOT EXISTS `ECardRecipients` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `ECardId` int NOT NULL,
        `ClientId` int NOT NULL,
        `Email` varchar(255) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `RecipientName` varchar(160) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `Status` varchar(20) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Queued',
        `SendGridMessageId` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `FailureReason` varchar(1000) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `SentAt` datetime(6) NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`),
        INDEX `IX_ECardRecipients_ECardId` (`ECardId`)
    ) CHARACTER SET=utf8mb4;");
        // Delivery-tracking columns (LastEvent/DeliveredAt/OpenedAt/ClickedAt/BouncedAt) are NOT listed
        // above on purpose -- EmailDeliverySchema.EnsureAsync owns them for all three recipient tables,
        // in both apps, from one list. Adding them here too would create a second place to keep in sync.
    }

    public static async Task EnsureELetterSchemaAsync(IPRODbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `ELetters` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `AgentUserId` int NOT NULL,
        `TemplateKey` varchar(60) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `Subject` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `Body` longtext CHARACTER SET utf8mb4 NOT NULL,
        `Status` varchar(20) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Draft',
        `ScheduledAt` datetime(6) NOT NULL,
        `SentAt` datetime(6) NULL,
        `TotalRecipients` int NOT NULL DEFAULT 0,
        `TotalSent` int NOT NULL DEFAULT 0,
        `CreatedAt` datetime(6) NOT NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;

    CREATE TABLE IF NOT EXISTS `ELetterRecipients` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `ELetterId` int NOT NULL,
        `ClientId` int NOT NULL,
        `Email` varchar(255) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `RecipientName` varchar(160) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `Status` varchar(20) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Queued',
        `SendGridMessageId` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `FailureReason` varchar(1000) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `SentAt` datetime(6) NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`),
        INDEX `IX_ELetterRecipients_ELetterId` (`ELetterId`)
    ) CHARACTER SET=utf8mb4;");
        // Delivery-tracking columns are owned by EmailDeliverySchema.EnsureAsync -- see EnsureECardSchemaAsync.
    }

    public static async Task EnsureTestimonialSubmissionSchemaAsync(IPRODbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `TestimonialSubmissions` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `AgentUserId` int NOT NULL,
        `FirstName` varchar(100) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `LastName` varchar(100) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `Email` varchar(255) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `Body` longtext CHARACTER SET utf8mb4 NOT NULL,
        `Status` int NOT NULL DEFAULT 0,
        `SubmittedAt` datetime(6) NOT NULL,
        `ReviewedAt` datetime(6) NULL,
        PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;");

        await db.Database.OpenConnectionAsync();
        try
        {
            await EnsureTableColumnAsync(db, "TestimonialSubmissions", "ClientId", "ALTER TABLE `TestimonialSubmissions` ADD COLUMN `ClientId` int NULL");
            await EnsureTableColumnAsync(db, "TestimonialSubmissions", "RequestToken", "ALTER TABLE `TestimonialSubmissions` ADD COLUMN `RequestToken` varchar(80) CHARACTER SET utf8mb4 NULL");
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public static async Task EnsureAgentDailyInsightSchemaAsync(IPRODbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `AgentDailyInsights` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `AgentUserId` int NOT NULL,
        `NewLeadCount` int NOT NULL DEFAULT 0,
        `StaleLeadCount` int NOT NULL DEFAULT 0,
        `NoFollowUpClientCount` int NOT NULL DEFAULT 0,
        `SuggestedActionType` varchar(30) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'None',
        `SuggestedActionText` varchar(300) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `SuggestedActionUrl` varchar(300) CHARACTER SET utf8mb4 NULL,
        `SuggestedActionReason` varchar(1000) CHARACTER SET utf8mb4 NULL,
        `GeneratedAt` datetime(6) NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`),
        UNIQUE KEY `IX_AgentDailyInsights_AgentUserId` (`AgentUserId`)
    ) CHARACTER SET=utf8mb4;");

        await db.Database.OpenConnectionAsync();
        try
        {
            await EnsureTableColumnAsync(db, "AgentDailyInsights", "RelatedEntityId", "ALTER TABLE `AgentDailyInsights` ADD COLUMN `RelatedEntityId` int NULL");
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public static async Task EnsureAiUsageSchemaAsync(IPRODbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `AiUsageDailyLogs` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `Date` date NOT NULL,
        `CallCount` int NOT NULL DEFAULT 0,
        `InputTokens` bigint NOT NULL DEFAULT 0,
        `OutputTokens` bigint NOT NULL DEFAULT 0,
        `EstimatedCostUsd` decimal(10,4) NOT NULL DEFAULT 0,
        `UpdatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`),
        UNIQUE KEY `IX_AiUsageDailyLogs_Date` (`Date`)
    ) CHARACTER SET=utf8mb4;");

        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `AiBillingSettings` (
        `Id` int NOT NULL,
        `TotalFundedUsd` decimal(10,4) NOT NULL DEFAULT 0,
        `LowBalanceThresholdPercent` int NOT NULL DEFAULT 20,
        `UpdatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;");

        await db.Database.ExecuteSqlRawAsync(@"
    INSERT INTO `AiBillingSettings` (`Id`, `TotalFundedUsd`, `LowBalanceThresholdPercent`, `UpdatedAt`)
    SELECT 1, 0, 20, UTC_TIMESTAMP()
    WHERE NOT EXISTS (SELECT 1 FROM `AiBillingSettings` WHERE `Id` = 1);");
    }

    public static async Task EnsureTrialFeatureSchemaAsync(IPRODbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `TrialInviteCodes` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `Code` varchar(60) CHARACTER SET utf8mb4 NOT NULL,
        `Description` varchar(300) CHARACTER SET utf8mb4 NULL,
        `BillingRuleId` int NOT NULL,
        `IsActive` tinyint(1) NOT NULL DEFAULT TRUE,
        `ExpiresAt` datetime(6) NULL,
        `MaxRedemptions` int NULL,
        `RedemptionCount` int NOT NULL DEFAULT 0,
        `CreatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`),
        UNIQUE KEY `IX_TrialInviteCodes_Code` (`Code`)
    ) CHARACTER SET=utf8mb4;");

        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `TrialInviteCodeRedemptions` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `TrialInviteCodeId` int NOT NULL,
        `AgentUserId` int NOT NULL,
        `RedeemedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;");

        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `TrialSettings` (
        `Id` int NOT NULL,
        `GracePeriodDays` int NOT NULL DEFAULT 1,
        `UpdatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;");

        // Same "INSERT ... SELECT ... WHERE NOT EXISTS" singleton pattern as AiBillingSettings above.
        // The row count this returns doubles as a one-time-only marker: it's only > 0 the very first
        // time this method ever runs, which is exactly when the backfill below should run too - on
        // every later restart the row already exists, the insert is a no-op, and the backfill (which
        // must never re-run against agents who registered normally after this feature shipped) stays
        // skipped.
        var trialSettingsRowsInserted = await db.Database.ExecuteSqlRawAsync(@"
    INSERT INTO `TrialSettings` (`Id`, `GracePeriodDays`, `UpdatedAt`)
    SELECT 1, 1, UTC_TIMESTAMP()
    WHERE NOT EXISTS (SELECT 1 FROM `TrialSettings` WHERE `Id` = 1);");

        if (trialSettingsRowsInserted > 0)
        {
            // One-time grandfather grace period: agents already using the system for free (no active
            // Billing row, e.g. via the entitlement fallback bug this feature closes) get a real week
            // before enforcement applies to them, instead of being cut off the instant this deploys.
            await db.Database.ExecuteSqlRawAsync(@"
    UPDATE `AgentUsers`
    SET `TrialEndsAt` = DATE_ADD(UTC_TIMESTAMP(), INTERVAL 7 DAY)
    WHERE `TrialEndsAt` IS NULL
      AND NOT EXISTS (SELECT 1 FROM `Billings` WHERE `Billings`.`AgentUserId` = `AgentUsers`.`Id` AND `Billings`.`Status` = 1);");
        }
    }

    public static async Task EnsureWebsiteStarterArticleSchemaAsync(IPRODbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `WebsiteStarterArticles` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `BusinessType` varchar(80) CHARACTER SET utf8mb4 NOT NULL,
        `Title` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
        `Summary` varchar(500) CHARACTER SET utf8mb4 NULL,
        `Content` longtext CHARACTER SET utf8mb4 NULL,
        `ImageUrl` varchar(1000) CHARACTER SET utf8mb4 NULL,
        `IsActive` tinyint(1) NOT NULL DEFAULT TRUE,
        `SortOrder` int NOT NULL DEFAULT 0,
        `CreatedAt` datetime(6) NOT NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;");

        await EnsureTableColumnAsync(db, "WebsiteStarterArticles", "Category", "ALTER TABLE `WebsiteStarterArticles` ADD COLUMN `Category` varchar(120) CHARACTER SET utf8mb4 NULL");
    }

    public static async Task EnsureWebsiteStarterFormSchemaAsync(IPRODbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `WebsiteStarterForms` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `BusinessType` varchar(80) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'All',
        `Title` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `Description` varchar(2000) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `SubmitButtonText` varchar(100) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `SuccessMessage` varchar(500) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `IsActive` tinyint(1) NOT NULL DEFAULT 1,
        `SortOrder` int NOT NULL DEFAULT 0,
        `CreatedAt` datetime(6) NOT NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`),
        KEY `idx_website_starter_forms_business_type` (`BusinessType`)
    ) CHARACTER SET=utf8mb4;");

        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `WebsiteStarterFormFields` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `WebsiteStarterFormId` int NOT NULL,
        `FieldType` varchar(50) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `Label` varchar(300) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `Placeholder` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `HelpText` varchar(500) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `IsRequired` tinyint(1) NOT NULL DEFAULT 0,
        `SortOrder` int NOT NULL DEFAULT 0,
        `CreatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`),
        KEY `idx_website_starter_form_fields_form` (`WebsiteStarterFormId`)
    ) CHARACTER SET=utf8mb4;");

        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `WebsiteStarterFormFieldOptions` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `WebsiteStarterFormFieldId` int NOT NULL,
        `Text` varchar(300) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `SortOrder` int NOT NULL DEFAULT 0,
        PRIMARY KEY (`Id`),
        KEY `idx_website_starter_form_field_options_field` (`WebsiteStarterFormFieldId`)
    ) CHARACTER SET=utf8mb4;");
    }

    public static async Task EnsurePollSchemaAsync(IPRODbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `PollSurveys` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `AgentUserId` int NOT NULL,
        `Title` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `Subject` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `IntroText` varchar(2000) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `Status` int NOT NULL DEFAULT 0,
        `ScheduledAt` datetime(6) NULL,
        `SentAt` datetime(6) NULL,
        `TotalRecipients` int NOT NULL DEFAULT 0,
        `TotalSent` int NOT NULL DEFAULT 0,
        `TotalFailed` int NOT NULL DEFAULT 0,
        `TotalResponded` int NOT NULL DEFAULT 0,
        `CreatedAt` datetime(6) NOT NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`),
        KEY `idx_poll_surveys_agent` (`AgentUserId`)
    ) CHARACTER SET=utf8mb4;");

        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `PollQuestions` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `PollSurveyId` int NOT NULL,
        `Text` varchar(500) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `SortOrder` int NOT NULL DEFAULT 0,
        `CreatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`),
        KEY `idx_poll_questions_survey` (`PollSurveyId`)
    ) CHARACTER SET=utf8mb4;");

        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `PollOptions` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `PollQuestionId` int NOT NULL,
        `Text` varchar(300) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `SortOrder` int NOT NULL DEFAULT 0,
        PRIMARY KEY (`Id`),
        KEY `idx_poll_options_question` (`PollQuestionId`)
    ) CHARACTER SET=utf8mb4;");

        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `PollSends` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `PollSurveyId` int NOT NULL,
        `AgentUserId` int NOT NULL,
        `AudienceType` int NOT NULL DEFAULT 0,
        `AudienceLabel` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `ClientCategoryId` int NULL,
        `ClientId` int NULL,
        `Status` int NOT NULL DEFAULT 0,
        `ScheduledAt` datetime(6) NOT NULL,
        `SentAt` datetime(6) NULL,
        `TotalRecipients` int NOT NULL DEFAULT 0,
        `TotalSent` int NOT NULL DEFAULT 0,
        `TotalFailed` int NOT NULL DEFAULT 0,
        `TotalResponded` int NOT NULL DEFAULT 0,
        `CreatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`),
        KEY `idx_poll_sends_survey` (`PollSurveyId`),
        KEY `idx_poll_sends_agent_scheduled` (`AgentUserId`, `ScheduledAt`),
        KEY `idx_poll_sends_status_scheduled` (`Status`, `ScheduledAt`)
    ) CHARACTER SET=utf8mb4;");

        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `PollRecipients` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `PollSurveyId` int NOT NULL,
        `PollSendId` int NULL,
        `ClientId` int NULL,
        `Email` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `RecipientName` varchar(160) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `Status` int NOT NULL DEFAULT 0,
        `SendGridMessageId` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `VoteToken` varchar(80) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `FailureReason` varchar(1000) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `SentAt` datetime(6) NULL,
        `FailedAt` datetime(6) NULL,
        `RespondedAt` datetime(6) NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`),
        KEY `idx_poll_recipients_survey` (`PollSurveyId`),
        KEY `idx_poll_recipients_send` (`PollSendId`),
        KEY `idx_poll_recipients_vote_token` (`VoteToken`)
    ) CHARACTER SET=utf8mb4;");

        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `PollAnswers` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `PollRecipientId` int NOT NULL,
        `PollQuestionId` int NOT NULL,
        `PollOptionId` int NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`),
        UNIQUE KEY `ux_poll_answers_recipient_question` (`PollRecipientId`, `PollQuestionId`),
        KEY `idx_poll_answers_option` (`PollOptionId`)
    ) CHARACTER SET=utf8mb4;");
    }

    public static async Task EnsureWebsiteFormSchemaAsync(IPRODbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `WebsiteForms` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `AgentUserId` int NOT NULL,
        `Title` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `Description` varchar(2000) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `SubmitButtonText` varchar(100) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `SuccessMessage` varchar(500) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `IsActive` tinyint(1) NOT NULL DEFAULT 1,
        `CreatedAt` datetime(6) NOT NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`),
        KEY `idx_website_forms_agent` (`AgentUserId`)
    ) CHARACTER SET=utf8mb4;");

        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `WebsiteFormFields` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `WebsiteFormId` int NOT NULL,
        `FieldType` varchar(50) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `Label` varchar(300) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `Placeholder` varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `HelpText` varchar(500) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `IsRequired` tinyint(1) NOT NULL DEFAULT 0,
        `SortOrder` int NOT NULL DEFAULT 0,
        `CreatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`),
        KEY `idx_website_form_fields_form` (`WebsiteFormId`)
    ) CHARACTER SET=utf8mb4;");

        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `WebsiteFormFieldOptions` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `WebsiteFormFieldId` int NOT NULL,
        `Text` varchar(300) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `SortOrder` int NOT NULL DEFAULT 0,
        PRIMARY KEY (`Id`),
        KEY `idx_website_form_field_options_field` (`WebsiteFormFieldId`)
    ) CHARACTER SET=utf8mb4;");

        await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS `WebsiteFormSubmissionAnswers` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `WebsiteLeadId` int NOT NULL,
        `WebsiteFormId` int NOT NULL,
        `WebsiteFormFieldId` int NOT NULL,
        `FieldLabel` varchar(300) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `FieldType` varchar(50) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `Value` varchar(4000) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
        `SortOrder` int NOT NULL DEFAULT 0,
        `CreatedAt` datetime(6) NOT NULL,
        PRIMARY KEY (`Id`),
        KEY `idx_website_form_submission_answers_lead` (`WebsiteLeadId`),
        KEY `idx_website_form_submission_answers_form` (`WebsiteFormId`)
    ) CHARACTER SET=utf8mb4;");
    }

    // Self-managing: opens its own connection unless the caller already has one open (detected via
    // connection.State), so a bare call from anywhere - a brand-new Ensure*SchemaAsync method, a nested
    // helper, whatever - is always safe. This is deliberate: this exact "caller forgot to wrap the call
    // in OpenConnectionAsync/CloseConnectionAsync" mistake has taken production down three times
    // (2026-07-16, 2026-07-24, 2026-07-26 - see 09_TROUBLESHOOTING.md). A documented convention wasn't
    // enough; making the helper foolproof is.
    public static async Task EnsureTableColumnAsync(IPRODbContext db, string tableName, string columnName, string alterSql)
    {
        var ownsConnection = db.Database.GetDbConnection().State != System.Data.ConnectionState.Open;
        if (ownsConnection) await db.Database.OpenConnectionAsync();
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = @"
    SELECT COUNT(1)
    FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = @tableName
      AND COLUMN_NAME = @columnName";

            var tableParameter = command.CreateParameter();
            tableParameter.ParameterName = "@tableName";
            tableParameter.Value = tableName;
            command.Parameters.Add(tableParameter);

            var parameter = command.CreateParameter();
            parameter.ParameterName = "@columnName";
            parameter.Value = columnName;
            command.Parameters.Add(parameter);

            var exists = Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
            if (!exists)
            {
                try
                {
                    await db.Database.ExecuteSqlRawAsync(alterSql);
                }
                catch (MySqlConnector.MySqlException ex)
                    when (ex.ErrorCode == MySqlConnector.MySqlErrorCode.NoSuchTable)
                {
                    // The TABLE doesn't exist, not just the column. INFORMATION_SCHEMA.COLUMNS returns 0
                    // for a missing table exactly as it does for a missing column, so the check above
                    // cannot tell the two apart and we only find out when the ALTER raises 1146.
                    //
                    // Until 2026-08-15 this escaped and killed startup, which meant NO app could boot
                    // against an empty database -- the documented disaster-recovery path was unreachable,
                    // and FinancialLedgerSchemaGuard's own "a fresh database gets its FKs recreated by
                    // MigrateAsync and immediately stripped here" comment described a sequence that could
                    // never actually run.
                    //
                    // Continuing is right: either a later repair function creates the table (they are
                    // ordered by dependency, not alphabetically), or MigrateAsync does. Loud on stderr
                    // rather than silent, because the other way to reach this line is a typo'd table name
                    // in a repair function, and that must not pass unnoticed.
                    Console.Error.WriteLine(
                        $"[SchemaRepair] Skipped adding column '{columnName}': table '{tableName}' does not exist yet. " +
                        "Expected on a fresh database; on an established one it means the table name is wrong.");
                }
                catch (MySqlConnector.MySqlException ex)
                    when (ex.ErrorCode == MySqlConnector.MySqlErrorCode.DuplicateFieldName)
                {
                    // The column appeared between our INFORMATION_SCHEMA check and this ALTER.
                    //
                    // This is check-then-act across two processes: ipro-prod-web and ipro-prod-admin
                    // deploy from the SAME push, start within seconds of each other, and run identical
                    // schema repair against the SAME database. Both can see the column missing; the one
                    // that ALTERs second gets MySQL 1060.
                    //
                    // Unhandled, that exception escapes Main and Linux exits the process via SIGABRT --
                    // the same signature that took both apps down on 2026-07-29, and the reason
                    // SeedGuard exists. SeedGuard covered the DML seeders; this is the DDL half that
                    // was left uncovered.
                    //
                    // Swallowing is correct rather than merely convenient: the desired end state is
                    // "column exists", and it does. Catching only 1060 keeps a typo'd ALTER or a
                    // missing privilege loud.
                }
            }
        }
        finally
        {
            if (ownsConnection) await db.Database.CloseConnectionAsync();
        }
    }

    // Self-managing for the same reason as EnsureTableColumnAsync above.
    public static async Task EnsureUniqueIndexAsync(IPRODbContext db, string tableName, string indexName, string alterSql)
    {
        var ownsConnection = db.Database.GetDbConnection().State != System.Data.ConnectionState.Open;
        if (ownsConnection) await db.Database.OpenConnectionAsync();
        try
        {
            await using (var command = db.Database.GetDbConnection().CreateCommand())
            {
                command.CommandText = @"
    SELECT COUNT(1)
    FROM INFORMATION_SCHEMA.STATISTICS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = @tableName
      AND INDEX_NAME = @indexName";

                var tableParameter = command.CreateParameter();
                tableParameter.ParameterName = "@tableName";
                tableParameter.Value = tableName;
                command.Parameters.Add(tableParameter);

                var indexParameter = command.CreateParameter();
                indexParameter.ParameterName = "@indexName";
                indexParameter.Value = indexName;
                command.Parameters.Add(indexParameter);

                var exists = Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
                if (exists) return;
            }

            try
            {
                await db.Database.ExecuteSqlRawAsync(alterSql);
            }
            catch (MySqlConnector.MySqlException ex)
                when (ex.ErrorCode == MySqlConnector.MySqlErrorCode.NoSuchTable)
            {
                // Same fresh-database case as EnsureTableColumnAsync: the table isn't there yet, so
                // INFORMATION_SCHEMA.STATISTICS reported no index and the ALTER raised 1146. Skip and
                // continue -- a later repair function or MigrateAsync creates the table, and the next
                // boot adds the index. Loud, because a typo'd table name lands here too.
                Console.Error.WriteLine(
                    $"[SchemaRepair] Skipped creating index '{indexName}': table '{tableName}' does not exist yet. " +
                    "Expected on a fresh database; on an established one it means the table name is wrong.");
            }
            catch (MySqlConnector.MySqlException ex)
                when (ex.ErrorCode == MySqlConnector.MySqlErrorCode.DuplicateKeyEntry ||
                      ex.ErrorCode == MySqlConnector.MySqlErrorCode.DuplicateKeyName)
            {
                // DuplicateKeyName (1061): the other app created this same index between our
                // INFORMATION_SCHEMA check and this ALTER -- the index exists, the invariant holds.
                //
                // DuplicateKeyEntry (1062) is NOT benign: pre-existing duplicate rows blocked index
                // creation, so the app is running WITHOUT the uniqueness guarantee. It still boots --
                // crashing both apps over a data-quality problem is the worse outage (2026-07-29) --
                // but it must never again be silent (independent review H-8): it screams to stderr on
                // every boot until the duplicates are cleaned up and a restart creates the index.
                //
                // Any other error (typo'd SQL, missing privilege) still surfaces loudly.
                if (ex.ErrorCode == MySqlConnector.MySqlErrorCode.DuplicateKeyEntry)
                {
                    Console.Error.WriteLine(
                        $"[SCHEMA] UNIQUE INDEX {indexName} ON {tableName} NOT CREATED: existing rows already " +
                        $"violate it. The app is running WITHOUT this uniqueness guarantee. Clean up the " +
                        $"duplicate rows and restart. MySQL said: {ex.Message}");
                }
            }
        }
        finally
        {
            if (ownsConnection) await db.Database.CloseConnectionAsync();
        }
    }

}
