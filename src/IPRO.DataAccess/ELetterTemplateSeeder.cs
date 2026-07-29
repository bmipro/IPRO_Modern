using System;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

// Seeds the starter letters that used to be a hardcoded array in IPRO.Entities. Runs once; after
// that the table belongs to SuperAdmin and this seeder must never touch it again, or an admin's
// edits would be silently reverted on the next deploy.
//
// These are starting points, not locked templates -- the agent edits the wording before sending.
// Merge tokens are baked into the bodies so the feature demonstrates itself.
public static class ELetterTemplateSeeder
{
    public static async Task SeedAsync(IPRODbContext db)
    {
        if (await db.ELetterTemplates.AnyAsync()) return;

        db.ELetterTemplates.AddRange(
            new ELetterTemplate
            {
                Key = "welcome",
                Name = "Welcome / new client",
                Description = "Sent after someone signs on -- what to expect and how to reach you.",
                Subject = "Welcome aboard, [First Name]",
                SortOrder = 10,
                Body =
                    """
                    Dear [First Name],

                    Thank you for choosing to work with [Advisor Company]. I'm glad to have you as a client, and I wanted to take a moment to welcome you properly.

                    Over the next little while I'll be reaching out to make sure everything is set up the way you need it. In the meantime, if anything at all comes up -- a question, a change in your circumstances, or something you'd like reviewed -- please don't hesitate to contact me directly.

                    You can reach me any time at [Advisor Phone] or by replying to this email.

                    Warm regards,
                    [Advisor Name]
                    """,
            },
            new ELetterTemplate
            {
                Key = "annual-review",
                Name = "Annual review request",
                Description = "Invite an existing client to book their yearly check-in.",
                Subject = "Time for your annual review, [First Name]",
                SortOrder = 20,
                Body =
                    """
                    Dear [First Name],

                    It's been about a year since we last reviewed your coverage together, and I'd like to set aside some time to go through it with you.

                    A lot can change in a year -- a new job, a move, a growing family, a shift in your plans. A short review makes sure what you have in place still matches where you actually are today.

                    The conversation usually takes under half an hour. Just reply to this email with a couple of times that suit you, or call me at [Advisor Phone] and we'll find a slot.

                    Best regards,
                    [Advisor Name]
                    [Advisor Company]
                    """,
            },
            new ELetterTemplate
            {
                Key = "policy-renewal",
                Name = "Policy renewal reminder",
                Description = "Heads-up that a policy or coverage is coming up for renewal.",
                Subject = "Your coverage is coming up for renewal",
                SortOrder = 30,
                Body =
                    """
                    Dear [First Name],

                    This is a friendly reminder that your coverage is approaching its renewal date.

                    There's nothing you need to do right now -- I'll be in touch shortly with the details. I did want to flag it early though, in case anything has changed on your end that we should factor in before it renews.

                    If you'd like to talk it through beforehand, reply here or call me at [Advisor Phone].

                    Kind regards,
                    [Advisor Name]
                    [Advisor Company]
                    """,
            },
            new ELetterTemplate
            {
                Key = "referral-thanks",
                Name = "Thanks for the referral",
                Description = "Acknowledge a client who sent business your way.",
                Subject = "Thank you, [First Name]",
                SortOrder = 40,
                Body =
                    """
                    Dear [First Name],

                    I wanted to say a genuine thank you for referring someone to me. It means a great deal.

                    Referrals are the highest compliment I can receive in this business -- it tells me you trust the work we've done together enough to put your own name behind it. I don't take that lightly, and I'll look after them the same way I look after you.

                    Thank you again.

                    Sincerely,
                    [Advisor Name]
                    [Advisor Company]
                    """,
            }
        );

        await db.SaveChangesAsync();
    }
}
