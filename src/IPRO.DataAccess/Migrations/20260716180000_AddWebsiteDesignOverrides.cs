using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPRO.DataAccess.Migrations
{
    [Migration("20260716180000_AddWebsiteDesignOverrides")]
    public partial class AddWebsiteDesignOverrides : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AgentWebsites.BackgroundColorOverride/ButtonStyleOverride/SectionSpacingOverride/HeroStyleOverride
            // are created by startup schema repair (EnsureWebsiteTemplateSchemaAsync) before migrations run.
            // Keeping this migration non-destructive prevents a failed partial deploy from blocking Azure startup.
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
