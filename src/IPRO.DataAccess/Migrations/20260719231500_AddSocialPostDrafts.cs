using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPRO.DataAccess.Migrations
{
    [Migration("20260719231500_AddSocialPostDrafts")]
    public partial class AddSocialPostDrafts : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SocialPostDrafts is created by startup schema repair (both apps) before
            // migrations run. Keeping this migration non-destructive prevents a failed
            // partial deploy from blocking Azure startup.
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
