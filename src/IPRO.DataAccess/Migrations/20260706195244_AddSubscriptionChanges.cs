using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPRO.DataAccess.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SubscriptionChanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    AgentUserId = table.Column<int>(type: "int", nullable: false),
                    CurrentBillingRuleId = table.Column<int>(type: "int", nullable: true),
                    RequestedBillingRuleId = table.Column<int>(type: "int", nullable: false),
                    BillingId = table.Column<int>(type: "int", nullable: true),
                    ChangeType = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Period = table.Column<int>(type: "int", nullable: false),
                    EffectiveDate = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ProratedCredit = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    ProratedCharge = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    AmountDue = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    AppliedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubscriptionChanges_AgentUsers_AgentUserId",
                        column: x => x.AgentUserId,
                        principalTable: "AgentUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SubscriptionChanges_BillingRules_CurrentBillingRuleId",
                        column: x => x.CurrentBillingRuleId,
                        principalTable: "BillingRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SubscriptionChanges_BillingRules_RequestedBillingRuleId",
                        column: x => x.RequestedBillingRuleId,
                        principalTable: "BillingRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SubscriptionChanges_Billings_BillingId",
                        column: x => x.BillingId,
                        principalTable: "Billings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionChanges_AgentUserId",
                table: "SubscriptionChanges",
                column: "AgentUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionChanges_BillingId",
                table: "SubscriptionChanges",
                column: "BillingId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionChanges_CurrentBillingRuleId",
                table: "SubscriptionChanges",
                column: "CurrentBillingRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionChanges_RequestedBillingRuleId",
                table: "SubscriptionChanges",
                column: "RequestedBillingRuleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SubscriptionChanges");
        }
    }
}
