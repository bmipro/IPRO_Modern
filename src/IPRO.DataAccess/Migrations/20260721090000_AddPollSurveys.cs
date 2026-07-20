using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPRO.DataAccess.Migrations
{
    [Migration("20260721090000_AddPollSurveys")]
    public partial class AddPollSurveys : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // PollSurveys/PollQuestions/PollOptions/PollSends/PollRecipients/PollAnswers are
            // created by startup schema repair (both apps) before migrations run. Keeping this
            // migration non-destructive prevents a failed partial deploy from blocking Azure startup.
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
