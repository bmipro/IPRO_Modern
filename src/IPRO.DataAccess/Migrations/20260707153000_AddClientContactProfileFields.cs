using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPRO.DataAccess.Migrations
{
    public partial class AddClientContactProfileFields : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BusinessPhone",
                table: "Clients",
                type: "varchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "BusinessPhone2",
                table: "Clients",
                type: "varchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CellPhone",
                table: "Clients",
                type: "varchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CellPhone2",
                table: "Clients",
                type: "varchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CompanyName",
                table: "Clients",
                type: "varchar(150)",
                maxLength: 150,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "DateOfBirth",
                table: "Clients",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Email2",
                table: "Clients",
                type: "varchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Fax",
                table: "Clients",
                type: "varchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Fax2",
                table: "Clients",
                type: "varchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "HomePhone2",
                table: "Clients",
                type: "varchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "UnitNumber",
                table: "Clients",
                type: "varchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "BusinessPhone", table: "Clients");
            migrationBuilder.DropColumn(name: "BusinessPhone2", table: "Clients");
            migrationBuilder.DropColumn(name: "CellPhone", table: "Clients");
            migrationBuilder.DropColumn(name: "CellPhone2", table: "Clients");
            migrationBuilder.DropColumn(name: "CompanyName", table: "Clients");
            migrationBuilder.DropColumn(name: "DateOfBirth", table: "Clients");
            migrationBuilder.DropColumn(name: "Email2", table: "Clients");
            migrationBuilder.DropColumn(name: "Fax", table: "Clients");
            migrationBuilder.DropColumn(name: "Fax2", table: "Clients");
            migrationBuilder.DropColumn(name: "HomePhone2", table: "Clients");
            migrationBuilder.DropColumn(name: "UnitNumber", table: "Clients");
        }
    }
}
