using System.Globalization;

namespace IPRO.Web.Infrastructure;

// W02-01 (audit 2026-11-26). The client page drew its round badge with FirstName[0] and LastName[0]. A contact made by
// a website form may have no last name (the forms do not ask for one, the lead-magnet form has no box for it at all),
// and an empty text has no first character: the page answered an error for every such contact. The initials are the
// first character of each name that has one; a contact with neither name shows a question mark.
public static class PersonInitials
{
    public static string Of(string? firstName, string? lastName)
    {
        var initials = First(firstName) + First(lastName);
        return initials.Length == 0 ? "?" : initials;
    }

    // The first whole character (a letter with its accent, an emoji), not half of one.
    private static string First(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) ? string.Empty : StringInfo.GetNextTextElement(trimmed, 0).ToUpperInvariant();
    }
}
