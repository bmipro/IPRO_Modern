using System;
using System.Collections.Generic;

namespace IPRO.Utility;

// Is this address at a consumer webmail provider? (TODO 440, 2026-08-31.)
//
// Why it matters: every client-facing sender sets Reply-To to the agent's own address. When that is
// free webmail, the message ships as business-domain From + freemail Reply-To -- the header shape of
// business-email-compromise -- and SpamAssassin charges 2.503 for it (FREEMAIL_FORGED_REPLYTO).
// Measured on mail-tester with everything else identical: 7.7/10 from an agent on Yahoo, 10/10 from
// an agent on a business domain. The providers consult this and substitute the support address.
//
// Curated, not exhaustive: the consumer providers an independent Canadian adviser is realistically
// signed up with. It deliberately EXCLUDES ISP mailboxes (rogers.com, shaw.ca, sympatico.ca,
// telus.net, bell.net, videotron.ca): those are paid accounts, SpamAssassin's FreeMail plugin does
// not list them, and stripping a legitimate Reply-To is the worse error. Exact registrable-domain
// match only -- "mail.yahoo.com" and "gmail.com.example.net" are not freemail.
public static class FreemailDomains
{
    private static readonly HashSet<string> Domains = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com",
        "yahoo.com", "yahoo.ca", "ymail.com", "rocketmail.com",
        "hotmail.com", "hotmail.ca", "outlook.com", "live.com", "live.ca", "msn.com",
        "aol.com",
        "icloud.com", "me.com", "mac.com",
        "protonmail.com", "proton.me", "pm.me",
        "mail.com", "gmx.com", "gmx.net", "zoho.com", "yandex.com",
    };

    public static bool IsFreemail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        var trimmed = email.Trim();
        var at = trimmed.LastIndexOf('@');
        // "@gmail.com", "x@" and "no-at-sign" are malformed, not the freemail pattern; the provider
        // rejects those on its own terms and this must not throw on them.
        if (at <= 0 || at == trimmed.Length - 1) return false;
        return Domains.Contains(trimmed[(at + 1)..]);
    }
}
