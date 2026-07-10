using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPRO.DataAccess.Migrations
{
    /// <inheritdoc />
    public partial class AddDripCampaignEnrollments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DripCampaignEnrollments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    AgentUserId = table.Column<int>(type: "int", nullable: false),
                    DripCampaignId = table.Column<int>(type: "int", nullable: false),
                    ClientId = table.Column<int>(type: "int", nullable: false),
                    ClientCategoryId = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    NextStepIndex = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    NextSendAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastSentAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastError = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DripCampaignEnrollments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DripCampaignEnrollments_AgentUsers_AgentUserId",
                        column: x => x.AgentUserId,
                        principalTable: "AgentUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DripCampaignEnrollments_ClientCategories_ClientCategoryId",
                        column: x => x.ClientCategoryId,
                        principalTable: "ClientCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_DripCampaignEnrollments_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DripCampaignEnrollments_DripCampaigns_DripCampaignId",
                        column: x => x.DripCampaignId,
                        principalTable: "DripCampaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_DripCampaignEnrollments_AgentUserId_Status_NextSendAt",
                table: "DripCampaignEnrollments",
                columns: new[] { "AgentUserId", "Status", "NextSendAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DripCampaignEnrollments_ClientCategoryId",
                table: "DripCampaignEnrollments",
                column: "ClientCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_DripCampaignEnrollments_ClientId",
                table: "DripCampaignEnrollments",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_DripCampaignEnrollments_DripCampaignId_ClientId_Status",
                table: "DripCampaignEnrollments",
                columns: new[] { "DripCampaignId", "ClientId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DripCampaignEnrollments");
        }
    }
}
