using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPRO.DataAccess.Migrations
{
    [Migration("20260711190000_AddAgentDomains")]
    public partial class AddAgentDomains : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
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
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NOT NULL,
    CONSTRAINT `PK_AgentDomains` PRIMARY KEY (`Id`),
    CONSTRAINT `FK_AgentDomains_AgentUsers_AgentUserId` FOREIGN KEY (`AgentUserId`) REFERENCES `AgentUsers` (`Id`) ON DELETE CASCADE,
    CONSTRAINT `FK_AgentDomains_AgentWebsites_AgentWebsiteId` FOREIGN KEY (`AgentWebsiteId`) REFERENCES `AgentWebsites` (`Id`) ON DELETE CASCADE
) CHARACTER SET=utf8mb4;");

            migrationBuilder.Sql(CreateIndexIfMissingSql(
                "AgentDomains",
                "IX_AgentDomains_DomainName",
                "CREATE UNIQUE INDEX `IX_AgentDomains_DomainName` ON `AgentDomains` (`DomainName`)"));

            migrationBuilder.Sql(CreateIndexIfMissingSql(
                "AgentDomains",
                "IX_AgentDomains_AgentUserId_IsPrimary",
                "CREATE INDEX `IX_AgentDomains_AgentUserId_IsPrimary` ON `AgentDomains` (`AgentUserId`, `IsPrimary`)"));

            migrationBuilder.Sql(CreateIndexIfMissingSql(
                "AgentDomains",
                "IX_AgentDomains_AgentWebsiteId",
                "CREATE INDEX `IX_AgentDomains_AgentWebsiteId` ON `AgentDomains` (`AgentWebsiteId`)"));
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AgentDomains");
        }

        private static string CreateIndexIfMissingSql(string tableName, string indexName, string createSql) => $@"
SET @indexExists = (
    SELECT COUNT(1)
    FROM INFORMATION_SCHEMA.STATISTICS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = '{tableName}'
      AND INDEX_NAME = '{indexName}'
);
SET @createIndexSql = IF(@indexExists = 0, '{createSql.Replace("'", "''")}', 'SELECT 1');
PREPARE createIndexStmt FROM @createIndexSql;
EXECUTE createIndexStmt;
DEALLOCATE PREPARE createIndexStmt;";
    }
}
