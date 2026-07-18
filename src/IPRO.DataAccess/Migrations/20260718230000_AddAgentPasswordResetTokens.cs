using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPRO.DataAccess.Migrations
{
    [Migration("20260718230000_AddAgentPasswordResetTokens")]
    public partial class AddAgentPasswordResetTokens : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AgentUsers.PasswordResetToken and PasswordResetTokenExpiresAt are created by
            // startup schema repair (both apps) before migrations run. Keeping this migration
            // non-destructive prevents a failed partial deploy from blocking Azure startup.
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
