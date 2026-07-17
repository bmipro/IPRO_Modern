using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPRO.DataAccess.Migrations
{
    [Migration("20260717190000_AddDripStepTrackingAndClicks")]
    public partial class AddDripStepTrackingAndClicks : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DripCampaignStepSends and NewsLetterSends/NewsLetters.TotalClicked are created by startup
            // schema repair (EnsureDripCampaignStepSendSchemaAsync / EnsureNewsLetterClickTrackingSchemaAsync)
            // before migrations run. Keeping this migration non-destructive prevents a failed partial
            // deploy from blocking Azure startup.
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
