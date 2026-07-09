using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPRO.DataAccess.Migrations
{
    /// <inheritdoc />
    public partial class AddNewsletterSendAudienceTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClientCategoryId",
                table: "NewsLetterSends",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ClientId",
                table: "NewsLetterSends",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_NewsLetterSends_ClientCategoryId",
                table: "NewsLetterSends",
                column: "ClientCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_NewsLetterSends_ClientId",
                table: "NewsLetterSends",
                column: "ClientId");

            migrationBuilder.AddForeignKey(
                name: "FK_NewsLetterSends_ClientCategories_ClientCategoryId",
                table: "NewsLetterSends",
                column: "ClientCategoryId",
                principalTable: "ClientCategories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_NewsLetterSends_Clients_ClientId",
                table: "NewsLetterSends",
                column: "ClientId",
                principalTable: "Clients",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_NewsLetterSends_ClientCategories_ClientCategoryId",
                table: "NewsLetterSends");

            migrationBuilder.DropForeignKey(
                name: "FK_NewsLetterSends_Clients_ClientId",
                table: "NewsLetterSends");

            migrationBuilder.DropIndex(
                name: "IX_NewsLetterSends_ClientCategoryId",
                table: "NewsLetterSends");

            migrationBuilder.DropIndex(
                name: "IX_NewsLetterSends_ClientId",
                table: "NewsLetterSends");

            migrationBuilder.DropColumn(
                name: "ClientCategoryId",
                table: "NewsLetterSends");

            migrationBuilder.DropColumn(
                name: "ClientId",
                table: "NewsLetterSends");
        }
    }
}
