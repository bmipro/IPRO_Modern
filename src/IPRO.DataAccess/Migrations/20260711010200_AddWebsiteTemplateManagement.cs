using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPRO.DataAccess.Migrations
{
    /// <inheritdoc />
    [Migration("20260711010200_AddWebsiteTemplateManagement")]
    public partial class AddWebsiteTemplateManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(AddColumnIfMissingSql(
                "BusinessType",
                "ALTER TABLE `WebsiteTemplates` ADD COLUMN `BusinessType` longtext CHARACTER SET utf8mb4 NULL"));

            migrationBuilder.Sql(AddColumnIfMissingSql(
                "IsDefault",
                "ALTER TABLE `WebsiteTemplates` ADD COLUMN `IsDefault` tinyint(1) NOT NULL DEFAULT FALSE"));

            migrationBuilder.Sql(AddColumnIfMissingSql(
                "TemplateKey",
                "ALTER TABLE `WebsiteTemplates` ADD COLUMN `TemplateKey` varchar(80) CHARACTER SET utf8mb4 NULL"));

            migrationBuilder.Sql("UPDATE WebsiteTemplates SET TemplateKey = CONCAT('template-', Id) WHERE TemplateKey IS NULL OR TemplateKey = '';");

            migrationBuilder.Sql(@"
SET @indexExists = (
    SELECT COUNT(1)
    FROM INFORMATION_SCHEMA.STATISTICS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'WebsiteTemplates'
      AND INDEX_NAME = 'IX_WebsiteTemplates_TemplateKey'
);
SET @createIndexSql = IF(
    @indexExists = 0,
    'CREATE UNIQUE INDEX `IX_WebsiteTemplates_TemplateKey` ON `WebsiteTemplates` (`TemplateKey`)',
    'SELECT 1'
);
PREPARE createIndexStmt FROM @createIndexSql;
EXECUTE createIndexStmt;
DEALLOCATE PREPARE createIndexStmt;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WebsiteTemplates_TemplateKey",
                table: "WebsiteTemplates");

            migrationBuilder.DropColumn(
                name: "BusinessType",
                table: "WebsiteTemplates");

            migrationBuilder.DropColumn(
                name: "IsDefault",
                table: "WebsiteTemplates");

            migrationBuilder.DropColumn(
                name: "TemplateKey",
                table: "WebsiteTemplates");

            migrationBuilder.AlterColumn<string>(
                name: "PreviewImageUrl",
                table: "WebsiteTemplates",
                type: "longtext",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(500)",
                oldMaxLength: 500)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "WebsiteTemplates",
                type: "longtext",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(120)",
                oldMaxLength: 120)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "WebsiteTemplates",
                type: "longtext",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(500)",
                oldMaxLength: 500)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");
        }

        private static string AddColumnIfMissingSql(string columnName, string alterSql) => $@"
SET @columnExists = (
    SELECT COUNT(1)
    FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'WebsiteTemplates'
      AND COLUMN_NAME = '{columnName}'
);
SET @addColumnSql = IF(@columnExists = 0, '{alterSql.Replace("'", "''")}', 'SELECT 1');
PREPARE addColumnStmt FROM @addColumnSql;
EXECUTE addColumnStmt;
DEALLOCATE PREPARE addColumnStmt;";
    }
}
