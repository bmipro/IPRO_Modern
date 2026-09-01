using System;

namespace IPRO.Utility;

// Are two addresses the same mailbox? (TODO 443, 2026-08-31.)
//
// Gmail ignores dots in the local part and everything from '+' on, and treats googlemail.com as
// gmail.com: john.smith+ipro@gmail.com, johnsmith@gmail.com and JohnSmith@googlemail.com are ONE
// mailbox. Found the hard way -- a test sent to bahmanmotamed@gmail.com arrived at
// bahman.motamed@gmail.com.
//
// Our data model does not know that. Client uniqueness is per exact string and suppression is a
// per-CLIENT-ROW flag, so one person entered twice under variants gets every send twice, and
// unsubscribing one row leaves the other mailing them -- the CASL exposure. Three places consult
// this: client create/edit uniqueness, CSV import de-duplication, and the suppression write.
//
// The rule is gmail.com/googlemail.com ONLY. Dot-stripping applied to any other domain would merge
// two genuinely different people (john.smith@ and johnsmith@ at a company are two mailboxes), so
// every other address is just trimmed and lower-cased. This never rewrites what is STORED -- the
// address stays as the agent typed it; only comparisons use the canonical form.
public static class CanonicalEmail
{
    public static string Canonical(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return string.Empty;
        var e = email.Trim().ToLowerInvariant();

        var at = e.LastIndexOf('@');
        if (at <= 0 || at == e.Length - 1) return e;   // malformed: compare as typed, never throw

        var local = e[..at];
        var domain = e[(at + 1)..];
        if (domain is not ("gmail.com" or "googlemail.com")) return e;

        var plus = local.IndexOf('+');
        if (plus >= 0) local = local[..plus];
        local = local.Replace(".", string.Empty);
        return local + "@gmail.com";                    // googlemail.com IS gmail.com
    }

    public static bool SamePerson(string? a, string? b)
    {
        var ca = Canonical(a);
        return ca.Length > 0 && ca == Canonical(b);
    }
}
