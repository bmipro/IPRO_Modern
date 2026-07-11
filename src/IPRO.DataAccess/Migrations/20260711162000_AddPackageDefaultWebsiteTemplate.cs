using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPRO.DataAccess.Migrations
{
    /// <inheritdoc />
    [Migration("20260711162000_AddPackageDefaultWebsiteTemplate")]
    public partial class AddPackageDefaultWebsiteTemplate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
SET @columnExists = (
    SELECT COUNT(1)
    FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'BillingRules'
      AND COLUMN_NAME = 'DefaultWebsiteTemplateId'
);
SET @addColumnSql = IF(
    @columnExists = 0,
    'ALTER TABLE `BillingRules` ADD COLUMN `DefaultWebsiteTemplateId` int NULL',
    'SELECT 1'
);
PREPARE addColumnStmt FROM @addColumnSql;
EXECUTE addColumnStmt;
DEALLOCATE PREPARE addColumnStmt;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultWebsiteTemplateId",
                table: "BillingRules");
        }
    }
}
