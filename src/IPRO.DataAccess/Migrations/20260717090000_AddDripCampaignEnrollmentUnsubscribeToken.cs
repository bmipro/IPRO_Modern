using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPRO.DataAccess.Migrations
{
    [Migration("20260717090000_AddDripCampaignEnrollmentUnsubscribeToken")]
    public partial class AddDripCampaignEnrollmentUnsubscribeToken : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DripCampaignEnrollments.UnsubscribeToken is created by startup schema repair
            // (EnsureDripCampaignEnrollmentSchemaAsync) before migrations run.
            // Keeping this migration non-destructive prevents a failed partial deploy from blocking Azure startup.
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
