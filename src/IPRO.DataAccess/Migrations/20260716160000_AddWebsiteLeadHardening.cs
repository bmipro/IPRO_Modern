using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPRO.DataAccess.Migrations
{
    [Migration("20260716160000_AddWebsiteLeadHardening")]
    public partial class AddWebsiteLeadHardening : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // WebsiteLeads.NotificationSent/NotificationError and the WebsiteSpamAttempts table
            // are created by startup schema repair (EnsureWebsiteLeadSchemaAsync / WebsiteContentSchema.EnsureAsync)
            // before migrations run. Keeping this migration non-destructive prevents a failed
            // partial deploy from blocking Azure startup.
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
