using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPRO.DataAccess.Migrations
{
    [Migration("20260719230000_AddAgentDocuments")]
    public partial class AddAgentDocuments : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AgentDocuments is created by startup schema repair (both apps) before
            // migrations run. Keeping this migration non-destructive prevents a failed
            // partial deploy from blocking Azure startup.
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
