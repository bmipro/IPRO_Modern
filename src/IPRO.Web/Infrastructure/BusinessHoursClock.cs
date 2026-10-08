using IPRO.DataAccess;
using IPRO.Entities;

namespace IPRO.Web.Infrastructure;

// 556: "open now" is read on the business's own clock (the time zone on the owner's profile), not the
// server's and not the visitor's -- a shop in Toronto is closed at 9 p.m. Toronto time wherever the
// page is read from. Also names who a visitor's form goes to.
public static class BusinessHoursClock
{
    public static DateTime Now(AgentUser? agent) => AgentLocalTime.FromUtc(DateTime.UtcNow, agent?.TimeZone);

    // Who a contact form's consent line names: the business, else the person, never a trade the
    // business may not be in ("this adviser" was on a bakery's form).
    public static string ConsentParty(AgentUser? agent)
    {
        if (!string.IsNullOrWhiteSpace(agent?.CompanyName)) return agent.CompanyName.Trim();
        var person = $"{agent?.FirstName} {agent?.LastName}".Trim();
        return person.Length > 0 ? person : "this business";
    }
}
