using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPRO.DataAccess.Migrations
{
    [Migration("20260717210000_AddSupportTickets")]
    public partial class AddSupportTickets : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SupportTickets and SupportTicketMessages are created by startup schema repair
            // (EnsureSupportTicketSchemaAsync, both apps) before migrations run. Keeping this
            // migration non-destructive prevents a failed partial deploy from blocking Azure startup.
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
