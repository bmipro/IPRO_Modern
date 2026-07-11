using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPRO.DataAccess.Migrations
{
    [Migration("20260711190000_AddAgentDomains")]
    public partial class AddAgentDomains : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AgentDomains is created by startup schema repair before migrations run.
            // Keeping this migration non-destructive prevents a failed partial deploy
            // from blocking Azure startup.
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AgentDomains");
        }
    }
}
