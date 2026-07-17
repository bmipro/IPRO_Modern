using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPRO.DataAccess.Migrations
{
    [Migration("20260716220000_AddDomainRetryAndApexTracking")]
    public partial class AddDomainRetryAndApexTracking : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AgentDomains.RetryCount/LastFailedAt/NextRetryAt/AutoRetryExhausted and
            // RootDnsStatus/RootRedirectsToWww/RootLastCheckedAt/RootLastError are created by
            // startup schema repair (EnsureAgentDomainSchemaAsync) before migrations run.
            // Keeping this migration non-destructive prevents a failed partial deploy from blocking Azure startup.
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
