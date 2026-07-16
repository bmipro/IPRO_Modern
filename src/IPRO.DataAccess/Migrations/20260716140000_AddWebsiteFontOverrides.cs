using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPRO.DataAccess.Migrations
{
    [Migration("20260716140000_AddWebsiteFontOverrides")]
    public partial class AddWebsiteFontOverrides : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AgentWebsites.FontFamilyOverride/HeadingFontSizeOverride/BodyFontSizeOverride are
            // created by startup schema repair (EnsureWebsiteTemplateSchemaAsync) before migrations
            // run. Keeping this migration non-destructive prevents a failed partial deploy from
            // blocking Azure startup.
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
