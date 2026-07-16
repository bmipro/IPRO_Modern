using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPRO.DataAccess.Migrations
{
    [Migration("20260716120000_AddWebsiteFooterSettings")]
    public partial class AddWebsiteFooterSettings : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AgentWebsites.FooterSettingsJson is created by startup schema repair
            // (EnsureWebsiteTemplateSchemaAsync) before migrations run. Keeping this
            // migration non-destructive prevents a failed partial deploy from
            // blocking Azure startup.
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
