using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPRO.DataAccess.Migrations
{
    [Migration("20260717200000_AddAdminUsersAndAuditLog")]
    public partial class AddAdminUsersAndAuditLog : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AdminUsers and AdminAuditLogEntries are created by startup schema repair
            // (EnsureAdminUserSchemaAsync, IPRO.Admin only) before migrations run. Keeping this
            // migration non-destructive prevents a failed partial deploy from blocking Azure startup.
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
