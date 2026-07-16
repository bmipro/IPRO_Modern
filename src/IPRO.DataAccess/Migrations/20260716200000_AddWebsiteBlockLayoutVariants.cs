using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPRO.DataAccess.Migrations
{
    [Migration("20260716200000_AddWebsiteBlockLayoutVariants")]
    public partial class AddWebsiteBlockLayoutVariants : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // WebsiteContentBlocks.LayoutVariant is created by startup schema repair
            // (EnsureWebsiteContentBlockSchemaAsync) before migrations run.
            // Keeping this migration non-destructive prevents a failed partial deploy from blocking Azure startup.
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
