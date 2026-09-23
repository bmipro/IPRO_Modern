using System;

namespace IPRO.Entities;

// 518 (2026-09-23): what a website lead is called in the leads list, the export and the dashboard.
// A submission from the Request Meeting page (its own form on /request-meeting, or a custom form
// whose page is that one) is a meeting request, not a "Contact request" -- TODO 504 item 1.
public static class WebsiteLeadText
{
    public static string KindLabel(string? submissionType, string? sourcePage, string? pageTitle)
    {
        switch (submissionType)
        {
            case WebsiteLeadTypes.Newsletter: return "Newsletter signup";
            case WebsiteLeadTypes.LeadMagnet: return "Lead magnet download";
        }
        if (Mentions(sourcePage, "meeting") || Mentions(pageTitle, "meeting")) return "Meeting request";
        return submissionType == WebsiteLeadTypes.CustomForm ? "Form submission" : "Contact request";
    }

    private static bool Mentions(string? text, string word) =>
        !string.IsNullOrWhiteSpace(text) && text.Contains(word, StringComparison.OrdinalIgnoreCase);
}
