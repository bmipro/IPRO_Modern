using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPRO.DataAccess.Migrations
{
    [Migration("20260726220000_AddWebsiteSidebarPositionOverride")]
    public partial class AddWebsiteSidebarPositionOverride : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AgentWebsites.SidebarPositionOverride is created by startup schema repair
            // (EnsureWebsiteTemplateSchemaAsync) before migrations run. Keeping this migration
            // non-destructive prevents a failed partial deploy from blocking Azure startup.
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
