using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPRO.DataAccess.Migrations
{
    [Migration("20260719130000_AddGoogleCalendarSync")]
    public partial class AddGoogleCalendarSync : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // GoogleCalendarConnections, ExternalCalendarEvents, and ClientFollowUps.GoogleEventId
            // are created by startup schema repair (both apps) before migrations run. Keeping this
            // migration non-destructive prevents a failed partial deploy from blocking Azure startup.
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
