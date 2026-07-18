using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPRO.DataAccess.Migrations
{
    [Migration("20260718100000_AddClientInvoices")]
    public partial class AddClientInvoices : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ClientInvoices, ClientInvoiceLineItems, RecurringInvoiceSchedules,
            // RecurringInvoiceLineItems, and AgentUsers.DefaultPaymentLink are created by startup
            // schema repair (EnsureClientInvoiceSchemaAsync, both apps) before migrations run.
            // Keeping this migration non-destructive prevents a failed partial deploy from
            // blocking Azure startup.
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
