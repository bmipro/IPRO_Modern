using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPRO.DataAccess.Migrations
{
    /// <inheritdoc />
    public partial class AddNewsletterSendRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NewsLetterSendId",
                table: "NewsLetterRecipients",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "NewsLetterSends",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    NewsLetterId = table.Column<int>(type: "int", nullable: false),
                    AgentUserId = table.Column<int>(type: "int", nullable: false),
                    AudienceType = table.Column<int>(type: "int", nullable: false),
                    AudienceLabel = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ScheduledAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    SentAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    TotalRecipients = table.Column<int>(type: "int", nullable: false),
                    TotalSent = table.Column<int>(type: "int", nullable: false),
                    TotalOpened = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsLetterSends", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NewsLetterSends_AgentUsers_AgentUserId",
                        column: x => x.AgentUserId,
                        principalTable: "AgentUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NewsLetterSends_NewsLetters_NewsLetterId",
                        column: x => x.NewsLetterId,
                        principalTable: "NewsLetters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_NewsLetterRecipients_NewsLetterSendId",
                table: "NewsLetterRecipients",
                column: "NewsLetterSendId");

            migrationBuilder.CreateIndex(
                name: "IX_NewsLetterSends_AgentUserId_ScheduledAt",
                table: "NewsLetterSends",
                columns: new[] { "AgentUserId", "ScheduledAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NewsLetterSends_NewsLetterId",
                table: "NewsLetterSends",
                column: "NewsLetterId");

            migrationBuilder.AddForeignKey(
                name: "FK_NewsLetterRecipients_NewsLetterSends_NewsLetterSendId",
                table: "NewsLetterRecipients",
                column: "NewsLetterSendId",
                principalTable: "NewsLetterSends",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_NewsLetterRecipients_NewsLetterSends_NewsLetterSendId",
                table: "NewsLetterRecipients");

            migrationBuilder.DropTable(
                name: "NewsLetterSends");

            migrationBuilder.DropIndex(
                name: "IX_NewsLetterRecipients_NewsLetterSendId",
                table: "NewsLetterRecipients");

            migrationBuilder.DropColumn(
                name: "NewsLetterSendId",
                table: "NewsLetterRecipients");
        }
    }
}
