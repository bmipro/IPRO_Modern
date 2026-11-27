namespace IPRO.Entities;

// 683 (the whole-code audit of 2026-11-26, W02-04). A contact's fields are limited in the database (a phone 40
// characters, a postal code 20, a name 80, a company 150, a street 200) and nothing checked them before the save:
// "416-555-0100 ext 234 / 647-555-0111 (cell)" in a phone box answered an error page and the form was lost, and
// one over-long cell among 800 imported contacts failed the whole import without naming the row. The limits are
// listed once, here; the form says which field is too long, and an import leaves that row out and says so.
// (ClientFieldLimitsTests keeps the numbers equal to the columns.)
public static class ClientFieldLimits
{
    public static readonly IReadOnlyList<(string Field, string Label, int Max, Func<Client, string?> Read)> Fields =
        new (string, string, int, Func<Client, string?>)[]
        {
            (nameof(Client.FirstName), "First name", 80, c => c.FirstName),
            (nameof(Client.LastName), "Last name", 80, c => c.LastName),
            (nameof(Client.CompanyName), "Company", 150, c => c.CompanyName),
            (nameof(Client.Email), "Email", 200, c => c.Email),
            (nameof(Client.Email2), "Second email", 200, c => c.Email2),
            (nameof(Client.Phone), "Home number 1", 40, c => c.Phone),
            (nameof(Client.HomePhone2), "Home number 2", 40, c => c.HomePhone2),
            (nameof(Client.BusinessPhone), "Business number 1", 40, c => c.BusinessPhone),
            (nameof(Client.BusinessPhone2), "Business number 2", 40, c => c.BusinessPhone2),
            (nameof(Client.CellPhone), "Cell number 1", 40, c => c.CellPhone),
            (nameof(Client.CellPhone2), "Cell number 2", 40, c => c.CellPhone2),
            (nameof(Client.Fax), "Fax number 1", 40, c => c.Fax),
            (nameof(Client.Fax2), "Fax number 2", 40, c => c.Fax2),
            (nameof(Client.Address), "Street", 200, c => c.Address),
            (nameof(Client.UnitNumber), "Unit", 40, c => c.UnitNumber),
            (nameof(Client.City), "City", 80, c => c.City),
            (nameof(Client.Province), "Province", 80, c => c.Province),
            (nameof(Client.PostalCode), "Postal code", 20, c => c.PostalCode),
            (nameof(Client.Country), "Country", 80, c => c.Country)
        };

    // What is too long for its field, one sentence each; empty when everything fits.
    public static IEnumerable<string> Problems(Client client)
    {
        foreach (var (_, label, max, read) in Fields)
        {
            var length = read(client)?.Length ?? 0;
            if (length > max) yield return $"{label} can be at most {max} characters. This one has {length}.";
        }
    }

    // An address a mail server could take: one @ with something on both sides and no spaces. Not a proof it exists.
    public static bool LooksLikeEmail(string? email)
    {
        var value = (email ?? string.Empty).Trim();
        var at = value.IndexOf('@');
        return at > 0 && at == value.LastIndexOf('@') && at < value.Length - 1 && !value.Any(char.IsWhiteSpace);
    }
}
