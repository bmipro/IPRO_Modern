using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPRO.DataAccess.Migrations
{
    /// <inheritdoc />
    public partial class AddNewsletterUnsubscribeTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UnsubscribeToken",
                table: "NewsLetterRecipients",
                type: "varchar(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_NewsLetterRecipients_UnsubscribeToken",
                table: "NewsLetterRecipients",
                column: "UnsubscribeToken");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NewsLetterRecipients_UnsubscribeToken",
                table: "NewsLetterRecipients");

            migrationBuilder.DropColumn(
                name: "UnsubscribeToken",
                table: "NewsLetterRecipients");
        }
    }
}
