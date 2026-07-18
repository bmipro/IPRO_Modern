using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPRO.DataAccess.Migrations
{
    [Migration("20260718120000_AddClientPortal")]
    public partial class AddClientPortal : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // PortalMessages, PortalDocuments, PortalAppointmentRequests, and the three new
            // Clients.Portal* columns are created by startup schema repair
            // (EnsureClientPortalSchemaAsync, both apps) before migrations run. Keeping this
            // migration non-destructive prevents a failed partial deploy from blocking Azure
            // startup.
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
