using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPRO.DataAccess.Migrations
{
    [DbContext(typeof(IPRODbContext))]
    [Migration("20260717180000_AddNewsLetterTemplates")]
    public partial class AddNewsLetterTemplates : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NewsLetterTemplates is created by startup schema repair (EnsureNewsLetterTemplateSchemaAsync)
            // before migrations run. Keeping this migration non-destructive prevents a failed partial
            // deploy from blocking Azure startup.
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
