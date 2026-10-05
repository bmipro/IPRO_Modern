using System.Globalization;
using System.Net;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.Extensions.Configuration;

namespace IPRO.Billing;

// 532: the two emails a referrer gets, each saying exactly what happens and when -- the owner, 2026-09-29, on an
// early draft that said "Here's your $50" at join: "we need to be transparent because if you say here is 50 and then
// pay after 30 days, you will have lots of phone calls and enemies". iPro never emails the friend (no CASL exposure):
// the referrer shares the link from their own email.
public static class ReferralEmails
{
    public static SignupNotice.Notice Joined(Referral r, AgentUser referrer, IConfiguration configuration)
    {
        var friend = FriendName(r);
        var reward = ReferralProgram.Money(r.RewardAmount);
        var when = r.ExpectedEarnAt.HasValue ? r.ExpectedEarnAt.Value.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture) : "a month from now";
        var earned = r.FriendPeriod == BillingPeriod.Annually
            ? $"Your {reward} is earned 30 days after {friend}'s first payment, around {when}, if {friend} is still subscribed then, and comes back to the card or PayPal account you pay iPro with."
            : $"Your {reward} is earned at {friend}'s second monthly payment, expected around {when}, and comes back to the card or PayPal account you pay iPro with.";
        var paragraphs = new[]
        {
            $"{friend} joined with your gift.",
            earned,
            "Thank you for the referral. Every referral you make, and where each one stands, is on your Refer a Friend page.",
        };
        return Build($"{friend} joined with your gift", referrer, paragraphs, PageUrl(configuration));
    }

    public static SignupNotice.Notice Paid(Referral r, AgentUser referrer, IConfiguration configuration)
    {
        var friend = FriendName(r);
        var amount = r.RewardTax > 0m
            ? $"{ReferralProgram.Money(r.RewardNet)} plus tax ({ReferralProgram.Money(r.RewardGross)})"
            : ReferralProgram.Money(r.RewardGross);
        var paragraphs = new[]
        {
            $"{amount} for {friend}'s referral was refunded today to the card or PayPal account you pay iPro with. PayPal shows it right away; a card can take a few business days.",
            $"Your credit note, {r.CreditNoteNumber}, is on your Refer a Friend page.",
        };
        return Build($"Your {ReferralProgram.Money(r.RewardNet)} referral reward for {friend}", referrer, paragraphs, PageUrl(configuration));
    }

    public static string PageUrl(IConfiguration configuration) =>
        $"{IPRO.Utility.WebAppUrlHelper.GetWebAppBaseUrl(configuration)}/portal/ReferAFriend";

    private static string FriendName(Referral r) =>
        string.IsNullOrWhiteSpace(r.FriendName) ? "Your friend" : r.FriendName.Trim();

    private static SignupNotice.Notice Build(string subject, AgentUser referrer, IReadOnlyList<string> paragraphs, string link)
    {
        static string E(string value) => WebUtility.HtmlEncode(value);
        var hello = string.IsNullOrWhiteSpace(referrer.FirstName) ? "Hello," : $"Hi {referrer.FirstName.Trim()},";
        var body = string.Concat(paragraphs.Select(p => $"<p style=\"margin:0 0 14px\">{E(p)}</p>"));
        var html = $"""
            <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#17223a">
              <div style="padding:18px 22px;background:#193f82;color:#ffffff"><h1 style="margin:0;font-size:20px">Refer a Friend</h1></div>
              <div style="padding:22px;border:1px solid #dce4ef;border-top:0;font-size:15px;line-height:1.5">
                <p style="margin:0 0 14px">{E(hello)}</p>
                {body}
                <p style="margin:0 0 18px"><a href="{E(link)}" style="display:inline-block;padding:10px 16px;background:#193f82;color:#ffffff;text-decoration:none;border-radius:6px">Open Refer a Friend</a></p>
                <p style="margin:0;color:#5b6475">iPro Advisers</p>
              </div>
            </div>
            """;
        var text = $"{hello}\n\n{string.Join("\n\n", paragraphs)}\n\nRefer a Friend: {link}\n\niPro Advisers\n";
        return new SignupNotice.Notice(subject, html, text);
    }
}
