using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

public static class WebsiteContentSchema
{
    public static async Task EnsureAsync(IPRODbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
CREATE TABLE IF NOT EXISTS `WebsitePages` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `AgentWebsiteId` int NOT NULL,
  `ParentPageId` int NULL,
  `Title` varchar(160) CHARACTER SET utf8mb4 NOT NULL,
  `Slug` varchar(120) CHARACTER SET utf8mb4 NOT NULL,
  `NavigationLabel` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
  `MetaTitle` varchar(180) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
  `MetaDescription` varchar(320) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
  `IsHomePage` tinyint(1) NOT NULL DEFAULT FALSE,
  `ShowInNavigation` tinyint(1) NOT NULL DEFAULT TRUE,
  `IsPublished` tinyint(1) NOT NULL DEFAULT TRUE,
  `SortOrder` int NOT NULL DEFAULT 0,
  `CreatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `UpdatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`Id`),
  UNIQUE KEY `IX_WebsitePages_AgentWebsiteId_Slug` (`AgentWebsiteId`, `Slug`),
  KEY `IX_WebsitePages_ParentPageId` (`ParentPageId`),
  CONSTRAINT `FK_WebsitePages_AgentWebsites_AgentWebsiteId` FOREIGN KEY (`AgentWebsiteId`) REFERENCES `AgentWebsites` (`Id`) ON DELETE CASCADE,
  CONSTRAINT `FK_WebsitePages_WebsitePages_ParentPageId` FOREIGN KEY (`ParentPageId`) REFERENCES `WebsitePages` (`Id`) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
""");

        await db.Database.ExecuteSqlRawAsync("""
CREATE TABLE IF NOT EXISTS `WebsiteContentBlocks` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `WebsitePageId` int NOT NULL,
  `BlockType` varchar(40) CHARACTER SET utf8mb4 NOT NULL,
  `Heading` varchar(220) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
  `Subheading` varchar(500) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
  `Body` longtext CHARACTER SET utf8mb4 NOT NULL,
  `ImageUrl` varchar(1000) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
  `ButtonText` varchar(100) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
  `ButtonUrl` varchar(1000) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
  `SettingsJson` longtext CHARACTER SET utf8mb4 NOT NULL,
  `SortOrder` int NOT NULL DEFAULT 0,
  `IsVisible` tinyint(1) NOT NULL DEFAULT TRUE,
  `CreatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `UpdatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`Id`),
  KEY `IX_WebsiteContentBlocks_WebsitePageId` (`WebsitePageId`),
  CONSTRAINT `FK_WebsiteContentBlocks_WebsitePages_WebsitePageId` FOREIGN KEY (`WebsitePageId`) REFERENCES `WebsitePages` (`Id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
""");

        await db.Database.ExecuteSqlRawAsync("""
CREATE TABLE IF NOT EXISTS `WebsiteMediaAssets` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `AgentWebsiteId` int NOT NULL,
  `OriginalFileName` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
  `BlobUrl` varchar(1000) CHARACTER SET utf8mb4 NOT NULL,
  `ContentType` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
  `FileSize` bigint NOT NULL,
  `CreatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`Id`),
  KEY `IX_WebsiteMediaAssets_AgentWebsiteId` (`AgentWebsiteId`),
  CONSTRAINT `FK_WebsiteMediaAssets_AgentWebsites_AgentWebsiteId` FOREIGN KEY (`AgentWebsiteId`) REFERENCES `AgentWebsites` (`Id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
""");

        await db.Database.ExecuteSqlRawAsync("""
CREATE TABLE IF NOT EXISTS `WebsiteLeads` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `AgentUserId` int NOT NULL,
  `AgentWebsiteId` int NOT NULL,
  `WebsitePageId` int NULL,
  `ClientId` int NULL,
  `SubmissionType` varchar(30) CHARACTER SET utf8mb4 NOT NULL,
  `FirstName` varchar(80) CHARACTER SET utf8mb4 NOT NULL,
  `LastName` varchar(80) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
  `Email` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
  `Phone` varchar(40) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
  `Message` longtext CHARACTER SET utf8mb4 NOT NULL,
  `SourceDomain` varchar(255) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
  `SourcePage` varchar(500) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
  `Referrer` varchar(1000) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
  `IpAddress` varchar(64) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
  `ConsentGiven` tinyint(1) NOT NULL DEFAULT FALSE,
  `IsRead` tinyint(1) NOT NULL DEFAULT FALSE,
  `ReadAt` datetime(6) NULL,
  `Status` varchar(30) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'New',
  `ProcessingNote` varchar(500) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
  `CreatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `UpdatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`Id`),
  KEY `IX_WebsiteLeads_AgentUserId_IsRead_Status` (`AgentUserId`,`IsRead`,`Status`),
  KEY `IX_WebsiteLeads_AgentWebsiteId` (`AgentWebsiteId`),
  KEY `IX_WebsiteLeads_WebsitePageId` (`WebsitePageId`),
  KEY `IX_WebsiteLeads_ClientId` (`ClientId`),
  CONSTRAINT `FK_WebsiteLeads_AgentUsers_AgentUserId` FOREIGN KEY (`AgentUserId`) REFERENCES `AgentUsers` (`Id`) ON DELETE CASCADE,
  CONSTRAINT `FK_WebsiteLeads_AgentWebsites_AgentWebsiteId` FOREIGN KEY (`AgentWebsiteId`) REFERENCES `AgentWebsites` (`Id`) ON DELETE CASCADE,
  CONSTRAINT `FK_WebsiteLeads_WebsitePages_WebsitePageId` FOREIGN KEY (`WebsitePageId`) REFERENCES `WebsitePages` (`Id`) ON DELETE SET NULL,
  CONSTRAINT `FK_WebsiteLeads_Clients_ClientId` FOREIGN KEY (`ClientId`) REFERENCES `Clients` (`Id`) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
""");

        await db.Database.ExecuteSqlRawAsync("""
CREATE TABLE IF NOT EXISTS `WebsitePageViews` (
  `Id` bigint NOT NULL AUTO_INCREMENT,
  `AgentWebsiteId` int NOT NULL,
  `WebsitePageId` int NULL,
  `SourceDomain` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
  `Path` varchar(500) CHARACTER SET utf8mb4 NOT NULL,
  `ReferrerHost` varchar(255) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
  `VisitorHash` varchar(64) CHARACTER SET utf8mb4 NOT NULL,
  `CreatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`Id`),
  KEY `IX_WebsitePageViews_AgentWebsiteId_CreatedAt` (`AgentWebsiteId`,`CreatedAt`),
  KEY `IX_WebsitePageViews_AgentWebsiteId_VisitorHash_CreatedAt` (`AgentWebsiteId`,`VisitorHash`,`CreatedAt`),
  KEY `IX_WebsitePageViews_WebsitePageId` (`WebsitePageId`),
  CONSTRAINT `FK_WebsitePageViews_AgentWebsites_AgentWebsiteId` FOREIGN KEY (`AgentWebsiteId`) REFERENCES `AgentWebsites` (`Id`) ON DELETE CASCADE,
  CONSTRAINT `FK_WebsitePageViews_WebsitePages_WebsitePageId` FOREIGN KEY (`WebsitePageId`) REFERENCES `WebsitePages` (`Id`) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
""");

        await db.Database.ExecuteSqlRawAsync("""
CREATE TABLE IF NOT EXISTS `WebsiteStarterPages` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `BillingRuleId` int NULL,
  `BusinessType` varchar(80) CHARACTER SET utf8mb4 NOT NULL,
  `Title` varchar(160) CHARACTER SET utf8mb4 NOT NULL,
  `Slug` varchar(120) CHARACTER SET utf8mb4 NOT NULL,
  `NavigationLabel` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
  `MetaTitle` varchar(180) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
  `MetaDescription` varchar(320) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
  `IsHomePage` tinyint(1) NOT NULL DEFAULT FALSE,
  `ShowInNavigation` tinyint(1) NOT NULL DEFAULT TRUE,
  `IsActive` tinyint(1) NOT NULL DEFAULT TRUE,
  `SortOrder` int NOT NULL DEFAULT 0,
  `CreatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `UpdatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`Id`),
  KEY `IX_WebsiteStarterPages_BillingRuleId` (`BillingRuleId`),
  KEY `IX_WebsiteStarterPages_BusinessType` (`BusinessType`),
  CONSTRAINT `FK_WebsiteStarterPages_BillingRules_BillingRuleId` FOREIGN KEY (`BillingRuleId`) REFERENCES `BillingRules` (`Id`) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
""");

        await db.Database.ExecuteSqlRawAsync("""
CREATE TABLE IF NOT EXISTS `WebsiteStarterBlocks` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `WebsiteStarterPageId` int NOT NULL,
  `BlockType` varchar(40) CHARACTER SET utf8mb4 NOT NULL,
  `Heading` varchar(220) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
  `Subheading` varchar(500) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
  `Body` longtext CHARACTER SET utf8mb4 NOT NULL,
  `ImageUrl` varchar(1000) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
  `ButtonText` varchar(100) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
  `ButtonUrl` varchar(1000) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
  `SettingsJson` longtext CHARACTER SET utf8mb4 NOT NULL,
  `SortOrder` int NOT NULL DEFAULT 0,
  `IsVisible` tinyint(1) NOT NULL DEFAULT TRUE,
  PRIMARY KEY (`Id`),
  KEY `IX_WebsiteStarterBlocks_WebsiteStarterPageId` (`WebsiteStarterPageId`),
  CONSTRAINT `FK_WebsiteStarterBlocks_WebsiteStarterPages_WebsiteStarterPageId` FOREIGN KEY (`WebsiteStarterPageId`) REFERENCES `WebsiteStarterPages` (`Id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
""");
    }
}
