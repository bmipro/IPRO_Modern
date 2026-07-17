using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPRO.DataAccess.Migrations
{
    [Migration("20260717220000_AddPromotionCodes")]
    public partial class AddPromotionCodes : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // PromotionCodes, PromotionCodeRedemptions, and SubscriptionChanges.PromotionCodeId are
            // created by startup schema repair (EnsurePromotionCodeSchemaAsync, both apps) before
            // migrations run. Keeping this migration non-destructive prevents a failed partial
            // deploy from blocking Azure startup.
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
